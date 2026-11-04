import test from 'node:test';
import assert from 'node:assert/strict';
import { peqAudio, activePeqBands } from '../clientside-peq.mjs';

const rms = (values) => Math.sqrt(values.reduce((sum, value) => sum + value * value, 0) / values.length);
const tone = (hz, rate = 12000) => Float32Array.from({ length: 6000 }, (_, i) => Math.sin(i * 2 * Math.PI * hz / rate));
const run = async (hz, params) => rms((await peqAudio({ sampleRate: 12000, channels: [tone(hz)] }, params, new AbortController().signal)).channels[0].slice(1000));

test('a new block with every band at 0 dB changes nothing and returns the input', async () => {
  const audio = { sampleRate: 12000, channels: [tone(500)] };
  assert.equal(activePeqBands({}, 12000).length, 0);
  assert.equal(await peqAudio(audio, {}, new AbortController().signal), audio);
  assert.equal(await peqAudio(audio, { enabled: 'off', band2_gain: '12 dB' }, new AbortController().signal), audio);
});

test('a peak lifts its frequency, a cut lowers it, a notch removes it, a shelf moves a whole side', async () => {
  const plain = rms(tone(1000).slice(1000));
  assert.ok(await run(1000, { band3_hz: '1000 Hz', band3_gain: '12 dB', band3_q: '2' }) > plain * 3);
  assert.ok(await run(1000, { band3_hz: '1000 Hz', band3_gain: '-12 dB', band3_q: '2' }) < plain * 0.35);
  assert.ok(Math.abs(await run(100, { band3_hz: '1000 Hz', band3_gain: '12 dB', band3_q: '4' }) - rms(tone(100).slice(1000))) < 0.05);
  assert.ok(await run(1000, { band4_type: 'notch', band4_hz: '1000 Hz', band4_q: '6' }) < plain * 0.1);
  assert.ok(await run(100, { band1_type: 'lowshelf', band1_hz: '300 Hz', band1_gain: '12 dB' }) > rms(tone(100).slice(1000)) * 3);
  assert.ok(await run(4000, { band6_type: 'highshelf', band6_hz: '2000 Hz', band6_gain: '-12 dB' }) < rms(tone(4000).slice(1000)) * 0.35);
});

test('lowcut and highcut remove one side with 12 dB per octave, and a band switched off is left out', async () => {
  const plainLow = rms(tone(100).slice(1000)), plainHigh = rms(tone(4000).slice(1000));
  assert.ok(await run(100, { band1_type: 'lowcut', band1_hz: '1000 Hz', band1_q: '0.7' }) < plainLow * 0.05);
  assert.ok(Math.abs(await run(4000, { band1_type: 'lowcut', band1_hz: '1000 Hz', band1_q: '0.7' }) - plainHigh) < plainHigh * 0.1);
  assert.ok(await run(4000, { band6_type: 'highcut', band6_hz: '500 Hz', band6_q: '0.7' }) < plainHigh * 0.05);
  assert.equal(activePeqBands({ band1_type: 'lowcut' }, 12000).length, 1);   // a cut is active at 0 dB gain
  assert.equal(activePeqBands({ band1_type: 'lowcut', band1_enabled: 'off' }, 12000).length, 0);
  assert.equal(activePeqBands({ band3_gain: '6 dB', band3_enabled: 'off' }, 12000).length, 0);
});
