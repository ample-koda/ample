import assert from 'node:assert/strict';
import test from 'node:test';
import { silenceAudio, trimAudio } from '../clientside-trim.mjs';

const rate = 1000;   // 1 sample per millisecond keeps the numbers readable
const burst = (from, to, total) => Float32Array.from({ length: total }, (_, i) => (i >= from && i < to ? 0.5 : 0));

test('trim cuts to the sound with padding, like the server', () => {
  const { audio, report } = trimAudio({ sampleRate: rate, channels: [burst(1000, 2000, 5000)] }, { threshold: '-45 dB', gap: '400 ms', padding: '50 ms' });
  assert.equal(audio.channels[0].length, 1100);   // 950..2050
  assert.match(report, /sound from 1.00 s to 2.00 s of 5.00 s \(1 section\)/);
});
test('several stretches: warn and all cut from the first to the last, first and last take one', () => {
  const two = { sampleRate: rate, channels: [Float32Array.from({ length: 6000 }, (_, i) => ((i >= 500 && i < 900) || (i >= 4000 && i < 4400) ? 0.5 : 0))] };
  const warn = trimAudio(two, { gap: '400 ms', padding: '0 ms' });
  assert.equal(warn.audio.channels[0].length, 3900); assert.match(warn.report, /WARNING/);
  assert.equal(trimAudio(two, { gap: '400 ms', padding: '0 ms', sections: 'first' }).audio.channels[0].length, 400);
  assert.equal(trimAudio(two, { gap: '400 ms', padding: '0 ms', sections: 'last' }).audio.channels[0].length, 400);
});
test('no sound: nothing is cut; fades shape the ends; silence has the length asked or the auto length', () => {
  const quiet = { sampleRate: rate, channels: [new Float32Array(100)] };
  assert.equal(trimAudio(quiet, {}).audio, quiet);
  const faded = trimAudio({ sampleRate: rate, channels: [burst(0, 1000, 1000)] }, { padding: '0 ms', fadein: '100 ms', fadeout: '10%' }).audio.channels[0];
  assert.equal(faded[0], 0); assert.ok(faded[50] < 0.5 && faded[500] === 0.5 && faded[999] === 0);
  assert.equal(silenceAudio({ length: '250 ms' }, 99, 1000).channels[0].length, 250);
  assert.equal(silenceAudio({ length: 'auto' }, 99, 1000).channels[0].length, 99);
});
