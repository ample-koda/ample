import test from 'node:test';
import assert from 'node:assert/strict';
import { fadeAmountAt, fadeable, formatFade, isFade, parseFade, shapeAmount } from '../fade-model.mjs';
import { isAutomation } from '../automation-model.mjs';

test('fades are read with bars and seconds and written back the same way', () => {
  const a = parseFade('fadeout(12s, 1.5s, equal)');
  assert.deepEqual([a.fadeIn, a.start.value, a.start.seconds, a.length.value, a.length.seconds, a.shape], [false, 12, true, 1.5, true, 'equal']);
  assert.equal(formatFade(a), 'fadeout(12s, 1.5s, equal)');
  const b = parseFade(' fadein( 5.5 , 2 ) ');
  assert.deepEqual([b.fadeIn, b.start.value, b.start.seconds, b.shape], [true, 5.5, false, 'lin']);
  assert.equal(formatFade(b), 'fadein(5.5, 2, lin)');
  assert.equal(formatFade({ fadeIn: true, start: { value: 5, seconds: false }, length: { value: 2, seconds: false }, shape: 'exp' }), 'fadein(5, 2, exp)');
});

test('what is not a fade is refused like the engine refuses it', () => {
  for (const bad of ['fadein(0, 2)', 'fadein(5)', 'fadein(5, 0)', 'fadein(5, 2, wobble)', 'fadein(5, 2, lin, 4)', 'fadeup(5, 2)', 'fadein(-1s, 2)', 'auto(1:0, 5:0)', '', null]) assert.equal(parseFade(bad), null, String(bad));
  assert.equal(parseFade('fadein(0s, 2)').start.seconds, true);   // seconds may start at the beginning
});

test('the shapes and the curve of a fade over time', () => {
  assert.equal(shapeAmount('lin', 0.5), 0.5);
  assert.ok(Math.abs(shapeAmount('equal', 0.5) ** 2 + shapeAmount('equal', 0.5) ** 2 - 1) < 1e-9);
  assert.equal(shapeAmount('exp', 0.5), 0.25);
  const out = parseFade('fadeout(3, 1, lin)');   // 2 seconds a bar: from 4 s to 6 s
  assert.equal(fadeAmountAt(out, 3.9, 2), 1);
  assert.equal(fadeAmountAt(out, 5, 2), 0.5);
  assert.equal(fadeAmountAt(out, 6.5, 2), 0);
  const inSeconds = parseFade('fadein(4s, 2s)');
  assert.equal(fadeAmountAt(inSeconds, 4, 2), 0);
  assert.equal(fadeAmountAt(inSeconds, 5, 2), 0.5);
  assert.equal(fadeAmountAt(inSeconds, 6, 2), 1);
});

test('where fades are offered and what counts as automation', () => {
  assert.equal(fadeable('volume', 'gain'), true);
  assert.equal(fadeable('reverb', 'mix'), true);
  assert.equal(fadeable('pan', 'pan'), false);
  assert.equal(fadeable('filter', 'cutoff'), false);
  assert.equal(isFade('fadeout(1, 2)'), true);
  assert.equal(isAutomation('fadein(1, 2)'), true);
  assert.equal(isAutomation('env(1, 2, 4, 2, -40, 0)'), true);
  assert.equal(isAutomation('-6 dB'), false);
});
