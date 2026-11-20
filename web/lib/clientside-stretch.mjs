import { pause } from './yield-slice.mjs';
import { barBeats } from './meter.mjs';
import { panPoints } from './live-pan-auto.mjs';
import { curveAt } from './clientside-control-curves.mjs';

export function stretchWorkingSetBytes(frames, channels) { return frames * channels * 12; }

/** The speed in percent of every sample when it is automated (auto, lfo, env, or the points of a control), as Effects.cs: held between 25 and 400. Null for a plain speed. */
export function speedCurve(value, samplesPerBar, rate, firstBar, length) {
  const points = panPoints(value, samplesPerBar, rate);
  return points ? curveAt(points, Math.max(1, firstBar), samplesPerBar, length, (v) => Math.max(25, Math.min(400, v))) : null;
}

/**
 * A speed that changes along the output (TimeStretch.Speed with a percent per sample): grains of 50 ms, a hop of half a grain, each grain read from the input at the position
 * that the speeds at the start of the earlier grains add up to. The input and the result are 16-bit whole numbers (Int32Array), the length stays.
 */
export async function stretchWithCurve(input, percent, rate, signal) {
  if (!percent.length || percent.every((v) => v === 100)) return input;
  const grain = Math.max(64, Math.trunc(rate / 20)), hopOut = Math.trunc(grain / 2), window = new Int32Array(grain);
  for (let i = 0; i < grain; i++) window[i] = Math.trunc(32768 * Math.min(i, grain - i) * 2 / grain);
  const y = new Int32Array(input.length);
  let position = 0;   // Q16 samples of the input
  for (let out = 0, count = 0; out < y.length; out += hopOut, count++) {
    if (count % 256 === 0) { signal?.throwIfAborted(); await pause(); }
    const start = Math.floor(position / 65536);
    if (start >= input.length) break;
    for (let i = 0; i < grain && out + i < y.length; i++) if (start + i < input.length) y[out + i] += (input[start + i] * window[i]) >> 15;
    position += Math.trunc(hopOut * Math.max(25, Math.min(400, percent[Math.min(out, percent.length - 1)])) * 65536 / 100);
  }
  return y;
}

/** Stretches with a speed that follows a curve: the float channels in, the float channels out, through the whole numbers of the server. */
async function stretchCurved(inputChannels, sampleRate, params, signal, { tempo, firstBar }) {
  const samplesPerBar = Math.floor(barBeats() * 60 * sampleRate * 1000 / Math.round(tempo * 1000));
  const percent = speedCurve(params.speed, samplesPerBar, sampleRate, firstBar, inputChannels[0].length);
  if (!percent) throw new Error('Only a stretch speed with points is previewed here.');
  const out = [];
  for (const input of inputChannels) {
    const pcm = new Int32Array(input.length);
    for (let i = 0; i < input.length; i++) pcm[i] = Math.round(Math.max(-1, Math.min(1, input[i])) * (input[i] < 0 ? 32768 : 32767));
    const moved = await stretchWithCurve(pcm, percent, sampleRate, signal);
    out.push(Float32Array.from(moved, (v) => Math.max(-32768, Math.min(32767, v)) / 32768));
  }
  return out;
}

export async function processStretchChannels(inputChannels, sampleRate, params, signal, { tempo = 120, firstBar = 1 } = {}) {
  if (Array.isArray(params.speed) || /^\s*(auto|lfo|env)\(/.test(String(params.speed ?? ''))) return stretchCurved(inputChannels, sampleRate, params, signal, { tempo, firstBar });
  const speed = Number(params.speed ?? '100');
  if (!Number.isFinite(speed)) throw new Error('Automated or invalid stretch speed uses the server renderer.');
  signal?.throwIfAborted();
  const worker = new Worker(new URL('./clientside-stretch-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  try {
    return await new Promise((resolve, reject) => {
      abort = () => reject(new DOMException('The local stretch preview was cancelled.', 'AbortError'));
      signal?.addEventListener('abort', abort, { once: true });
      worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(data.channels.map((channel) => new Float32Array(channel)));
      worker.onerror = (event) => reject(new Error(event.message || 'The local stretch preview failed.'));
      const channels = inputChannels.map((input) => {
        const pcm = new Int16Array(input.length);
        for (let i = 0; i < input.length; i++) pcm[i] = Math.round(Math.max(-1, Math.min(1, input[i])) * (input[i] < 0 ? 32768 : 32767));
        return pcm;
      });
      const buffers = channels.map((channel) => channel.buffer);
      worker.postMessage({ channels: buffers, speed, sampleRate }, buffers);
    });
  } finally { signal?.removeEventListener('abort', abort); worker.terminate(); }
}
