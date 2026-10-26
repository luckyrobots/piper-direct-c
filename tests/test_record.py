#!/usr/bin/env python3
"""Recorder regression: passive fixtures, integrity, interruption, no overwrite."""
import json
from pathlib import Path
import signal
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parent.parent
with tempfile.TemporaryDirectory() as temporary:
    root = Path(temporary)
    episode = root / 'episode'
    command = ['python3', str(ROOT / 'tools/capture_episode.py'), '--simulate', '--device', 'simulation:one', '--seconds', '.3', '--out', str(episode)]
    result = subprocess.run(command, capture_output=True, text=True)
    assert result.returncode == 0, result.stderr
    manifest = json.loads((episode / 'manifest.json').read_text())
    assert manifest['status'] == 'capture_complete'
    assert manifest['measurement_audit']['rows'] >= 5
    assert not manifest['robot_commands_sent'] and not manifest['cup_pickup_verified']
    assert not manifest['lucky_engine_episode'] and not manifest['measurement_audit']['motion_observed']
    before = (episode / 'measurements.jsonl').read_bytes()
    assert subprocess.run(command, capture_output=True).returncode != 0
    assert (episode / 'measurements.jsonl').read_bytes() == before
    raw = root / 'interrupted.jsonl'
    proc = subprocess.Popen([str(ROOT / 'build/piper-record'), '--simulate', '--device', 'simulation:one', '--seconds', '4', '--out', str(raw)], stderr=subprocess.DEVNULL)
    time.sleep(.2)
    proc.send_signal(signal.SIGINT)
    assert proc.wait(timeout=3) == 2
    footer = json.loads(raw.read_text().splitlines()[-1])
    assert footer['reason'] == 'interrupted' and not footer['capture_complete']
    bad = root / 'bad.jsonl'
    assert subprocess.run([str(ROOT / 'build/piper-record'), '--simulate', '--device', 'simulation:one', '--seconds', 'nan', '--out', str(bad)], capture_output=True).returncode == 1
    assert not bad.exists()
print('Passive recorder passed: Parquet round trip, explicit observation provenance, no overwrite, interruption, invalid duration.')
