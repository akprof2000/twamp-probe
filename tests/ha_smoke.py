"""Run an isolated 3-replica ClickHouse/Keeper cluster, configurator and two collectors."""
import argparse
import concurrent.futures
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen
from uuid import uuid4


def call(url, body=None, timeout=15, headers=None):
    payload = None if body is None else json.dumps(body).encode()
    with urlopen(Request(url, data=payload, headers={'Content-Type': 'application/json', **(headers or {})}), timeout=timeout) as response:
        data = response.read()
        return json.loads(data) if data else None


def wait(label, check, seconds=120):
    end = time.monotonic() + seconds
    last = None
    while time.monotonic() < end:
        try:
            value = check()
            if value:
                print(label, flush=True)
                return value
        except (OSError, URLError, ValueError) as ex:
            last = ex
        time.sleep(0.5)
    raise AssertionError(f'Timed out: {label} ({last})')


def docker(*argv):
    return subprocess.check_output(['docker', *argv], text=True, encoding='utf8').strip()


def free_port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]

def check_registry_ui(server, endpoints, probe, executable):
    from playwright.sync_api import sync_playwright, expect
    with sync_playwright() as driver:
        browser = driver.chromium.launch(executable_path=str(executable))
        page = browser.new_page()
        page.on('dialog', lambda dialog: dialog.accept())
        page.goto(server + '/index.html')
        page.locator('[data-tab="ha"]').click()
        for key in ('a', 'b'):
            page.locator('#ha-register input').fill(endpoints[key])
            page.locator('#ha-register button').click()
            expect(page.locator('#ha-nodes tr').last).to_contain_text('srv-' + key)
        page.locator('#ha-add-probe [name=name]').fill('UI smoke')
        page.locator('#ha-add-probe [name=url]').fill(probe)
        page.locator('#ha-add-probe button').click()
        expect(page.locator('#ha-probes tr')).to_have_count(1)
        page.locator('#ha-preview').click()
        expect(page.locator('#ha-plan')).to_contain_text('UI smoke')
        expect(page.locator('#ha-apply')).to_be_enabled()
        page.locator('#ha-nodes tr').first.get_by_text('Удалить').click()
        page.locator('#ha-replacement button').first.click()
        expect(page.locator('#ha-notice')).to_contain_text('нужен другой съёмник')
        page.locator('#ha-probes tr').get_by_text('Удалить', exact=True).click()
        expect(page.locator('#ha-apply')).to_be_disabled()
        page.locator('#ha-nodes tr').first.get_by_text('Удалить').click()
        page.locator('#ha-replacement button').first.click()
        expect(page.locator('#ha-nodes tr')).to_have_count(1)
        page.locator('#ha-reload').click()
        expect(page.locator('#ha-nodes tr')).to_have_count(0)
        browser.close()
    print('Browser: registration, pairs, preview, deletion and draft reset', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--configurator', type=Path, required=True)
    parser.add_argument('--collector', type=Path, required=True)
    parser.add_argument('--browser', type=Path, help='locally installed Chromium/Edge; requires Python Playwright')
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    prefix = 'twamp-ha-' + uuid4().hex[:10]
    containers = [f'{prefix}-{i}' for i in range(1,4)]
    http_ports, keeper_ports = {}, {}
    procs, logs, configs, apps = {}, {}, {}, {}
    password = uuid4().hex
    keeper_digest = 'twamp:' + uuid4().hex
    class Probe(BaseHTTPRequestHandler):
        def log_message(self, *_): pass
        def do_GET(self):
            if self.path.lower().endswith('checkdata'):
                with lock:
                    batch = queue[0] if queue else {'batchId':str(uuid4()), 'items':[]}
                if not batch['items']: time.sleep(0.25)
                self.reply(batch)
            else: self.reply([])
        def do_POST(self):
            self.rfile.read(int(self.headers.get('Content-Length',0)))
            if 'confirmdata?' in self.path.lower():
                if self.headers.get('X-Twamp-Quorum')!='2':
                    self.send_error(428);return
                with lock:
                    matched = bool(queue and queue[0]['batchId'] == self.path.split('=')[-1])
                    if matched and permit_ack.is_set():
                        acks.append(queue.pop(0)['batchId'])
                    elif matched:
                        self.send_error(503);return
                self.reply(matched)
            elif self.path.partition('?')[0].lower().endswith('checkin'): self.reply({'requestInfo':probe,'version':'ha-smoke'})
            else: self.reply(True)
        def reply(self,data):
            body=json.dumps(data).encode()
            self.send_response(200);self.send_header('Content-Type','application/json')
            self.send_header('Content-Length',str(len(body)));self.end_headers()
            try: self.wfile.write(body)
            except OSError: pass
    lock, queue, acks, permit_ack = threading.Lock(), [], [], threading.Event()
    permit_ack.set()
    probe_server=ThreadingHTTPServer(('127.0.0.1',0),Probe)
    probe=f'http://127.0.0.1:{probe_server.server_port}'
    threading.Thread(target=probe_server.serve_forever,daemon=True).start()
    def enqueue():
        batch_id, result = str(uuid4()),str(uuid4())
        item={'resultId': result,'taskId':str(uuid4()),'taskTitle':'test','creation':'2026-11-24T02:00:00Z','console':'test output'}
        with lock: queue.append({'batchId':batch_id,'items':[item]})
        return batch_id,result
    def create_node(i):
        name=containers[i]
        docker('run','-d','--network',prefix,'--network-alias',f'ch{i+1}','--name',name,
               '--cpus','2','-p',f'127.0.0.1:{http_ports[i]}:8123','-p',f'127.0.0.1:{keeper_ports[i]}:9181',
               '-v',f'{repo / "deploy/ha/clickhouse.xml"}:/etc/clickhouse-server/config.d/twamp.xml:ro',
               '-e',f'CLICKHOUSE_PASSWORD={password}','-e',f'CH_NODE_ID={i+1}','-e',f'CH_HOST=ch{i+1}',
               '-e',f'CH_REPLICA=replica{i+1}','-e','CH_HOST_1=ch1','-e','CH_HOST_2=ch2','-e','CH_HOST_3=ch3',
               'clickhouse/clickhouse-server:25.8-alpine')
    def sql(i,q):
        req=Request(f'http://127.0.0.1:{http_ports[i]}/',q.encode(),headers={'X-ClickHouse-User':'default','X-ClickHouse-Key':password})
        with urlopen(req,timeout=8) as res:return res.read().decode().strip()
    def start(key):
        where,exe=apps[key]; log=logs[key]=open(where/'run.log','ab')
        procs[key]=subprocess.Popen([str(exe)],cwd=where,stdout=log,stderr=subprocess.STDOUT,
                                   env={**os.environ,'DOTNET_PROCESSOR_COUNT':'2','NO_PROXY':'localhost,127.0.0.1','no_proxy':'localhost,127.0.0.1'})
    def stop(key):
        if key in procs:
            p=procs.pop(key);p.kill();p.wait(timeout=10);logs.pop(key).close()
    def cleanup():
        for key in list(procs):stop(key)
        for name in containers:subprocess.run(['docker','rm','-fv',name],capture_output=True)
        subprocess.run(['docker','network','rm',prefix],capture_output=True)
        probe_server.shutdown();probe_server.server_close()
    docker('network','create',prefix)
    with tempfile.TemporaryDirectory(prefix='twamp-ha-') as tmp:
        try:
            reserved=set()
            for ports in [http_ports,keeper_ports]:
                for i in range(3):
                    port=free_port()
                    while port in reserved:port=free_port()
                    ports[i]=port;reserved.add(port)
            with concurrent.futures.ThreadPoolExecutor(3) as pool:list(pool.map(create_node,range(3)))
            for i in range(3):wait('ClickHouse '+str(i+1),lambda i=i:sql(i,'SELECT 1')=='1')
            ha={'Enabled':True,'Keeper':','.join(f'127.0.0.1:{keeper_ports[i]}' for i in range(3)),
                'Root':'/twamp-smoke','Digest':keeper_digest,'SessionTimeoutMs':6000,'StandbyDelaySeconds':2,'Target':'twamp.results_ha'}
            endpoints={}
            for key in ['server','a','b']:
                where=Path(tmp)/key;where.mkdir()
                exe=(args.configurator if key=='server' else args.collector).resolve()
                apps[key]=(where,exe)
                endpoints[key]=f'http://127.0.0.1:{free_port()}'
                cfg={'Urls':endpoints[key],'HighAvailability':ha}
                if key=='server':cfg.update({'StaticPathApp':str(repo/'SPI.Twamp.Server/wwwroot'),'Database':{'Path':str(where/'catalog.db')}})
                else:
                    cfg.update({'Collector':{'Id':key,'Machine':'srv-'+key},'Configurator':{'Url':endpoints['server']},
                                'ClickHouse':{'Urls':[f'http://127.0.0.1:{http_ports[i]}' for i in range(3)],'Database':'twamp','Table':'results_ha',
                                              'Password':password,'TimeoutSec':6,'RetrySeconds':1,'SpoolPath':str(where/'spool')}})
                configs[key]=cfg
                (where/'appsettings.json').write_text(json.dumps(cfg),encoding='utf8');start(key)
            server=endpoints['server']
            wait('Empty authoritative registry',lambda:call(server+'/api/ha')['version']==0)
            for key in ['a','b']:wait('Collector '+key,lambda:call(endpoints[key]+'/identity')['ha'])
            if args.browser:
                check_registry_ui(server, endpoints, probe, args.browser)
            nodes=[{'id':k,'machine':'srv-'+k,'url':endpoints[k],'capacity':100,'standby':k=='b'} for k in ['a','b']]
            pair={'name':'Smoke','url':probe,'first':'a','second':'b'}
            draft={'version':0,'collectors':nodes,'probes':[pair]}
            for invalid in [{**pair,'second':'a'},{**pair,'second':'missing'}]:
                try:call(server+'/api/ha/preview',{**draft,'probes':[invalid]})
                except HTTPError as e:assert e.code==400
                else:raise AssertionError('Invalid pair accepted')
            preview=call(server+'/api/ha/preview',draft)
            assert preview['plan']['assignments'][0]['primary']=='a'
            assert not call(server+'/api/ha')['plan']['assignments']
            draft['approved']={probe:'a'}
            assert call(server+'/api/ha/apply',draft)['version']==1
            try:call(server+'/api/ha/apply',draft)
            except HTTPError as e:assert e.code==409
            else:raise AssertionError('Stale plan accepted')
            assert not call(server+'/api/collector/catalog?collectorId=a')['probes']
            assert call(server+'/api/ha/link?'+urlencode({'url':endpoints['b'],'probe':probe}))['reachable']
            owner=lambda key: call(endpoints[key]+'/snapshot')['states'].get(probe,{}).get('owner')==key
            wait('A owns probe',lambda:owner('a'))
            permit_ack.clear()
            batch,result=enqueue()
            count=lambda i,r:sql(i,f"SELECT count() FROM twamp.results_ha FINAL WHERE ResultId='{r}'")=='1'
            wait('Committed on all replicas before ACK',lambda:all(count(i,result) for i in range(3)))
            with lock:assert not acks
            stop('server');stop('a');stop('b');start('b');permit_ack.set()
            wait('Standby cold start, manager down, lost ACK recovered',lambda:batch in acks)
            assert all(count(i,result) for i in range(3))
            assert all(sql(i,f"SELECT count() FROM twamp.results_ha WHERE ResultId='{result}'")=='1' for i in range(3))
            start('a');wait('A recovered, B stays owner',lambda:owner('b') and call(endpoints['a']+'/identity')['ha'])
            # Fail one physical node (data and Keeper). The other two still accept writes.
            docker('stop','-t','1',containers[0]);batch,result=enqueue()
            wait('Loss of one cluster node permits quorum ACK',lambda:batch in acks)
            assert count(1,result) and count(2,result)
            for i in [1,2]:sql(i,'SYSTEM STOP FETCHES twamp.results_ha')
            batch,result=enqueue()
            time.sleep(5)
            with lock:assert batch not in acks
            for i in [1,2]:sql(i,'SYSTEM START FETCHES twamp.results_ha')
            wait('Keeper available, ACK waits for data replicas',lambda:batch in acks)
            docker('stop','-t','1',containers[1]);batch,result=enqueue()
            time.sleep(9)
            with lock:assert batch not in acks and queue[0]['batchId']==batch
            docker('start',containers[1])
            wait('Quorum restored, pending batch delivered',lambda:batch in acks)
            wait('Data present on surviving replicas',lambda:count(1,result) and count(2,result))
            print('HA smoke passed',flush=True)
        except Exception:
            for key,(where,_) in apps.items():
                print(key,(where/'run.log').read_text(encoding='utf8',errors='replace')[-4200:],flush=True)
            raise
        finally:cleanup()

if __name__=='__main__':
    main()
