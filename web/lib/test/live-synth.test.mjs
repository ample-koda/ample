import test from 'node:test';
import assert from 'node:assert/strict';
import { drumFor, eventsInWindow, loopLength, mergeTies, voiceFor } from '../live-synth.mjs';

test('the pieces of a tied note become one note', () => {
  const merged = mergeTies([
    { at: 3000, length: 1000, pitch: 60, velocity: 90, tie: 7 },
    { at: 4000, length: 2000, pitch: 60, velocity: 90, tie: 7 },
    { at: 0, length: 1000, pitch: 64, velocity: 90 },
    { at: 4000, length: 500, pitch: 60, velocity: 90, tie: 8 },     // another tie id: not joined
  ]);
  assert.equal(merged.length, 3);
  assert.deepEqual(merged.find((n) => n.tie === 7), { at: 3000, length: 3000, pitch: 60, velocity: 90, tie: 7 });
});

test('a piece that does not start where the last one ended is its own note', () => {
  assert.equal(mergeTies([{ at: 0, length: 100, pitch: 60, velocity: 90, tie: 1 }, { at: 500, length: 100, pitch: 60, velocity: 90, tie: 1 }]).length, 2);
});

test('events in a window carry seconds from the tempo, and the window is half open', () => {
  const rows = [{ at: 0, length: 1000, pitch: 60, velocity: 127 }, { at: 1000, length: 1000, pitch: 62, velocity: 64 }, { at: 2000, length: 1000, pitch: 64, velocity: 64 }];
  const events = eventsInWindow(rows, { ppq: 1000, bpm: 120, from: 0, to: 1 });   // a beat is half a second
  assert.deepEqual(events.map((e) => [e.pitch, e.start, e.duration]), [[60, 0, 0.5], [62, 0.5, 0.5]]);
  assert.ok(events[0].gain > events[1].gain);
  assert.equal(eventsInWindow(rows, { ppq: 1000, bpm: 120, from: 0.5, to: 1 }).length, 1);
});

test('channel 9 is a drum kit and other channels are pitched voices of the program', () => {
  const [drum] = eventsInWindow([{ at: 0, length: 100, pitch: 36, velocity: 100, channel: 9 }], { ppq: 480, bpm: 100, from: 0, to: 1 });
  assert.equal(drum.drum, 'kick');
  const [lead] = eventsInWindow([{ at: 0, length: 100, pitch: 72, velocity: 100 }], { ppq: 480, bpm: 100, from: 0, to: 1, program: 80 });
  assert.equal(lead.voice.kind, 'tone');
  assert.equal(lead.voice.waves[0].type, 'square');
});

test('every program has a voice and the groups sound different', () => {
  for (let p = 0; p < 128; p++) { const v = voiceFor(p); assert.ok(v.waves.length >= 1 && v.cutoff > 100 && v.attack > 0); }
  assert.notDeepEqual(voiceFor(0), voiceFor(33));
  assert.equal(drumFor(42).drum, 'hat');
});

test('a loop is the end of the notes rounded up to a whole bar', () => {
  const rows = [{ at: 0, length: 100, pitch: 60, velocity: 90 }, { at: 4500, length: 1000, pitch: 60, velocity: 90 }];
  assert.equal(loopLength(rows, { ppq: 1000, bpm: 120 }), 4);   // the notes end in bar 2, so two bars of two seconds
  assert.equal(loopLength([], { ppq: 1000, bpm: 120 }), 2);
});

test('events can be placed with a tempo map (a tempo change inside the song)', async () => {
  const { tempoMap } = await import('../audio-clock.mjs');
  const map = tempoMap([{ beat: 0, bpm: 120 }, { beat: 2, bpm: 60 }]);
  const rows = [{ at: 1000, length: 1000, pitch: 60, velocity: 90 }, { at: 2000, length: 1000, pitch: 62, velocity: 90 }, { at: 3000, length: 1000, pitch: 64, velocity: 90 }];
  const events = eventsInWindow(rows, { ppq: 1000, bpm: 120, from: 0, to: 10, map });
  assert.deepEqual(events.map((e) => [e.pitch, e.start, e.duration]), [[60, .5, .5], [62, 1, 1], [64, 2, 1]]);
  const constant = eventsInWindow(rows, { ppq: 1000, bpm: 120, from: 0, to: 10, map: tempoMap([{ beat: 0, bpm: 120 }]) });
  assert.deepEqual(constant.map((e) => e.start), eventsInWindow(rows, { ppq: 1000, bpm: 120, from: 0, to: 10 }).map((e) => e.start));
});

test('a note already sounding at the left edge is cropped to the window start', () => {
  const rows = [{ at: 0, length: 960, pitch: 60, velocity: 100 }];
  const args = { ppq: 480, bpm: 120, from: 0.5, to: 1 };
  assert.equal(eventsInWindow(rows, args).length, 0);
  const [e] = eventsInWindow(rows, { ...args, sustained: true });
  assert.equal(e.start, 0.5); assert.ok(Math.abs(e.duration - 0.5) < 1e-9);
});

test('a held note with less than the minimum duration left is skipped, not stretched past its end', () => {
  const rows = [{ at: 0, length: 960, pitch: 60, velocity: 100 }];   // ends at 1 s
  const args = { ppq: 480, bpm: 120, from: 0.99, to: 2, sustained: true };
  assert.equal(eventsInWindow(rows, args).length, 0);
  const [e] = eventsInWindow(rows, { ...args, from: 0.95 });
  assert.ok(Math.abs(e.duration - 0.05) < 1e-9);
});
