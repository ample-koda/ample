import test from 'node:test';
import assert from 'node:assert/strict';
import { chorusAudio } from '../clientside-chorus.mjs';

test('chorus uses a modulated delayed wet signal and respects dry mix and bypass', async () => {
  const audio = { sampleRate: 1000, channels: [Float32Array.of(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)] };
  const dry = await chorusAudio(audio, { mix: '0%' }, new AbortController().signal);
  assert.deepEqual([...dry.channels[0]], [...audio.channels[0]]);
  const wet = await chorusAudio(audio, { depth: '1 ms', period: '100 ms', mix: '100%' }, new AbortController().signal);
  assert.ok(wet.channels[0].some((value, index) => index > 0 && value !== 0));
  assert.equal(await chorusAudio(audio, { enabled: 'off' }, new AbortController().signal), audio);
});

test('chorus checks cancellation before processing', async () => {
  const controller = new AbortController(); controller.abort();
  await assert.rejects(() => chorusAudio({ sampleRate: 1000, channels: [new Float32Array(10)] }, {}, controller.signal), { name: 'AbortError' });
});
