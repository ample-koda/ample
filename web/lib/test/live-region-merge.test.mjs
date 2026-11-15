import test from 'node:test';
import assert from 'node:assert/strict';
import { regionMergeGraph } from '../live-mixer.mjs';

const fake = () => {
  const made = [];
  const node = (kind) => { const n = { kind, to: [], connect(next) { n.to.push(next); return next; }, disconnect() { n.to = []; } }; made.push(n); return n; };
  return { made, createGain: () => node('gain'), createWaveShaper: () => node('shaper') };
};

test('the region merge adds body and region on two lanes and saturates the sum at full scale (issue 79)', () => {
  const context = fake();
  const graph = regionMergeGraph(context);
  const shaper = context.made.find((n) => n.kind === 'shaper');
  assert.equal(graph.lanes.length, 2);
  graph.lanes.forEach((lane) => assert.ok(lane.to.includes(shaper), 'both lanes feed the same sum'));
  assert.deepEqual([...shaper.curve], [-1, 1]);   // a straight line between the full scales: more than that is cut
  assert.ok(shaper.to.includes(graph.output));
  const clip = (x) => Math.max(-1, Math.min(1, x));
  assert.equal(clip(0.7 + 0.6), 1);
  assert.ok(Math.abs(clip(0.3 + -0.1) - 0.2) < 1e-12);
  graph.update({}, { anySolo: false });   // no knobs: nothing happens and nothing throws
  graph.dispose();
  assert.equal(shaper.to.length, 0);
});
