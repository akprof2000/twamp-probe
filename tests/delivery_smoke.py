"""Exercise the three release binaries against a disposable local ClickHouse instance."""
import argparse
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen
from uuid import uuid4


def free_url():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return f"http://127.0.0.1:{sock.getsockname()[1]}"


def request(url, payload=None, headers=None, method=None):
    req = Request(url, data=payload, headers=headers or {}, method=method)
    with urlopen(req, timeout=4) as res:
        return res.read().decode()


def wait(check, label):
    deadline = time.monotonic() + 45
    last_error = None
    while time.monotonic() < deadline:
        try:
            value = check()
            if value:
                return value
        except (OSError, URLError) as error:
            last_error = error
        time.sleep(.15)
    raise AssertionError(f"Timeout waiting for {label}: {last_error}")


def seed_legacy(start, stop, binary, server, offline, spool, headers):
    task = str(uuid4())
    batches = [str(uuid4()) for _ in range(3)]
    confirmed = set()
    ids = [str(uuid4()) for _ in range(5)]
    acknowledged = threading.Event()
    records = [dict(resultId=id_, taskId=task, creation="2026-01-15T10:00:00Z",
                    mode="TWampy", console=f"legacy raw {i}", errorConsole="fixture")
               for i, id_ in enumerate(ids)]

    class LegacyProbe(BaseHTTPRequestHandler):
        def answer(self, value):
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps(value).encode())

        def do_GET(self):
            if self.path.lower().endswith("/checkdata"):
                if acknowledged.wait(.2):
                    self.answer(dict(batchId=batches[-1], items=[]))
                else:
                    index = len(confirmed)
                    self.answer(dict(batchId=batches[index], items=records[index*2:index*2+2]))
            else:
                self.answer([])

        def do_POST(self):
            if "/confirmdata?" in self.path.lower():
                confirmed.add(self.path.split("=")[-1].lower())
                if len(confirmed) == len(batches):
                    acknowledged.set()
            self.answer(True)

        def log_message(self, *_):
            pass

    with ThreadingHTTPServer(("127.0.0.1", 0), LegacyProbe) as fake:
        thread = threading.Thread(target=fake.serve_forever, daemon=True)
        thread.start()
        try:
            probe_url = "http://127.0.0.1:" + str(fake.server_port)
            start("server", binary, {"Urls": server,
                  "ClickHouse": {"Enabled": True, "Url": offline, "SpoolPath": str(spool),
                                 "BatchRows": 2, "FlushMinutes": 60, "RetrySeconds": 1}})
            wait(lambda: request(server + "/api/userinterface/listclients", headers=headers), "legacy server")
            request(server + "/api/userinterface/SetInfoClient",
                    json.dumps({"name": "legacy-probe", "requestInfo": probe_url}).encode(), headers)
            assert acknowledged.wait(20), "Legacy server did not ACK its buffered batch"
            files = list(spool.glob("seg-*.ndjson")) + list(spool.glob("current.ndjson"))
            assert len(files) == 3 and any(p.name == "current.ndjson" for p in files), files
            rows = [json.loads(line) for p in files for line in p.read_text(encoding="utf8").splitlines()]
            assert sorted(r["ResultId"] for r in rows) == sorted(ids)
        finally:
            stop("server")
            fake.shutdown()
            thread.join()
    return task, ids, probe_url


def run(args, root):
    ports = set()
    while len(ports) < 4:
        ports.add(free_url())
    probe, server, collector, offline = sorted(ports)
    processes = {}
    logs = []
    table = "delivery_" + uuid4().hex
    task = str(uuid4())
    ids = [str(uuid4()), str(uuid4())]
    records = [dict(resultId=id_, taskId=task, creation="2026-01-15T10:00:00Z",
                    ipAddress="127.0.0.1", requestInfo=probe, mode="TWampy",
                    console="unparsed output", outcome="Success") for id_ in ids]
    for service in ("probe", "server", "collector"):
        (root / service).mkdir()
    legacy = root / "probe" / "JobResult.json"
    legacy.write_text(json.dumps(records))
    spool = root / "collector" / "spool"
    key = str(uuid4())
    headers = {"X-Api-Key": key, "Content-Type": "application/json"}

    def sql(query):
        return request(args.clickhouse, query.encode(), {"X-ClickHouse-User": "default", "X-ClickHouse-Key": os.environ.get("TWAMP_CLICKHOUSE_PASSWORD", "")})

    def start(name, binary, config):
        directory = root / name
        config["Auth"] = {"ApiKey": key}
        (directory / "appsettings.json").write_text(json.dumps(config), encoding="utf8")
        output = open(directory / "service.log", "ab")
        logs.append(output)
        # A developer's deployment variables must not redirect a test service to a real database.
        prefixes = ("CLICKHOUSE__", "DATABASE__", "COLLECTOR__", "CONFIGURATOR__", "AUTH__", "PROBE__", "ASPNETCORE_")
        env = {k: v for k, v in os.environ.items() if not k.upper().startswith(prefixes) and k.upper() != "URLS"}
        processes[name] = subprocess.Popen([binary], cwd=directory, stdout=output, stderr=output, env=env,
                                          creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))

    def stop(name):
        proc = processes.pop(name, None)
        if proc and proc.poll() is None:
            proc.kill()
            proc.wait(timeout=10)

    probe_config = {"Urls": probe, "Probe": {"MaxParallel": 4, "ResultBatchSize": 10}}
    collector_config = {"Urls": collector, "Collector": {"Id": "site-a"}, "Configurator": {"Url": server}, "Probe": {"HttpTimeoutSec": 40},
                        "ClickHouse": {"Url": offline, "SpoolPath": str(spool), "Database": "default",
                                       "Table": table, "RetrySeconds": 1,
                                       "Password": os.environ.get("TWAMP_CLICKHOUSE_PASSWORD", "")}}
    try:
        legacy_task, legacy_ids, old_probe = (seed_legacy(start, stop, args.legacy, server, offline, spool, headers)
                                             if args.legacy else (None, [], None))
        start("probe", args.probe, probe_config)
        start("server", args.configurator, {"Urls": server, "Collector": {"Url": collector}})
        wait(lambda: request(server + "/api/collector/catalog", headers=headers), "configurator")
        state_url = probe + "/api/ProbeInterface/ProbeState"
        wait(lambda: request(state_url, headers=headers), "probe")
        start("collector", args.collector, collector_config)
        wait(lambda: request(collector + "/identity", headers=headers), "collector identity")
        registration = request(server + "/api/userinterface/collectors", json.dumps({"url": collector}).encode(), headers)
        assert json.loads(registration)["id"] == "site-a"
        request(server + "/api/userinterface/SetInfoClient",
                json.dumps({"name": "smoke", "requestInfo": probe, "collectorId": "site-a"}).encode(), headers)
        assert json.loads(request(server + "/api/collector/catalog?collectorId=site-a", headers=headers))["probes"] == [probe]
        assert json.loads(request(server + "/api/collector/catalog?collectorId=default", headers=headers))["probes"] == ([old_probe] if old_probe else [])
        stop("server")
        start("server", args.configurator, {"Urls": server, "Collector": {"Url": collector}})
        registered = wait(lambda: request(server + "/api/userinterface/collectors", headers=headers),
                          "registrations restored after configurator restart")
        assert any(item["id"] == "site-a" and item["url"] == collector for item in json.loads(registered))
        catalog = request(server + "/api/collector/catalog?collectorId=site-a", headers=headers)
        assert json.loads(catalog)["probes"] == [probe]
        batch_url = probe + "/api/ProbeInterface/CheckData"
        first = json.loads(request(batch_url, headers=headers))
        assert {i["resultId"] for i in first["items"]} == set(ids)
        try:
            request(probe + "/api/ProbeInterface/ConfirmData?batchId=" + first["batchId"], b"", headers)
        except HTTPError as error:
            assert error.code == 428
        else:
            raise AssertionError("Legacy ACK without a database commit accepted")
        wait(lambda: list(spool.rglob("*.pending.ndjson")), "durable collector buffer")
        assert legacy.exists() and json.loads(request(batch_url, headers=headers))["batchId"] == first["batchId"]
        assert "unparsed output" in next(spool.rglob("*.pending.ndjson")).read_text(encoding="utf8")
        # Crash both owners with ClickHouse offline, then recover the collector with the probe still down.
        stop("collector")
        stop("probe")
        collector_config["ClickHouse"]["Url"] = args.clickhouse
        start("collector", args.collector, collector_config)
        wait(lambda: list(spool.rglob("*.stored.ndjson")), "committed batch awaiting probe ACK")
        rows = [json.loads(line) for line in sql(f"SELECT ResultId, Console, SourceProbe, IPAddress FROM default.{table} FINAL WHERE Id='{task}' FORMAT JSONEachRow").splitlines()]
        assert {r["ResultId"] for r in rows} == set(ids) and len(rows) == 2
        assert all(r["SourceProbe"] == probe and r["Console"] == "unparsed output" and r["IPAddress"] == "127.0.0.1" for r in rows)
        assert legacy.exists()
        # A second collector restart must resume ACK without a second insert.
        stop("collector")
        start("probe", args.probe, probe_config)
        wait(lambda: request(state_url, headers=headers), "restarted probe")
        assert json.loads(request(batch_url, headers=headers))["batchId"] == first["batchId"]
        start("collector", args.collector, collector_config)
        wait(lambda: not legacy.exists() and not list(spool.rglob("*.ndjson")), "confirmed deletion on both sides")
        assert sql(f"SELECT count() FROM default.{table} WHERE Id='{task}'").strip() == "2"
        if legacy_task:
            migrated = sql(f"SELECT ResultId FROM default.{table} WHERE Id='{legacy_task}'").splitlines()
            assert sorted(migrated) == sorted(legacy_ids), migrated
            print("PASS: all 5 legacy rows from two sealed segments and current.ndjson")
        status = json.loads(request(server + "/api/userinterface/ClickHouseStatus?collectorId=site-a", headers=headers))
        assert status["pendingRows"] == 0
        # Exercise the normal task entry point and the live result producer.
        live_task = str(uuid4())
        single_ping = "-n 1" if os.name == "nt" else "-c 1"
        now = datetime.now(timezone.utc)
        job = {"start": now.isoformat(), "end": (now + timedelta(hours=1)).isoformat(),
               "id": live_task, "title": "loopback", "type": "Repeater", "mode": "WinPing",
               "endNode": "127.0.0.1", "requestInfo": probe, "circles": 1, "repeats": 1,
               "timeoutSec": 5, "parameters": {"args": single_ping}}
        request(server + "/api/userinterface/tasks", json.dumps(job).encode(), headers)
        wait(lambda: sql(f"SELECT count() FROM default.{table} FINAL WHERE Title='loopback' AND Id='{live_task}'").strip() == "1",
             "live probe measurement delivered to ClickHouse")
        print("PASS: durable replay, committed rows, ACK after restart and no duplicate INSERT")
    except BaseException:
        for log in logs:
            log.flush()
        for log in sorted(root.glob("*/service.log")):
            print(log.parent.name, log.read_text(encoding="utf8", errors="replace")[-3500:])
        raise
    finally:
        for name in list(processes):
            stop(name)
        for log in logs:
            log.close()
        sql(f"DROP TABLE IF EXISTS default.{table}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("configurator", "collector", "probe"):
        parser.add_argument("--" + name, type=lambda p: str(Path(p).resolve(strict=True)), required=True)
    parser.add_argument("--legacy", type=lambda p: str(Path(p).resolve(strict=True)))
    parser.add_argument("--clickhouse", required=True)
    args = parser.parse_args()
    if urlsplit(args.clickhouse).hostname not in ("127.0.0.1", "localhost"):
        parser.error("use a disposable loopback ClickHouse")
    with tempfile.TemporaryDirectory(prefix="twamp-delivery-") as work:
        run(args, Path(work))
