"""Read-only, bounded telemetry sample for a manual viewport drag."""
import json
import argparse
import time
import urllib.request
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--seconds', type=int, default=6, choices=range(1, 61))
duration = parser.parse_args().seconds
rows = []
start = time.monotonic()
while time.monotonic() - start < duration:
    with urllib.request.urlopen('http://127.0.0.1:8765/api/state', timeout=1) as response:
        state = json.load(response)
    rows.append(dict(t=round(time.monotonic() - start, 3), motion=state['motion'],
                     message=state['message'], joints=[j['actual_deg'] for j in state['devices'][0]['joints']]))
    time.sleep(.025)
Path('build/j6-live-drag-check.json').write_text(json.dumps(rows))
print(json.dumps(dict(samples=len(rows), following=sum(r['motion'].get('following', False) for r in rows),
                     first=rows[0]['joints'], last=rows[-1]['joints'],
                     messages=list(dict.fromkeys(r['message'] for r in rows))), indent=2))
