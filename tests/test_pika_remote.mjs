// The Host Pika client (web/pika_remote.mjs): what it sends, and what it never sends by itself.
import assert from 'node:assert/strict';
import { PikaClient } from '../web/pika_remote.mjs';
const calls = [];
let token = 'fixture-token', failure = null;
const fetcher = async function (url, options = {}) {
  assert.equal(this, undefined, 'fetch must not receive the client as its receiver');
  const body = options.body ? JSON.parse(options.body) : null;
  calls.push({ url, body, keepalive: options.keepalive === true, method: options.method || 'GET' });
  if (url.startsWith('/api/pika/state')) return { ok: true, json: async () => ({ owner: 'owner-1', connected: true }) };
  assert.equal(options.headers['X-Piper-Token'], token);
  if (failure) return { ok: false, json: async () => ({ ok: false, error: failure }) };
  if (url.endsWith('/connect')) return { ok: true, json: async () => ({ ok: true, device: '/dev/serial/by-id/pika-A', distribution: null }) };
  return { ok: true, json: async () => ({ ok: true }) };
};
const client = new PikaClient({ owner: 'owner-1', token: () => token, fetcher });
const last = () => calls.at(-1);

// Before a session exists the client addresses the Host's default assignment.
await client.snapshot();
assert.equal(last().url, '/api/pika/state');
await client.disable();
assert.deepEqual(last().body, { owner: 'owner-1' });
assert.equal(last().keepalive, true, 'A disable must outlive the page');

// W2: connect sends take_over only when its caller asks for it.
await client.connect();
assert.equal(last().url, '/api/pika/connect');
assert.deepEqual(last().body, { owner: 'owner-1' }, 'A plain connect never takes over and sends no motor command');
assert.deepEqual(client.gripper, { device: '/dev/serial/by-id/pika-A', distribution: null });
await client.connect({ takeOver: true });
assert.equal(last().body.take_over, true);
assert.equal(calls.filter(call => call.url.endsWith('/connect')).length, 2, 'The client never retries');

// Once bound, every request names that gripper explicitly (null distribution = native Linux).
const bound = { owner: 'owner-1', pika_device: '/dev/serial/by-id/pika-A', pika_distribution: null };
await client.enable();
assert.deepEqual(last(), { url: '/api/pika/enable', body: { ...bound, clear: true }, keepalive: false, method: 'POST' });
await client.position(.6);
assert.deepEqual(last().body, { ...bound, value: .6 });
assert.throws(() => client.position(NaN), /Invalid Pika value/);
await client.heartbeat();
assert.deepEqual(last().body, bound);
await client.snapshot();
assert.equal(last().url, '/api/pika/state?pika_device=%2Fdev%2Fserial%2Fby-id%2Fpika-A');

// The token is read per request, so a restarted Host's new token is used.
token = 'next-token'; await client.heartbeat();

// A refusal is an error carrying the Host's reason; nothing is retried.
failure = 'This dashboard does not own the Pika session';
const before = calls.length;
await assert.rejects(client.enable(), /does not own the Pika session/);
assert.equal(calls.length, before + 1);
failure = null;

await client.disconnect();
assert.deepEqual(last(), { url: '/api/pika/disconnect', body: bound, keepalive: true, method: 'POST' });
assert.equal(client.gripper, null);
await client.connect(); client.forget();
assert.equal(client.gripper, null);
console.log('Pika Host client passed: explicit take-over only, gripper binding, token, refusal, disable and disconnect.');
