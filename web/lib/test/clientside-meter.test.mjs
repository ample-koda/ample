import test from 'node:test';
import assert from 'node:assert/strict';
import { meterAudio } from '../clientside-meter.mjs';

test('meter preserves input and reports PCM peak, loudness, clipping and stereo correlation', async () => {
  const left = Float32Array.from({ length: 48000 }, (_, i) => i < 1000 ? 1 : Math.sin(i / 13) * 0.25);
  const right = Float32Array.from(left, (value) => value * 0.5);
  const audio = { sampleRate: 48000, channels: [left, right] };
  const result = await meterAudio(audio, {}, new AbortController().signal);
  assert.equal(result.audio, audio);
  assert.match(result.report, /peak -?0\.0 dBFS/);
  assert.match(result.report, /loudness -\d+\.\d LUFS/);
  assert.match(result.report, /clipped 1000/);
  assert.match(result.report, /stereo L -?0\.0 dBFS R -6\.0 dBFS/);
  assert.match(result.report, /correlation 1\.00/);
});

test('meter reports silence for short silent audio and is bypassable', async () => {
  const audio = { sampleRate: 1000, channels: [new Float32Array(100)] };
  assert.match((await meterAudio(audio, {}, new AbortController().signal)).report, /loudness silence/);
  assert.deepEqual(await meterAudio(audio, { enabled: 'off' }, new AbortController().signal), { audio, report: null });
});

test('meter checks cancellation before and during measurement', async () => {
  const before = new AbortController(); before.abort();
  await assert.rejects(() => meterAudio({ sampleRate: 1000, channels: [new Float32Array(10)] }, {}, before.signal), { name: 'AbortError' });
  const during = new AbortController();
  const work = meterAudio({ sampleRate: 1000, channels: [new Float32Array(200000)] }, {}, during.signal);
  setTimeout(() => during.abort(), 0);
  await assert.rejects(() => work, { name: 'AbortError' });
});
