import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, rm, readFile } from 'node:fs/promises';
import { existsSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import net from 'node:net';
import { randomUUID } from 'node:crypto';
import http from 'node:http';

const directory = await mkdtemp(`${tmpdir()}/piper-api-`);
const port = 20000 + process.pid % 20000, base = `http://127.0.0.1:${port}`;
let server, logs = '', state, keepalive, lease;
const owner = 'automated-simulator-test';
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
/* W7 contract: every request this suite makes is recorded as "METHOD route status"
   and compared with web/openapi.json at the end, in both directions. */
const observed = new Set();
const documentedRoute = route => /^\/api\/pika\/(?!state$|devices$)/.test(route) ? '/api/pika/{action}' : route;
async function call(method, route, init = {}) {
  const r = await fetch(base + route, { method, ...init });
  observed.add(`${method} ${documentedRoute(route.split('?')[0])} ${r.status}`);
  return r;
}
async function refresh() { const r = await call('GET', '/api/state'); assert.equal(r.status, 200); state = await r.json(); return state; }
function selected() { return state.devices.find(d => d.id === state.selected); }
async function post(route, values = {}, expected = 200, headers = {}) {
  const d = selected();
  const r = await call('POST', route, { headers: { 'Content-Type': 'application/json', 'X-Piper-Token': state.token, ...headers }, body: JSON.stringify({ device_id: d?.id, generation: d?.generation, owner, standalone: true, clear: true, supported: true, ...values }) });
  const data = await r.json(); assert.equal(r.status, expected, JSON.stringify(data)); return data;
}
async function until(condition, timeout = 7000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) { await refresh(); if (condition()) return; await sleep(60); }
  assert.fail(`Timed out: ${JSON.stringify(state)}`);
}
async function launch() {
  // The native daemon is Linux-only; on Windows the managed Host is the server under test.
  const executable = process.env.PIPER_TEST_EXE || (process.platform === 'win32' ? 'managed/Piper.Host/bin/Release/net9.0/Piper.Host.exe' : './build/piper-control');
  if (!existsSync(executable)) throw Error(`Server executable ${executable} is missing. Build it, or set PIPER_TEST_EXE.`);
  server = spawn(executable, ['--simulate', '--port', String(port), '--state-dir', directory, '--web-root', 'web'], { stdio: ['ignore', 'pipe', 'pipe'] });
  server.stdout.on('data', b => { logs += b; });
  server.stderr.on('data', b => { logs += b; });
  const deadline = Date.now() + 15000;
  while (Date.now() < deadline) { try { await refresh(); return; } catch { if (server.exitCode !== null) throw Error(logs); await sleep(60); } }
  throw Error(`Server failed to start within 15 seconds:\n${logs}`);
}
async function stopServer(signal = 'SIGTERM') { if (!server || server.exitCode !== null) return; const exited = new Promise(resolve => server.once('exit', resolve)); server.kill(signal); await exited; }
async function startAction(values) {
  // Admission requires an observed quiet pose after the previous operation.
  // Its completion event may precede the next coherent stationary window.
  await until(() => selected().status.stationary_s >= .3);
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
  await until(() => selected().status.stationary_s >= .3);
}
/* Documented answers this offline simulator cannot produce, with the reason.
   Everything else in web/openapi.json must have been produced by this run. */
const unreachable = {
  c: {
    'GET /api/camera/frame 200': 'needs a webcam',
    'POST /api/select 500': 'needs a failing state directory',
    'POST /api/command 500': 'needs a failing state directory',
  },
  managed: {
    'POST /api/select 500': 'needs a failing state directory',
    'POST /api/command 500': 'needs a failing state directory',
    'POST /api/maintenance/restart 202': 'needs a supervised or plugin-owned host',
    'GET /api/pika/devices 409': 'simulation never runs discovery',
    'POST /api/pika/devices 409': 'simulation never runs discovery',
    'POST /api/pika/{action} 200': 'simulation rejects every gripper action',
  },
};
function checkContract(spec, backend) {
  const documented = new Set(), paths = new Set();
  for (const [path, item] of Object.entries(spec.paths)) {
    assert.ok(['c', 'managed', 'both'].includes(item['x-piper-backend']), `${path} must name its backends`);
    if (item['x-piper-backend'] !== 'both' && item['x-piper-backend'] !== backend) continue;
    paths.add(path);
    for (const method of ['get', 'post']) for (const status of Object.keys(item[method]?.responses ?? {})) documented.add(`${method.toUpperCase()} ${path} ${status}`);
  }
  // 403 (wrong token, origin or host) is documented once for every route, in info.description.
  assert.match(spec.info.description, /403/);
  const undocumented = [...observed].filter(answer => !documented.has(answer) && !answer.endsWith(' 403'));
  assert.deepEqual(undocumented, [], 'The server gave answers that web/openapi.json does not document');
  const unproduced = [...documented].filter(answer => !observed.has(answer) && !(answer in unreachable[backend]));
  assert.deepEqual(unproduced, [], 'web/openapi.json documents answers the server never gave');
  const stale = Object.keys(unreachable[backend]).filter(answer => !documented.has(answer) || observed.has(answer));
  assert.deepEqual(stale, [], 'The unreachable list names answers that are undocumented or were produced');
  // The routes the server source defines are exactly the documented ones.
  const literals = file => new Set([...readFileSync(new URL(`../${file}`, import.meta.url), 'utf8').matchAll(/"(\/api\/[a-z./]*)"/g)].map(match => match[1]));
  const defined = backend === 'c' ? new Set([...literals('src/controller.c'), ...literals('src/server.c')]) :
    new Set(['managed/Piper.Host/HostRoutes.cs', 'managed/Piper.Core/ControllerService.cs', 'managed/Piper.Core/CoordinatedStop.cs'].flatMap(file => [...literals(file)]).map(route => route === '/api/pika/' ? '/api/pika/{action}' : route).concat(['/api/pika/state', '/api/pika/devices']));
  assert.deepEqual([...paths].sort(), [...defined].sort(), 'Documented routes and the routes in the server source differ');
}
async function query() { await post('/api/query', {}, 202); await until(() => !state.query_active); assert.equal(selected().firmware, 'S-V1.8-2'); }
try {
  await launch();
  const managed = !!state.backend?.startsWith('dotnet-');
  assert.equal(state.simulated, true); assert.equal(state.devices.length, 1); assert.equal(selected().tx_frames, 0);
  if (!managed) { // The bench webcam is served only by the C server.
    assert.equal(state.camera.configured, false); assert.equal(state.camera.running, false);
    assert.equal((await call('GET', '/api/camera/frame')).status, 409);
    await post('/api/camera', { enabled: true }, 403, { 'X-Piper-Token': 'wrong' });
    await post('/api/camera', { enabled: true }, 409); // Simulation cannot open a real webcam.
    await post('/api/camera', { enabled: 'yes' }, 400);
    await post('/api/camera', { enabled: false });
  }
  await refresh(); assert.equal(selected().tx_frames, 0);
  assert.equal((await fetch(base + '/')).status, 200);
  const pikaAsset = await fetch(base + '/pika.mjs');
  assert.equal(pikaAsset.status, 200);
  assert.match(pikaAsset.headers.get('content-security-policy'), /media-src 'self' blob:/);
  // W9: the served dashboard has no serial path; it drives Pika only through the Host client.
  const pikaSource = await pikaAsset.text();
  assert.doesNotMatch(pikaSource, /navigator\.serial|requestPort/);
  assert.match(pikaSource, /from '\.\/pika_remote\.mjs'/);
  assert.match(await (await fetch(base + '/pika_remote.mjs')).text(), /export class PikaClient/);
  if (managed) {
    assert.equal(state.pika.configured, false);
    await post('/api/pika/connect', { owner: owner }, 409); // Simulation cannot open real serial hardware.
    assert.equal((await (await call('GET', '/api/pika/state')).json()).connected, false);
    assert.deepEqual((await (await call('GET', '/api/pika/devices')).json()).devices, []);
    assert.deepEqual((await post('/api/pika/devices')).devices, []);
    await post('/api/pika/disable', { owner }, 409);
    const malformed = await call('POST', '/api/pika/connect', { headers: { 'Content-Type': 'application/json', 'X-Piper-Token': state.token }, body: '{"owner":' });
    assert.equal(malformed.status, 400);
    await post('/api/maintenance/restart', { backend_process_id: 1, backend_instance_id: 'stale', release_grippers: true }, 409);
  }
  const spec = await (await call('GET', '/api/openapi.json')).json(); assert.equal(spec.openapi, '3.1.0');
  assert.deepEqual(spec, JSON.parse(readFileSync(new URL('../web/openapi.json', import.meta.url), 'utf8')), 'The server serves the checked-in API description');
  await post('/api/select', { device_id: 'usb:absent' }, 404);
  await post('/api/select', { device_id: state.selected });
  await post('/api/query', { generation: selected().generation + 1 }, 409);
  await post('/api/command', { action: 'joint', joint: 1, degrees: 5 }, 409);
  await post('/api/stop', {}, 403, { 'X-Piper-Token': 'wrong' });
  await post('/api/stop', {}, 403, { Origin: 'https://hostile.example' });
  const foreignHost = await new Promise((resolve, reject) => {
    const req = http.get(base + '/api/state', { headers: { Host: 'hostile.example' } }, r => { r.resume(); resolve(r.statusCode); }); req.on('error', reject);
  });
  assert.equal(foreignHost, 403);
  const invalid = await call('POST', '/api/command', { headers: { 'Content-Type': 'application/json', 'X-Piper-Token': state.token }, body: '{"action":"stop","action":"joint"}' });
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
  // W7: a heartbeat that names its adapter is pinned to that connection generation;
  // one that names none renews the selected arm's lease.
  await post('/api/heartbeat', { request_id: lease, generation: selected().generation + 1 }, 409);
  await post('/api/heartbeat', { request_id: lease, device_id: undefined, generation: undefined });
  await post('/api/heartbeat', { request_id: lease });
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
  /* W1: the operator STOP is an emergency stop: it names only its sender,
     stops another owner's motion at once and is never refused. */
  await sleep(350); await action({ action: 'enable' });
  await startAction({ action: 'joint', joint: 1, degrees: 10 }); await sleep(200);
  const dashboard = randomUUID();
  if (managed) await post('/api/shutdown', {}, 409);
  const operatorStop = await call('POST', managed ? '/api/stop/coordinated' : '/api/stop', { headers: { 'Content-Type': 'application/json', 'X-Piper-Token': state.token }, body: JSON.stringify({ owner: dashboard }) });
  const stopReply = await operatorStop.json();
  assert.equal(operatorStop.status, 200, JSON.stringify(stopReply));
  const armStop = managed ? JSON.parse(stopReply.arm.message) : stopReply;
  if (managed) { assert.equal(stopReply.arm.ok, true); assert.equal(stopReply.pika.ok, true); }
  assert.deepEqual(armStop.stops.map(stop => [stop.device_id, stop.ok]), [[state.selected, true]]);
  clearInterval(keepalive);
  await until(() => !state.motion.active, 2500);
  assert.equal(state.phase, 'stopped'); assert.match(state.message, /Operator global stop/); assert(selected().recovery_required);
  // A command that reaches the server after STOP is refused by the recovery latch.
  await post('/api/command', { action: 'joint', joint: 1, degrees: 0 }, 409);
  await post('/api/command', { action: 'enable' }, 409);
  // Repeating the operator STOP on the arm-only route is accepted on both backends.
  assert.equal((await post('/api/stop', { owner: dashboard, device_id: undefined, generation: undefined })).stops.length, 1);
  // A stale generation never prevents an emergency stop of the named arm.
  const staleStop = await post('/api/stop', { owner: dashboard, generation: selected().generation + 1 }, 409);
  assert.equal(staleStop.stop_intent, 'emergency'); assert.equal(staleStop.stop_scope, 'device'); assert.equal(staleStop.stop_scope_degraded, true);
  assert.deepEqual(staleStop.stops.map(stop => stop.ok), [true]);
  // A generation that names no arm cannot be scoped: every arm stops and the reply says so.
  const widened = await post('/api/stop', { owner: dashboard, device_id: undefined }, 409);
  assert.equal(widened.stop_scope, 'all'); assert.equal(widened.stop_scope_degraded, true);
  assert(widened.stop_scope_reason); assert.deepEqual(widened.stops.map(stop => stop.ok), [true]);
  // The coordinated route reports the same widened scope as a failed arm leg, and still holds.
  if (managed) { const both = await post('/api/stop/coordinated', { owner: dashboard, device_id: undefined }, 409); assert.equal(both.arm.ok, false); assert.equal(both.pika.ok, false); assert.match(both.pika.message, /Every Pika was asked to hold instead\. All Pika holds acknowledged\./); }
  // The emergency coordinated stop names nobody and is still honoured.
  if (managed) assert.equal((await post('/api/stop/coordinated', { owner: undefined, device_id: undefined, generation: undefined })).pika.ok, true);
  await until(() => selected().status.stationary_s >= .4);
  await action({ action: 'reset' }); assert(!selected().recovery_required); assert(selected().joints.every(j => !j.enabled));
  // W7: a reconnect that carries a generation is pinned to it.
  await post('/api/reconnect', { generation: selected().generation + 1 }, 409);
  const generation = selected().generation, reconnectTx = selected().tx_frames;
  const reconnectPose = selected().joints.map(j => j.actual_deg);
  await post('/api/reconnect'); await until(() => !state.query_active);
  assert(selected().generation > generation); assert.equal(selected().firmware, 'S-V1.8-2');
  assert.equal(selected().tx_frames - reconnectTx, 14); // Firmware, gripper configuration and six angle/speed plus six acceleration reads only.
  assert.equal(state.phase, 'monitor'); // A verified recovery stays cleared across a read-only reconnect.
  assert(selected().joints.every(j => j.limits_fresh && j.acceleration_fresh && j.max_acceleration_rad_s2 === 5 && j.enabled === false));
  assert.deepEqual(selected().joints.map(j => j.actual_deg), reconnectPose);
  assert(!state.motion.active); assert(!selected().recovery_required);
  // Without device_id and generation every CAN socket is reopened.
  await post('/api/reconnect', { device_id: undefined, generation: undefined }); await until(() => !state.query_active);
  assert(selected().generation > generation + 1);
  await sleep(350); await action({ action: 'enable' });
  await startAction({ action: 'joint', joint: 1, degrees: 12 }); clearInterval(keepalive);
  await stopServer('SIGKILL');
  const persisted = JSON.parse(await readFile(`${directory}/control-state.json`)); assert(persisted.devices['simulation:one'].blocked);
  await launch(); assert(selected().recovery_required); assert.equal(selected().tx_frames, 0);
  if (managed) {
    const exited = new Promise(resolve => server.once('exit', resolve));
    await post('/api/shutdown', {});
    await exited;
  }
  checkContract(spec, managed ? 'managed' : 'c');
  console.log('HTTP/simulator integration passed: full position/power/gripper workflow, exact signed targets, retained joints, slow ramp completion, leases, hostile requests, slow client, reconnect, crash persistence.');
} catch (error) { console.error(logs); throw error; }
finally { clearInterval(keepalive); await stopServer(); await rm(directory, { recursive: true, force: true }); }
