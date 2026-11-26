"""Run real services with two probes, three collectors and a disposable ClickHouse."""
import argparse
import concurrent.futures
import http.client
import json
import os
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
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


def check(label, test, seconds=80):
    deadline = time.monotonic() + seconds
    last = None
    while time.monotonic() < deadline:
        try:
            if test():
                print(label, flush=True)
                return
        except (OSError, ValueError) as ex:
            last = ex
        time.sleep(.4)
    raise AssertionError(f'{label}: {last}')


def port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]


class Relay:
    def __init__(self, target):
        self.down = False
        self.partition = False
        self.inserts = 0
        relay = self
        address = urlsplit(target)
        class Handler(BaseHTTPRequestHandler):
            def forward(self):
                if relay.down or relay.partition and self.path.startswith('/peer/presence'):
                    self.send_error(503)
                    return
                size = int(self.headers.get('Content-Length',0))
                body = self.rfile.read(size) if size else None
                if self.headers.get('Transfer-Encoding','').lower() == 'chunked':
                    parts=[]
                    while True:
                        length=int(self.rfile.readline().split(b';')[0].strip(),16)
                        if not length:
                            self.rfile.readline()
                            break
                        parts.append(self.rfile.read(length))
                        self.rfile.readline()
                    body=b''.join(parts)
                conn = http.client.HTTPConnection(address.hostname,address.port,timeout=35)
                try:
                    conn.request(self.command,self.path,body,headers={k:v for k,v in self.headers.items() if k.lower() not in ('host','connection','transfer-encoding')})
                    reply=conn.getresponse()
                    payload=reply.read()
                    if reply.status==200 and 'JSONEachRow' in self.path: relay.inserts+=1
                    self.send_response(reply.status)
                    self.send_header('Content-Type',reply.getheader('Content-Type','text/plain'))
                    self.send_header('Content-Length',str(len(payload)))
                    self.end_headers()
                    self.wfile.write(payload)
                except OSError:
                    try: self.send_error(502)
                    except OSError: pass
                finally: conn.close()
            do_GET=do_POST=forward
            def log_message(self,*args): pass
        self.server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
        self.url=f'http://127.0.0.1:{self.server.server_port}'
        threading.Thread(target=self.server.serve_forever,daemon=True).start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('probe','collector','configurator'):
        parser.add_argument('--'+name,required=True,type=lambda p: str(Path(p).resolve(strict=True)))
    parser.add_argument('--clickhouse',required=True)
    parser.add_argument('--browser',action='store_true',help='exercise the registration page using local Playwright Chromium')
    args=parser.parse_args()
    key=uuid4().hex
    probe_ids=('p1','p2')
    collectors=('a','b','c')
    who=('manager','manager2',*probe_ids,*collectors)
    urls={k:'http://127.0.0.1:'+str(port()) for k in who}
    table='ordered_'+uuid4().hex
    headers={'X-Api-Key':key,'Content-Type':'application/json'}
    def api(url,data=None,method=None):
        with urlopen(Request(url,data=None if data is None else json.dumps(data).encode(),headers=headers,method=method),timeout=15) as r:
            content=r.read()
            return json.loads(content) if content else None
    def sql(query):
        hdr={'X-ClickHouse-User':'default','X-ClickHouse-Key':os.environ.get('TWAMP_CLICKHOUSE_PASSWORD','')}
        with urlopen(Request(args.clickhouse,query.encode(),headers=hdr),timeout=10) as r:return r.read().decode().strip()
    backend=[Relay(args.clickhouse),Relay(args.clickhouse)]
    peers={k:Relay(urls[k]) for k in collectors}
    procs,logs={},{}
    with tempfile.TemporaryDirectory() as tmp:
        root=Path(tmp)
        def start(k):
            exe=args.configurator if k.startswith('manager') else args.probe if k in probe_ids else args.collector
            log=logs[k]=open(root/k/'run.log','ab')
            env={**os.environ,'NO_PROXY':'*','no_proxy':'*','DOTNET_PROCESSOR_COUNT':'2'}
            procs[k]=subprocess.Popen([exe],cwd=root/k,stdout=log,stderr=log,env=env)
        def stop(k):
            if k in procs:
                p=procs.pop(k);p.kill();p.wait(timeout=10);logs.pop(k).close()
        def owner(k,p):
            return api(urls[k]+'/snapshot')['states'].get(urls[p],{}).get('owner')==k
        def submit(p):
            now=datetime.now(timezone.utc)
            task=dict(id=str(uuid4()),start=(now-timedelta(minutes=1)).isoformat(),end=(now+timedelta(hours=1)).isoformat(),
                      title=p,type='Scheduler',mode='WinPing',cronExpression='*/3 * * * * *',cronWithSeconds=True,
                      repeats=1,circles=1,timeoutSec=2,endNode='127.0.0.1',requestInfo=urls[p],parameters={'args':'-c 1' if os.name!='nt' else '-n 1'})
            api(urls['manager']+'/api/userinterface/tasks',task)
            return task['id']
        def rows(p):
            return int(sql(f"SELECT count() FROM default.{table} FINAL WHERE Title='{p}'"))
        try:
            for k in who:
                d=root/k; d.mkdir()
                cfg={'Urls':urls[k],'Auth':{'ApiKey':key}}
                if k.startswith('manager'):
                    cfg.update({'Database':{'Path':str(d/'catalog.db')}})
                    if args.browser:cfg['StaticPathApp']=str(Path(args.configurator).parent/'wwwroot')
                elif k in collectors:
                    cfg.update({'Collector':{'Id':k},
                                'ClickHouse':{'Urls':[b.url for b in backend],'Database':'default','Table':table,
                                              'Password':os.environ.get('TWAMP_CLICKHOUSE_PASSWORD',''),'TimeoutSec':5,'FailoverRetrySeconds':2,
                                              'RetrySeconds':1,'SpoolPath':str(d/'spool')}})
                else:cfg['Probe']={'MaxParallel':4,'PortRange':'','ResultBatchSize':10}
                (d/'appsettings.json').write_text(json.dumps(cfg),encoding='utf8')
                if k != 'manager2': start(k)
            manager=urls['manager']+'/api/userinterface/'
            check('Manager and native services started',lambda:api(manager+'collectors') and all(api(urls[k]+'/identity') for k in collectors))
            for k in collectors:api(manager+'collectors',{'url':peers[k].url})
            tasks={}
            revisions={}
            for p,ids in [('p1',['a','b','c']),('p2',['b','a','c'])]:
                api(manager+'setinfoclient',{'requestInfo':urls[p],'collectorId':ids[0],'name':p})
                if p=='p1' and args.browser:
                    from playwright.sync_api import sync_playwright, expect
                    with sync_playwright() as driver:
                        browser=driver.chromium.launch(args=['--no-sandbox'])
                        page=browser.new_page()
                        page.add_init_script("localStorage.setItem('apiKey',"+json.dumps(key)+")")
                        page.goto(urls['manager']+'/index.html')
                        row=page.locator('tr[data-probe="'+urls[p]+'"]')
                        for label,value in zip(('Основной съёмник','Резерв 1','Резерв 2'),ids):
                            row.get_by_label(label).select_option(value)
                        row.get_by_text('Сохранить съёмники').click()
                        expect(page.locator('#msgStatus')).to_contain_text('Назначение сохранено')
                        browser.close()
                    r=next(record for record in api(manager+'collector-routes') if record['probe']==urls[p])
                    print('Linux Chromium: saved ordered collectors using the three columns',flush=True)
                else:
                    r=api(manager+'collector-routes',{'probe':urls[p],'members':ids,'revision':0})
                assert r['applied'] and len(r['members'])==3
                revisions[p]=r['revision']
                tasks[p]=submit(p)
            check('Primary A owns P1, B owns P2 and both deliver scheduled measurements',
                  lambda:owner('a','p1') and owner('b','p2') and all(rows(p)>0 for p in probe_ids))
            # Preparing a revision must preserve the active assignment while one recipient is down.
            stop('c')
            try:api(manager+'collector-routes',{'probe':urls['p1'],'revision':revisions['p1'],'members':['a','c','b']})
            except HTTPError as err:assert err.code==502
            else:raise AssertionError('Incomplete route reported as applied')
            previous=rows('p1')
            check('Incomplete configuration leaves the previous owner collecting',lambda:owner('a','p1') and rows('p1')>previous)
            start('c')
            check('C restarted',lambda:api(urls['c']+'/identity')['id']=='c')
            pending=next(record for record in api(manager+'collector-routes') if record['probe']==urls['p1'])
            r=api(manager+'collector-routes',{'probe':urls['p1'],'revision':pending['revision'],'members':['a','b','c']})
            assert r['applied'] and r['revision']>pending['revision']
            revisions['p1']=r['revision']
            check('All revisions delivered',lambda:api(urls['p1']+'/api/probeinterface/collectorlease')['revision']==revisions['p1'] and owner('a','p1'))
            stop('manager')
            print('Configurator stopped',flush=True)
            for relay in peers.values():relay.partition=True
            time.sleep(34)
            assert owner('a','p1') and owner('b','p2')
            assert not any(owner('c',p) for p in probe_ids)
            for relay in peers.values():relay.partition=False
            print('Isolated peers cannot displace live owners; probe rejects overlapping tokens',flush=True)
            stop('a')
            check('B takes P1 and continues P2 without manager',lambda:all(owner('b',p) for p in probe_ids))
            stop('b')
            check('C takes both probes with both higher priorities offline',lambda:all(owner('c',p) for p in probe_ids))
            before=[rows(p) for p in probe_ids]
            backend[0].down=True
            check('Data delivered through ClickHouse endpoint 2',lambda:backend[1].inserts and all(rows(p)>n for p,n in zip(probe_ids,before)))
            count=backend[0].inserts
            backend[0].down=False
            check('Automatic return to ClickHouse endpoint 1',lambda:backend[0].inserts>count)
            stop('c')
            for p in probe_ids:stop(p);start(p)
            start('c')
            check('Cold restart of probes and last collector while manager stays down',lambda:all(owner('c',p) for p in probe_ids))
            before=[rows(p) for p in probe_ids]
            check('Persisted schedules deliver after cold restart',lambda:all(rows(p)>n for p,n in zip(probe_ids,before)))
            assert sql(f"SELECT abs(dateDiff('second',max(Started),now()))<90 FROM default.{table}")=='1'
            start('b');start('a')
            check('Return to original priorities A/P1 and B/P2',lambda:owner('a','p1') and owner('b','p2') and not any(owner('c',p) for p in probe_ids))
            for p in probe_ids:
                try:api(urls[p]+'/api/probeinterface/checkdata')
                except HTTPError as e:assert e.code==409
                else:raise AssertionError('unfenced read accepted')
            start('manager2')
            other=urls['manager2']+'/api/userinterface/'
            check('Fresh configurator starts with its own empty database',lambda:api(other+'collector-routes')==[])
            report=api(other+'collector-routes/import',{'url':peers['c'].url})
            assert report['probes']==2 and report['collectors']==3 and not report['unavailable'],report
            routes=api(other+'collector-routes')
            assert {r['probe']:r['revision'] for r in routes}=={urls[p]:revisions[p] for p in probe_ids}
            assert {t['id'] for t in api(other+'tasks')}==set(tasks.values())
            print('Second configurator recovered both probes, their schedules and all three priorities from one collector',flush=True)
            task=next(t for t in api(other+'tasks') if t['id']==tasks['p2'])
            changed={**task,'title':'edited on second configurator'}
            api(other+'tasks',changed)
            updated=api(other+'collector-routes',{'probe':urls['p1'],'revision':revisions['p1'],'members':['b','a','c']})
            assert updated['applied']
            stop('manager2')
            start('manager')
            check('Original configurator restarted',lambda:api(manager+'collectors'))
            try:api(manager+'tasks',{**task,'title':'stale edit'})
            except HTTPError as err:assert err.code==409
            else:raise AssertionError('Stale schedule replaced newer task')
            try:api(manager+'tasks/'+task['id'],method='DELETE')
            except HTTPError as err:assert err.code==409
            else:raise AssertionError('Stale deletion reported success')
            assert api(urls['p2']+'/api/probeinterface/tasks')[0]['title']==changed['title']
            try:api(manager+'collector-routes',{'probe':urls['p1'],'revision':revisions['p1'],'members':['a','c','b']})
            except HTTPError as err:assert err.code==409
            else:raise AssertionError('Outdated configurator replaced newer route')
            check('B collects both after stale configuration was rejected',lambda:all(owner('b',p) for p in probe_ids))
            refreshed=api(manager+'collector-routes/import',{'url':peers['b'].url})
            assert refreshed['probes']==2 and not refreshed['unavailable']
            assert next(t for t in api(manager+'tasks') if t['id']==task['id'])['title']==changed['title']
            api(manager+'clients?requestInfo='+urls['p1'],method='DELETE')
            stop('manager')
            assert api(urls['p1']+'/api/probeinterface/tasks')==[]
            stop('p1');start('p1')
            check('Removed probe remains unscheduled after restart with both configurators off',
                  lambda:api(urls['p1']+'/api/probeinterface/tasks')==[])
            print('Ordered failover passed',flush=True)
        except BaseException:
            for k in who:
                if (root/k/'run.log').exists():print(k,(root/k/'run.log').read_text(encoding='utf8',errors='replace')[-2400:],flush=True)
            raise
        finally:
            for k in list(procs):stop(k)
            for relay in [*backend,*peers.values()]:relay.close()
            sql(f'DROP TABLE IF EXISTS default.{table}')


if __name__=='__main__':main()
