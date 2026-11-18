import test from 'node:test';
import assert from 'node:assert/strict';
import { activeBlocks, barAt, loopOf, placedBlocks, statusText } from '../listen-remix.mjs';

const song = `composition "x" { ample 1  tempo 120  master-seed 7 }
midi-clip theme at 1 for 4 {
  notes 1:0:1:60:100
}
midi-clip answer at 5 for 2 {
  notes 1:0:1:64:100
}
drums beat at 3 {
  pattern x...
}
synth lead {
  from theme.notes -> notes
}
`;

test('the placed blocks are found with their bar and length; blocks that are not on the timeline are not', () => {
  assert.deepEqual(placedBlocks(song), [
    { name: 'theme', type: 'midi-clip', at: 1, bars: 4 },
    { name: 'answer', type: 'midi-clip', at: 5, bars: 2 },
    { name: 'beat', type: 'drums', at: 3, bars: null },
  ]);
});

test('the active blocks of a bar: inside their bars, and a block without a length from its bar on', () => {
  assert.deepEqual(activeBlocks(song, 1), ['theme']);
  assert.deepEqual(activeBlocks(song, 3), ['theme', 'beat']);
  assert.deepEqual(activeBlocks(song, 5), ['answer', 'beat']);
  assert.deepEqual(activeBlocks(song, 7), ['beat']);
});

test('the loop of the whole song, and of a range of bars cut from it', () => {
  assert.deepEqual(loopOf(16, 2, { kind: 'song' }), { segments: [{ from: 0, to: 16 }], loop: { start: 0, end: 16 }, firstBar: 1 });
  assert.deepEqual(loopOf(16, 2, { kind: 'bars', firstBar: 3, lastBar: 4 }), { segments: [{ from: 4, to: 8 }], loop: { start: 4, end: 8 }, firstBar: 3 });
});

test('a range past the end is cut at the end, and an empty or unreadable one is refused', () => {
  assert.deepEqual(loopOf(16, 2, { kind: 'bars', firstBar: 7, lastBar: 20 }).loop, { start: 12, end: 16 });
  assert.equal(loopOf(16, 2, { kind: 'bars', firstBar: 9, lastBar: 9 }), null);
  assert.equal(loopOf(0, 2, { kind: 'song' }), null);
  assert.equal(loopOf(16, 0, { kind: 'song' }), null);
  assert.deepEqual(loopOf(16, 2, { kind: 'bars', firstBar: 4, lastBar: 2 }).loop, { start: 6, end: 8 }, 'last before first is one bar');
});

test('the bar at a time of the render is a fraction of a bar counted from 1', () => {
  assert.equal(barAt(0, 2), 1);
  assert.equal(barAt(5, 2), 3.5);
});

test('the status line says what the session does, never "building #null"', () => {
  const s = (over) => ({ active: true, status: 'idle', building: null, playing: null, error: null, ...over });
  assert.equal(statusText(s({ active: false, status: 'stopped' })), 'Stopped');
  assert.equal(statusText(s({ active: false })), '');
  assert.equal(statusText(s({ status: 'building', building: 3 })), 'building #3...');
  assert.equal(statusText(s({ status: 'building', building: null })), 'building...');
  assert.equal(statusText(s({ status: 'building', building: 2, playing: 1 })), 'building #2... (you hear an older version)');
  assert.equal(statusText(s({ playing: 2 }), { job: { kind: 'bars', firstBar: 3, lastBar: 4 } }), 'hearing build #2 (bars 3-4)');
  assert.equal(statusText(s({ playing: 2 })), 'hearing build #2');
  assert.equal(statusText(s({ status: 'error', error: 'AMP1 broken', playing: 1 })), 'AMP1 broken: you still hear the last good build');
  assert.equal(statusText(s({ status: 'error', error: 'AMP1 broken' })), 'AMP1 broken');
  assert.match(statusText(s(), { waitSeconds: 5 }), /^next build in 5 s/);
  assert.equal(statusText(s(), { loading: true }), 'loading the sound...');
  assert.equal(statusText(s()), 'listening: builds when you stop typing');
});

test('a clip placed with `for auto` is a placed block that sounds from its bar on (its place is where its notes are marked)', () => {
  const text = 'midi-clip high at 5 for auto {\n  notes theme.notes |> transpose(+12)\n}\n';
  assert.deepEqual(placedBlocks(text), [{ name: 'high', type: 'midi-clip', at: 5, bars: null }]);
});
