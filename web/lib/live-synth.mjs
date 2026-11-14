// A small synthesiser that plays the notes of the piano roll in the browser at once, without a build (plan G05, live preview).
// This file is pure: it decides what to play and how each voice is shaped; web/app/live-play.js makes the sound with Web Audio.
// It is a sketch pad, not the render: the built song uses the General MIDI soundfont and the real blocks. @ample:live-synth

/** Joins the pieces of a tied note (same tie id and pitch, one piece starting where the other ends) into one note. Rows are as `absolute(model)` gives them. */
import { barBeats } from './meter.mjs';
export function mergeTies(rows) {
  const out = [], open = new Map();
  const sorted = [...rows].sort((a, b) => a.at - b.at || a.pitch - b.pitch);
  for (const row of sorted) {
    if (row.tie === undefined) { out.push({ ...row }); continue; }
    const key = `${row.tie}:${row.pitch}:${row.channel ?? 0}:${row.bank ?? 0}:${row.program ?? 0}`, last = open.get(key);
    if (last && last.at + last.length === row.at) { last.length += row.length; continue; }
    const copy = { ...row };
    open.set(key, copy); out.push(copy);
  }
  return out.sort((a, b) => (a.index ?? a.at) - (b.index ?? b.at));
}

const DRUMS = new Map([
  [35, 'kick'], [36, 'kick'], [37, 'rim'], [38, 'snare'], [39, 'clap'], [40, 'snare'], [41, 'tom'], [43, 'tom'], [45, 'tom'], [47, 'tom'], [48, 'tom'], [50, 'tom'],
  [42, 'hat'], [44, 'hat'], [46, 'openhat'], [49, 'crash'], [57, 'crash'], [51, 'ride'], [52, 'crash'], [53, 'ride'], [55, 'crash'], [59, 'ride'],
]);

/** How a drum note sounds: the name of the drum (kick, snare, hat...) and the pitch of the note. */
export const drumFor = (pitch) => ({ drum: DRUMS.get(pitch) || (pitch < 60 ? 'tom' : 'rim'), pitch });

const tone = (waves, attack, decay, sustain, release, cutoff) => ({ kind: 'tone', waves, attack, decay, sustain, release, cutoff });

/** The shape of a pitched voice for a General MIDI program (0 to 127): oscillators, envelope (seconds; sustain 0 to 1 of the peak) and the low-pass cutoff in Hz. */
export function voiceFor(program) {
  const p = Math.max(0, Math.min(127, Math.round(program) || 0));
  if (p < 8) return tone([{ type: 'triangle', gain: 0.7 }, { type: 'sine', gain: 0.5, ratio: 2 }], 0.004, 0.9, 0.0, 0.25, 5000);        // pianos
  if (p < 16) return tone([{ type: 'sine', gain: 0.8 }, { type: 'sine', gain: 0.3, ratio: 4 }], 0.002, 0.5, 0.0, 0.4, 7000);          // bells and mallets
  if (p < 24) return tone([{ type: 'square', gain: 0.35 }, { type: 'sine', gain: 0.5 }], 0.01, 0.2, 0.8, 0.12, 2600);                // organs
  if (p < 32) return tone([{ type: 'triangle', gain: 0.8 }, { type: 'sawtooth', gain: 0.2 }], 0.003, 0.45, 0.0, 0.2, 3200);           // guitars
  if (p < 40) return tone([{ type: 'sawtooth', gain: 0.6 }, { type: 'sine', gain: 0.5 }], 0.005, 0.3, 0.55, 0.12, 700);               // basses
  if (p < 56) return tone([{ type: 'sawtooth', gain: 0.4 }, { type: 'sawtooth', gain: 0.4, detune: 9 }], 0.09, 0.2, 0.85, 0.35, 2400);  // strings and choirs
  if (p < 64) return tone([{ type: 'sawtooth', gain: 0.55 }, { type: 'square', gain: 0.2 }], 0.035, 0.15, 0.8, 0.12, 3200);           // brass
  if (p < 80) return tone([{ type: 'square', gain: 0.4 }, { type: 'triangle', gain: 0.4 }], 0.04, 0.1, 0.85, 0.1, 3600);              // reeds and pipes
  if (p < 88) return tone([{ type: 'square', gain: 0.5 }, { type: 'sawtooth', gain: 0.2, detune: 6 }], 0.01, 0.1, 0.85, 0.1, 4200);    // synth leads
  if (p < 96) return tone([{ type: 'sawtooth', gain: 0.4 }, { type: 'triangle', gain: 0.4, detune: 7 }], 0.25, 0.3, 0.8, 0.6, 1800);   // pads
  return tone([{ type: 'triangle', gain: 0.8 }], 0.01, 0.4, 0.3, 0.3, 4000);
}

/**
 * The notes that start in the time window [from, to) seconds of the song, as events to play: start and duration in seconds, the voice and a gain.
 * rows come from `mergeTies(absolute(model))`; a note without a channel or program plays what the connected instrument has (defaults).
 */
export function eventsInWindow(rows, { ppq, bpm, from, to, program = 0, channel = 0, map = null, sustained = false }) {
  // `map` (tempoMap of audio-clock.mjs) puts the notes on a song with tempo changes; without it the tempo is the constant `bpm`
  const secondsPerTick = 60 / bpm / ppq, out = [];
  for (const n of rows) {
    const start = map ? map.secondsAt(n.at / ppq) : n.at * secondsPerTick;
    const held = sustained && start < from && (n.channel ?? channel) !== 9;   // a note already sounding at the left edge: the rest of it starts there
    if (!held && (start < from || start >= to)) continue;
    const ch = n.channel ?? channel, prog = n.program ?? program;
    const endSeconds = map ? map.secondsAt((n.at + n.length) / ppq) : (n.at + n.length) * secondsPerTick;
    if (held && endSeconds - from < 0.03) continue;   // the rest is shorter than the minimum duration: it would click past its real end
    const duration = Math.max(0.03, endSeconds - (held ? from : start));
    const gain = Math.pow(Math.max(1, Math.min(127, n.velocity)) / 127, 1.4) * 0.45;
    out.push(ch === 9 ? { ...n, start: held ? from : start, duration, gain, pitch: n.pitch, ...drumFor(n.pitch) } : { ...n, start: held ? from : start, duration, gain, pitch: n.pitch, voice: voiceFor(prog) });
  }
  return out;
}

/** The length of the notes in seconds (where a loop ends): the end of the last note, rounded up to a whole bar. */
export function loopLength(rows, { ppq, bpm }) {
  const bar = barBeats() * ppq;
  const end = rows.reduce((m, n) => Math.max(m, n.at + n.length), 0);
  return Math.max(1, Math.ceil(end / bar)) * bar * 60 / bpm / ppq;
}
