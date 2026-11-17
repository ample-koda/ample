import test from 'node:test';
import assert from 'node:assert/strict';
import { fxCore } from '../fx-core.mjs';
import { flangerAudio } from '../clientside-flanger.mjs';
import { phaserAudio } from '../clientside-phaser.mjs';
import { eqAudio } from '../clientside-eq.mjs';
import { peqAudio } from '../clientside-peq.mjs';
import { triangleWave, phaserWave } from '../live-lfo.mjs';

const RATE = 48000, FRAMES = 20000;
const signal = () => {
  let seed = 7; const noise = () => { seed = (seed * 1664525 + 1013904223) >>> 0; return seed / 2 ** 31 - 1; };
  return { sampleRate: RATE, channels: [0, 1].map((c) => Float32Array.from({ length: FRAMES }, (_, i) => 0.5 * noise() * 0.5 + 0.3 * Math.sin(i * (0.02 + c * 0.01)))) };
};
// the live worklet: 128 samples at a time, the wet sound only (flanger, phaser: the dry/wet mix is a graph matter, computed here like the offline render)
function viaCore(kind, params, audio, lfo, mix = null) {
  const core = fxCore(kind, RATE, 2), wave = lfo ? Float32Array.from({ length: FRAMES }, (_, i) => lfo(i)) : null;
  core.configure(params);
  const outs = audio.channels.map(() => new Float32Array(FRAMES));
  for (let s = 0; s < FRAMES; s += 128) { const e = Math.min(FRAMES, s + 128); core.process(audio.channels, outs, wave, s, e); }
  return outs.map((out, c) => mix === null ? out : Float32Array.from(out, (v, i) => audio.channels[c][i] * (1 - mix) + v * mix));
}
const dbBelow = (a, b) => { let d = 0, e = 0; for (let c = 0; c < a.length; c++) for (let i = 0; i < a[c].length; i++) { d += (a[c][i] - b[c][i]) ** 2; e += b[c][i] ** 2; } return 10 * Math.log10(Math.max(d, 1e-30) / e); };

test('flanger core is the offline flanger', async () => {
  for (const params of [{ depth: '2 ms', period: '2000 ms', feedback: '50', mix: '50' }, { depth: '3 ms', period: '300 ms', feedback: '90', mix: '100' }, { depth: '1 ms', period: '5000 ms', feedback: '0', mix: '30' }]) {
    const audio = signal(), offline = await flangerAudio(audio, params);
    const period = Math.floor(Number.parseFloat(params.period) * RATE / 1000), mix = Number.parseFloat(params.mix) / 100;
    assert.ok(dbBelow(viaCore('flanger', params, audio, triangleWave(period), mix), offline.channels) <= -100, JSON.stringify(params));
  }
});

test('phaser core is the offline phaser', async () => {
  for (const params of [{ stages: '4', period: '2500 ms', mix: '50' }, { stages: '8', period: '400 ms', mix: '100' }, { stages: '2', period: '1000 ms', mix: '70' }]) {
    const audio = signal(), offline = await phaserAudio(audio, params);
    const period = Math.trunc(Number.parseFloat(params.period) * RATE / 1000), mix = Number.parseFloat(params.mix) / 100;
    assert.ok(dbBelow(viaCore('phaser', params, audio, phaserWave(period), mix), offline.channels) <= -100, JSON.stringify(params));
  }
});

test('eq core is the offline eq', async () => {
  for (const params of [{ low: '4 dB', high: '-3 dB' }, { low: '-9 dB', mid: '5 dB', high: '8 dB', low_hz: '120 Hz', high_hz: '6000 Hz' }]) {
    const audio = signal();
    assert.ok(dbBelow(viaCore('eq', params, audio), (await eqAudio(audio, params)).channels) <= -100);
  }
});

test('peq core is the offline peq, also when a band is switched on while it runs', async () => {
  for (const params of [{ band2_gain: '4 dB' }, { band1_gain: '-6 dB', band3_gain: '5 dB', band4_type: 'notch', band4_hz: '3000 Hz', band5_type: 'highcut', band5_hz: '9000 Hz', band5_q: '2', band6_enabled: 'off' }]) {
    const audio = signal();
    assert.ok(dbBelow(viaCore('peq', params, audio), (await peqAudio(audio, params)).channels) <= -100);
  }
});
