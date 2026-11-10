// W9 regression: the agent behind the Host is the only jaw authority. The
// dashboard opens no serial port on any platform, including Windows (where the
// WCH COM driver crashed) and Linux/macOS browsers that offer Web Serial.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const web = new URL('../web/', import.meta.url);
for (const file of ['pika.mjs', 'pika_remote.mjs', 'app.js', 'bench.mjs', 'index.html'])
  assert.doesNotMatch(readFileSync(new URL(file, web), 'utf8'), /navigator\.serial|requestPort|getPorts|Web Serial/i, `${file} must not use or offer Web Serial`);

for (const platform of ['Win32', 'Linux x86_64', 'MacIntel']) {
  const elements = new Map(), posts = []; let touched = 0, stateListener;
  const element = id => {
    if (!elements.has(id)) elements.set(id, { checked: false, disabled: false, textContent: '', addEventListener() {} });
    return elements.get(id);
  };
  globalThis.document = { hidden: false, hasFocus: () => true, getElementById: element, addEventListener() {} };
  globalThis.window = { piperPage: { owner: 'owner', token: () => 'fixture', ask: async () => true }, addEventListener(name, fn) { if (name === 'piper-state') stateListener = fn; } };
  globalThis.setInterval = () => {}; globalThis.setTimeout = () => {};
  globalThis.fetch = async (url, options) => { posts.push(url); return { ok: false, status: 409, json: async () => ({ ok: false, error: 'Pika hardware is not configured' }) }; };
  const serial = new Proxy({}, { get() { touched++; return async () => { throw Error('must not run'); }; } });
  Object.defineProperty(globalThis, 'navigator', { value: { platform, get serial() { touched++; return serial; } }, configurable: true });
  await import(`../web/pika.mjs?platform=${encodeURIComponent(platform)}`);
  // A Host without an assigned gripper, then the standalone daemon: no connect is possible in either.
  for (const detail of [{ simulated: false, backend: 'dotnet-windows', pika: { configured: false, transport: 'disabled' } }, { simulated: false }]) {
    stateListener({ detail });
    for (let i = 0; i < 20; i++) await Promise.resolve();
    assert.equal(element('pika-connect').disabled, true);
    await element('pika-connect').onclick();
    assert.match(element('pika-status').textContent, /Pika is unavailable/);
  }
  assert.equal(touched, 0, `${platform}: navigator.serial must never be touched, even by a direct handler call`);
  assert.equal(posts.some(url => url.includes('/connect')), false, 'An unassigned gripper is never connected');
}
console.log('Pika serial regression passed: the dashboard opens no serial port on any platform.');
