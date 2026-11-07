import assert from 'node:assert/strict';
const elements = new Map(); let attempted = 0;
const element = id => {
  if (!elements.has(id)) elements.set(id, { checked: false, disabled: false, textContent: '', addEventListener() {} });
  return elements.get(id);
};
globalThis.document = { hidden: false, hasFocus: () => true, getElementById: element, addEventListener() {} };
globalThis.window = { addEventListener() {} };
globalThis.setInterval = () => {}; globalThis.setTimeout = () => {};
Object.defineProperty(globalThis, 'navigator', { value: { platform: 'Win32', serial: { async requestPort() { attempted++; throw Error('must not run'); } } }, configurable: true });
globalThis.fetch = async () => ({ ok: true, json: async () => ({ simulated: false, token: 'fixture', pika: { configured: false, transport: 'disabled' } }) });
await import('../web/pika.mjs');
for (let i = 0; i < 20; i++) await Promise.resolve();
assert.equal(element('pika-connect').disabled, true);
await element('pika-connect').onclick();
assert.equal(attempted, 0, 'Windows WCH COM must never be opened, even by a direct handler call');
assert.match(element('pika-status').textContent, /Windows WCH serial is disabled/);
console.log('Windows Pika regression passed: crash-prone browser COM path remains blocked.');
