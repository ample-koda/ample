import test from 'node:test';
import assert from 'node:assert/strict';
import { crossfadeAudio, cutAudio, fadeAudio, joinAudio, reverseAudio, shiftAudio, splitAudio } from '../clientside-sample-ops.mjs';

test('reverse preserves sample rate and reverses each channel independently', async () => {
  const result = await reverseAudio({ sampleRate: 48000, channels: [Float32Array.of(0.1, 0.2, 0.3), Float32Array.of(-0.4, 0.5, -0.6)] }, new AbortController().signal);
  assert.equal(result.sampleRate, 48000);
  assert.deepEqual([...result.channels[0]].map((value) => Math.round(value * 1e6) / 1e6), [0.3, 0.2, 0.1]);
  assert.deepEqual([...result.channels[1]].map((value) => Math.round(value * 1e6) / 1e6), [-0.6, 0.5, -0.4]);
});

test('cut applies seconds bounds and preserves duration with silence after a non-looping crop', async () => {
  const result = await cutAudio({ sampleRate: 1, channels: [Float32Array.of(0.1, 0.2, 0.3, 0.4)] }, { start: '1s', end: '3s', loop: 'off' }, new AbortController().signal);
  assert.deepEqual([...result.channels[0]].map((value) => Math.round(value * 1e6) / 1e6), [0.2, 0.3, 0, 0]);
});

test('cut first/last selection and loop behavior match the block rules', async () => {
  const audio = { sampleRate: 1, channels: [Float32Array.of(0.1, 0.2, 0.3, 0.4)] };
  const first = await cutAudio(audio, { first: '2s', last: '1s', loop: 'on' }, new AbortController().signal);
  const last = await cutAudio(audio, { last: '2s', loop: 'on' }, new AbortController().signal);
  const values = (result) => [...result.channels[0]].map((value) => Math.round(value * 1e6) / 1e6);
  assert.deepEqual(values(first), [0.1, 0.2, 0.1, 0.2]);
  assert.deepEqual(values(last), [0.3, 0.4, 0.3, 0.4]);
});

test('fade applies linear in/out curves at second-based boundaries', async () => {
  const audio = { sampleRate: 1, channels: [Float32Array.of(1, 1, 1, 1, 1)] }, signal = new AbortController().signal;
  const fadeIn = await fadeAudio(audio, { direction: 'in', start: '0s', length: '4s', shape: 'linear' }, 60, 1, signal);
  const fadeOut = await fadeAudio(audio, { direction: 'out', start: '0s', length: '4s', shape: 'linear' }, 60, 1, signal);
  const values = (result) => [...result.channels[0]].map((value) => Math.round(value * 1e6) / 1e6);
  assert.deepEqual(values(fadeIn), [0, 0.25, 0.5, 0.75, 1]);
  assert.deepEqual(values(fadeOut), [1, 0.75, 0.5, 0.25, 0]);
});

test('shift moves audio in either direction and zero-fills the block boundary', async () => {
  const audio = { sampleRate: 1, channels: [Float32Array.of(0.1, 0.2, 0.3, 0.4)] }, signal = new AbortController().signal;
  const later = await shiftAudio(audio, { seconds: '1s', beats: '0' }, 60, 0, signal);
  const earlier = await shiftAudio(audio, { seconds: '-1s', beats: '0' }, 60, 0, signal);
  const values = (result) => [...result.channels[0]].map((value) => Math.round(value * 1e6) / 1e6);
  assert.deepEqual(values(later), [0, 0.1, 0.2, 0.3]);
  assert.deepEqual(values(earlier), [0.2, 0.3, 0.4, 0]);
});

test('crossfade joins two sources with matching fade-out and fade-in envelopes', async () => {
  const a = { sampleRate: 1, channels: [Float32Array.of(1, 1, 1, 1)] }, b = { sampleRate: 1, channels: [Float32Array.of(0.5, 0.5, 0.5, 0.5)] };
  const result = await crossfadeAudio(a, b, { start: '0s', length: '4s', shape: 'linear' }, 60, 1, 0, 1, new AbortController().signal);
  assert.deepEqual([...result.channels[0]].map((value) => Math.round(value * 1e6) / 1e6), [1, 0.875, 0.75, 0.625]);
});

test('join follows input order and inserts the combined seconds and beat gap', async () => {
  const inputs = [{ audio: { sampleRate: 4, channels: [Float32Array.of(0.1, 0.2)] }, index: 1 }, { audio: { sampleRate: 4, channels: [Float32Array.of(0.7, 0.8)] }, index: 2 }];
  const result = await joinAudio(inputs, { seconds: '0.5s', beats: '1' }, 60, 0, 4, new AbortController().signal);
  assert.deepEqual([...result.channels[0]].map((value) => Math.round(value * 1e6) / 1e6), [0.1, 0.2, 0, 0, 0, 0, 0, 0, 0.7, 0.8]);
});

test('splitter exposes independent mono channels and duplicates mono input', () => {
  const audio = { sampleRate: 4, channels: [Float32Array.of(0.1, 0.2), Float32Array.of(0.7, 0.8)] }, result = splitAudio(audio);
  assert.deepEqual([...result.left.channels[0]], [...audio.channels[0]]);
  assert.deepEqual([...result.right.channels[0]], [...audio.channels[1]]);
  const mono = splitAudio({ sampleRate: 4, channels: [Float32Array.of(0.3, 0.4)] });
  assert.deepEqual([...mono.left.channels[0]], [...mono.right.channels[0]]);
});
