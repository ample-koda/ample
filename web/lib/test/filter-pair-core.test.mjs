import test from 'node:test';
import assert from 'node:assert/strict';
import { FilterPairCore } from '../filter-pair-core.mjs';
import { filterAudio } from '../clientside-filter.mjs';
import { filterWave } from '../live-lfo.mjs';
import { loadVectors, serverChannels, quantizedInput, deviationDb } from '../dsp-conformance.mjs';

const vectors = loadVectors();
/** Runs the core the way the worklet does, in blocks of 128 samples. */
function run(input, params, lfo) {
  const core = new FilterPairCore(input.sampleRate, 2); core.configure(params);
  const out = input.channels.map((c) => new Float32Array(c.length));
  for (let start = 0; start < input.channels[0].length; start += 128) core.process(input.channels, out, lfo, start, Math.min(input.channels[0].length, start + 128));
  return out;
}

test('the worklet core is the offline render of the plain filter, sample for sample (low pass, high pass)', async () => {
  const input = quantizedInput();
  for (const [params, text] of [[{ cutoff: 800, highpass: false }, { cutoff: '800 Hz' }], [{ cutoff: 1500, highpass: true }, { type: 'highpass', cutoff: '1500 Hz' }]]) {
    const out = run(input, params), reference = await filterAudio({ sampleRate: input.sampleRate, channels: input.channels.map((c) => c.slice()) }, text, 120, 1);
    assert.ok(deviationDb(reference.channels, out, input.sampleRate, 0) <= -140, JSON.stringify(text));
  }
});

test('the worklet core is within -54 dB of the server: low pass, high pass and the cutoff wobble', () => {
  const input = quantizedInput(), bar = Math.floor(4 * 60 * input.sampleRate / 120);
  const cases = { filter: [{ cutoff: 800 }, null], 'filter~highpass': [{ cutoff: 1500, highpass: true }, null], 'filter~sweep': [{ cutoff: 800, depth: 3 }, filterWave('sine', 4, bar)] };
  for (const [key, [params, wave]] of Object.entries(cases)) {
    const lfo = wave ? Float32Array.from({ length: input.channels[0].length }, (_, i) => wave(i)) : null, out = run(input, params, lfo);
    const db = deviationDb(serverChannels(vectors.cases[key]), out, input.sampleRate);
    assert.ok(db <= -54, `${key}: ${db.toFixed(1)} dB`);
  }
});

test('a changed knob moves the cutoff and keeps the state (no click)', () => {
  const core = new FilterPairCore(44100, 1), x = [Float32Array.from({ length: 256 }, () => 0.5)], y = [new Float32Array(256)];
  core.configure({ cutoff: 200 }); core.process(x, y, null, 0, 128);
  const before = y[0][127]; core.configure({ cutoff: 4000 }); core.process(x, y, null, 128, 256);
  assert.ok(Math.abs(y[0][128] - before) < 0.2 && y[0][255] > before);
});
