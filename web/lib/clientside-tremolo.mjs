import { pause } from './yield-slice.mjs';
import { gateLevel } from './trance-gate.mjs';
import { barBeats } from './meter.mjs';

/** Applies AMPLE's tremolo/autopan LFO over decoded buffers; automation is not part of this block. */
export async function tremoloAudio(audio, params, tempo, firstBar, signal) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, samplesPerBar = Math.floor(rate * (barBeats() * 60) / tempo), division = Number.parseInt(String(params.sync || '').split('/')[1], 10);
  let hz = Math.max(0.05, Math.min(40, Number.parseFloat(params.rate) || 4));
  if (params.sync && params.sync !== 'off' && Number.isFinite(division) && division > 0) hz = rate / (samplesPerBar / division);
  const depth = Math.max(0, Math.min(100, Number.parseFloat(params.depth) || 0)) / 100, phase = (Number.parseFloat(params.phase) || 0) / 360, pan = params.mode === 'autopan', shape = params.shape || 'sine';
  if (!['sine', 'triangle', 'square', 'gate'].includes(shape)) throw new Error(`Unsupported tremolo shape “${shape}”.`);
  const pattern = shape === 'gate' ? String(params.pattern ?? 'x.x.x.x.x.x.x.x.').replace(/^"|"$/g, '') : '', stepLength = samplesPerBar / (Number.isFinite(division) && division > 0 ? division : 16), gateRamp = Math.min(Math.floor(rate * 0.002), stepLength / 2);
  const stereo = audio.channels.length > 1, count = stereo || pan ? 2 : 1, channels = Array.from({ length: count }, () => new Float32Array(audio.channels[0].length)), origin = (firstBar - 1) * samplesPerBar;
  for (let start = 0; start < channels[0].length; start += 65536) {
    signal?.throwIfAborted();
    const end = Math.min(channels[0].length, start + 65536);
    for (let i = start; i < end; i++) {
      const t = ((origin + i) * hz / rate) + phase, frac = t - Math.floor(t);
      const lfo = shape === 'gate' ? gateLevel(pattern, origin + i, stepLength, gateRamp) : shape === 'triangle' ? (4 * Math.abs(frac - 0.5)) - 1 : shape === 'square' ? frac < 0.5 ? 1 : -1 : Math.sin(2 * Math.PI * frac);
      const up = 1 - (depth * (1 - lfo) / 2), left = audio.channels[0][i], right = audio.channels[1]?.[i] ?? left;
      channels[0][i] = left * (pan ? up : up);
      if (count === 2) channels[1][i] = (pan ? (stereo ? right : left) : right) * (pan ? 1 - depth * (1 + lfo) / 2 : up);
    }
    await pause();
  }
  return { sampleRate: rate, channels };
}
