// The built-in drum voice as hits (plan local-preview F2). The offline renderer (clientside-drums-worker.js) and the live chain share this file: the song's pattern is a list of hits
// (kick, snare, hat) with a start, a length and an amplitude; every hit is rendered once, exactly (the integer maths of the server's built-in kit), and the noise of a hit
// comes from a position in the seeded stream, so the same hit always sounds the same. The offline render adds the hits up; the live chain triggers them on the audio clock.
import { StableRng } from './stable-rng.mjs';

const clip = (value) => Math.max(-32768, Math.min(32767, value));

/** The pattern names of a song text: `beat-A*2 beat-B` is beat-A, beat-A, beat-B. */
export const sequence = (text) => String(text || '').trim().split(/[\s,]+/).filter(Boolean).flatMap((entry) => {
  const [name, count] = entry.split('*');
  return Array.from({ length: Number(count || 1) }, () => name);
});

/** The seeded noise of the kit as a lazily grown stream: at(i) is the i-th 16-bit draw (what the old renderer drew one after the other). */
export function noiseStream(seed) {
  const rng = new StableRng(BigInt(seed)), values = [];
  return { at(index) { while (values.length <= index) values.push(Number(rng.next() >> 48n)); return values[index]; } };
}

/**
 * The hits of a take: [{ bar, step, kind, start, length, tonal, amplitude, noiseAt }] in the order the kit plays them. `start` and `length` are in samples from the first bar,
 * `length` already cut at the end of the take, `noiseAt` is where in the noise stream the hit starts (tonal hits use none). Same arguments as the offline render.
 */
export function drumHits({ params, sampleRate, firstBar, bars, activeBars, beatsPerBar = 4 }) {
  const tempoMilli = Math.round((Number(params.tempo) || 120) * 1000), samplesPerBar = Math.floor((beatsPerBar * 60 * sampleRate * 1000) / tempoMilli), steps = Math.max(1, Math.round(4 * beatsPerBar));
  const total = bars * samplesPerBar, step = Math.floor(samplesPerBar / steps), song = sequence(params.song), hits = [];
  let noise = 0;
  const add = (bar, index, kind, at, length, tonal, amplitude) => {
    const cut = Math.min(length, total - at);
    if (cut <= 0) return;
    hits.push({ bar, step: index, kind, start: at, length: cut, fullLength: length, tonal, amplitude, noiseAt: tonal ? 0 : noise });
    if (!tonal) noise += cut;
  };
  for (const bar of activeBars) {
    const start = (bar - firstBar) * samplesPerBar, selected = song.length ? song[((bar - firstBar) % song.length + song.length) % song.length] : 'pattern';
    const pattern = selected === 'pattern' ? String(params.pattern || 'beat-A') : selected;
    const kicks = pattern === 'beat-B' ? [0, 6, 8, 14] : [0, 8];
    for (let index = 0; index < steps; index++) {
      const at = start + (index * step);
      if (kicks.includes(index)) add(bar, index, 'kick', at, step * 4, true, 30000);
      if (index === 4 || index === 12) add(bar, index, 'snare', at, step * 3, false, 18000);
      if (index % 2 === 0) add(bar, index, 'hat', at, step, false, 6000);
    }
  }
  return { hits, total, samplesPerBar, step };
}

/** One hit as integer-valued samples (Float32Array of the 16-bit values, before any clipping). Rendering it again gives the same numbers. */
export function renderHit(hit, noise) {
  const out = new Float32Array(hit.length), length = hit.fullLength;
  let phase = 0;
  for (let index = 0; index < hit.length; index++) {
    const envelope = Math.trunc(hit.amplitude * (length - index) / length);
    if (hit.tonal) {
      phase = (phase + 60000000 - Math.trunc(index * 40000000 / length)) >>> 0;
      const x = phase >>> 16, triangle = x < 32768 ? (x * 2) - 32768 : ((65535 - x) * 2) - 32768;
      out[index] = Math.trunc(triangle * envelope / 32768);
    } else out[index] = Math.trunc((noise.at(hit.noiseAt + index) - 32768) * envelope / 32768);
  }
  return out;
}

/** The whole take as 16-bit samples: every hit rendered and added with the clip of the server (the exact offline render). */
export function assembleDrums(plan, seed) {
  const output = new Int16Array(plan.total), noise = noiseStream(seed);
  for (const hit of plan.hits) {
    const samples = renderHit(hit, noise);
    for (let index = 0; index < samples.length; index++) output[hit.start + index] = clip(output[hit.start + index] + samples[index]);
  }
  return output;
}

/** A hit as audio for the live chain: float samples in -1..1 (the same scaling as the offline render). */
export const hitAudio = (samples) => Float32Array.from(samples, (sample) => (sample < 0 ? sample / 32768 : sample / 32767));
