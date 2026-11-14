import test from 'node:test';
import assert from 'node:assert/strict';
import { songSeconds } from '../song-position.mjs';

test('a clip placed at bar 9 starts 16 seconds into a 4/4 song at 120 (issue 242)', () => {
  assert.equal(songSeconds({ placedAtBar: 9, localBeats: 0, beatsPerBar: 4, bpm: 120 }), 16);
});

test('the place inside the clip is added', () => {
  assert.equal(songSeconds({ placedAtBar: 9, localBeats: 4, beatsPerBar: 4, bpm: 120 }), 18);
});

test('the meter of the song decides the length of a bar', () => {
  assert.equal(songSeconds({ placedAtBar: 3, localBeats: 0, beatsPerBar: 3, bpm: 120 }), 3);
});

test('a clip at bar 1 and a missing placement start at the clip place', () => {
  assert.equal(songSeconds({ localBeats: 2, bpm: 120 }), 1);
  assert.equal(songSeconds({ placedAtBar: 0, localBeats: -3, bpm: 120 }), 0);
});
