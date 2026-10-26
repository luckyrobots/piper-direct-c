#!/usr/bin/env python3
"""Capture a real PiPer observation episode without transmitting robot commands.

Robot CAN is read by the native piper-record executable. Python packages files,
reads the existing dashboard camera, and exports the measurements to Parquet.
This is not a LuckyEngine simulation episode or a controller replay program.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parent.parent


def export_measurements(source, destination):
    import pyarrow as pa
    import pyarrow.parquet as pq

    with source.open() as stream:
        records = [json.loads(line) for line in stream]
    if not records or records[0].get('type') != 'header' or records[-1].get('type') != 'footer':
        raise ValueError('Recording has no finalized header/footer; preserve it as incomplete.')
    header, footer = records[0], records[-1]
    rows = records[1:-1]
    if not footer['capture_complete'] or footer['samples'] != len(rows) or not rows:
        raise ValueError(f"Incomplete recording: {footer.get('reason')}")
    previous = -1.0
    for i, row in enumerate(rows):
        if row.get('type') != 'sample' or row['frame_index'] != i or row['timestamp'] <= previous:
            raise ValueError('Non-contiguous frame indices or non-monotonic timestamps.')
        previous = row['timestamp']
    inferred = pa.Table.from_pylist(rows).schema
    arrays = {'joint_degrees', 'joint_radians', 'joint_sample_age_s', 'motor_current_a', 'motor_velocity_rad_s'}
    scalars = {'timestamp', 'host_monotonic_s', 'gripper_feedback_mm', 'gripper_effort', 'gripper_sample_age_s'}
    schema = pa.schema([(field.name, pa.list_(pa.field('element', pa.float64())) if field.name in arrays
                         else pa.float64() if field.name in scalars else field.type) for field in inferred])
    table = pa.Table.from_pylist(rows, schema=schema)
    metadata = dict(table.schema.metadata or {})
    metadata[b'piper.provenance'] = json.dumps(header).encode()
    metadata[b'piper.actions'] = b'Absent: observations only. Joint readings are not commanded targets.'
    table = table.replace_schema_metadata(metadata)
    pq.write_table(table, destination, compression='zstd')
    check = pq.read_table(destination)
    if not table.equals(check, check_metadata=True):
        raise ValueError('Parquet round-trip verification failed.')
    span = [max(r['joint_degrees'][i] for r in rows) - min(r['joint_degrees'][i] for r in rows) for i in range(6)]
    gripper = [r['gripper_feedback_mm'] for r in rows if r['gripper_fresh']]
    return {'header': header, 'footer': footer, 'rows': len(rows),
            'duration_s': rows[-1]['timestamp'] - rows[0]['timestamp'],
            'joint_excursion_deg': span,
            'motion_observed': max(span) > .1,
            'gripper_feedback_range_mm': [min(gripper), max(gripper)] if gripper else None,
            'gripper_within_standard_command_range': bool(gripper) and min(gripper) >= 0 and max(gripper) <= 70}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--device', required=True, help='Stable adapter identity, e.g. usb:SERIAL')
    p.add_argument('--out', required=True, type=Path, help='New episode directory; existing directories are rejected')
    p.add_argument('--seconds', type=float, default=30)
    p.add_argument('--simulate', action='store_true', help='Test recorder only; never accesses hardware or camera')
    p.add_argument('--camera', action='store_true', help='Record timestamped frames from the already-running Logitech camera')
    args = p.parse_args()
    if not .2 <= args.seconds <= 600 or (args.simulate and args.camera):
        p.error('Duration must be 0.2–600 seconds; simulation cannot capture the real camera.')
    if (args.simulate and args.device != 'simulation:one') or (not args.simulate and not args.device.startswith('usb:')):
        p.error('Choose a stable USB identity, or simulation:one with --simulate.')
    args.out.mkdir(parents=True, exist_ok=False)
    raw = args.out / 'measurements.jsonl'
    parquet = args.out / 'measurements.parquet'
    manifest = {'schema_version': 1, 'kind': 'real_robot_observations' if not args.simulate else 'recorder_test_fixture',
                'status': 'recording', 'device_id': args.device, 'robot_commands_sent': False,
                'task_success_verified': False, 'cup_pickup_verified': False,
                'lucky_engine_episode': False, 'sim_real_comparison_available': False,
                'camera_requested': args.camera, 'camera_frames': 0, 'camera_errors': 0,
                'camera_timing': 'Host request interval, not synchronized camera exposure time.',
                'limitations': ['No issued action targets in this passive capture.',
                                'Joint signs, zero offsets and gripper aperture mapping require validation against the Lucky model.',
                                'A completed capture is not evidence of a successful cup pickup.']}
    manifest_path = args.out / 'manifest.json'
    def save():
        temporary = args.out / 'manifest.json.tmp'
        with temporary.open('w') as stream:
            stream.write(json.dumps(manifest, indent=2) + '\n')
            stream.flush()
            os.fsync(stream.fileno())
        temporary.replace(manifest_path)
        directory = os.open(args.out, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    save()
    proc = None
    try:
        if not args.simulate:
            with urlopen('http://127.0.0.1:8765/api/state', timeout=2) as response:
                state = json.load(response)
            device = next(d for d in state['devices'] if d['id'] == args.device)
            if not device['fresh']:
                raise ValueError('Selected arm has no fresh telemetry.')
            (args.out / 'before-state.json').write_text(json.dumps({'device': device, 'motion': state['motion'], 'camera': state['camera']}, indent=2) + '\n')
        command = [str(ROOT / 'build/piper-record'), '--device', args.device, '--out', str(raw), '--seconds', str(args.seconds)]
        if args.simulate:
            command.append('--simulate')
        with (args.out / 'recorder.log').open('x') as log, (args.out / 'camera-timestamps.jsonl').open('x') as frames:
            proc = subprocess.Popen(command, stdout=log, stderr=log)
            next_camera = 0.0
            while proc.poll() is None:
                if args.camera and time.monotonic() >= next_camera:
                    requested = time.monotonic()
                    try:
                        with urlopen('http://127.0.0.1:8765/api/camera/frame', timeout=1) as response:
                            frame = response.read(5_000_001)
                        if len(frame) > 5_000_000 or not frame.startswith(b'\xff\xd8') or not frame.endswith(b'\xff\xd9'):
                            raise ValueError('Invalid camera JPEG')
                        name = f"camera-{manifest['camera_frames']:06d}.jpg"
                        (args.out / name).write_bytes(frame)
                        frames.write(json.dumps({'file': name, 'request_monotonic_s': requested, 'received_monotonic_s': time.monotonic()}) + '\n')
                        manifest['camera_frames'] += 1
                    except Exception as error:
                        manifest['camera_errors'] += 1
                        frames.write(json.dumps({'request_monotonic_s': requested, 'error': str(error)}) + '\n')
                    next_camera = time.monotonic() + .2
                time.sleep(.02)
            if proc.returncode:
                raise RuntimeError(f'Recorder exited {proc.returncode}; see recorder.log. No successful episode exported.')
        manifest['measurement_audit'] = export_measurements(raw, parquet)
        manifest['files'] = {path.name: {'sha256': hashlib.sha256(path.read_bytes()).hexdigest(), 'bytes': path.stat().st_size} for path in (raw, parquet)}
        # A final manifest must never precede durable measurements or images.
        # Camera errors remain explicit gaps in camera-timestamps.jsonl.
        for path in args.out.iterdir():
            if path.is_file() and path != manifest_path:
                with path.open('rb') as stream:
                    os.fsync(stream.fileno())
        manifest['status'] = 'capture_complete'
        save()
        print(json.dumps({'episode': str(args.out.resolve()), 'rows': manifest['measurement_audit']['rows'], 'motion_observed': manifest['measurement_audit']['motion_observed'], 'cup_pickup_verified': False}))
    except BaseException as error:
        if proc is not None and proc.poll() is None:
            proc.terminate()
            proc.wait(timeout=5)
        manifest.update(status='incomplete', error=str(error))
        save()
        raise


if __name__ == '__main__':
    main()
