import test from 'node:test';
import assert from 'node:assert/strict';
import { gainAtSeconds } from '../live-auto.mjs';

const bar = 60 / 84 * 4;
test('auto with seconds joins the points by straight lines and holds the ends', () => {
  const t = 'auto(0s:0, 238s:0, 241s:-18)';
  assert.equal(gainAtSeconds(t, 0, bar), 0); assert.equal(gainAtSeconds(t, 100, bar), 0);
  assert.ok(Math.abs(gainAtSeconds(t, 239.5, bar) + 9) < 1e-9); assert.equal(gainAtSeconds(t, 250, bar), -18);
});
test('auto with bars: bar 1 is the start', () => {
  assert.ok(Math.abs(gainAtSeconds('auto(1:-10, 3:0)', bar, bar) + 5) < 1e-9);
});
test('fadein and fadeout follow the server shapes', () => {
  assert.ok(Math.abs(gainAtSeconds('fadein(5.714286s, 0.8s, lin)', 6.114286, bar) - 20 * Math.log10(.5)) < 1e-6);
  assert.equal(gainAtSeconds('fadein(5.714286s, 0.8s, lin)', 100, bar), 0);
  assert.ok(gainAtSeconds('fadein(5.714286s, 0.8s, lin)', 1, bar) < -100);
  assert.ok(Math.abs(gainAtSeconds('fadeout(10s, 2s, equal)', 11, bar) - 20 * Math.log10(Math.sqrt(.5))) < 1e-6);
  assert.ok(Math.abs(gainAtSeconds('fadein(2, 1, exp)', bar * 1.5, bar) - 20 * Math.log10(.25)) < 1e-6);
});
test('anything else is left to the caller', () => {
  for (const text of ['-6 dB', '', 'lfo(1, 2)', 'auto()', 'auto(x:1)', 'fadein(5)']) assert.equal(gainAtSeconds(text, 1, bar), null, text);
});
