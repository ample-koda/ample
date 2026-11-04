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
