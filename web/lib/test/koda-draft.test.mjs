import test from 'node:test';
import assert from 'node:assert/strict';
import { addedBlocks, ghostSpots } from '../koda-draft.mjs';

const diff = [
  '--- a/song.amplet', '+++ b/song.amplet', '@@ -10,2 +10,12 @@',
  ' mixer mix {',
  '+reverb room at 1 for 8 {',
  '+  from keys.audio -> in',
  '+}',
  '+delay echo {',
  '+  from room.out -> in',
  '+  time 250 ms',
  '+}',
  '   gains 0 0',
  '+  from keys.audio -> in3',
].join('\n');

test('the blocks a diff adds, with what they read from; an added line inside an old block is no block', () => {
  assert.deepEqual(addedBlocks(diff), [
    { type: 'reverb', name: 'room', from: [{ block: 'keys', output: 'audio', input: 'in' }] },
    { type: 'delay', name: 'echo', from: [{ block: 'room', output: 'out', input: 'in' }] },
  ]);
  assert.deepEqual(addedBlocks(''), []);
});

test('a ghost goes to the right of the block it reads from, the next one under the first, and one without a source in a free corner', () => {
  const nodes = [{ id: 'keys', x: 100, y: 50, w: 300, h: 90 }];
  const spots = ghostSpots([
    { type: 'reverb', name: 'a', from: [{ block: 'keys', output: 'audio', input: 'in' }] },
    { type: 'delay', name: 'b', from: [{ block: 'keys', output: 'audio', input: 'in' }] },
    { type: 'sampler', name: 'c', from: [] },
  ], nodes);
  assert.deepEqual([spots[0].x, spots[0].y], [460, 50]);
  assert.deepEqual([spots[1].x, spots[1].y], [460, 160]);
  assert.equal(spots[2].anchor, null);
});
