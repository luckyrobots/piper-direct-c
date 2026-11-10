// The generated dashboard client (web/api.mjs) against the generated description the
// servers serve (web/openapi.json). No server: the fetcher is a recorder.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createApi, reply } from '../web/api.mjs';

const description = JSON.parse(readFileSync(new URL('../web/openapi.json', import.meta.url), 'utf8'));
const definition = { routes: Object.entries(description.paths).flatMap(([path, item]) => ['get', 'post'].filter(method => item[method]).map(method => ({
  id: item[method].operationId, path, class: item[method]['x-piper-class'],
  examples: Object.values(item[method].requestBody?.content['application/json'].examples ?? {}).map(example => ({ body: example.value })),
}))) };
const sent = [];
const api = createApi({ token: () => 'page-token', fetcher: async (route, init) => { sent.push({ route, init }); return { ok: true, status: 200, json: async () => ({ ok: true }) }; } });
const last = () => sent.at(-1);

// One operation per route, and nothing else.
assert.deepEqual(Object.keys(api).sort(), definition.routes.map(route => route.id).sort());
assert.ok(Object.isFrozen(api));

// Every example the definition gives is a request the client builds, byte for byte,
// to that route with the page's token.
let examples = 0;
for (const route of definition.routes) for (const example of route.examples ?? []) {
  await api[route.id](example.body);
  assert.equal(last().route, route.path);
  assert.equal(last().init.method, 'POST');
  assert.equal(last().init.body, JSON.stringify(example.body));
  assert.deepEqual(last().init.headers, { 'Content-Type': 'application/json', 'X-Piper-Token': 'page-token' });
  ++examples;
}
assert.ok(examples >= 20);

// A control cannot carry a field its route does not define; nothing is sent.
const before = sent.length;
for (const route of definition.routes.filter(route => route.class !== 'stop'))
  await assert.rejects(() => api[route.id]({ not_a_field: 1 }), TypeError, route.id);
assert.equal(sent.length, before);

// STOP is never refused by the client: it sends exactly what it is given, whatever that is.
for (const route of definition.routes.filter(route => route.class === 'stop')) {
  await api[route.id]({ owner: 'page' }, { keepalive: true, timeout: 5000, token: 'older-token' });
  assert.equal(last().route, route.path);
  assert.equal(last().init.body, '{"owner":"page"}');
  assert.equal(last().init.keepalive, true);
  assert.equal(last().init.headers['X-Piper-Token'], 'older-token');
  assert.ok(last().init.signal instanceof AbortSignal);
  await api[route.id]({ not_a_field: 1, device_id: undefined });
  assert.equal(last().init.body, '{"not_a_field":1}');
  await api[route.id]();
  assert.equal(last().init.body, '{}');
  assert.equal('keepalive' in last().init, false);
  assert.equal('signal' in last().init, false);
}
assert.deepEqual(definition.routes.filter(route => route.class === 'stop').map(route => route.id), ['stop', 'coordinatedStop']);

// A read sends its parameters as a query and leaves out the ones that are absent or null.
await api.state({}, { timeout: 1000 });
assert.equal(last().route, '/api/state');
assert.equal(last().init.method, undefined);
assert.equal(last().init.cache, 'no-store');
await api.pikaState({ pika_device: '/dev/ttyUSB0', pika_distribution: null });
assert.equal(last().route, '/api/pika/state?pika_device=%2Fdev%2FttyUSB0');
await api.pikaState({ pika_device: 'COM7', pika_distribution: 'Ubuntu' });
assert.equal(last().route, '/api/pika/state?pika_device=COM7&pika_distribution=Ubuntu');
await assert.rejects(() => api.pikaState({ device: 'COM7' }), TypeError);

// reply() answers a success and throws a refusal's own message.
assert.deepEqual(await reply({ ok: true, json: async () => ({ ok: true, n: 1 }) }), { ok: true, n: 1 });
await assert.rejects(() => reply({ ok: false, json: async () => ({ error: 'Lease expired.' }) }), /Lease expired\./);
await assert.rejects(() => reply({ ok: false, json: async () => ({}) }, 'No answer.'), /No answer\./);

console.log(`Generated API client passed: ${definition.routes.length} operations, ${examples} examples built byte for byte; STOP unchecked; hardware disabled.`);
