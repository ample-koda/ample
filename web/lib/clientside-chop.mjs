// The chop block in the browser: the same steps as Chop in the engine (ExtraChop.cs): cut a recording into slices (found by the level or given as start times) and put them on the beat in the order of a
// pattern. Float math, so an approximation of the engine. @ample:chop
import { pause } from './yield-slice.mjs';

const number = (value, fallback, min, max) => { const parsed = Number.parseFloat(String(value ?? '').replace(/[a-z %]/gi, '')); return Number.isFinite(parsed) ? Math.min(max, Math.max(min, parsed)) : fallback; };
const clean = (value) => String(value ?? '').replace(/^"|"$/g, '').trim();

/** The settings of a chop block (the parameter texts of the song). */
export function chopSettings(params) {
  return {
    sensitivity: number(params.sensitivity, 50, 0, 100), gap: number(params.gap, 150, 20, 1000), step: number(params.step, 1, 0.0625, 16), gate: number(params.gate, 90, 5, 100) / 100,
    fit: number(params.fit, 0, 0, 100) / 100, repeat: Math.round(number(params.repeat, 1, 1, 64)),
  };
}

/** The steps of a pattern text: { slice (0 based, -1 a rest), times }. Throws a message for a step that cannot be read. */
export function parsePattern(text, slices) {
  const body = clean(text);
  if (!body) return Array.from({ length: slices }, (_, slice) => ({ slice, times: 1 }));
  return body.split(/[\s,]+/).filter(Boolean).map((word) => {
    if (word === '-' || word === '.') return { slice: -1, times: 1 };
    const match = /^(\d+)(?:\*(\d+))?$/.exec(word), slice = match ? Number(match[1]) : 0, times = match?.[2] ? Number(match[2]) : 1;
    if (!match || slice < 1 || slice > slices || times < 1 || times > 16) throw new Error(`The pattern step '${word}' is not a slice number from 1 to ${slices}, a rest (-) or a repeat like 3*2.`);
    return { slice: slice - 1, times };
  });
}

/** The start of every slice (in samples) from the words of a sound: the parts louder than a share of the peak, short gaps closed. */
export function autoSlices(mono, rate, sensitivity = 50, gapMs = 150) {
  if (!mono.length) return [0];
  let peak = 1e-9;
  for (let i = 0; i < mono.length; i++) peak = Math.max(peak, Math.abs(mono[i]));
  const threshold = peak * 0.30 * Math.pow(0.01 / 0.30, Math.min(100, Math.max(0, sensitivity)) / 100);
  const smooth = 1 - Math.exp(-1 / (rate * 0.008)), gap = Math.max(1, Math.floor(gapMs * rate / 1000)), lead = Math.floor(0.006 * rate) + Math.floor(0.012 * rate);
  const starts = [];
  let level = 0, quiet = gap + 1;
  for (let i = 0; i < mono.length; i++) {
    level += (Math.abs(mono[i]) - level) * smooth;
    if (level > threshold) { if (quiet > gap) starts.push(Math.max(0, i - lead)); quiet = 0; } else quiet++;
  }
  return starts.length ? starts : [0];
}

/** Start times in seconds ("0.3 1.1 1.9") as samples; rising, inside the sound. Throws a message otherwise. */
export function parseStarts(text, rate, length) {
  const starts = [];
  for (const word of clean(text).split(/[\s,;]+/).filter(Boolean)) {
    const seconds = Number.parseFloat(word.replace(/s$/, ''));
    if (!Number.isFinite(seconds) || seconds < 0) throw new Error(`The slice start '${word}' is not a time in seconds.`);
    const at = Math.round(seconds * rate);
    if (at >= length || (starts.length && at <= starts.at(-1))) throw new Error(`The slice start '${word}' is past the end of the sound or not later than the one before it.`);
    starts.push(at);
  }
  return starts.length ? starts : [0];
}

/** The starts of the slices of a block: its own list, or the words found in the sound ('auto'). */
export function sliceStarts(mono, rate, params) {
  const s = chopSettings(params), text = clean(params.slices);
  return !text || text.toLowerCase() === 'auto' ? autoSlices(mono, rate, s.sensitivity, s.gap) : parseStarts(text, rate, mono.length);
}

/** Builds the chopped sound of one channel. */
export function chopChannel(source, starts, pattern, rate, samplesPerBeat, s) {
  const stepLength = Math.max(1, Math.round(samplesPerBeat * Math.max(0.0625, s.step))), repeats = Math.min(64, Math.max(1, s.repeat));
  const result = new Float32Array(stepLength * pattern.length * repeats), fade = Math.max(1, Math.floor(0.004 * rate));
  for (let round = 0; round < repeats; round++) {
    for (let step = 0; step < pattern.length; step++) {
      const { slice, times } = pattern[step];
      if (slice < 0) continue;
      const from = starts[Math.min(slice, starts.length - 1)], to = slice + 1 < starts.length ? starts[slice + 1] : source.length, natural = Math.max(1, to - from);
      const part = Math.floor(stepLength / times), sounding = Math.max(fade * 2, Math.floor(part * Math.min(1, Math.max(0.05, s.gate))));
      for (let k = 0; k < times; k++) {
        const begin = (round * pattern.length + step) * stepLength + k * part, speed = Math.pow(Math.min(2, Math.max(0.5, natural / part)), Math.min(1, Math.max(0, s.fit)));
        for (let i = 0; i < sounding && begin + i < result.length; i++) {
          const position = from + i * speed, index = Math.floor(position);
          if (position >= to - 1 || index + 1 >= source.length) break;
          result[begin + i] += (source[index] + (source[index + 1] - source[index]) * (position - index)) * Math.min(1, Math.min(i, sounding - 1 - i) / fade);
        }
      }
    }
  }
  return result;
}

/** Chops the audio ({ sampleRate, channels }) as the block says; tempo in beats per minute. */
export async function chopAudio(audio, params, tempo, signal) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, frames = audio.channels[0]?.length || 0, mono = new Float32Array(frames);
  for (const channel of audio.channels) for (let i = 0; i < frames; i++) mono[i] += channel[i] / audio.channels.length;
  signal?.throwIfAborted();
  const starts = sliceStarts(mono, rate, params), pattern = parsePattern(params.pattern, starts.length), settings = chopSettings(params);
  const channels = [];
  for (const channel of audio.channels) { await pause(); signal?.throwIfAborted(); channels.push(chopChannel(channel, starts, pattern, rate, rate * 60 / tempo, settings)); }
  return { sampleRate: rate, channels };
}
