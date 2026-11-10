// Drives the real Pika panel (web/pika.mjs) and the real Host client
// (web/pika_remote.mjs) against a fake Host that applies the ownership rules of
// managed/Piper.Host's /api/pika routes. Only the DOM, the clock and the
// network are faked.
import assert from 'node:assert/strict';
const elements = new Map(), intervals = [], timers = [], events = new Map(), asks = [];
let now = 1000, answers = [], serialRequests = 0;
const element = id => {
  if (!elements.has(id)) elements.set(id, { checked: false, disabled: false, hidden: false, textContent: '', value: '', classList: { toggle() {} }, setAttribute() {}, addEventListener(name, fn) { events.set(`${id}:${name}`, fn); } });
  return elements.get(id);
};
const me = 'page-owner';
// Several modules listen to the same document and window events: fire them all.
const listen = (key, fn) => { const earlier = events.get(key); events.set(key, earlier ? (...args) => { earlier(...args); fn(...args); } : fn); };
globalThis.document = { hidden: false, hasFocus: () => true, getElementById: element, addEventListener(name, fn) { listen(`document:${name}`, fn); } };
globalThis.window = {
  piperPage: Object.freeze({ owner: me, token: () => 'token-1', ask: async statement => { asks.push(statement); assert.ok(answers.length, `Unexpected operator prompt: ${statement.title}`); return answers.shift(); } }),
  addEventListener(name, fn) { listen(`window:${name}`, fn); },
};
Object.defineProperty(globalThis, 'performance', { value: { now: () => now }, configurable: true });
globalThis.setInterval = fn => { intervals.push(fn); };
globalThis.setTimeout = fn => { timers.push(fn); };
Object.defineProperty(globalThis, 'navigator', { value: { serial: { async requestPort() { serialRequests++; throw Error('The dashboard must not open a serial port'); } } }, configurable: true });

// The fake Host: one gripper, one owner, the same refusals as PikaRoutes.
const host = { owner: null, connected: false, enabled: false, telemetry: null, posts: [], fail: {}, gate: {} };
const reply = (status, body) => ({ ok: status === 200, status, json: async () => body });
globalThis.fetch = async (url, options = {}) => {
  if (url.startsWith('/api/pika/state')) return reply(200, { owner: host.owner, connected: host.connected, fresh: !!host.telemetry, enabled: host.enabled, telemetry: host.telemetry, message: '' });
  assert.ok(url.startsWith('/api/pika/'), `pika.mjs must follow app.js piper-state events, not fetch ${url}`);
  const action = url.slice('/api/pika/'.length), body = JSON.parse(options.body);
  assert.equal(options.headers['X-Piper-Token'], 'token-1');
  host.posts.push({ action, body, keepalive: options.keepalive === true });
  await host.gate[action];
  if (host.fail[action]) return reply(409, { ok: false, error: host.fail[action] });
  if (action === 'connect') {
    if (host.owner && host.owner !== body.owner && body.take_over !== true) return reply(409, { ok: false, error: 'Pika is controlled by another dashboard tab' });
    host.owner = body.owner; host.connected = true; host.enabled = false;
    return reply(200, { ok: true, device: '/dev/pika0', distribution: 'Ubuntu' });
  }
  if (host.owner !== body.owner) return reply(409, { ok: false, error: 'This dashboard does not own the Pika session' });
  if (action === 'enable') host.enabled = true;
  if (action === 'disable') host.enabled = false;
  if (action === 'position' && !host.enabled) return reply(409, { ok: false, error: 'Pika is not enabled' });
  if (action === 'disconnect') { host.owner = null; host.connected = false; host.enabled = false; }
  return reply(200, { ok: true });
};

const { angleFromWidth } = await import('../web/pika.mjs');
const settle = async () => { for (let i = 0; i < 40; i++) await Promise.resolve(); };
// Advances the clock and runs the watch loop's pending delays.
const tick = async (ms = 100) => { now += ms; for (const fn of timers.splice(0)) fn(); await settle(); };
const robot = enabled => ({ id: 'can0', fresh: true, recovery_required: false, status: { arm_status: 0, error: 0 }, joints: [{ enabled }, { enabled }] });
const state = ({ enabled = true, owner = null, backend = 'dotnet-linux' } = {}) => ({
  simulated: false, ...(backend ? { backend } : {}), selected: 'can0', devices: [robot(enabled)],
  ...(backend ? { pika: { configured: true, transport: 'linux-serial', owner, device: '/dev/pika0' } } : {}),
});
const serve = detail => events.get('window:piper-state')({ detail });
const telemetry = (width = 20, speed = 0) => { host.telemetry = { motor: { Position: angleFromWidth(width), Speed: speed }, motorstatus: { Status: '0x0' } }; };
const actions = (from = 0) => host.posts.slice(from).map(post => post.action).filter(action => action !== 'heartbeat');
const gate = action => { let release; host.gate[action] = new Promise(resolve => { release = resolve; }); return () => { delete host.gate[action]; release(); }; };
const stop = () => events.get('window:piper-stop')();
await settle();

// W2/W9: a focused, visible page with an enabled robot and an assigned gripper
// sends nothing by itself: no connect, no take-over, no enable, no serial port.
assert.equal(events.has('window:focus'), false, 'Focus must not be a Pika trigger');
serve(state()); telemetry();
for (let i = 0; i < 5; i++) { for (const fn of intervals) fn(); await tick(1500); serve(state()); }
assert.deepEqual(host.posts, [], 'Nothing is sent until the operator acts');
assert.equal(serialRequests, 0);
assert.equal(element('pika-connect').disabled, false);
assert.equal(element('pika-enable').disabled, true, 'Enable needs a connected session');

// W3: STOP sends the jaw disable even when this page believes it has no session.
stop(); await settle();
assert.deepEqual(actions(), ['disable'], 'STOP always sends the disable');
assert.equal(host.posts[0].keepalive, true, 'The disable must outlive the page');

// Connecting only reads, and never asks for another owner's gripper.
let mark = host.posts.length;
await element('pika-connect').onclick();
assert.deepEqual(actions(mark), ['connect']);
assert.equal('take_over' in host.posts[mark].body, false, 'A plain connect never takes over');
assert.equal(host.posts[mark].body.owner, me);
assert.equal(asks.length, 0);
await tick();
assert.equal(host.enabled, false, 'Connecting must not power the jaws');
assert.equal(element('pika-enable').disabled, false);

// W2: jaw power needs the operator's click and a confirmation naming this gripper; a refusal sends nothing.
mark = host.posts.length; answers = [false];
await element('pika-enable').onclick();
assert.equal(asks.length, 1);
assert.match(asks[0].text, /\/dev\/pika0/, 'The prompt names the gripper it powers');
assert.deepEqual(actions(mark), [], 'A declined confirmation sends no enable');
assert.equal(host.enabled, false);
answers = [true];
await element('pika-enable').onclick();
assert.equal(asks.length, 2, 'Each enable is confirmed again');
assert.deepEqual(actions(mark), ['enable']);
assert.deepEqual(host.posts.find(post => post.action === 'enable').body, { owner: me, pika_device: '/dev/pika0', pika_distribution: 'Ubuntu', clear: true });
assert.equal(element('pika-open').disabled, false);

// A bounded step is one position request, completed by measured arrival.
mark = host.posts.length; element('pika-step').value = '5';
await element('pika-open').onclick();
assert.deepEqual(actions(mark), ['position']);
const target = Number(element('pika-status').textContent.match(/Requested ([\d.]+)/)[1]);
assert.ok(Math.abs(target - 25) < 1e-6);
telemetry(target - .42); await tick(); await tick(200);
assert.match(element('pika-status').textContent, /within 1.0 mm/, 'A 0.42 mm settling offset is an arrival');
mark = host.posts.length; element('pika-slider').value = '80';
await element('pika-slider').onchange();
assert.deepEqual(actions(mark), ['position'], 'The slider sends one absolute target');
assert.ok(Math.abs(host.posts.at(-1).body.value - angleFromWidth(80)) < 1e-9);
telemetry(80); await tick(); await tick(200);

// W4: the robot leaving the enabled state disables the jaws, and the robot
// coming back never powers them again.
mark = host.posts.length;
serve(state({ enabled: false })); await settle();
assert.deepEqual(actions(mark), ['disable'], 'A robot that is not enabled takes jaw power away');
serve(state());
for (let i = 0; i < 5; i++) { for (const fn of intervals) fn(); await tick(300); serve(state()); }
assert.deepEqual(actions(mark), ['disable'], 'Robot power coming back never re-enables the jaws');
assert.equal(host.enabled, false);
assert.equal(asks.length, 2, 'No prompt appears without a click');
assert.equal(element('pika-open').disabled, true);
assert.equal(element('pika-enable').disabled, false, 'Only the operator can enable again');

// W3: STOP in the read-only state still sends the disable.
mark = host.posts.length; stop(); await settle();
assert.deepEqual(actions(mark), ['disable']);

// A Host that reports the jaws enabled does not arm this page.
host.enabled = true; await tick(); await tick();
assert.equal(element('pika-open').disabled, true, 'Server-reported power never arms the page');
host.enabled = false;

// B18/W3: a STOP that lands while an enable is in flight is followed by a
// disable after that enable, because the enable may be applied after STOP's own disable.
mark = host.posts.length; answers = [true];
let release = gate('enable');
let enabling = element('pika-enable').onclick(); await settle();
assert.deepEqual(actions(mark), ['enable']);
stop(); await settle();
assert.deepEqual(actions(mark), ['enable', 'disable']);
release(); await enabling; await settle();
assert.deepEqual(actions(mark), ['enable', 'disable', 'disable'], 'An interrupted enable is disabled again after it lands');
assert.equal(host.enabled, false);
assert.equal(element('pika-open').disabled, true, 'An interrupted enable never arms');
// The interruption died with that request: the next confirmed enable works.
mark = host.posts.length; answers = [true];
await element('pika-enable').onclick();
assert.deepEqual(actions(mark), ['enable'], 'A past STOP must not make the next enable disable itself');
assert.equal(element('pika-open').disabled, false);
await element('pika-disable').onclick(); await settle();
assert.equal(host.enabled, false);
assert.equal(element('pika-open').disabled, true);

// An enable whose reply is an error may still have powered the jaws: disable follows.
mark = host.posts.length; answers = [true]; host.fail.enable = 'reply lost';
await element('pika-enable').onclick();
delete host.fail.enable;
assert.deepEqual(actions(mark), ['enable', 'disable'], 'A failed enable is followed by a disable');
assert.match(element('pika-status').textContent, /reply lost/);
assert.equal(element('pika-open').disabled, true);

// Stale telemetry while armed requests the disable.
answers = [true]; await element('pika-enable').onclick();
mark = host.posts.length; host.telemetry = null; now += 2100;
for (const fn of intervals) fn(); await settle();
assert.deepEqual(actions(mark), ['disable'], 'Stale telemetry disables an armed gripper');
telemetry(); await tick();

// A hidden page disables first and disconnects only after the disable was answered.
mark = host.posts.length; release = gate('disable');
document.hidden = true; events.get('document:visibilitychange')(); await settle();
assert.deepEqual(actions(mark), ['disable'], 'The disconnect waits for the disable');
release(); await settle(); await settle();
assert.deepEqual(actions(mark), ['disable', 'disconnect']);
document.hidden = false;
assert.equal(host.owner, null);
assert.equal(element('pika-connect').disabled, false);

// W2: another owner's gripper. Declining the take-over sends nothing.
host.owner = 'other-owner'; host.connected = true; serve(state({ owner: 'other-owner' }));
for (const fn of intervals) fn(); await tick(1500);
mark = host.posts.length; let asked = asks.length; answers = [false];
await element('pika-connect').onclick();
assert.equal(asks.length, asked + 1);
assert.match(asks.at(-1).text, /other-owner/, 'The take-over prompt names the current owner');
assert.match(asks.at(-1).text, /\/dev\/pika0/);
assert.deepEqual(actions(mark), [], 'A declined take-over sends nothing');
assert.equal(host.owner, 'other-owner');
// The owner changed while the operator was deciding: the confirmation named someone else, so nothing is taken.
answers = [true]; host.owner = 'third-owner';
await element('pika-connect').onclick();
assert.deepEqual(actions(mark), [], 'A confirmation for one owner never takes another');
assert.match(element('pika-status').textContent, /ownership changed/i);
assert.equal(host.owner, 'third-owner');
// Confirmed, and the named owner still holds it: exactly one take_over.
host.owner = 'other-owner'; answers = [true];
await element('pika-connect').onclick();
assert.deepEqual(actions(mark), ['connect']);
assert.equal(host.posts[mark].body.take_over, true);
assert.equal(host.owner, me);
assert.equal(host.enabled, false, 'Taking over does not power the jaws');
await tick();

// Another session takes this gripper: the page drops to disconnected and never takes it back.
answers = [true]; await element('pika-enable').onclick();
assert.equal(element('pika-open').disabled, false);
host.owner = 'thief'; mark = host.posts.length;
await tick(); await tick();
assert.equal(element('pika-disconnect').disabled, true, 'A lost session is disconnected');
assert.equal(element('pika-open').disabled, true);
serve(state({ owner: 'thief' }));
for (let i = 0; i < 5; i++) { for (const fn of intervals) fn(); await tick(1500); serve(state({ owner: 'thief' })); }
assert.equal(actions(mark).some(action => action === 'connect' || action === 'enable' || action === 'position'), false, 'A taken gripper is never taken back or powered automatically');
assert.equal(host.owner, 'thief');

// The standalone native daemon has no Pika routes: nothing is posted to it, and Connect is unavailable.
serve(state({ backend: null }));
mark = host.posts.length; stop(); await settle();
assert.deepEqual(host.posts.slice(mark), []);
assert.equal(element('pika-connect').disabled, true);
assert.match(element('pika-transport-description').textContent, /only through the managed Host/);
assert.equal(serialRequests, 0, 'The dashboard never opens a serial port');
console.log('Pika UI tests passed');
