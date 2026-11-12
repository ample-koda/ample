// Live against offline: renders a fixed test signal through the live graph of a block (an OfflineAudioContext, the same code the knob moves) and through the browser render of
// the same block (the libraries of "Build and play preview"), and says how far apart they are. A block whose live sound is the render (the worklets) shows as exact; the Web Audio
// approximations show how far they are, so the word "approximate" is a measurement. Advisory; nothing is written. @ample:live-parity
import { liveEffectGraph, prepareWorklets } from './studio/preview/audio.js';
import { processReverbBuffer } from '../lib/clientside-reverb.mjs';
import { gateAudio } from '../lib/clientside-gate.mjs';
import { limiterAudio } from '../lib/clientside-limiter.mjs';
import { deesserAudio } from '../lib/clientside-deesser.mjs';
import { eqAudio } from '../lib/clientside-eq.mjs';
import { peqAudio } from '../lib/clientside-peq.mjs';
import { filterAudio } from '../lib/clientside-filter.mjs';
import { compressorAudio } from '../lib/clientside-compressor.mjs';
import { delayAudio } from '../lib/clientside-delay.mjs';
import { distortionAudio } from '../lib/clientside-distortion.mjs';
import { chorusAudio } from '../lib/clientside-chorus.mjs';
import { flangerAudio } from '../lib/clientside-flanger.mjs';
import { phaserAudio } from '../lib/clientside-phaser.mjs';
import { panAudio } from '../lib/clientside-pan.mjs';
import { widthAudio } from '../lib/clientside-width.mjs';
import { tremoloAudio } from '../lib/clientside-tremolo.mjs';
import { volumeAudio } from '../lib/clientside-volume.mjs';

const RATE = 44100;
/** The render of the browser for each block that has a live graph: (audio, params, tempo) => audio. */
export const PARITY_REFERENCES = {
  reverb: (audio, params) => ({ sampleRate: audio.sampleRate, channels: processReverbBuffer(audio.channels, params, audio.sampleRate) }),
  gate: (audio, params) => gateAudio(audio, params), limiter: (audio, params) => limiterAudio(audio, params), deesser: (audio, params) => deesserAudio(audio, params),
  eq: (audio, params) => eqAudio(audio, params), peq: (audio, params) => peqAudio(audio, params), delay: (audio, params) => delayAudio(audio, params), distortion: (audio, params) => distortionAudio(audio, params),
  chorus: (audio, params) => chorusAudio(audio, params), flanger: (audio, params) => flangerAudio(audio, params), phaser: (audio, params) => phaserAudio(audio, params),
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

/** A word for the distance between live and render, in dB of the difference against the render (more negative is closer). */
export function describeDeviation(db) {
  if (!Number.isFinite(db)) return { word: 'unknown', level: 'unknown' };
  if (db <= -60) return { word: 'exact', level: 'exact' };
  if (db <= -35) return { word: 'close', level: 'close' };
  if (db <= -18) return { word: 'approximate', level: 'approximate' };
  return { word: 'differs', level: 'differs' };
}

const cache = new Map();
/** Checks one block with its parameters. Returns { type, deviationDb, level, word, latencySamples } or null when the block has no browser render to compare with. */
export async function parityCheck(type, params, { tempo = 120, seconds = 2 } = {}) {
  const reference = PARITY_REFERENCES[type];
  if (!reference) return null;
  const key = JSON.stringify([type, params, tempo]);
  if (cache.has(key)) return cache.get(key);
  const signal = testSignal(seconds), frames = signal.channels[0].length;
  const context = new OfflineAudioContext(2, frames, RATE), buffer = context.createBuffer(2, frames, RATE);
  signal.channels.forEach((channel, c) => buffer.copyToChannel(channel, c));
  await prepareWorklets(context);
  const source = context.createBufferSource(), out = context.createGain();
  source.buffer = buffer; out.connect(context.destination);
  const graph = liveEffectGraph(context, source, out, type, params, tempo);
  source.start();
  const live = await context.startRendering(), delay = Math.round((graph.latency || 0) * RATE);
  graph.dispose();
  const rendered = await reference({ sampleRate: RATE, channels: signal.channels.map((c) => c.slice()) }, params, tempo);
  let difference = 0, energy = 0;
  const skip = Math.floor(RATE * .05);   // the first 50 ms: the smoothing of the knobs settles
  for (let c = 0; c < 2; c++) {
    const a = live.getChannelData(c), b = rendered.channels[Math.min(c, rendered.channels.length - 1)];
    for (let i = skip; i < frames - delay; i++) { const d = a[i + delay] - b[i]; difference += d * d; energy += b[i] * b[i]; }
  }
  const deviationDb = energy > 0 ? 10 * Math.log10(Math.max(difference, 1e-20) / energy) : NaN;
  const result = { type, deviationDb, latencySamples: delay, ...describeDeviation(deviationDb) };
  cache.set(key, result);
  return result;
}
