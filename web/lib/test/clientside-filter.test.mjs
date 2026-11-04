import test from 'node:test';
import assert from 'node:assert/strict';
import { filterAudio } from '../clientside-filter.mjs';

const rms = (values) => Math.sqrt(values.reduce((sum, value) => sum + value * value, 0) / values.length);

test('filter lowpass and highpass separate low and high tones', async () => {
  const rate = 12000, frames = 6000, low = Float32Array.from({ length: frames }, (_, i) => Math.sin(i * 2 * Math.PI * 100 / rate)), high = Float32Array.from({ length: frames }, (_, i) => Math.sin(i * 2 * Math.PI * 4000 / rate));
  const lowOut = await filterAudio({ sampleRate: rate, channels: [low] }, { cutoff: '500 Hz' }, 120, 1, new AbortController().signal);
  const highOut = await filterAudio({ sampleRate: rate, channels: [high] }, { type: 'highpass', cutoff: '500 Hz' }, 120, 1, new AbortController().signal);
  assert.ok(rms(lowOut.channels[0].slice(1000)) > 0.6);
  assert.ok(rms(highOut.channels[0].slice(1000)) > rms(high.slice(1000)) * 0.95);
});

test('filter LFO and range phase affect output; bypass and cancellation are respected', async () => {
  const audio = { sampleRate: 12000, channels: [Float32Array.from({ length: 12000 }, (_, i) => Math.sin(i * 2 * Math.PI * 900 / 12000))] };
  const still = await filterAudio(audio, { cutoff: '500 Hz', lfo_rate: 'off' }, 120, 1, new AbortController().signal);
  const moving = await filterAudio(audio, { cutoff: '500 Hz', lfo_rate: '1/1', lfo_depth: '5 oct', lfo_shape: 'triangle' }, 120, 1, new AbortController().signal);
  assert.notDeepEqual([...still.channels[0]], [...moving.channels[0]]);
  assert.equal(await filterAudio(audio, { enabled: 'off' }, 120, 1, new AbortController().signal), audio);
  const controller = new AbortController(); controller.abort();
  await assert.rejects(() => filterAudio(audio, {}, 120, 1, controller.signal), { name: 'AbortError' });
});

test('resonance adds a peak at the cutoff and zero keeps the plain filter', async () => {
  const rate = 12000, at = Float32Array.from({ length: 6000 }, (_, i) => Math.sin(i * 2 * Math.PI * 1000 / rate) * 0.1), signal = new AbortController().signal;
  const plain = await filterAudio({ sampleRate: rate, channels: [at] }, { cutoff: '1000 Hz' }, 120, 1, signal);
  const zero = await filterAudio({ sampleRate: rate, channels: [at] }, { cutoff: '1000 Hz', resonance: '0 %' }, 120, 1, signal);
  const ringing = await filterAudio({ sampleRate: rate, channels: [at] }, { cutoff: '1000 Hz', resonance: '80 %' }, 120, 1, signal);
  assert.deepEqual([...zero.channels[0]], [...plain.channels[0]]);
  assert.ok(rms(ringing.channels[0].slice(1000)) > rms(plain.channels[0].slice(1000)) * 2);
});

test('bandpass keeps the cutoff tone, notch removes it, slope 24 cuts a far tone harder, defaults stay unchanged', async () => {
  const rate = 12000, tone = (hz) => Float32Array.from({ length: 6000 }, (_, i) => Math.sin(i * 2 * Math.PI * hz / rate)), signal = new AbortController().signal;
  const run = async (hz, params) => rms((await filterAudio({ sampleRate: rate, channels: [tone(hz)] }, { cutoff: '1000 Hz', ...params }, 120, 1, signal)).channels[0].slice(1000));
  assert.ok(await run(1000, { type: 'bandpass', resonance: '50 %' }) > 0.5);
  assert.ok(await run(100, { type: 'bandpass', resonance: '50 %' }) < 0.15);
  assert.ok(await run(1000, { type: 'notch', resonance: '50 %' }) < 0.15);
  assert.ok(await run(100, { type: 'notch', resonance: '50 %' }) > 0.5);
  assert.ok(await run(4000, { slope: '24' }) < (await run(4000, { slope: '12' })) * 0.6);
  const a = await filterAudio({ sampleRate: rate, channels: [tone(300)] }, { cutoff: '1000 Hz' }, 120, 1, signal);
  const b = await filterAudio({ sampleRate: rate, channels: [tone(300)] }, { cutoff: '1000 Hz', type: 'lowpass', slope: '12' }, 120, 1, signal);
  assert.deepEqual([...a.channels[0]], [...b.channels[0]]);
});
