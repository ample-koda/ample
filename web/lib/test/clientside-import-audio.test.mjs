import test from 'node:test';
import assert from 'node:assert/strict';
import { importedPort, placeImportedAudio, placeImportedPorts } from '../clientside-import-audio.mjs';

const audio = (left, right, sampleRate) => ({ sampleRate, channels: [Float32Array.from(left), ...(right ? [Float32Array.from(right)] : [])] });
const samples = (channel) => [...channel].map((sample) => Math.round(sample * 1e6) / 1e6);

test('imported audio crops the source, applies seconds offset and places it on the song timeline', () => {
  const result = placeImportedAudio(audio([0.1, 0.2, 0.3, 0.4], null, 1), { start: '1s', end: '3s', offset: '1s', loop: 'off' }, 4, 2, null, 1, 1);
  assert.deepEqual(samples(result.channels[0]), [0, 0.2, 0.3, 0, 0, 0, 0, 0]);
});

test('imported audio accepts beat offsets and extracts a selected arrangement range', () => {
  const result = placeImportedAudio(audio([0.1, 0.2, 0.3, 0.4], null, 1), { offset: '1 beat', loop: 'off' }, 4, 8, { firstBar: 3, bars: 2 }, 3, 1);
  assert.equal(result.channels[0].length, 8);
  assert.deepEqual(samples(result.channels[0]), [0, 0.1, 0.2, 0.3, 0.4, 0, 0, 0]);
});

test('loop crossfade blends the start of each repeat with the tail of the one before; the first pass plays plain, as the server does', () => {
  const result = placeImportedAudio(audio([0.1, 0.2, 0.3, 0.4], null, 1), { loop: 'on', crossfade: '2s' }, 4, 2, null, 1, 1);
  const near = (got, want) => got.forEach((v, i) => assert.ok(Math.abs(v - want[i]) < 1e-6, `${i}: ${v} against ${want[i]}`));
  near(result.channels[0], [0.1, 0.2, 0.31543219, 0.33784927, 0.31543219, 0.33784927, 0.31543219, 0.33784927]);
});

test('the ports of a loop: body is the loop without its seam, region only the seam, and together they are the sound (issue 79)', () => {
  const placed = placeImportedPorts(audio([0.1, 0.2, 0.3, 0.4], [0.4, 0.3, 0.2, 0.1], 1), { loop: 'on', crossfade: '2s' }, 4, 2, null, 1, 1);
  const body = importedPort(placed, 'body'), region = importedPort(placed, 'region');
  assert.deepEqual([...placed.seam], [0, 0, 1, 1, 1, 1, 1, 1]);
  assert.deepEqual(samples(body.channels[0]), [0.1, 0.2, 0, 0, 0, 0, 0, 0]);
  assert.deepEqual(samples(region.channels[1]).slice(0, 3), [0, 0, samples(placed.audio.channels[1])[2]]);
  placed.audio.channels.forEach((channel, c) => channel.forEach((value, i) => assert.equal(body.channels[c][i] + region.channels[c][i], value)));
  assert.equal(importedPort(placed, 'audio'), placed.audio);
});

test('a loop without a crossfade has no seam, and loop_start and loop_end make an intro and an outro', () => {
  const plain = placeImportedPorts(audio([0.1, 0.2, 0.3, 0.4], null, 1), { loop: 'on' }, 4, 2, null, 1, 1);
  assert.ok(![...plain.seam].includes(1));
  assert.ok(importedPort(plain, 'region').channels[0].every((v) => v === 0));
  const framed = placeImportedPorts(audio([0.1, 0.2, 0.3, 0.4, 0.5, 0.6], null, 1), { loop: 'on', loop_start: '1', loop_end: '5' }, 6, 2, null, 1, 1);
  assert.deepEqual(samples(framed.audio.channels[0]).slice(0, 12), [0.1, 0.2, 0.3, 0.4, 0.5, 0.2, 0.3, 0.4, 0.5, 0.2, 0.3, 0.6]);   // the intro once, the loop 1..5, the outro once at the end
});

test('stereo channels keep independent source content and align to the output length', () => {
  const result = placeImportedAudio(audio([0.1, 0.2], [0.7, 0.8], 11025), { loop: 'off' }, 4, 1, null, 1, 11025);
  assert.deepEqual(samples(result.channels[0]), [0.1, 0.2, 0, 0]);
  assert.deepEqual(samples(result.channels[1]), [0.7, 0.8, 0, 0]);
});
