import test from 'node:test';
import assert from 'node:assert/strict';

const self = globalThis.self = { postMessage: (message) => { self.message = message; } };
await import('../clientside-synth-worker.js');

const note = { bar: 1, beat: 0, beats: 1, pitch: 69, velocity: 127, pan: 0 };

test('local synth emits the selected waveform at the MIDI pitch', () => {
  self.onmessage({ data: { notes: [{ ...note, beats: 4 }], params: { wave: 'sine', gain: '0', attack: '1', decay: '1', sustain: '100', release: '1', cutoff: '20000' }, sampleRate: 8000, firstBar: 1, bars: 1, samplesPerBar: 8000 } });
  const output = new Float32Array(self.message.channels[0]);
  let crossings = 0;
  for (let i = 100; i < 7900; i++) if (output[i - 1] <= 0 && output[i] > 0) crossings++;
  assert.ok(Math.abs(crossings - 440) < 15);
  assert.ok(Math.max(...output) > 0.1);
});

test('local synth applies ADSR, detune and per-note panning', () => {
  self.onmessage({ data: { notes: [{ ...note, pan: -100, beats: 0.25 }], params: { wave: 'square', gain: '0', attack: '50', decay: '20', sustain: '25', release: '100', detune: '12', cutoff: '20000' }, sampleRate: 8000, firstBar: 1, bars: 1, samplesPerBar: 8000 } });
  const left = new Float32Array(self.message.channels[0]), right = new Float32Array(self.message.channels[1]);
  assert.ok(Math.max(...left) > Math.max(...right) * 4);
  assert.ok(Math.max(...left.slice(0, 10)) < Math.max(...left.slice(100, 500)));
});

test('working-set estimate is proportional to frames and stereo channels', async () => {
  const { synthWorkingSetBytes } = await import('../clientside-synth.mjs');
  assert.equal(synthWorkingSetBytes(100, 2), 2400);
});
