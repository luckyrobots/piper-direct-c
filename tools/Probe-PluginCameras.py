"""Exercise installed preview capture without issuing hardware commands."""
import argparse, json, subprocess, threading, time, tempfile, shutil
from pathlib import Path
parser=argparse.ArgumentParser()
parser.add_argument('package'); parser.add_argument('settings'); parser.add_argument('--seconds',type=int,default=8)
args=parser.parse_args()
package=Path(args.package)
settings=tempfile.TemporaryDirectory(prefix='pipers-camera-probe-')
shutil.copy2(Path(args.settings)/'preferences.json',Path(settings.name)/'preferences.json')
p=subprocess.Popen([str(package/'LuckyRobots.Pipers.exe')],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8')
frames={}; statuses={}; first=time.monotonic()
def send(value):
    p.stdin.write(json.dumps(dict(protocol=1,**value))+'\n'); p.stdin.flush()
def read():
    for line in p.stdout:
        obj=json.loads(line)
        if obj['type']=='request':
            result={'sceneId':'probe','cameras':[]} if obj['method']=='scene.cameras' else {}
            send(dict(type='response',requestId=obj['requestId'],result=result))
        elif obj['type']=='frame':
            f=obj['frame']; row=frames.setdefault(f['imageId'],dict(count=0,first=time.monotonic(),width=f['width'],height=f['height'],ages=[],firstSequence=f['sequence']))
            row['lastSequence']=f['sequence']
            row['count']+=1; row['last']=time.monotonic(); row['ages'].append(time.time()*1000-f['timestampUnixMicroseconds']/1000)
        elif obj['type']=='patch':
            statuses.update({k:v for k,v in obj['widgets'].items() if k.endswith('.info')})
t=threading.Thread(target=read,daemon=True);t.start()
send(dict(type='start',start=dict(pluginId='luckyrobots.pipers',settingsDirectory=settings.name,packageDirectory=str(package.resolve()))))
time.sleep(args.seconds)
send(dict(type='stop')); p.wait(timeout=8);t.join(timeout=1)
for row in frames.values():
    row['fps']=round((row['count']-1)/max(.001,row['last']-row['first']),1)
    row['capturedFps']=round((row.pop('lastSequence')-row.pop('firstSequence'))/max(.001,row['last']-row['first']),1)
    row['maxHostPipeAgeMs']=round(max(row.pop('ages')),1)
    del row['first'];del row['last']
print(json.dumps(dict(frames=frames,statuses=statuses,exit=p.returncode,stderr=p.stderr.read()[-2000:]),indent=2))
settings.cleanup()
