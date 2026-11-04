import test from 'node:test';
import assert from 'node:assert/strict';
import { RANGES, isNumeric, rangeOf, readNumber, sliderPlacement } from '../numfield-model.js';

test('what is typed is read as a number, with a comma too, and moved into the range', () => {
  assert.deepEqual(readNumber('12', [0, 60, 1]), { value: 12, text: '12', clamped: false, error: null });
  assert.equal(readNumber('7,5', null).value, 7.5);
  assert.equal(readNumber(' -3 ', [-24, 12, 1]).value, -3);
  const high = readNumber('99', [0, 60, 1]);
  assert.equal(high.value, 60);
  assert.equal(high.clamped, true);
  assert.equal(readNumber('-5', [0, 60, 1]).value, 0);
  assert.equal(readNumber('0', [0, 60, 1]).clamped, false);
});

test('anything that is not a number is an error with a plain sentence and no value', () => {
  for (const bad of ['', '  ', 'abc', '1e3', '--2', '1.2.3', null, undefined]) {
    const r = readNumber(bad, [0, 10, 1]);
    assert.equal(r.value, null);
    assert.match(r.error, /is not a number/);
  }
});

test('ranges come from the template when it says so, else from the table; other parameters stay plain fields', () => {
  assert.deepEqual(rangeOf('swing'), [0, 60, 1]);
  assert.deepEqual(rangeOf('anything', { min: 2, max: 9 }), [2, 9, 1]);
  assert.deepEqual(rangeOf('swing', { min: 5, max: 6, step: 0.5 }), [5, 6, 0.5]);
  assert.equal(rangeOf('voice'), null);
  assert.equal(isNumeric('humanize'), true);
  assert.equal(isNumeric('bars', { type: 'number' }), true);
  assert.equal(isNumeric('kit'), false);
  assert.equal(isNumeric('volume', { type: 'number' }), true);
  assert.ok(Object.values(RANGES).every(([min, max, step]) => min < max && step > 0));
});

test('floating sliders open above only when the panel has no room below', () => {
  const panel = { top: 0, bottom: 500 };
  assert.equal(sliderPlacement({ top: 430, bottom: 470 }, panel, 48), 'above');
  assert.equal(sliderPlacement({ top: 10, bottom: 40 }, panel, 48), 'below');
  assert.equal(sliderPlacement({ top: 460, bottom: 490 }, { top: 450, bottom: 500 }, 48), 'below');
});
