// The browser (offline JS) renders of the blocks that have a live graph, and the deterministic test signal, in one node-safe module (local-preview chunk 6.5). The live-parity check,
// the conformance vectors of the server (tools/gen-dsp-vectors.mjs) and their tests all use it. @ample:dsp-references
import { processReverbBuffer } from './clientside-reverb.mjs';
import { gateAudio } from './clientside-gate.mjs';
import { limiterAudio } from './clientside-limiter.mjs';
import { deesserAudio } from './clientside-deesser.mjs';
import { eqAudio } from './clientside-eq.mjs';
import { peqAudio } from './clientside-peq.mjs';
import { filterAudio } from './clientside-filter.mjs';
import { compressorAudio } from './clientside-compressor.mjs';
import { delayAudio } from './clientside-delay.mjs';
import { distortionAudio } from './clientside-distortion.mjs';
import { chorusAudio } from './clientside-chorus.mjs';
import { flangerAudio } from './clientside-flanger.mjs';
import { phaserAudio } from './clientside-phaser.mjs';
import { panAudio } from './clientside-pan.mjs';
import { widthAudio } from './clientside-width.mjs';
import { tremoloAudio } from './clientside-tremolo.mjs';
import { volumeAudio } from './clientside-volume.mjs';
import { masterAudioBuffer } from './clientside-mix.mjs';

export const RATE = 44100;
/** The render of the browser for each block that has a live graph: (audio, params, tempo) => audio. */
export const PARITY_REFERENCES = {
  reverb: (audio, params) => ({ sampleRate: audio.sampleRate, channels: processReverbBuffer(audio.channels, params, audio.sampleRate) }),
  gate: (audio, params) => gateAudio(audio, params), limiter: (audio, params) => limiterAudio(audio, params), deesser: (audio, params) => deesserAudio(audio, params),
  eq: (audio, params) => eqAudio(audio, params), peq: (audio, params) => peqAudio(audio, params), delay: (audio, params, tempo) => delayAudio(audio, params, undefined, tempo), distortion: (audio, params) => distortionAudio(audio, params),
  chorus: (audio, params) => chorusAudio(audio, params), flanger: (audio, params) => flangerAudio(audio, params), phaser: (audio, params) => phaserAudio(audio, params),
  master: (audio, params, tempo) => masterAudioBuffer(audio, params, tempo),
  pan: (audio, params) => panAudio(audio, params), width: (audio, params) => widthAudio(audio, params), volume: (audio, params) => volumeAudio(audio, params),
  filter: (audio, params, tempo) => filterAudio(audio, params, tempo, 1),
  compressor: (audio, params) => compressorAudio(audio, params, null),
  tremolo: (audio, params, tempo) => tremoloAudio(audio, params, tempo, 1),   // duck and the tremolo gate follow the playing take, so a silent offline context cannot compare them
};

/** The test signal: a stereo mix of gliding tones and bursts of noise at two levels (for the dynamics blocks), deterministic. */
export function testSignal(seconds = 2, rate = RATE) {
  const frames = Math.floor(seconds * rate), left = new Float32Array(frames), right = new Float32Array(frames);
  let seed = 90210;
  for (let i = 0; i < frames; i++) {
    seed = (seed * 1664525 + 1013904223) >>> 0;
    const noise = (seed / 2 ** 32) * 2 - 1, t = i / rate, loud = Math.floor(t * 2) % 2 === 0 ? .7 : .08;
    const tone = Math.sin(2 * Math.PI * (220 + 60 * t) * t) * .5 + Math.sin(2 * Math.PI * 3100 * t) * .15;
    left[i] = loud * tone + noise * (t % 1 < .15 ? .3 : .01);
    right[i] = loud * (tone * .8 + Math.sin(2 * Math.PI * 330 * t) * .2);
  }
  return { sampleRate: rate, channels: [left, right] };
}


/** The parameters each block is checked with (text values as in the song). A block that is not listed has no conformance vector. */
export const CONFORMANCE_PARAMS = {
  reverb: { size: '50', decay: '60', mix: '40' }, gate: { threshold: '-30 dB' }, limiter: { ceiling: '-6 dB' }, deesser: { threshold: '-40 dB' }, eq: { low: '4 dB', high: '-3 dB' },
  peq: { band2_gain: '4 dB' }, filter: { cutoff: '800 Hz' }, compressor: { threshold: '-18 dB', ratio: '4' }, delay: { time: '250 ms', feedback: '35', mix: '30' }, distortion: { drive: '12 dB', mix: '100' },
  chorus: { depth: '5', period: '1500 ms', mix: '50' }, flanger: { depth: '2 ms', period: '2000 ms', feedback: '50', mix: '50' }, phaser: { stages: '4', period: '2500 ms', mix: '50' },
  pan: { pan: '16' }, width: { width: '150' }, volume: { gain: '-6 dB' }, tremolo: { shape: 'sine', rate: '4', depth: '50' },
};
export const CONFORMANCE_SECONDS = 0.4, CONFORMANCE_TEMPO = 120;
