// node --test plugins/test/wav-channels.test.mjs: the WAV helpers that keep the channels of a sound (the ACE music block keeps its stereo image).
import test from 'node:test';
import assert from 'node:assert/strict';
import { parseWavChannels, toChannels16, wavChannels } from '../sdk/wav.mjs';

const ramp = (n, scale) => Int16Array.from({ length: n }, (_, i) => Math.round(Math.sin(i / 7) * scale));
const stereo = (left, right, rate = 8000) => wavChannels([left, right], rate);
const mono = (samples, rate = 8000) => wavChannels([samples], rate);

test('a stereo WAV is read as two channels that keep their own samples (issue 332)', () => {
  const left = ramp(200, 8000), right = ramp(200, 3000);
  const { rate, channels } = parseWavChannels(stereo(left, right));
  assert.equal(rate, 8000);
  assert.equal(channels.length, 2);
  assert.equal(Math.round(channels[0][50] * 32768), left[50]);
  assert.equal(Math.round(channels[1][50] * 32768), right[50]);
});

test('auto keeps both channels and does not mix them', () => {
  const left = ramp(200, 8000), right = ramp(200, 3000);
  const out = toChannels16(stereo(left, right), 8000, 200, null, 'auto');
  assert.equal(out.length, 2);
  assert.deepEqual([...out[0].slice(0, 20)], [...left.slice(0, 20)]);
  assert.deepEqual([...out[1].slice(0, 20)], [...right.slice(0, 20)]);
});

test('mono mixes the two channels to their average on purpose', () => {
  const left = Int16Array.from({ length: 100 }, () => 10000), right = Int16Array.from({ length: 100 }, () => -10000);
  const out = toChannels16(stereo(left, right), 8000, 100, null, 'mono');
  assert.equal(out.length, 1);
  assert.equal(out[0][10], 0, 'opposite channels cancel, as a mono mix does');
});

test('stereo makes two channels from a mono sound, auto leaves it mono', () => {
  const one = ramp(100, 5000);
  assert.equal(toChannels16(mono(one), 8000, 100, null, 'auto').length, 1);
  const two = toChannels16(mono(one), 8000, 100, null, 'stereo');
  assert.equal(two.length, 2);
  assert.deepEqual([...two[0]], [...two[1]]);
});

test('the length is exactly what was wanted, padded with silence, and a kept part is faded at its end', () => {
  const left = Int16Array.from({ length: 50 }, () => 8000), right = Int16Array.from({ length: 50 }, () => 8000);
  const out = toChannels16(stereo(left, right), 8000, 120, 50, 'auto');
  assert.equal(out[0].length, 120);
  assert.equal(out[1].length, 120);
  assert.equal(out[0][100], 0);
  assert.ok(out[0][49] < 8000, 'the end of the kept part is faded');
});

test('the rate is converted for both channels', () => {
  const left = ramp(400, 8000), right = ramp(400, 4000);
  const out = toChannels16(stereo(left, right, 8000), 4000, null, null, 'auto');
  assert.equal(out[0].length, 200);
  assert.equal(out[1].length, 200);
});

test('wavChannels writes a file that reads back with the same channels', () => {
  const left = ramp(64, 9000), right = ramp(64, 1000);
  const back = parseWavChannels(wavChannels([left, right], 44100));
  assert.equal(back.rate, 44100);
  assert.equal(back.channels.length, 2);
  assert.equal(back.channels[0].length, 64);
  assert.equal(Math.round(back.channels[1][10] * 32768), right[10]);
});

test('audio that is not a WAV is refused', () => {
  assert.throws(() => parseWavChannels(Buffer.alloc(100)), /not a WAV/);
});
