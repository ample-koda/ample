import { test } from 'node:test';
import assert from 'node:assert/strict';
import { velocityColor, shadeHsl } from '../pianoroll-theme.mjs';

test('velocityColor is cool at velocity 0 and hot at velocity 127, both valid hsl()', () => {
  const low = velocityColor(0), high = velocityColor(127);
  assert.match(low, /^hsl\(210, 85%, 42%\)$/);
  assert.match(high, /^hsl\(30, 85%, 56%\)$/);
});

test('velocityColor is monotonic in hue as velocity rises', () => {
  const hueOf = (v) => Number(/^hsl\((\d+)/.exec(velocityColor(v))[1]);
  let prev = hueOf(0);
  for (let v = 1; v <= 127; v++) { const h = hueOf(v); assert.ok(h <= prev, `hue should not rise with velocity (v=${v})`); prev = h; }
});

test('velocityColor clamps out-of-range input', () => {
  assert.equal(velocityColor(-5), velocityColor(0));
  assert.equal(velocityColor(999), velocityColor(127));
});

test('shadeHsl darkens and lightens, clamped to 0..100', () => {
  assert.equal(shadeHsl('hsl(30, 85%, 56%)', -18), 'hsl(30, 85%, 38%)');
  assert.equal(shadeHsl('hsl(30, 85%, 5%)', -18), 'hsl(30, 85%, 0%)');
  assert.equal(shadeHsl('hsl(30, 85%, 95%)', 18), 'hsl(30, 85%, 100%)');
});

test('shadeHsl returns non-hsl input unchanged', () => {
  assert.equal(shadeHsl('#e0a030', -18), '#e0a030');
});
