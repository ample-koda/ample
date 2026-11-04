import test from 'node:test';
import assert from 'node:assert/strict';
import { seamSpans, seamText } from '../seam-spans.mjs';

test('the joins are the stretches of the loop-region peaks that are not silent, in seconds', () => {
  const peaks = new Float32Array(20);   // 10 bins over 10 seconds
  peaks.set([-0.5, 0.5, -0.4, 0.4], 4);   // bins 2 and 3
  peaks.set([-0.3, 0.3], 14);   // bin 7
  const spans = seamSpans(peaks, 10);
  assert.deepEqual(spans, [{ from: 2, to: 4 }, { from: 7, to: 8 }]);
  assert.equal(seamText(spans), 'Loop seams (2): 2–4 s, 7–8 s');
  assert.deepEqual(seamSpans(new Float32Array(20), 10), []);
  assert.equal(seamText([]), '');
});
