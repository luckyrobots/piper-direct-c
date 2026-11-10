import assert from 'node:assert/strict';
import { packet, widthFromAngle, angleFromWidth, JsonStream } from '../web/pika.mjs';
assert.deepEqual([...packet(11)], [11, 0, 0, 0, 0, 13, 10]);
assert.deepEqual([...packet(22, 1)], [22, 0, 0, 128, 63, 13, 10]);
assert.throws(() => packet(12)); // No zero/calibration command exposed.
assert.throws(() => packet(22, NaN));
for (const w of [0, 1, 10, 30, 60, 90]) assert.ok(Math.abs(widthFromAngle(angleFromWidth(w)) - w) < .001);
for (const w of [-1, 91, NaN, Infinity]) assert.throws(() => angleFromWidth(w));
const parser = new JsonStream();
assert.deepEqual(parser.push('noise {"motor": {"Position":'), []);
assert.deepEqual(parser.push('1,},"motorstatus":{},}{"x":"brace } and ,}"}'), [{motor:{Position:1},motorstatus:{}},{x:'brace } and ,}'}]);
assert.deepEqual(parser.push('{bad}{"ok":true}'), [{ok:true}]);
parser.push('{'.repeat(20000)); assert.equal(parser.buffer.length, 0);
console.log('Pika codec, linkage inversion, and fragmented telemetry tests passed');
