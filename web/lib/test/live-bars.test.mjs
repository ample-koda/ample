import test from 'node:test';
import assert from 'node:assert/strict';
import { fitLiveBars, liveBarsNote, LIVE_BYTES, LIVE_BYTES_PER_FRAME } from '../live-bars.mjs';
import { synthWorkingSetBytes } from '../clientside-synth.mjs';

const per = (bpm, rate = 44100) => Math.floor(4 * 60 * rate / bpm);
test('short songs play whole', () => { assert.equal(fitLiveBars(32, per(120)), 32); assert.equal(liveBarsNote(32, 32), ''); });
test('long songs fit the synth working set and say so', () => {
  const p = per(120), bars = fitLiveBars(400, p);
  assert.ok(bars >= 1 && bars < 400);
  assert.ok(synthWorkingSetBytes(bars * p, 2) <= 128 * 1024 * 1024);
  assert.equal(liveBarsNote(400, bars), `previewing bars 1-${bars} of 400`);
  assert.equal(LIVE_BYTES_PER_FRAME, synthWorkingSetBytes(1, 2));
  assert.ok(LIVE_BYTES < 128 * 1024 * 1024);
});
test('never fewer than one bar', () => { assert.equal(fitLiveBars(5, 1e9), 1); });

// The window of a long song (the live chain follows the loop range of the chain bar)
import { windowStart, barAt, songSeconds, windowNotes } from '../live-bars.mjs';
test('a song that fits has no window: it starts at bar 1 whatever was asked', () => {
  assert.equal(windowStart(32, 32, 20), 1);
  assert.equal(windowStart(8, 8, 5), 1);
  assert.equal(liveBarsNote(32, 32, 1), '');
});
test('the window starts at the wanted bar and always stays full inside the song', () => {
  assert.equal(windowStart(120, 49, 41), 41);
  assert.equal(windowStart(120, 49, 1), 1);
  assert.equal(windowStart(120, 49, 100), 72);   // 120 - 49 + 1: the last full window
  assert.equal(windowStart(120, 49, 0), 1);
  assert.equal(windowStart(120, 49, -7), 1);
  assert.equal(windowStart(120, 49, 'x'), 1);
  assert.equal(windowStart(120, 49, 41.9), 41);
});
test('the badge names the bars of the window', () => {
  assert.equal(liveBarsNote(120, 49, 41), 'previewing bars 41-89 of 120');
  assert.equal(liveBarsNote(120, 49), 'previewing bars 1-49 of 120');
  assert.equal(liveBarsNote(120, 49, windowStart(120, 49, 200)), 'previewing bars 72-120 of 120');
});
test('a position of the window is a bar and a time of the song', () => {
  assert.equal(barAt(41, 0, 2), 41);
  assert.equal(barAt(41, 3.99, 2), 42);
  assert.equal(barAt(41, 4, 2), 43);
  assert.equal(barAt(1, 5, 2), 3);
  assert.equal(songSeconds(41, 1.5, 2), 81.5);
  assert.equal(songSeconds(1, 1.5, 2), 1.5);
});
test('midi seconds are cut to the window and moved to its start; the first window is untouched', () => {
  const notes = [{ start: 0, end: 3, pitch: 60 }, { start: 5, end: 7, pitch: 62 }, { start: 9, end: 12, pitch: 64 }, { start: 20, end: 21, pitch: 65 }];
  assert.equal(windowNotes(notes, 1, 4, 2), notes);
  // window = bars 3-6 of 2 s bars: seconds 4 to 12
  assert.deepEqual(windowNotes(notes, 3, 4, 2).map((n) => [n.pitch, n.start, n.end]), [[62, 1, 3], [64, 5, 8]]);
  assert.deepEqual(windowNotes([{ start: 3, end: 6, pitch: 1 }], 3, 4, 2).map((n) => [n.start, n.end]), [[0, 2]]);   // a note that began before the window sounds from its start
});
