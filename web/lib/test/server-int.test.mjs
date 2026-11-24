import test from 'node:test';
import assert from 'node:assert/strict';
import { serverInt } from '../server-int.mjs';
import { chorusAudio } from '../clientside-chorus.mjs';

test('a whole-number parameter is read like the server: the leading sign and digits', () => {
  assert.equal(serverInt('5.5 ms', 1), 5);
  assert.equal(serverInt('1500 ms', 1), 1500);
  assert.equal(serverInt('-12 dB', 0), -12);
  assert.equal(serverInt('"7"', 0), 7);
  assert.equal(serverInt(27, 0), 27);
  assert.equal(serverInt('ms', 9), 9);
  assert.equal(serverInt(undefined, 9), 9);
});

test('a chorus depth of 5.5 ms is the depth 5 of the server in the offline port (the real song has depth 5.5 ms)', async () => {
  const input = { sampleRate: 44100, channels: [Float32Array.from({ length: 8000 }, (_, i) => Math.sin(i / 7)), Float32Array.from({ length: 8000 }, (_, i) => Math.sin(i / 9))] };
  const a = await chorusAudio({ sampleRate: 44100, channels: input.channels.map((c) => c.slice()) }, { depth: '5.5 ms', period: '1800 ms', mix: '27' });
  const b = await chorusAudio({ sampleRate: 44100, channels: input.channels.map((c) => c.slice()) }, { depth: '5', period: '1800', mix: '27' });
  assert.deepEqual(a.channels[0], b.channels[0]);
});

test('a level in whole decibels is rounded as the server reads it, and the previews of the effects use it (bug 23)', async () => {
  const { serverRoundedDb } = await import('../server-int.mjs');
  assert.deepEqual(['-2.8 dB', '-0.7', '0.5', '-18.5 dB', '"-3 dB"', '.5', 'x', ''].map((text) => serverRoundedDb(text, 99)), [-3, -1, 1, -19, -3, 99, 99, 99]);
});
