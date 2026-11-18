import test from 'node:test';
import assert from 'node:assert/strict';
import { clipFirstBarOf, frameOf, litAt, songBarAt, timingSnapshot } from '../listen-timing.mjs';

const graph = {
  nodes: [
    { id: 'theme', parts: [{ at: 1, length: 2 }], ins: [], outs: ['notes'] },
    { id: 'lead', parts: [], ins: ['notes'], outs: ['audio'] },
  ],
  links: [{ from: 'theme', to: 'lead', outName: 'notes', inName: 'notes' }],
};
const timingOf = (id) => (id === 'theme' ? { placed: true } : { placed: false, origin: 3 });

test('the snapshot keeps the tempo, the meter, the frames and the clip bars of the build', () => {
  const snap = timingSnapshot({ tempo: 120, quarters: 3, graph, timingOf });
  assert.equal(snap.barSeconds, 1.5);
  assert.deepEqual(frameOf(snap, 'theme'), { first: 1, offset: 0 });
  assert.deepEqual(frameOf(snap, 'lead'), { first: 1, offset: 2 });
  assert.equal(clipFirstBarOf(snap, 'theme'), 1);
  assert.equal(clipFirstBarOf(snap, 'nothing'), 1);
});

test('the bar of the sound follows the tempo of the build, whatever the song says later', () => {
  const old = timingSnapshot({ tempo: 120, quarters: 4, graph, timingOf });
  const result = { takeFirstBar: 1, sliceOffset: 0, timing: old };
  assert.equal(songBarAt(result, 4), 3, 'two seconds a bar: 4 s is bar 3');
  const later = timingSnapshot({ tempo: 60, quarters: 4, graph, timingOf });
  assert.equal(songBarAt({ ...result, timing: later }, 4), 2, 'the same time is bar 2 at the new tempo: marks of the old sound must not use it');
  assert.equal(songBarAt({ takeFirstBar: 3, sliceOffset: 2, timing: old }, 2), 5, 'a slice starts at its own bar and offset');
});

test('the blocks that sound in a bar come from the graph of the build, and after the end the last bar counts', () => {
  const snap = timingSnapshot({ tempo: 120, quarters: 4, graph, timingOf });
  assert.ok(litAt(snap, 1).includes('theme'));
  assert.deepEqual(litAt(snap, 99), litAt(snap, snap.lit.length));
  const moved = { nodes: [{ ...graph.nodes[0], parts: [{ at: 5, length: 2 }] }, graph.nodes[1]], links: graph.links };
  const later = timingSnapshot({ tempo: 120, quarters: 4, graph: moved, timingOf });
  assert.ok(litAt(snap, 1).includes('theme') && !litAt(later, 1).includes('theme'), 'a clip that moved later is not sounding in bar 1 of the new text; the old sound still has it');
});
