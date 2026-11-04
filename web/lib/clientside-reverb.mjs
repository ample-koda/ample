import { cutoffCoefficient } from './one-pole.mjs';

// The comb and all-pass lengths of the classic Freeverb (samples at 44.1 kHz), as on the server (Effects.cs Reverb); the right channel is a little longer.
const COMBS = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617], ALLPASSES = [556, 441, 341, 225], SPREAD = 23;

/** Bytes the reverb needs: the feed and the wet sum (8 bytes a sample each) and the output (4) per channel, and a little for the rings. */
export function reverbWorkingSetBytes(frames, channels) { return (frames * channels * 20) + 65536; }

/**
 * The room of the server, sample for sample: the input loses its lows (160 Hz), eight damped combs ring in parallel and four all-passes smooth the tail;
 * the dry sound stays and mix sets the level of the room. Integer arithmetic like the server (every value stays far below 2^53, so the doubles are exact).
 */
export function processReverbBuffer(inputChannels, params, sampleRate, signal) {
  const frames = inputChannels[0]?.length || 0, size = Math.trunc(clamp(value(params.size, 50), 1, 100)), decay = Math.trunc(clamp(value(params.decay, 60), 0, 95)), mix = Math.trunc(clamp(value(params.mix, 25), 0, 100));
  const feedback = 22938 + Math.trunc(9175 * decay / 95), damping = 9830, g = feedback / 32768;
  const gain = Math.round(32768 * Math.sqrt(0.5 * (1 - (g * g)) / COMBS.length)), lowA = cutoffCoefficient(160, sampleRate);
  return inputChannels.map((input, channel) => {
    const spread = channel === 1 ? SPREAD : 0, pcm = new Float64Array(frames), feed = new Float64Array(frames), wet = new Float64Array(frames);
    let state = 0;
    for (let i = 0; i < frames; i++) {
      if ((i & 0x3FFF) === 0) signal?.throwIfAborted();
      pcm[i] = inputPcm(input[i]);
      state += Math.floor(lowA * (pcm[i] - state) / 32768);
      feed[i] = Math.floor((pcm[i] - state) * gain / 32768);
    }
    for (const base of COMBS) {
      const length = Math.max(1, Math.trunc((base + spread) * sampleRate * (60 + size) / (44100 * 110))), ring = new Float64Array(length);
      let store = 0;
      for (let i = 0, at = 0; i < frames; i++, at = at + 1 === length ? 0 : at + 1) {
        if ((i & 0x3FFF) === 0) signal?.throwIfAborted();
        const delayed = ring[at];
        store = Math.floor(((delayed * (32768 - damping)) + (store * damping)) / 32768);
        ring[at] = feed[i] + Math.floor(store * feedback / 32768);
        wet[i] += delayed;
      }
    }
    for (const base of ALLPASSES) {
      const length = Math.max(1, Math.trunc((base + spread) * sampleRate / 44100)), ring = new Float64Array(length);
      for (let i = 0, at = 0; i < frames; i++, at = at + 1 === length ? 0 : at + 1) {
        const delayed = ring[at], current = wet[i];
        wet[i] = delayed - current;
        ring[at] = current + Math.floor(delayed / 2);
      }
    }
    const output = new Float32Array(frames);
    for (let i = 0; i < frames; i++) {
      if ((i & 0x3FFF) === 0) signal?.throwIfAborted();
      const sample = clipPcm(pcm[i] + Math.trunc(wet[i] * mix / 100));
      output[i] = sample < 0 ? sample / 32768 : sample / 32767;
    }
    return output;
  });
}

export async function processReverbChannels(inputChannels, params, sampleRate, signal) {
  signal?.throwIfAborted();
  const worker = new Worker(new URL('./clientside-reverb-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  try {
    return await new Promise((resolve, reject) => {
      abort = () => reject(new DOMException('The local reverb preview was cancelled.', 'AbortError'));
      signal?.addEventListener('abort', abort, { once: true });
      worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(data.channels.map((channel) => new Float32Array(channel)));
      worker.onerror = (event) => reject(new Error(event.message || 'The local reverb preview failed.'));
      const channels = inputChannels.map((channel) => channel.slice());
      worker.postMessage({ channels: channels.map((channel) => channel.buffer), params, sampleRate }, channels.map((channel) => channel.buffer));
    });
  } finally { signal?.removeEventListener('abort', abort); worker.terminate(); }
}

function value(input, fallback) { const parsed = Number.parseFloat(input); return Number.isFinite(parsed) ? parsed : fallback; }
function clamp(input, minimum, maximum) { return Math.max(minimum, Math.min(maximum, input)); }
function inputPcm(input) { return Math.round(clamp(input * (input < 0 ? 32768 : 32767), -32768, 32767)); }
function clipPcm(input) { return clamp(input, -32768, 32767); }
