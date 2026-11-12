import assert from 'node:assert/strict';
import test from 'node:test';
import { renderBefore } from './reference/piano-before.mjs';
import { PianoVoice, renderPianoNotes } from '../piano-core.mjs';

const notes = [{ bar: 1, beat: 0, beats: 1, pitch: 60 }, { bar: 1, beat: 0.5, beats: 3, pitch: 64 }, { bar: 1, beat: 0.5, beats: 3.5, pitch: 67 }, { bar: 2, beat: 2, beats: 2, pitch: 40 }, { bar: 3, beat: 0, beats: 4, pitch: 100 }, { bar: 9, beat: 0, beats: 1, pitch: 60 }];

test('the render on the shared core is bit-identical to the version before it', () => {
  for (const rate of [44100, 48000]) {
    const spb = Math.floor(4 * 60 * rate / 92);
    assert.deepEqual([...renderBefore(notes, rate, 1, 4, spb)], [...renderPianoNotes(notes, rate, 1, 4, spb)]);
    assert.deepEqual([...renderBefore(notes, rate, 2, 2, spb)], [...renderPianoNotes(notes, rate, 2, 2, spb)]);
  }
});

test('notes streamed in blocks of 128 samples sum to the render, with its clipping', () => {
  const rate = 48000, spb = 96000, frames = 2 * spb, reference = renderPianoNotes(notes.slice(0, 4), rate, 1, 2, spb), out = new Float32Array(frames);
  const live = notes.slice(0, 4).map((n) => ({ start: Math.floor((n.bar - 1 + n.beat / 4) * spb), voice: new PianoVoice(rate, { pitch: n.pitch, length: n.beats * spb / 4 }) }));
  for (let at = 0; at < frames; at += 128) for (const slot of live) { const to = Math.min(frames, at + 128); if (slot.start < to) slot.voice.process(out, Math.max(at, slot.start), to); }
  assert.deepEqual([...out], [...reference]);
});
