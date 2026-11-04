/**
 * The `click` block in the browser: the same integer square bursts as the server's ExtraClick.cs, one per beat (two or four with per_beat),
 * a louder, higher one on the first beat of every bar (accent), 25 ms long at most with a linear fade. The server writes 16-bit samples; here they are divided by 32768.
 */

import { barBeats } from './meter.mjs';

const burst = (samples, start, sampleRate, accent, step) => {
  const length = Math.min(Math.max(1, Math.trunc(sampleRate * 25 / 1000)), step);
  const period = Math.max(2, Math.trunc(sampleRate / (accent ? 1500 : 1000)));
  const peak = accent ? 16000 : 9000;
  for (let k = 0; k < length && start + k < samples.length; k++) {
    const amplitude = Math.trunc(peak * (length - k) / length);
    samples[start + k] = ((Math.trunc(k / Math.trunc(period / 2))) % 2 === 0 ? amplitude : -amplitude) / 32768;
  }
};

/** The clicks of a block over `bars` bars (what the server renders for one placement), as a mono Float32Array. */
export function clickBars(params, bars, samplesPerBar, sampleRate) {
  const accent = String(params.accent ?? 'on').trim().toLowerCase() !== 'off';
  const requested = Number.parseInt(String(params.per_beat ?? '').trim(), 10), perBeat = [1, 2, 4].includes(requested) ? requested : 1;
  const samples = new Float32Array(Math.max(0, bars * samplesPerBar));
  const perBar = barBeats() * perBeat, inBar = Math.ceil(perBar);   // clicks in a bar: 4 in 4/4, 3 in 3/4, 3.5 in 7/8
  const step = Math.max(1, Math.trunc(samplesPerBar / perBar));
  for (let bar = 0; bar < bars; bar++) for (let j = 0; j < inBar; j++) burst(samples, bar * samplesPerBar + Math.trunc(j * samplesPerBar / perBar), sampleRate, accent && j === 0, step);
  return samples;
}

/**
 * Places the clicks on the song timeline for a preview range: silence outside the block's parts, the clicks of every part inside.
 * @param {{at:number,length:number}[]} parts the placements of the block (first bar and length in bars)
 */
export function clickAudio(params, parts, samplesPerBar, sampleRate, firstBar, bars) {
  const out = new Float32Array(Math.max(0, bars * samplesPerBar));
  if (String(params.enabled ?? 'on') === 'off') return { sampleRate, channels: [out] };
  for (const part of parts) {
    const from = Math.max(firstBar, part.at), to = Math.min(firstBar + bars, part.at + part.length);
    if (to <= from) continue;
    out.set(clickBars(params, part.length, samplesPerBar, sampleRate).subarray((from - part.at) * samplesPerBar, (to - part.at) * samplesPerBar), (from - firstBar) * samplesPerBar);
  }
  return { sampleRate, channels: [out] };
}
