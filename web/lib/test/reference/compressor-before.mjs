// compressor as it was before it moved onto dynamics-core.mjs: the reference the new code must equal bit for bit (test: dynamics-core.test.mjs). Imports are rewritten to the lib folder.
const pause = async () => {};
import { cutoffAlpha } from '../../one-pole.mjs';
const number = (value, fallback) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/db|ms|hz/gi, '').trim());
  return Number.isFinite(parsed) ? parsed : fallback;
};

function lowPass(samples, hz, rate) {
  const coefficient = cutoffAlpha(hz, rate);   // the key filter, as on the server (compressor version 6)
  const output = new Float32Array(samples.length);
  let state = 0;
  for (let i = 0; i < samples.length; i++) { state += coefficient * (samples[i] - state); output[i] = state; }
  return output;
}

/** AMPLE-linked compressor. Float envelope/gain math approximates the server Q15 path. */
export async function compressorAudio(audio, params, keyAudio, signal) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, length = audio.channels[0].length, channels = audio.channels;
  const thresholdDb = Math.max(-60, Math.min(0, number(params.threshold, -18)));
  const threshold = 10 ** (thresholdDb / 20);
  const ratio = Math.max(1, Math.min(20, number(params.ratio, 4)));
  const attack = Math.max(1, Math.trunc(Math.max(1, Math.min(500, number(params.attack, 10))) * rate / 1000));
  const release = Math.max(1, Math.trunc(Math.max(5, Math.min(2000, number(params.release, 120))) * rate / 1000));
  const makeup = 10 ** (Math.max(0, Math.min(12, number(params.makeup, 0))) / 20);
  const keyChannels = keyAudio?.channels || null, keyRate = keyAudio?.sampleRate || rate;
  const keyFilter = Math.max(0, Math.min(8000, number(params.keyFilter, 0)));
  const filteredKey = keyChannels && keyFilter > 0 ? keyChannels.map((channel) => {
    const low = lowPass(channel, keyFilter, keyRate), high = new Float32Array(channel.length);
    for (let i = 0; i < channel.length; i++) high[i] = channel[i] - low[i];
    return high;
  }) : keyChannels;
  const output = channels.map(() => new Float32Array(length));
  let envelope = 0;
  for (let i = 0; i < length; i++) {
    if ((i & 0xFFFF) === 0) { signal?.throwIfAborted(); await pause(); }
    let level = 0;
    if (filteredKey) {
      for (const channel of filteredKey) if (i < channel.length) level = Math.max(level, Math.abs(channel[i]));
    } else for (const channel of channels) level = Math.max(level, Math.abs(channel[i]));
    envelope += (level - envelope) / (level > envelope ? attack : release);
    const gain = envelope > threshold ? (threshold + (envelope - threshold) / ratio) / envelope : 1;
    for (let channel = 0; channel < channels.length; channel++) output[channel][i] = channels[channel][i] * gain * makeup;
  }
  return { sampleRate: rate, channels: output };
}
