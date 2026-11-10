// Runs the whole dashboard (web/app.js) against a fake DOM and a fake server,
// and answers its real operator prompt by pressing the dialog's own buttons.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { randomUUID } from 'node:crypto';
// app.js is a module; the vm runs it as a script, so its one import (the generated API

// client) is handed to it as a value instead.

const generatedClient = await import('../web/api.mjs');

const module = await readFile(new URL('../web/app.js', import.meta.url), 'utf8');

const source = module.replace(/^import \{([^}]+)\} from '\.\/api\.mjs';$/m, (_, names) => `'use strict'; const {${names}} = generatedClient;`);

assert.notEqual(source, module, 'app.js must import the generated API client');

assert.doesNotMatch(source, /^import /m, 'app.js imports only the generated API client');
const html = await readFile(new URL('../web/index.html', import.meta.url), 'utf8');
assert.doesNotMatch(html, /type="checkbox"/, 'Dashboard must not retain confirmation checkboxes');
assert.doesNotMatch(html, /id="recover"/, 'The always-hidden recover button is gone');
for (const id of ['safety-prompt', 'safety-title', 'safety-text', 'safety-continue', 'safety-cancel']) assert.match(html, new RegExp(`id="${id}"`), `The prompt needs #${id}`);
assert.doesNotMatch(source, /window\.confirm\(|confirmPiperControl/, 'Every confirmation goes through the one operator prompt');

// A modal dialog makes the rest of the page inert, STOP included. The prompt blocks only the workspace.

assert.doesNotMatch(source, /showModal/, 'The operator prompt must not be modal: STOP stays clickable');

assert(html.indexOf('id="stop"') < html.indexOf('id="shell"') && html.indexOf('id="safety-prompt"') < html.indexOf('id="shell"'), 'STOP and the prompt live outside the workspace the prompt makes inert');

function fake(initial = {}) {
  const store = { ...initial };
  return new Proxy(function () {}, {
    get(_, key) {
      if (key in store) return store[key];
      if (key === Symbol.toPrimitive) return () => '';
      if (typeof key === 'symbol' || key === 'then') return undefined;
      return store[key] = fake();
    },
    set(_, key, value) { store[key] = value; return true; },
    apply() { return fake(); },
  });
}
const joint = enabled => index => ({ index, fresh: true, enabled, actual_deg: 0, min_deg: -150, max_deg: 150, limits_fresh: true, max_speed_rad_s: 1, velocity_rad_s: 0 });
const robot = (overrides = {}, enabled = false) => ({ id: 'simulation:one', name: 'Sim', interface: 'sim', present: true, fresh: true, generation: 3, blocker: '', range_entry_deg: 0, firmware_supported: true, recovery_required: false, reset_pending: false, status: { control: 0, teach_state: 0, arm_status: 0, error: 0 }, gripper: {}, joints: [1, 2, 3, 4, 5, 6].map(joint(enabled)), ...overrides });
const serverState = (overrides = {}) => ({ token: 't', simulated: true, selected: 'simulation:one', phase: 'monitor', message: '', query_active: false, motion: { active: false, request_id: 0 }, devices: [robot()], ...overrides });
const flags = ['standalone', 'clear', 'supported', 'reset_completed'];

// One page load. `page.served` is what GET /api/state answers (null = unreachable);
// `page.answer(route, body, token)` answers each POST.
function boot(served = serverState()) {
  const page = { served, posts: [], dispatched: [], shown: 0, closed: 0, answer: () => ({ ok: true, status: 200, json: async () => ({ message: 'ok' }) }) };
  const dom = new Map(), listeners = new Map(), timers = [];
  const values = { speed: '17', tolerance: '0.2', step: '1', 'gripper-effort': '0.1', width: '25', 'gripper-step': '1' };
  const listen = (key, fn) => listeners.set(key, [...(listeners.get(key) || []), fn]);
  page.byId = id => { if (!dom.has(id)) dom.set(id, fake({ value: values[id] ?? '', addEventListener: (name, fn) => listen(`${id}:${name}`, fn), show() { page.shown++; }, focus() {}, close() { page.closed++; } })); return dom.get(id); };
  page.fire = (key, event = {}) => { for (const fn of listeners.get(key) || []) fn(event); };
  page.app = vm.createContext({
    window: { addEventListener: (name, fn) => listen(`window:${name}`, fn), dispatchEvent: event => page.dispatched.push(event) },
    document: { getElementById: page.byId, createElement: () => fake(), addEventListener: (name, fn) => listen(`document:${name}`, fn), querySelector: () => fake(), querySelectorAll: () => [], hidden: false, hasFocus: () => true, activeElement: null },
    generatedClient, crypto: { randomUUID }, performance, AbortSignal, JSON, Number, Math, Object, Set, Error, Promise, Date,
    CustomEvent: class { constructor(type, init) { this.type = type; this.detail = init?.detail; } },
    Event: class { constructor(type) { this.type = type; } },
    Option: class {},
    setTimeout: fn => { timers.push(fn); return 0; }, setInterval: () => 0, clearTimeout: () => {},
    fetch: async (route, init = {}) => {
      if (route === '/api/state') {
        if (!page.served) throw Error('unreachable');
        return { ok: true, json: async () => JSON.parse(JSON.stringify(page.served)) };
      }
      const post = { route, body: JSON.parse(init.body), token: init.headers['X-Piper-Token'], keepalive: init.keepalive === true };
      page.posts.push(post);
      return page.answer(route, post.body, post.token);
    },
  });
  page.settle = async () => { for (let i = 0; i < 20; i++) await new Promise(resolve => setImmediate(resolve)); };
  page.poll = async () => { const next = timers.filter(fn => fn.name === 'poll').at(-1); timers.length = 0; next(); await page.settle(); };
  page.read = expression => vm.runInContext(expression, page.app);
  page.open = () => page.read('prompting !== null');
  // Presses a control, then answers each prompt it raises with the dialog's own buttons.
  page.press = async (id, ...answers) => {
    const done = page.byId(id).onclick(); await page.settle();
    const titles = [];
    for (const accept of answers) {
      assert.ok(page.open(), `${id} must ask the operator`);
      titles.push(String(page.byId('safety-title').textContent));
      page.byId(accept ? 'safety-continue' : 'safety-cancel').onclick(); await page.settle();
    }
    assert.equal(page.open(), false, `${id} left a prompt open: ${String(page.byId('safety-title').textContent)}`);
    await done; await page.settle();
    return titles;
  };
  page.sent = () => Object.fromEntries(flags.filter(key => key in page.posts.at(-1).body).map(key => [key, page.posts.at(-1).body[key]]));
  vm.runInContext(source, page.app);
  return page;
}

let page = boot(); await page.settle();
assert.equal(page.dispatched.at(-1)?.type, 'piper-state', 'Each state poll is shared with pika.mjs');
assert.equal(page.dispatched.at(-1).detail.token, 't');
const owner = page.app.window.piperPage.owner;
assert.match(owner, /^[0-9a-f-]{36}$/);
assert.equal(page.app.window.piperPage.token(), 't');
assert.equal(page.app.window.confirmPiperControl, undefined, 'No always-true approval hook exists');

// W5: every power and recovery action asks its own question each time, and a
// refusal sends nothing. Flags are exactly the statements of that question.
const perAction = [
  ['enable', 'enable', /Enable motor power/, { standalone: true, clear: true, supported: true }],
  ['disable', 'disable', /Disable motor power/, { standalone: true, supported: true }],
  ['reset', 'reset', /Reset the controller/, { standalone: true, clear: true, supported: true }],
  ['verify', 'verify-reset', /Verify the completed reset/, { standalone: true, supported: true, reset_completed: true }],
];
for (const [id, action, title, expected] of perAction) {
  const before = page.posts.length, shown = page.shown;
  assert.match((await page.press(id, false))[0], title);
  assert.equal(page.posts.length, before, `Declining "${action}" sends nothing`);
  for (let round = 0; round < 2; round++) {
    await page.press(id, true);
    assert.equal(page.posts.at(-1).route, '/api/command');
    assert.equal(page.posts.at(-1).body.action, action, `${id} sends ${action} and nothing else`);
    assert.deepEqual(page.sent(), expected);
  }
  assert.equal(page.posts.length, before + 2, 'One request per confirmed click');
  assert.equal(page.shown, shown + 3, `"${action}" is confirmed every time, never once per session`);
  for (const name of Object.keys(expected)) assert.match(String(page.byId('safety-text').textContent), { standalone: /only controller/, clear: /clear of/, supported: /supported/, reset_completed: /reset or power cycle has completed/ }[name], `The "${action}" prompt states "${name}"`);
}
assert.equal(page.posts.some(post => post.body.action === 'verify-current'), false, 'Enable never clears the recovery latch');
assert.ok(page.dispatched.some(event => event.type === 'piper-robot-disable'), 'Disabling the robot tells the Pika panel');

// Web-B: motion commands share one session approval whose prompt states exactly the flags they carry.
let before = page.posts.length;
assert.match((await page.press('cycle', false))[0], /Make sure it is safe/);
assert.equal(page.posts.length, before, 'A declined motion approval sends nothing');
assert.match(String(page.byId('safety-text').textContent), /clear of/);
assert.match(String(page.byId('safety-text').textContent), /only controller/);
let shown = page.shown;
const first = page.byId('cycle').onclick(), second = page.byId('hold').onclick(); await page.settle();
assert.equal(page.shown, shown + 1, 'Concurrent motion commands share one prompt');
page.byId('safety-continue').onclick(); await first; await second; await page.settle();
assert.equal(page.posts.length, before + 1, 'Only one command is in flight');
assert.equal(page.posts.at(-1).body.action, 'cycle');
assert.deepEqual(page.sent(), { standalone: true, clear: true }, 'A motion command never carries supported or reset_completed');
await page.press('hold');
assert.deepEqual(page.sent(), { standalone: true, clear: true });
assert.equal(page.shown, shown + 1, 'An approved session needs no further motion prompt');
await page.press('enable', true);
assert.equal(page.shown, shown + 2, 'Motion approval never covers a power action');
await page.press('query');
assert.equal(page.posts.at(-1).route, '/api/query');
assert.deepEqual(page.sent(), {}, 'Non-command routes carry no attestations');
// A restarted server (new token) revokes the approval.
page.served = serverState({ token: 't2' }); await page.poll();
before = page.posts.length;
await page.press('cycle', false);
assert.equal(page.posts.length, before, 'A new server token needs a new motion approval');
await page.press('cycle', true);
assert.equal(page.posts.at(-1).token, 't2');

// W5: a saved stop disables Enable; only Verify recovery clears the latch, with the
// verification that matches the measured motor state, confirmed for that action.
page.served = serverState({ token: 't2', devices: [robot({ recovery_required: true })] }); await page.poll();
assert.equal(page.byId('enable').disabled, true, 'Enable is unavailable while recovery is required');
assert.equal(page.byId('incident-verify').disabled, false);
before = page.posts.length;
assert.match((await page.press('incident-verify', false))[0], /Clear the saved stop/);
assert.equal(page.posts.length, before, 'Declining recovery verification sends nothing');
await page.press('incident-verify', true);
assert.equal(page.posts.at(-1).body.action, 'verify-disabled');
assert.deepEqual(page.sent(), { standalone: true, supported: true }, 'Disabled motors: no clear statement was made, so none is sent');
page.served = serverState({ token: 't2', devices: [robot({ recovery_required: true }, true)] }); await page.poll();
await page.press('incident-verify', true);
assert.equal(page.posts.at(-1).body.action, 'verify-current');
assert.deepEqual(page.sent(), { standalone: true, clear: true, supported: true });
page.served = serverState({ token: 't2', devices: [robot({ recovery_required: true, reset_pending: true })] }); await page.poll();
await page.press('incident-verify', true);
assert.equal(page.posts.at(-1).body.action, 'verify-reset');
assert.deepEqual(page.sent(), { standalone: true, supported: true, reset_completed: true });
// No selected arm: the handlers refuse instead of throwing.
page.served = serverState({ token: 't2', selected: '', devices: [] }); await page.poll();
assert.notEqual(page.dispatched.at(-1).detail, null, 'Rendering without a selected arm must not fail the poll');
before = page.posts.length;
for (const id of ['incident-verify', 'copy', 'gripper-minus', 'gripper-plus']) await page.press(id);
assert.equal(page.posts.length, before, 'Controls without feedback send nothing');
page.served = serverState({ token: 't2' }); await page.poll();

// W1: STOP names no device, generation or request, so stale client belief cannot narrow or void it.
const stopBody = () => { const post = page.posts.at(-1); assert.deepEqual(post.body, { owner }, 'STOP carries only the page identity'); assert.equal(post.keepalive, true); return post; };
before = page.posts.length;
await page.press('stop');
assert.equal(page.posts.length, before + 1);
assert.equal(stopBody().route, '/api/stop', 'The standalone daemon is asked for its global stop');
assert.equal(page.dispatched.at(-1).type, 'piper-stop', 'STOP always tells the Pika panel');
// Escape is the same stop.
before = page.posts.length; let prevented = 0;
page.fire('document:keydown', { key: 'Escape', preventDefault() { prevented++; }, target: { tagName: 'BODY' } }); await page.settle();
assert.equal(page.posts.length, before + 1); assert.equal(stopBody().route, '/api/stop'); assert.equal(prevented, 1);
// STOP is still delivered, with the last known identity, when the server stopped answering state polls.
page.served = null; await page.poll();
assert.equal(page.dispatched.at(-1).detail, null);
before = page.posts.length; await page.press('stop');
assert.equal(page.posts.length, before + 1); assert.equal(stopBody().token, 't2');
// A restarted server refuses the old token: STOP re-reads the identity and is delivered once more.
page.served = serverState({ token: 't3', backend: 'dotnet-linux' });
page.answer = (route, body, token) => token === 't3' ? { ok: true, status: 200, json: async () => ({ ok: true, arm: { ok: true, message: JSON.stringify({ ok: true, message: 'STOP requested on every arm.' }) }, pika: { ok: false, message: 'No owned Pika session.' } }) } : { ok: false, status: 403, json: async () => ({ error: 'Forbidden' }) };
before = page.posts.length; await page.press('stop');
assert.deepEqual(page.posts.slice(before).map(post => [post.route, post.token]), [['/api/stop', 't2'], ['/api/stop/coordinated', 't3']], 'A refused STOP is retried against the current server');
stopBody();
assert.match(String(page.byId('message').textContent), /STOP requested on every arm\. Gripper hold: No owned Pika session\./);
// Served by the Host: the coordinated stop (arms and gripper).
await page.poll(); before = page.posts.length; await page.press('stop');
assert.deepEqual(page.posts.slice(before).map(post => post.route), ['/api/stop/coordinated']); stopBody();
// An arm stop the server could not confirm is reported as not confirmed.
page.answer = () => ({ ok: false, status: 409, json: async () => ({ ok: false, arm: { ok: false, message: 'CAN transmit failed.' }, pika: { ok: true, message: 'held' } }) });
await page.press('stop');
assert.match(String(page.byId('toast').textContent), /STOP was not confirmed: CAN transmit failed\..*physical stop/);

// STOP refuses an open prompt: the action it was asking about is never sent.
page.answer = () => ({ ok: true, status: 200, json: async () => ({ ok: true, message: 'ok' }) });
before = page.posts.length;
const enabling = page.byId('enable').onclick(); await page.settle();
assert.equal(page.open(), true);
await page.byId('stop').onclick(); await enabling; await page.settle();
assert.equal(page.open(), false, 'STOP dismisses the prompt');
assert.deepEqual(page.posts.slice(before).map(post => post.route), ['/api/stop/coordinated'], 'A prompt dismissed by STOP sends no command');

// A STOP pressed before the first state poll answered still finds the server.
page = boot(null); await page.settle();
page.served = serverState({ token: 'fresh' });
await page.press('stop');
assert.deepEqual(page.posts.map(post => [post.route, post.token, post.body]), [['/api/stop', 'fresh', { owner: page.app.window.piperPage.owner }]], 'STOP needs no earlier state poll');

// B19: STOP while a command is in flight must not let that reply re-create the lease.
page = boot(); await page.settle();
const deferred = id => { let finish; page.answer = route => route === '/api/command' ? new Promise(resolve => { finish = () => resolve({ ok: true, status: 200, json: async () => ({ message: 'moving', request_id: id }) }); }) : { ok: true, status: 200, json: async () => ({ message: 'stopped' }) }; return () => finish(); };
let finish = deferred(9);
let moving = page.byId('cycle').onclick(); await page.settle();
page.byId('safety-continue').onclick(); await page.settle();
assert.equal(page.read('lease.phase'), 'pending');
before = page.posts.length; await page.byId('cycle').onclick(); await page.settle();
assert.equal(page.posts.length, before, 'Only one command is in flight');
await page.byId('stop').onclick(); await page.settle();
assert.equal(page.posts.at(-1).route, '/api/stop');
assert.equal(page.read('lease.phase'), 'none');
finish(); await moving; await page.settle();
assert.equal(page.read('lease.phase'), 'none', 'A reply that arrives after STOP must not re-create the lease');
assert.deepEqual(page.posts.at(-1), { route: '/api/stop', body: { request_id: 9, owner: page.app.window.piperPage.owner, device_id: 'simulation:one' }, token: 't', keepalive: true }, 'The late request is released by a stop scoped to exactly that request');

// W6: leaving while the first request is still pending. The lease ends at once,
// and the request the server admitted afterwards is stopped when its reply names it.
for (const leave of ['window:blur', 'window:pagehide', 'document:visibilitychange']) {
  page = boot(); await page.settle();
  finish = deferred(41);
  moving = page.byId('cycle').onclick(); await page.settle();
  page.byId('safety-continue').onclick(); await page.settle();
  assert.equal(page.read('lease.phase'), 'pending');
  before = page.posts.length;
  page.app.document.hidden = leave === 'document:visibilitychange';
  page.fire(leave); await page.settle();
  assert.equal(page.read('lease.phase'), 'none', `${leave} ends a pending lease`);
  assert.equal(page.posts.length, before, 'Nothing can be released before the request has an id');
  finish(); await moving; await page.settle();
  assert.equal(page.read('lease.phase'), 'none', 'No lease is created after leaving');
  assert.deepEqual(page.posts.slice(before), [{ route: '/api/stop', body: { request_id: 41, owner: page.app.window.piperPage.owner, device_id: 'simulation:one' }, token: 't', keepalive: true }], `${leave}: the request admitted after leaving is stopped`);
}
// Leaving with a held lease releases it; a normal reply creates the lease.
page = boot(); await page.settle();
page.answer = () => ({ ok: true, status: 200, json: async () => ({ message: 'moving', request_id: 10 }) });
await page.press('cycle', true);
assert.deepEqual(JSON.parse(page.read('JSON.stringify(lease)')), { phase: 'leased', request_id: 10 });
page.fire('window:blur'); await page.settle();
assert.equal(page.read('lease.phase'), 'none');
assert.deepEqual(page.posts.at(-1).body, { request_id: 10, owner: page.app.window.piperPage.owner, device_id: 'simulation:one' });
console.log('Dashboard control tests passed: per-action confirmations and refusals, motion approval, recovery verification, unconditional STOP, lease release after leaving.');
