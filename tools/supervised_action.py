#!/usr/bin/env python3
"""Run one reviewed action through the guarded C API and save its status trace.
No SDK or direct CAN writes. Defaults to preview; --execute needs an attended arm.
"""
import argparse,json,threading,time,uuid
from pathlib import Path
from urllib.request import Request,urlopen


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--request',required=True,type=Path)
    p.add_argument('--out',required=True,type=Path)
    p.add_argument('--device',default='usb:004000485246570520323934')
    p.add_argument('--port',type=int,default=8765)
    p.add_argument('--execute',action='store_true')
    p.add_argument('--attended',action='store_true')
    p.add_argument('--supported',action='store_true')
    p.add_argument('--simulate',action='store_true')
    args=p.parse_args();command=json.loads(args.request.read_text())
    if not args.execute: print(json.dumps(command,indent=2));return
    if not args.attended:p.error('An attended arm and clear path are required.')
    if command['action'] in ('reset','enable','disable') and not args.supported:p.error('Motor power changes require physical support.')
    base=f'http://127.0.0.1:{args.port}'
    def state():
        with urlopen(base+'/api/state',timeout=.6) as r:return json.load(r)
    s=state();d=next(d for d in s['devices'] if d['id']==args.device)
    if s['simulated']!=args.simulate or s['selected']!=args.device or s['motion']['active'] or not d['fresh']:raise RuntimeError('Wrong mode/selection, busy controller or stale feedback.')
    if not args.simulate and not s['camera']['fresh']:raise RuntimeError('Fresh bench camera required.')
    token=s['token'];generation=d['generation'];owner='supervised-'+str(uuid.uuid4())
    def post(route,fields):
        payload={'device_id':args.device,'generation':generation,'owner':owner,**fields}
        req=Request(base+route,data=json.dumps(payload).encode(),headers={'Content-Type':'application/json','X-Piper-Token':token,'Origin':base})
        with urlopen(req,timeout=.6) as r:return json.load(r)
    deadline=time.monotonic()+5
    while s['query_active'] and time.monotonic()<deadline:time.sleep(.1);s=state()
    if s['query_active']:raise RuntimeError('Information query has not completed.')
    args.out.mkdir(parents=True,exist_ok=False)
    (args.out/'request.json').write_text(json.dumps({'request':command,'device':args.device,'generation':generation,'simulated':args.simulate,'attended':args.attended,'supported':args.supported},indent=2)+'\n')
    lease=None;done=threading.Event();thread=None;completed=False
    def heartbeat():
        while not done.wait(.15):
            try:post('/api/heartbeat',{'request_id':lease})
            except Exception:pass # Main loop verifies state; C controller expires missing heartbeats.
    try:
        r=post('/api/command',{'standalone':True,'clear':args.attended,'supported':args.supported,**command})
        lease=r.get('request_id');print(json.dumps(r),flush=True)
        if lease is None:raise RuntimeError('Expected an asynchronous control lease.')
        thread=threading.Thread(target=heartbeat,daemon=True);thread.start()
        deadline=time.monotonic()+125
        with (args.out/'states.jsonl').open('x') as log:
            while time.monotonic()<deadline:
                t=time.monotonic();s=state();d=next(d for d in s['devices'] if d['id']==args.device)
                log.write(json.dumps({'host_monotonic_s':t,'received_monotonic_s':time.monotonic(),'device':d,'motion':s['motion'],'phase':s['phase'],'message':s['message'],'camera':s['camera']})+'\n');log.flush()
                if s['token']!=token or d['generation']!=generation or s['selected']!=args.device:raise RuntimeError('Controller identity or connection changed.')
                if not d['fresh'] or (not args.simulate and not s['camera']['fresh']):raise RuntimeError('Robot feedback or camera lost.')
                if not s['motion']['active']:
                    if s['phase']!='complete':raise RuntimeError(s['message'])
                    completed=True;break
                if s['motion']['owner']!=owner or s['motion']['request_id']!=lease:raise RuntimeError('Control lease changed.')
                time.sleep(max(0,.05-(time.monotonic()-t)))
        if not completed:raise RuntimeError('Action exceeded supervised deadline.')
        (args.out/'completion.json').write_text(json.dumps({'complete':True,'message':s['message'],'device':d},indent=2)+'\n')
        print(json.dumps({'complete':True,'message':s['message'],'degrees':[j['actual_deg'] for j in d['joints']],'gripper':d['gripper']}),flush=True)
    finally:
        if lease is not None and not completed:
            try:post('/api/stop',{'request_id':lease})
            except Exception:pass
        done.set()
        if thread:thread.join(1)

if __name__=='__main__':main()
