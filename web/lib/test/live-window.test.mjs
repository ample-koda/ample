// The live renders of a window of a long song (bar offset) are the same bars as a render of the whole song: the libraries the live chain calls take the first bar of the window.
import test from 'node:test';
import assert from 'node:assert/strict';
import { drumHits } from '../drum-core.mjs';
import { sequencePreviewPlan } from '../clientside-sequencer.mjs';
import { fireNotes } from '../control-follow.mjs';
import { clickAudio } from '../clientside-click.mjs';
import { curveLevel } from '../control-live.mjs';
import { gainAtSeconds } from '../live-auto.mjs';
import { rewindAudio } from '../clientside-rewind.mjs';
import { songSeconds, windowStart, windowNotes } from '../live-bars.mjs';

const RATE = 44100, BAR = 2 * RATE;   // 120 bpm, 4/4

test('drum hits of a window are those of the same bars of the whole song, moved to the window start', () => {
  const params = { tempo: 120, pattern: 'beat-B' }, all = (n) => Array.from({ length: n }, (_, k) => 1 + k);
  const full = drumHits({ params, sampleRate: RATE, firstBar: 1, bars: 12, activeBars: all(12) });
  const first = 5, bars = 4, win = drumHits({ params, sampleRate: RATE, firstBar: first, bars, activeBars: all(12).filter((b) => b >= first && b < first + bars) });
  const inWindow = full.hits.filter((h) => h.bar >= first && h.bar < first + bars);
  assert.equal(win.hits.length, inWindow.length);
  win.hits.forEach((h, i) => { assert.equal(h.bar, inWindow[i].bar); assert.equal(h.kind, inWindow[i].kind); assert.equal(h.start, inWindow[i].start - (first - 1) * full.samplesPerBar); });
});

const seq = [
  'sequencer 1',
  'channel lead notes vol=0',
  'pattern a bars=2',
  '  lead 1:0:1:60:100 2:0:1:67:100',
  'pattern b bars=1',
  '  lead 1:0:1:72:100',
  'playlist',
  '  clip a track=1 at=1 for=6',
  '  clip b track=1 at=7 for=6',
].join('\n');
test('sequencer notes of a window are the notes of the same bars of the whole song', () => {
  const planFull = sequencePreviewPlan(seq, '', 1, 12), planWin = sequencePreviewPlan(seq, '', 7, 4);
  assert.ok(planFull && planWin);
  const full = planFull.channels[0].notes, win = planWin.channels[0].notes;
  assert.deepEqual(win, full.filter((n) => n.bar >= 7 && n.bar < 11));
  assert.ok(win.length && win.every((n) => n.pitch === 72));
});

test('fire notes of a window carry the song bar and only fall in the window', () => {
  const values = new Float32Array(4000).fill(0);   // 4 s of a series, per millisecond, that starts at bar 5
  values[1000] = 900; values[3500] = 900;
  const params = { threshold: '0.5', gap: '100' };
  const notes = fireNotes(values, params, { samplesPerBar: BAR, rate: RATE, firstBar: 5, bars: 2 });
  assert.deepEqual(notes.map((n) => n.bar), [5, 6]);
  assert.equal(notes[0].beat, 2);   // 1.0 s into a 2 s bar
  const whole = fireNotes(values, params, { samplesPerBar: BAR, rate: RATE, bars: 2 });
  assert.deepEqual(whole.map((n) => n.bar), [1, 2]);
});

test('the click of a window is the same bars of the whole click, and a part outside the window is silent', () => {
  const part = [{ at: 1, length: 12 }], params = { bpm: 120 };
  const full = clickAudio(params, part, BAR, RATE, 1, 12).channels[0], win = clickAudio(params, part, BAR, RATE, 7, 3).channels[0];
  assert.deepEqual(win, full.subarray(6 * BAR, 9 * BAR));
  assert.ok(clickAudio(params, [{ at: 1, length: 4 }], BAR, RATE, 7, 3).channels[0].every((v) => v === 0));
});

test('a curve block is read at the bar of the song, not of the window', () => {
  const params = { value: 'auto(1:0, 11:1000)' };
  assert.equal(curveLevel(params, 1), 0);
  assert.equal(curveLevel(params, 6), 500);
  assert.equal(curveLevel(params, 11), 1000);
});

test('a volume curve is read at the seconds of the song: the window position plus the bars before it', () => {
  const text = 'auto(1:-20, 41:0)', bar = 2, first = 21;
  assert.equal(gainAtSeconds(text, songSeconds(1, 0, bar), bar), -20);
  assert.ok(Math.abs(gainAtSeconds(text, songSeconds(first, 0, bar), bar) - 20 * Math.log10((0.1 + 1) / 2)) < 1e-9);   // bar 21 is halfway in the linear gain (the server joins the points in the gain, not in dB): -5.19 dB
  assert.ok(Math.abs(gainAtSeconds(text, songSeconds(first, 20 * bar, bar), bar) - 0) < 1e-9);
  assert.equal(gainAtSeconds(text, 0, bar), -20);   // without the offset the window would have played the start of the curve
});

test('rewind events count from the block start: a window moves them back, earlier ones are not heard', () => {
  const sound = { sampleRate: RATE, channels: [Float32Array.from({ length: 4 * RATE }, (_, i) => Math.sin(i / 20))] };
  const params = { events: 'at 3s scratch 500ms back 1s', fade: '0' };
  const whole = rewindAudio(sound, params, BAR, null).audio.channels[0], same = rewindAudio(sound, params, BAR, null, undefined, 0).audio.channels[0];
  assert.deepEqual(same, whole);
  const dropped = rewindAudio(sound, params, BAR, null, undefined, 4 * RATE).audio.channels[0];
  assert.deepEqual(dropped, sound.channels[0]);   // the event began before the window: nothing changes
  const moved = rewindAudio(sound, params, BAR, null, undefined, RATE).audio.channels[0];   // the event is now at 2 s of the window
  assert.deepEqual(moved.subarray(0, 2 * RATE - 1), sound.channels[0].subarray(0, 2 * RATE - 1));
  assert.notDeepEqual(moved.subarray(2 * RATE, 2.5 * RATE), sound.channels[0].subarray(2 * RATE, 2.5 * RATE));
});

test('the loop range start sets the window: from bar 41 of 120 the window starts at bar 41', () => {
  const first = windowStart(120, 49, 41), notes = [{ start: 80, end: 81, pitch: 60 }];
  assert.equal(windowNotes(notes, first, 49, 2)[0].start, 0);
});
