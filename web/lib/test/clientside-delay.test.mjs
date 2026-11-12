import test from 'node:test';
import assert from 'node:assert/strict';
import { delayAudio } from '../clientside-delay.mjs';

test('delay spaces feedback echoes by the requested milliseconds and keeps the input duration', async () => {
  const audio = { sampleRate: 4, channels: [Float32Array.of(1, 0, 0, 0, 0)] };
  const result = await delayAudio(audio, { time: '250 ms', feedback: '50%', mix: '100%' }, new AbortController().signal);
  assert.deepEqual([...result.channels[0]], [1, 1, 0.5, 0.25, 0.125]);
  assert.equal(result.channels[0].length, audio.channels[0].length);
});

test('delay bypass returns its source and abort is checked between processing chunks', async () => {
  const audio = { sampleRate: 1, channels: [Float32Array.of(0.2)] };
  assert.equal(await delayAudio(audio, { enabled: 'off' }, new AbortController().signal), audio);
  const controller = new AbortController(); controller.abort();
  await assert.rejects(() => delayAudio(audio, { time: '1ms' }, controller.signal), { name: 'AbortError' });
});

test('sync locks the echo to a note value of the tempo, in whole samples of the bar (as the server)', async () => {
  const { delayAudio, syncSamples } = await import('../clientside-delay.mjs');
  assert.equal(syncSamples({ sync: 'off' }, 44100, 120), 0);
  assert.equal(syncSamples({ sync: '1/4' }, 44100, 120), 22050);     // a quarter note at 120 bpm is half a second
  assert.equal(syncSamples({ sync: '1/8' }, 44100, 90), 14700);
  assert.equal(syncSamples({ sync: '1/8t' }, 44100, 120), 7350);     // a bar of 88200 samples holds 12 eighth triplets
  const click = new Float32Array(44100); click[0] = 1;
  const out = await delayAudio({ sampleRate: 44100, channels: [click] }, { time: '250 ms', feedback: '0%', mix: '100%', sync: '1/4' }, undefined, 120);
  assert.ok(out.channels[0][22050] > 0.9 && out.channels[0][11025] === 0);   // the echo sits at the note value, not at time
  const plain = await delayAudio({ sampleRate: 44100, channels: [click] }, { time: '250 ms', feedback: '0%', mix: '100%', sync: 'off' }, undefined, 120);
  assert.ok(plain.channels[0][11025] > 0.9);
});
