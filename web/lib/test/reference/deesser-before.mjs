// deesser as it was before it moved onto dynamics-core.mjs: the reference the new code must equal bit for bit (test: dynamics-core.test.mjs).
const pause = async () => {};
import { cutoffAlpha } from '../../one-pole.mjs';
const db = (value, fallback) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/dBFS|dB/gi, '').trim());
  return Math.max(-60, Math.min(0, Number.isFinite(parsed) ? parsed : fallback));
};

/** Attenuates the high band with AMPLE's linked detector timings; float math is an approximation. */
export async function deesserAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, frames = audio.channels[0]?.length || 0;
  const hz = Math.max(2000, Math.min(rate / 2 - 100, Number.parseFloat(String(params.frequency ?? '6000').replace('Hz', '')) || 6000));
  const coefficient = cutoffAlpha(hz, rate);   // as on the server (de-esser version 2)
  const threshold = 10 ** (db(params.threshold, -30) / 20), ratio = Math.max(1, Math.min(20, Number.parseFloat(params.ratio) || 4));
  const attack = Math.max(1, Math.floor(rate / 2000)), release = Math.max(1, Math.floor(rate / 50));
  const low = audio.channels.map(() => 0), output = audio.channels.map(() => new Float32Array(frames));
  let envelope = 0;
  for (let start = 0; start < frames; start += 65536) {
    signal?.throwIfAborted();
    const end = Math.min(frames, start + 65536);
    for (let i = start; i < end; i++) {
      let level = 0;
      for (let c = 0; c < audio.channels.length; c++) {
        low[c] += coefficient * (audio.channels[c][i] - low[c]);
        level = Math.max(level, Math.abs(audio.channels[c][i] - low[c]));
      }
      envelope += (level - envelope) / (level > envelope ? attack : release);
      const gain = envelope > threshold ? (threshold + (envelope - threshold) / ratio) / envelope : 1;
      for (let c = 0; c < audio.channels.length; c++) output[c][i] = low[c] + (audio.channels[c][i] - low[c]) * gain;
    }
    await pause();
  }
  return { sampleRate: rate, channels: output };
}
