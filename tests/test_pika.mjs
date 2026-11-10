import assert from 'node:assert/strict';
import * as pika from '../web/pika.mjs';
const { widthFromAngle, angleFromWidth, arrivalToleranceMm } = pika;
for (const w of [0, 1, 10, 30, 60, 90]) assert.ok(Math.abs(widthFromAngle(angleFromWidth(w)) - w) < .001);
for (const w of [-1, 91, NaN, Infinity]) assert.throws(() => angleFromWidth(w));
assert.equal(arrivalToleranceMm, 1);
// The agent owns the serial protocol (Q5): the page exports no packet codec or stream parser.
assert.deepEqual(Object.keys(pika).sort(), ['angleFromWidth', 'arrivalToleranceMm', 'widthFromAngle']);
console.log('Pika linkage inversion tests passed');
