import test from 'node:test';
import assert from 'node:assert/strict';
import { eqAudio } from '../clientside-eq.mjs';

test('EQ applies low and high shelf gains and unity settings reproduce the signal', async () => {
  const rate = 12000, tone = Float32Array.from({ length: 2400 }, (_, i) => Math.sin(i * Math.PI / 30));
  const audio = { sampleRate: rate, channels: [tone] };
  const unity = await eqAudio(audio, {}, new AbortController().signal);
  assert.ok([...unity.channels[0]].every((value, i) => Math.abs(value - tone[i]) < 1e-6));
  const boosted = await eqAudio(audio, { low: '12 dB', low_hz: '200 Hz', mid: '0 dB', high: '0 dB' }, new AbortController().signal);
  assert.ok(Math.max(...boosted.channels[0].slice(400)) > Math.max(...tone.slice(400)) * 1.8);
});

test('EQ bypass preserves input and checks cancellation', async () => {
  const audio = { sampleRate: 12000, channels: [Float32Array.of(0.2)] };
  assert.equal(await eqAudio(audio, { enabled: 'off' }, new AbortController().signal), audio);
  const controller = new AbortController(); controller.abort();
  await assert.rejects(() => eqAudio(audio, {}, controller.signal), { name: 'AbortError' });
});

test('corners written the wrong way round are put in order: a mid boost stays a boost (bug 27)', async () => {
  const rate = 44100, length = 8820, tone = Float32Array.from({ length }, (_, i) => 0.25 * Math.sin(2 * Math.PI * 1400 * i / rate));
  const peak = (audio) => audio.channels[0].slice(length / 2).reduce((m, v) => Math.max(m, Math.abs(v)), 0);
  const signal = new AbortController().signal;
  const right = await eqAudio({ sampleRate: rate, channels: [tone] }, { mid: '12 dB', low_hz: '1000 Hz', high_hz: '2000 Hz' }, signal);
  const crossed = await eqAudio({ sampleRate: rate, channels: [tone] }, { mid: '12 dB', low_hz: '2000 Hz', high_hz: '1000 Hz' }, signal);
  assert.ok(Math.abs(peak(right) - peak(crossed)) < 1e-6, `${peak(right)} and ${peak(crossed)}`);
  assert.ok(peak(crossed) > 0.25, 'the boost raises the tone');
});
