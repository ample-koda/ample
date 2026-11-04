import test from 'node:test';
import assert from 'node:assert/strict';
import { seamWindows, sumChannels } from '../seam-windows.mjs';

test('the joins of a region are the stretches that are not silent', () => {
  const rate = 1000;
  const a = new Float32Array(10000);
  for (let i = 1000; i < 1500; i++) a[i] = 0.5;
  for (let i = 4000; i < 4600; i++) a[i] = -0.4;
  for (let i = 4650; i < 4700; i++) a[i] = 0.3;   // a short gap (50 ms) is bridged
  assert.deepEqual(seamWindows([a], rate), [{ from: 1000, to: 1500 }, { from: 4000, to: 4700 }]);
  assert.deepEqual(seamWindows([new Float32Array(500)], rate), []);
  assert.deepEqual(seamWindows([], rate), []);
});

test('two sets of channels are added, the shorter one padded with silence', () => {
  const [l] = sumChannels([Float32Array.from([1, 1, 1])], [Float32Array.from([0.5])]);
  assert.deepEqual([...l], [1.5, 1, 1]);
});
