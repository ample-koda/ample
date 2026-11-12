// Live against offline: renders a fixed test signal through the live graph of a block (an OfflineAudioContext, the same code the knob moves) and through the browser render of
// the same block (the libraries of "Build and play preview"), and says how far apart they are. A block whose live sound is the render (the worklets) shows as exact; the Web Audio
// approximations show how far they are, so the word "approximate" is a measurement. Advisory; nothing is written. @ample:live-parity
import { liveEffectGraph, prepareWorklets } from './studio/preview/audio.js';
import { PARITY_REFERENCES, testSignal, RATE } from '../lib/dsp-references.mjs';
export { PARITY_REFERENCES, testSignal };

/** A word for the distance between live and render, in dB of the difference against the render (more negative is closer). */
export function describeDeviation(db) {
  if (!Number.isFinite(db)) return { word: 'unknown', level: 'unknown' };
  if (db <= -60) return { word: 'exact', level: 'exact' };
  if (db <= -35) return { word: 'close', level: 'close' };
  if (db <= -18) return { word: 'approximate', level: 'approximate' };
  return { word: 'differs', level: 'differs' };
}

/** Renders a signal ({ sampleRate, channels }) through the live graph of one block offline. Returns { channels (Float32Array[]), latencySamples }. */
export async function renderLive(type, params, signal, tempo = 120) {
  const frames = signal.channels[0].length;
  const context = new OfflineAudioContext(2, frames, signal.sampleRate), buffer = context.createBuffer(2, frames, signal.sampleRate);
  signal.channels.forEach((channel, c) => buffer.copyToChannel(channel, c));
  await prepareWorklets(context);
  const source = context.createBufferSource(), out = context.createGain();
  source.buffer = buffer; out.connect(context.destination);
  // the LFOs start with the sound, as in the render they are compared with
  const graph = liveEffectGraph(context, source, out, type, params, tempo, undefined, { position: () => context.currentTime, loop: () => frames / signal.sampleRate });
  source.start();
  const live = await context.startRendering(), latencySamples = Math.round((graph.latency || 0) * signal.sampleRate);
  graph.dispose();
  return { channels: [live.getChannelData(0), live.getChannelData(1)], latencySamples };
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
  // the LFOs start with the sound, as in the render they are compared with
  const graph = liveEffectGraph(context, source, out, type, params, tempo, undefined, { position: () => context.currentTime, loop: () => frames / RATE });
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
