import assert from 'node:assert/strict';
import test from 'node:test';
import { ReverbStream } from '../reverb-stream.mjs';
import { cutoffCoefficient } from '../one-pole.mjs';
import { processReverbBuffer } from '../clientside-reverb.mjs';


// The comb and all-pass lengths of the classic Freeverb (samples at 44.1 kHz), as on the server (Effects.cs Reverb); the right channel is a little longer.
const COMBS = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617], ALLPASSES = [556, 441, 341, 225], SPREAD = 23;


/**
 * The room of the server, sample for sample: the input loses its lows (160 Hz), eight damped combs ring in parallel and four all-passes smooth the tail;
 * the dry sound stays and mix sets the level of the room. Integer arithmetic like the server (every value stays far below 2^53, so the doubles are exact).
 */
function referenceReverb(inputChannels, params, sampleRate, signal) {
  const frames = inputChannels[0]?.length || 0, size = Math.trunc(clamp(value(params.size, 50), 1, 100)), decay = Math.trunc(clamp(value(params.decay, 60), 0, 95)), mix = Math.trunc(clamp(value(params.mix, 25), 0, 100));
  const feedback = 22938 + Math.trunc(9175 * decay / 95), damping = 9830, g = feedback / 32768;
  const gain = Math.round(32768 * Math.sqrt(0.5 * (1 - (g * g)) / COMBS.length)), lowA = cutoffCoefficient(160, sampleRate);
  return inputChannels.map((input, channel) => {
    const spread = channel === 1 ? SPREAD : 0, pcm = new Float64Array(frames), feed = new Float64Array(frames), wet = new Float64Array(frames);
    let state = 0;
    for (let i = 0; i < frames; i++) {
      if ((i & 0x3FFF) === 0) signal?.throwIfAborted();
      pcm[i] = inputPcm(input[i]);
      state += Math.floor(lowA * (pcm[i] - state) / 32768);
      feed[i] = Math.floor((pcm[i] - state) * gain / 32768);
    }
    for (const base of COMBS) {
      const length = Math.max(1, Math.trunc((base + spread) * sampleRate * (60 + size) / (44100 * 110))), ring = new Float64Array(length);
      let store = 0;
      for (let i = 0, at = 0; i < frames; i++, at = at + 1 === length ? 0 : at + 1) {
        if ((i & 0x3FFF) === 0) signal?.throwIfAborted();
        const delayed = ring[at];
        store = Math.floor(((delayed * (32768 - damping)) + (store * damping)) / 32768);
        ring[at] = feed[i] + Math.floor(store * feedback / 32768);
        wet[i] += delayed;
      }
    }
    for (const base of ALLPASSES) {
      const length = Math.max(1, Math.trunc((base + spread) * sampleRate / 44100)), ring = new Float64Array(length);
      for (let i = 0, at = 0; i < frames; i++, at = at + 1 === length ? 0 : at + 1) {
        const delayed = ring[at], current = wet[i];
        wet[i] = delayed - current;
        ring[at] = current + Math.floor(delayed / 2);
      }
    }
    const output = new Float32Array(frames);
    for (let i = 0; i < frames; i++) {
      if ((i & 0x3FFF) === 0) signal?.throwIfAborted();
      const sample = clipPcm(pcm[i] + Math.trunc(wet[i] * mix / 100));
      output[i] = sample < 0 ? sample / 32768 : sample / 32767;
    }
    return output;
  });
}


function value(input, fallback) { const parsed = Number.parseFloat(input); return Number.isFinite(parsed) ? parsed : fallback; }
function clamp(input, minimum, maximum) { return Math.max(minimum, Math.min(maximum, input)); }
function inputPcm(input) { return Math.round(clamp(input * (input < 0 ? 32768 : 32767), -32768, 32767)); }
function clipPcm(input) { return clamp(input, -32768, 32767); }

const noise = (frames, seed) => { const out = new Float32Array(frames); let x = seed; for (let i = 0; i < frames; i++) { x = (x * 1664525 + 1013904223) >>> 0; out[i] = ((x / 2 ** 32) * 2 - 1) * (i % 9000 < 3000 ? .8 : .05); } return out; };

test('the offline render is bit-identical to the batch algorithm it replaced', () => {
  const channels = [noise(30000, 1), noise(30000, 2)];
  for (const params of [{ size: '50', decay: '60', mix: '25' }, { size: '7', decay: '95', mix: '100' }, { size: '100', decay: '0', mix: '0' }]) {
    for (const rate of [44100, 48000]) {
      const old = referenceReverb(channels, params, rate), now = processReverbBuffer(channels, params, rate);
      assert.deepEqual(now.map((c) => [...c]), old.map((c) => [...c]), `${JSON.stringify(params)} at ${rate}`);
    }
  }
});

test('the stream in blocks of 128 samples gives the same samples as one pass', () => {
  const input = noise(20000, 5), whole = processReverbBuffer([input], { size: 63, decay: 80, mix: 40 }, 48000)[0];
  const stream = new ReverbStream(48000, 0); stream.configure({ size: 63, decay: 80, mix: 40 });
  const out = new Float32Array(input.length);
  for (let start = 0; start < input.length; start += 128) for (let i = start; i < Math.min(input.length, start + 128); i++) out[i] = stream.process(input[i]);
  assert.deepEqual([...out], [...whole]);
});

test('turning size and decay while it runs stays finite and keeps ringing', () => {
  const stream = new ReverbStream(44100, 1), input = noise(40000, 9);
  stream.configure({ size: 50, decay: 60, mix: 100 });
  let energy = 0;
  for (let i = 0; i < input.length; i++) {
    if (i % 4000 === 0) stream.configure({ size: 1 + (i / 4000 * 11) % 100, decay: (i / 4000 * 17) % 95, mix: 100 });
    const sample = stream.process(input[i]);
    assert.ok(Number.isFinite(sample) && Math.abs(sample) <= 1);
    if (i > 20000) energy += sample * sample;
  }
  assert.ok(energy > 0);
});
