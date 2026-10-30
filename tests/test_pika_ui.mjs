import assert from 'node:assert/strict';
const elements = new Map(), intervals = [], events = new Map(), writes = [];
let now = 1000, simulated = false, serialRequests = 0, serialInput, closed = false, approved = false;
const element = id => {
  if (!elements.has(id)) elements.set(id, { checked:false, disabled:false, textContent:'', addEventListener(name, fn) { events.set(`${id}:${name}`, fn); } });
  return elements.get(id);
};
globalThis.document = { hidden:false, hasFocus:()=>true, getElementById:element, addEventListener(name,fn) { events.set(`document:${name}`,fn); } };
globalThis.window = { confirmPiperControl:()=>approved, addEventListener(name,fn) { events.set(`window:${name}`,fn); } };
Object.defineProperty(globalThis, 'performance', { value:{now:()=>now}, configurable:true });
globalThis.setInterval = fn => { intervals.push(fn); };
globalThis.setTimeout = () => {};
globalThis.fetch = async () => ({ok:true,json:async()=>({simulated})});
const port = {
  readable:new ReadableStream({start(c) { serialInput = c; }}),
  writable:new WritableStream({write(bytes) { writes.push([...bytes]); }}),
  async open(options) { assert.equal(options.baudRate,460800); },
  async close() { closed = true; }
};
Object.defineProperty(globalThis, 'navigator', { value:{serial:{async requestPort() { serialRequests++; return port; }}}, configurable:true });
const { angleFromWidth } = await import('../web/pika.mjs');
const settle = async () => { for (let i=0;i<20;i++) await Promise.resolve(); };
await settle();
await element('pika-connect').onclick();
assert.equal(serialRequests,1); assert.equal(writes.length,0,'Connecting must not enable or move');
assert.equal(element('pika-enable').disabled,true,'Missing telemetry blocks enable');
serialInput.enqueue(new TextEncoder().encode('{"motor":{"Position":0.5},"motorstatus":{"Status":"0x0"}}'));
await settle();
await element('pika-enable').onclick();
assert.equal(writes.length,0,'Missing operator acknowledgment blocks enable');
approved=true;
await element('pika-enable').onclick();
assert.equal(writes.at(-1)[0],11);
await element('pika-open').onclick();
assert.equal(writes.at(-1)[0],22);
assert.match(element('pika-status').textContent,/Requested/);
let target = Number(element('pika-status').textContent.match(/Requested ([\d.]+)/)[1]);
const report = width => serialInput.enqueue(new TextEncoder().encode(JSON.stringify({motor:{Position:angleFromWidth(width),Speed:0},motorstatus:{Status:'0x40'}})));
report(target-.42); await settle(); now += 200; report(target-.42); await settle();
assert.match(element('pika-status').textContent,/within 1.0 mm/,'Observed 0.42 mm settling offset must not falsely time out');
element('pika-step').value='5';
await element('pika-open').onclick();
target = Number(element('pika-status').textContent.match(/Requested ([\d.]+)/)[1]);
report(target-.42); await settle(); now += 200; report(target-.42); await settle();
assert.match(element('pika-status').textContent,/within 1.0 mm/);
const count = writes.length;
element('pika-slider').value='80';
await element('pika-slider').onchange();
assert.equal(writes.length,count+1,'Slider sends one absolute target, not a sequence of 5 mm clicks');
const angle80=new DataView(Uint8Array.from(writes.at(-1)).buffer).getFloat32(1,true);
assert.ok(Math.abs(angle80-angleFromWidth(80))<.000001,'Slider target must be 80 mm regardless of current opening');
report(80);await settle();now+=200;report(80);await settle();
const afterSlider=writes.length;
now += 2100;
await element('pika-open').onclick();
assert.equal(writes.length,afterSlider+1,'Stale telemetry must disable an armed gripper');
assert.equal(writes.at(-1)[0],10,'Stale telemetry must not send a move');
// Re-enable with fresh data, then stale feedback must request disable.
serialInput.enqueue(new TextEncoder().encode('{"motor":{"Position":0.5},"motorstatus":{}}'));
await settle(); await element('pika-enable').onclick(); now += 2100;
for (const fn of intervals) fn(); await settle();
assert.equal(writes.at(-1)[0],10,'Stale feedback requests disable');
await element('pika-disconnect').onclick();
assert.equal(closed,true);
assert.equal(element('pika-open').disabled,true);
console.log('Pika UI lifecycle passed: read-only connect, missing feedback/ack guards, explicit enable, bounded step, stale rejection/disable, disconnect');
