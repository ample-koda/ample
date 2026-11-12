import assert from 'node:assert/strict';
import test from 'node:test';
import { renderBefore } from './reference/synth-before.mjs';
import { SynthVoice, renderSynthNotes, synthSettings } from '../synth-core.mjs';

const notes = [
  { bar: 1, beat: 0, beats: 1, pitch: 60, velocity: 100, pan: 0 }, { bar: 1, beat: 1.5, beats: .5, pitch: 64, velocity: 80, pan: -40, fine: 25 },
  { bar: 2, beat: 0, beats: 2, pitch: 48, velocity: 127, pan: 60 }, { bar: 3, beat: 3, beats: 1, pitch: 72, velocity: 90 }, { bar: 9, beat: 0, beats: 1, pitch: 60, velocity: 90 },
];
const sets = [{}, { wave: 'square', detune: '12', cutoff: '3000 Hz', attack: '2 ms', release: '80 ms' }, { wave: 'noise', gain: '-12 dB' }, { wave: 'sine', sustain: '30', decay: '50 ms' }, { enabled: 'off' }];
const same = (a, b) => { assert.equal(a.length, b.length); for (let c = 0; c < a.length; c++) assert.deepEqual([...a[c]], [...b[c]]); };

test('the render on the shared core is bit-identical to the version before it', () => {
  for (const params of sets) for (const rate of [44100, 48000]) {
    const spb = Math.floor(4 * 60 * rate / 100);
    same(renderBefore(notes, params, rate, 1, 4, spb), renderSynthNotes(notes, params, rate, 1, 4, spb));
    same(renderBefore(notes, params, rate, 2, 2, spb), renderSynthNotes(notes, params, rate, 2, 2, spb));
  }
});

test('a note streamed in blocks of 128 samples gives the render\'s samples', () => {
  const rate = 48000, spb = 96000, params = sets[1], frames = 2 * spb;
  const reference = renderSynthNotes(notes.slice(0, 3), params, rate, 1, 2, spb), left = new Float32Array(frames), right = new Float32Array(frames);
  const settings = synthSettings(params, rate), live = notes.slice(0, 3).map((n) => ({ start: Math.floor((n.bar - 1 + n.beat / 4) * spb), voice: new SynthVoice(settings, { pitch: n.pitch, velocity: n.velocity, held: Math.floor(n.beats * spb / 4), pan: n.pan, fine: n.fine }) }));
  for (let at = 0; at < frames; at += 128) for (const slot of live) {
    const to = Math.min(frames, at + 128);
    if (slot.start >= to) continue;
    slot.voice.process(left, right, Math.max(at, slot.start), to);
  }
  same(reference, [left, right]);
});

test('a note ends by itself after its release tail', () => {
  const voice = new SynthVoice(synthSettings({ release: '100 ms' }, 48000), { pitch: 69, velocity: 100, held: 4800 });
  const l = new Float32Array(48000 * 3), r = new Float32Array(48000 * 3);
  voice.process(l, r, 0, l.length);
  assert.equal(voice.done, true); assert.equal(voice.length, 4800 + Math.ceil(.1 * 48000 * 8));
  assert.equal(l[voice.length + 10], 0);
});
