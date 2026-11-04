import { limiterAudio } from './clientside-limiter.mjs';
import { pause } from './yield-slice.mjs';
import { barBeats } from './meter.mjs';

const gain = (text, fallback) => {
  const value = Number.parseInt(String(text ?? '').replace(/dB/gi, '').trim(), 10);
  return Number.isFinite(value) ? Math.max(-60, Math.min(48, value)) : fallback;
};
const linear = (db) => 10 ** (db / 20);
const softLimit = (value) => {
  const sign = Math.sign(value), magnitude = Math.abs(value), knee = 30000 / 32768;
  return sign * (magnitude > knee ? knee + ((magnitude - knee) * (1 - knee)) / (magnitude - knee + 1 - knee) : magnitude);
};

/** Mixes decoded mono/stereo buffers locally. Result is an approximate float mix, not a take. */
export async function mixAudioBuffers(inputs, options, signal) {
  if (!inputs.length) throw new Error('This mix has no enabled audio inputs.');
  const sampleRate = inputs[0].audio.sampleRate;
  if (inputs.some((item) => item.audio.sampleRate !== sampleRate)) throw new Error('Connected audio inputs use different sample rates and cannot be mixed locally yet.');
  const length = Math.max(...inputs.map((item) => item.audio.channels[0].length));
  const stereo = options.channels === 'stereo' || options.channels !== 'mono' && inputs.some((item) => item.audio.channels.length > 1);
  const count = stereo ? 2 : 1, channels = Array.from({ length: count }, () => new Float32Array(length));
  const levels = String(options.gains ?? '').trim().split(/[\s,]+/).filter(Boolean);
  const pans = String(options.pan ?? '').trim().split(/[\s,]+/).filter(Boolean);
  for (let lane = 0; lane < inputs.length; lane++) {
    const { audio, index } = inputs[lane];
    const amount = linear(gain(levels[index], 0));
    const pan = pans[index] === 'l' ? -1 : pans[index] === 'r' ? 1 : 0;
    for (let start = 0; start < length; start += 65536) {
      signal?.throwIfAborted();
      const end = Math.min(length, start + 65536);
      for (let i = start; i < end; i++) {
        const left = audio.channels[0][i] || 0;
        const right = audio.channels[1]?.[i] ?? left;
        if (stereo) {
          channels[0][i] += left * amount * (pan > 0 ? 1 - pan : 1);
          channels[1][i] += right * amount * (pan < 0 ? 1 + pan : 1);
        } else channels[0][i] += (left + right) * 0.5 * amount;
      }
      await pause();
    }
  }
  const busGain = options.mute ? 0 : linear(gain(options.busGain, 0));
  for (const channel of channels) for (let start = 0; start < length; start += 65536) {
    signal?.throwIfAborted();
    const end = Math.min(length, start + 65536);
    for (let i = start; i < end; i++) channel[i] = softLimit(channel[i] * busGain);
    await pause();
  }
  return { sampleRate, channels };
}

/** Builds both send outputs; through stays dry and send applies the per-lane levels. */
export async function sendAudioBuffers(inputs, options, signal) {
  const through = await mixAudioBuffers(inputs, { channels: options.channels }, signal);
  const levels = String(options.levels ?? '-12 -12 -12 -12').trim().split(/[\s,]+/).filter(Boolean);
  const sendInputs = inputs.map(({ audio, index }) => ({ audio: {
    sampleRate: audio.sampleRate,
    channels: audio.channels.map((channel) => Float32Array.from(channel, (sample) => sample * linear(gain(levels[index], -12)))),
  }, index }));
  return { through, send: await mixAudioBuffers(sendInputs, { channels: options.channels }, signal) };
}

/** Limits before slicing, like the server master; target is a loudness reference only. */
export async function masterAudioBuffer(audio, options, tempo, signal) {
  if (options.enabled === 'off') return audio;
  const limited = await limiterAudio(audio, { ceiling: Math.max(-12, gain(options.limiter, -1)), release: 100, lookahead: 5 }, signal);
  let first = Math.max(1, Number.parseInt(options.slice_from, 10) || 1);
  let end = Number.parseInt(options.slice_to, 10) || Math.ceil(audio.channels[0].length / (audio.sampleRate * (barBeats() * 60) / tempo));
  if (end < first) throw new Error('The master slice ends before it starts.');
  const startSample = Math.min(audio.channels[0].length, Math.round((first - 1) * audio.sampleRate * (barBeats() * 60) / tempo));
  const endSample = Math.min(audio.channels[0].length, Math.round(end * audio.sampleRate * (barBeats() * 60) / tempo));
  const channels = limited.channels.map((channel) => channel.slice(startSample, endSample));
  return { sampleRate: audio.sampleRate, channels };
}
