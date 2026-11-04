import test from 'node:test';
import assert from 'node:assert/strict';
import { deesserAudio } from '../clientside-deesser.mjs';

test('de-esser attenuates high frequency energy above threshold and preserves duration', async () => {
  const rate = 24000, tone = Float32Array.from({ length: 2400 }, (_, i) => Math.sin(i * 2 * Math.PI * 6000 / rate) * 0.7);
  const audio = { sampleRate: rate, channels: [tone] };
  const result = await deesserAudio(audio, { frequency: '2000 Hz', threshold: '-40 dB', ratio: '8' }, new AbortController().signal);
  const rms = (values) => Math.sqrt(values.reduce((sum, value) => sum + value * value, 0) / values.length);
  assert.ok(rms(result.channels[0].slice(400)) < rms(tone.slice(400)) * 0.6);
  assert.equal(result.channels[0].length, tone.length);
});

test('de-esser bypass preserves the original and checks cancellation', async () => {
  const audio = { sampleRate: 12000, channels: [Float32Array.of(0.1)] };
  assert.equal(await deesserAudio(audio, { enabled: 'off' }, new AbortController().signal), audio);
  const controller = new AbortController(); controller.abort();
  await assert.rejects(() => deesserAudio(audio, {}, controller.signal), { name: 'AbortError' });
});
