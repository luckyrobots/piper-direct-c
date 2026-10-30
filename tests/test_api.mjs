import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, rm, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import net from 'node:net';
import http from 'node:http';

const directory = await mkdtemp(`${tmpdir()}/piper-api-`);
const port = 20000 + process.pid % 20000, base = `http://127.0.0.1:${port}`;
let server, logs = '', state, keepalive, lease;
const owner = 'automated-simulator-test';
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function refresh() { const r = await fetch(`${base}/api/state`); assert.equal(r.status, 200); state = await r.json(); return state; }
function selected() { return state.devices.find(d => d.id === state.selected); }
async function post(route, values = {}, expected = 200, headers = {}) {
  const d = selected();
  const r = await fetch(base + route, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Piper-Token': state.token, ...headers }, body: JSON.stringify({ device_id: d?.id, generation: d?.generation, owner, standalone: true, clear: true, supported: true, ...values }) });
  const data = await r.json(); assert.equal(r.status, expected, JSON.stringify(data)); return data;
}
async function until(condition, timeout = 7000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) { await refresh(); if (condition()) return; await sleep(60); }
  assert.fail(`Timed out: ${JSON.stringify(state)}`);
}
async function launch() {
  const executable = process.env.PIPER_TEST_EXE || './build/piper-control';
  server = spawn(executable, ['--simulate', '--port', String(port), '--state-dir', directory], { stdio: ['ignore', 'ignore', 'pipe'] });
  server.stderr.on('data', b => { logs += b; });
  for (let i = 0; i < 100; i++) { try { await refresh(); return; } catch { if (server.exitCode !== null) throw Error(logs); await sleep(30); } }
  throw Error('Server failed to start.');
}
async function stopServer(signal = 'SIGTERM') { if (!server || server.exitCode !== null) return; const exited = new Promise(resolve => server.once('exit', resolve)); server.kill(signal); await exited; }
async function startAction(values) {
  const result = await post('/api/command', values, 202); lease = result.request_id;
  clearInterval(keepalive);
  keepalive = setInterval(() => { post('/api/heartbeat', { request_id: lease }).catch(() => {}); }, 150);
  return result;
}
async function action(values) {
  await startAction(values);
  await until(() => !state.motion.active);
  clearInterval(keepalive);
  assert.equal(state.phase, 'complete', state.message);
}
async function query() { await post('/api/query', {}, 202); await until(() => !state.query_active); assert.equal(selected().firmware, 'S-V1.8-2'); }
try {
  await launch();
  assert.equal(state.simulated, true); assert.equal(state.devices.length, 1); assert.equal(selected().tx_frames, 0);
  assert.equal(state.camera.configured, false); assert.equal(state.camera.running, false);
  assert.equal((await fetch(base + '/api/camera/frame')).status, 409);
  await post('/api/camera', { enabled: true }, 403, { 'X-Piper-Token': 'wrong' });
  await post('/api/camera', { enabled: true }, 409); // Simulation cannot open a real webcam.
  await post('/api/camera', { enabled: 'yes' }, 400);
  await post('/api/camera', { enabled: false });
  await refresh(); assert.equal(selected().tx_frames, 0);
  assert.equal((await fetch(base + '/')).status, 200);
  const pikaAsset = await fetch(base + '/pika.mjs');
  assert.equal(pikaAsset.status, 200);
  assert.match(pikaAsset.headers.get('content-security-policy'), /media-src 'self' blob:/);
  assert.match(await pikaAsset.text(), /navigator.serial.requestPort/);
  assert.equal((await fetch(base + '/pika_remote.mjs')).status, 200);
  if (state.backend?.startsWith('dotnet-')) {
    assert.equal(state.pika.configured, false);
    await post('/api/pika/connect', { owner: owner }, 409); // Simulation cannot open real serial hardware.
    assert.equal((await (await fetch(base + '/api/pika/state')).json()).connected, false);
  }
  const spec = await (await fetch(base + '/api/openapi.json')).json(); assert.equal(spec.openapi, '3.1.0');
  await post('/api/command', { action: 'joint', joint: 1, degrees: 5 }, 409);
  await post('/api/stop', {}, 403, { 'X-Piper-Token': 'wrong' });
  await post('/api/stop', {}, 403, { Origin: 'https://hostile.example' });
  const foreignHost = await new Promise((resolve, reject) => {
    const req = http.get(base + '/api/state', { headers: { Host: 'hostile.example' } }, r => { r.resume(); resolve(r.statusCode); }); req.on('error', reject);
  });
  assert.equal(foreignHost, 403);
  const invalid = await fetch(base + '/api/command', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Piper-Token': state.token }, body: '{"action":"stop","action":"joint"}' });
  assert.equal(invalid.status, 400);
  await query(); await sleep(350); await refresh();
  assert.equal(selected().blocker, ''); assert(selected().joints.every(j => j.enabled === false));
  await post('/api/command', { action: 'joint', joint: 1, degrees: 5 }, 409);
  const beforeEnableTx = selected().tx_frames;
  await action({ action: 'enable' }); assert(selected().joints.every(j => j.enabled));
  assert.equal(selected().tx_frames - beforeEnableTx, 6); // Fresh simulator feedback needs one power command per driver.
  assert.equal(selected().status.control, 0); // Enable does not command MoveJ or a pose.
  const cycleStart = selected().joints.map(j => j.actual_deg);
  await startAction({ action: 'cycle' });
  await until(() => state.motion.sequence_stage === 'raising');
  assert.deepEqual(state.motion.return_degrees, cycleStart);
  await post('/api/command', { action: 'hold' }, 202);
  await until(() => !state.motion.active); clearInterval(keepalive);
  assert.equal(state.phase, 'complete'); assert.equal(state.motion.sequence_stage, '');
  const before = selected().joints.map(j => j.actual_deg);
  await action({ action: 'joint', joint: 1, degrees: -1.234 });
  assert(Math.abs(selected().joints[0].actual_deg + 1.234) <= .002);
  assert.deepEqual(selected().joints.slice(1).map(j => j.actual_deg), before.slice(1));
  const completedTx = selected().tx_frames;
  await post('/api/stop', { request_id: lease }, 409);
  await refresh(); assert.equal(selected().tx_frames, completedTx);
  await post('/api/command', { action: 'joint', joint: 2, degrees: -1 }, 400);
  await post('/api/command', { action: 'joint', joint: 1, degrees: 1, generation: selected().generation + 1 }, 409);
  await action({ action: 'jog', joint: 1, degrees: .234 }); assert.equal(selected().joints[0].actual_deg, -1);
  await action({ action: 'zero', joint: 1 }); assert.equal(selected().joints[0].actual_deg, 0);
  /* A tiny target inside tolerance must still be fully commanded; tolerance
     cannot cause premature completion while the slow ramp is unfinished. */
  const began = Date.now(); await action({ action: 'joint', joint: 1, degrees: .15, speed_deg_s: .1 });
  assert(Date.now() - began > 1400); assert.equal(selected().joints[0].actual_deg, .15);
  await action({ action: 'pose', degrees: [0, 45.5, -65.5, .5, 10.5, -.5] });
  assert.deepEqual(selected().joints.map(j => j.actual_deg), [0, 45.5, -65.5, .5, 10.5, -.5]);
  for (let joint = 1; joint <= 6; joint++) {
    const previous = selected().joints.map(j => j.actual_deg);
    const target = previous[joint - 1] + (joint % 2 ? -.5 : .5);
    await action({ action: 'joint', joint, degrees: target });
    previous[joint - 1] = target;
    assert.deepEqual(selected().joints.map(j => j.actual_deg), previous);
  }
  const width = selected().gripper.width_mm;
  await action({ action: 'gripper', width_mm: width + 1 }); assert(Math.abs(selected().gripper.width_mm - width - 1) < .01);
  await startAction({ action: 'joint', joint: 1, degrees: 10 }); await sleep(200);
  await post('/api/command', { action: 'hold' }, 202); await until(() => !state.motion.active); clearInterval(keepalive);
  assert.equal(state.phase, 'complete'); assert(selected().joints[0].actual_deg < 3);
  await startAction({ action: 'joint', joint: 1, degrees: 10 });
  await post('/api/reconnect', {}, 409);
  await post('/api/command', { action: 'joint', joint: 2, degrees: 40, owner: 'different-client-123' }, 409);
  await post('/api/heartbeat', { request_id: lease, owner: 'different-client-123' }, 409);
  await post('/api/stop', { request_id: lease, owner: 'different-client-123' }, 409);
  clearInterval(keepalive);
  /* A slow HTTP client must not starve CAN receive/lease expiry. */
  const slow = net.createConnection(port, '127.0.0.1'); slow.write(`POST /api/command HTTP/1.1\r\nHost: 127.0.0.1:${port}\r\nContent-Length: 500\r\n`);
  await until(() => !state.motion.active, 2500); slow.destroy();
  assert.equal(state.phase, 'stopped'); assert.match(state.message, /heartbeat expired/i); assert(selected().recovery_required);
  await query();
  assert.equal(state.phase, 'blocked'); // Information reads do not hide an unresolved stop.
  assert.match(state.message, /heartbeat expired/i); assert(selected().recovery_required);
  await post('/api/command', { action: 'joint', joint: 1, degrees: 0 }, 409);
  await until(() => selected().status.stationary_s >= .4);
  await action({ action: 'reset' }); assert(!selected().recovery_required); assert(selected().joints.every(j => !j.enabled));
  await action({ action: 'enable' }); await action({ action: 'disable' }); assert(!selected().recovery_required);
  await action({ action: 'enable' }); await action({ action: 'disable' }); assert(!selected().recovery_required);
  await post('/api/command', { action: 'verify-reset', reset_completed: false }, 409);
  await post('/api/command', { action: 'verify-reset', reset_completed: true });
  await refresh(); assert(!selected().recovery_required);
  const generation = selected().generation, reconnectTx = selected().tx_frames;
  const reconnectPose = selected().joints.map(j => j.actual_deg);
  await post('/api/reconnect'); await until(() => !state.query_active);
  assert(selected().generation > generation); assert.equal(selected().firmware, 'S-V1.8-2');
  assert.equal(selected().tx_frames - reconnectTx, 14); // Firmware, gripper configuration and six angle/speed plus six acceleration reads only.
  assert.equal(state.phase, 'monitor'); // A verified recovery stays cleared across a read-only reconnect.
  assert(selected().joints.every(j => j.limits_fresh && j.acceleration_fresh && j.max_acceleration_rad_s2 === 5 && j.enabled === false));
  assert.deepEqual(selected().joints.map(j => j.actual_deg), reconnectPose);
  assert(!state.motion.active); assert(!selected().recovery_required);
  await sleep(350); await action({ action: 'enable' });
  await startAction({ action: 'joint', joint: 1, degrees: 12 }); clearInterval(keepalive);
  await stopServer('SIGKILL');
  const persisted = JSON.parse(await readFile(`${directory}/control-state.json`)); assert(persisted.devices['simulation:one'].blocked);
  await launch(); assert(selected().recovery_required); assert.equal(selected().tx_frames, 0);
  console.log('HTTP/simulator integration passed: full position/power/gripper workflow, exact signed targets, retained joints, slow ramp completion, leases, hostile requests, slow client, reconnect, crash persistence.');
} catch (error) { console.error(logs); throw error; }
finally { clearInterval(keepalive); await stopServer(); await rm(directory, { recursive: true, force: true }); }
