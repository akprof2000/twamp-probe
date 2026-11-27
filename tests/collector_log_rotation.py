"""Verify the published Linux collector, including an upgrade of the 1.29.1 log settings."""
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
from urllib.request import urlopen
from zipfile import ZIP_DEFLATED, ZipFile


def archived_text(path):
    with ZipFile(path) as archive:
        entry, = archive.infolist()
        assert entry.compress_type == ZIP_DEFLATED and archive.testzip() is None, path
        return archive.read(entry).decode('utf-8-sig')


def check(published):
    with tempfile.TemporaryDirectory() as tmp:
        app = Path(tmp) / 'collector'
        shutil.copytree(published, app)
        exe = app / 'SPI.Twamp.Collector'
        settings = app / 'appsettings.json'
        defaults = json.loads(settings.read_text(encoding='utf-8-sig'))
        examples = Path(__file__).resolve().parents[1] / 'docs/examples/one-probe-two-collectors'
        for collector in ('collector-a', 'collector-b'):
            sample = examples / collector / 'appsettings.json'
            assert json.loads(sample.read_text(encoding='utf-8'))['NLog'] == defaults['NLog'], sample
        assert defaults['NLog']['targets']['file']['archiveAboveSize'] == 10 * 1024 * 1024
        # Keep the published reference and run the same config migration as install.sh.
        reference = app / 'reference.json'
        settings.rename(reference)
        previous = defaults['NLog']
        previous.pop('extensions')
        for key in ('archiveFileName', 'enableArchiveFileCompression', 'archiveNumbering'):
            del previous['targets']['file'][key]
        defaults['Collector']['Id'] = 'rotation-check'
        settings.write_text(json.dumps(defaults))
        subprocess.run([exe, '--merge-config', settings, reference], cwd=app, check=True)
        merged = json.loads(settings.read_text(encoding='utf-8-sig'))
        assert merged['Collector']['Id'] == 'rotation-check'
        assert merged['NLog'] == json.loads(reference.read_text(encoding='utf-8-sig'))['NLog']

        logs = app / 'log'
        logs.mkdir()
        active = logs / 'Collector.log'
        preexisting = 'before upgrade\n' * 800
        active.write_text(preexisting)
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            base = f'http://127.0.0.1:{sock.getsockname()[1]}'
        env = {**os.environ, 'Urls': base, 'ClickHouse__Url': 'http://127.0.0.1:1',
               'NLog__targets__file__archiveAboveSize': '8192',
               'Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics': 'Information'}
        with (app / 'stdout').open('w') as stream:
            p = subprocess.Popen([exe], cwd=app, env=env, stdout=stream, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 20
                while True:
                    try:
                        with urlopen(base + '/identity', timeout=1) as r:
                            assert json.load(r)['id'] == 'rotation-check'
                        break
                    except OSError:
                        if p.poll() is not None or time.monotonic() >= deadline:
                            raise RuntimeError((app / 'stdout').read_text()[-2000:])
                        time.sleep(.1)
                assert archived_text(logs / 'Collector.0.zip') == preexisting
                for i in range(48):
                    with urlopen(base + f'/identity?rotation={i:02d}&padding=' + 'a'*1200, timeout=3) as r:
                        r.read()
            finally:
                if p.poll() is None:
                    p.terminate()
                p.wait(timeout=10)
        assert p.returncode == 0, p.returncode
        archives = [logs / f'Collector.{n}.zip' for n in range(5)]
        assert set(logs.iterdir()) == {active, *archives}, list(logs.iterdir())
        entries = [archived_text(path) for path in archives]
        current = active.read_text(encoding='utf-8-sig')
        assert 'Application is shutting down' in current
        retained = '\n'.join([current, *entries])
        assert 'rotation=47' in retained and 'rotation=00' not in retained
        print(f'{published.name}: active UTF-8 log, five readable deflated ZIP archives, retention and upgrade OK')


if __name__ == '__main__':
    check(Path(sys.argv[1]).resolve(strict=True))
