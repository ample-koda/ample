// Plays the notes of the piano roll in the browser at once (no build): a small Web Audio synthesiser fed by web/lib/live-synth.mjs.
// Notes are scheduled a short window ahead, from the current notes of the roll, so an edit made while it plays is heard the next time its note comes round.
// @ample:live-synth

import { eventsInWindow } from '../lib/live-synth.mjs';
import { tempoMap } from '../lib/audio-clock.mjs';
import { rollPosition } from '../lib/roll-transport.mjs';
import { PLAYBACK_LEVEL } from '../lib/clientside-note-preview.mjs';
import { playbackOutput } from './playback-volume.js';
import { getContext, resumeOnGesture, armGestureResume } from '../lib/audio-engine.mjs';

const AHEAD = 0.4;      // seconds scheduled in front of the clock
const TICK = 80;        // milliseconds between two scheduling rounds

let context = null, noise = null;

function audio() {
  if (!context) {
    context = getContext();
    const length = context.sampleRate;
    noise = context.createBuffer(1, length, context.sampleRate);
    const data = noise.getChannelData(0);
    let seed = 20_000_001;   // a fixed noise, so the drums sound the same every time
    for (let i = 0; i < length; i++) { seed = (seed * 1664525 + 1013904223) >>> 0; data[i] = seed / 2 ** 31 - 1; }
  }
  return context;
}

const hz = (pitch) => 440 * 2 ** ((pitch - 69) / 12);

function playTone(ctx, out, e, at) {
  const v = e.voice, end = at + e.duration;
  const filter = ctx.createBiquadFilter();
  filter.type = 'lowpass'; filter.frequency.value = Math.min(v.cutoff + hz(e.pitch) * 1.5, 16000); filter.Q.value = 0.4;
  const env = ctx.createGain();
  env.gain.setValueAtTime(0.0001, at);
  env.gain.linearRampToValueAtTime(e.gain, at + v.attack);
  // sustain 0 is a plucked sound: it dies away while the key is held; otherwise it settles to the sustain level
  const level = Math.max(0.0001, e.gain * v.sustain);
  env.gain.setTargetAtTime(level, at + v.attack, Math.max(0.02, v.decay / 3));
  env.gain.setValueAtTime(Math.max(level, 0.0001), Math.max(end, at + v.attack + 0.01));
  env.gain.setTargetAtTime(0.0001, Math.max(end, at + v.attack + 0.01), Math.max(0.02, v.release / 3));
  const stop = Math.max(end, at + v.attack + 0.01) + v.release * 2 + 0.05;
  for (const w of v.waves) {
    const osc = ctx.createOscillator(), g = ctx.createGain();
    osc.type = w.type; osc.frequency.value = hz(e.pitch) * (w.ratio || 1); osc.detune.value = w.detune || 0;
    g.gain.value = w.gain;
    osc.connect(g).connect(filter);
    osc.start(at); osc.stop(stop);
  }
  filter.connect(env).connect(out);
}

function burst(ctx, out, at, { type = 'highpass', freq = 6000, q = 0.7, gain = 1, length = 0.1, tail = 0.05 }) {
  const src = ctx.createBufferSource(), filter = ctx.createBiquadFilter(), env = ctx.createGain();
  src.buffer = noise; filter.type = type; filter.frequency.value = freq; filter.Q.value = q;
  env.gain.setValueAtTime(gain, at);
  env.gain.setTargetAtTime(0.0001, at, length / 3);
  src.connect(filter).connect(env).connect(out);
  src.start(at, 0); src.stop(at + length + tail + 0.2);
}

function sweep(ctx, out, at, from, to, time, gain, length) {
  const osc = ctx.createOscillator(), env = ctx.createGain();
  osc.type = 'sine'; osc.frequency.setValueAtTime(from, at); osc.frequency.exponentialRampToValueAtTime(to, at + time);
  env.gain.setValueAtTime(gain, at); env.gain.setTargetAtTime(0.0001, at, length / 3);
  osc.connect(env).connect(out); osc.start(at); osc.stop(at + length * 2 + 0.1);
}

function playDrum(ctx, out, e, at) {
  const g = e.gain * 1.6;
  switch (e.drum) {
    case 'kick': sweep(ctx, out, at, 150, 45, 0.12, g * 1.4, 0.28); break;
    case 'snare': burst(ctx, out, at, { type: 'highpass', freq: 1500, gain: g, length: 0.16 }); sweep(ctx, out, at, 220, 150, 0.05, g * 0.6, 0.1); break;
    case 'clap': burst(ctx, out, at, { type: 'bandpass', freq: 1800, q: 1.2, gain: g, length: 0.14 }); break;
    case 'rim': burst(ctx, out, at, { type: 'bandpass', freq: 3500, q: 3, gain: g, length: 0.04 }); break;
    case 'hat': burst(ctx, out, at, { type: 'highpass', freq: 7500, gain: g * 0.6, length: 0.04 }); break;
    case 'openhat': burst(ctx, out, at, { type: 'highpass', freq: 7000, gain: g * 0.55, length: 0.25 }); break;
    case 'crash': burst(ctx, out, at, { type: 'highpass', freq: 5000, gain: g * 0.6, length: 0.9 }); break;
    case 'ride': burst(ctx, out, at, { type: 'highpass', freq: 8000, gain: g * 0.35, length: 0.4 }); break;
    default: sweep(ctx, out, at, 160 - (e.pitch % 12) * 4, 70, 0.18, g, 0.3);   // toms
  }
}

/**
 * Starts live playback. `rows()` gives the merged notes now (called every round, so edits are heard), `bpm()` and `ppq` the timing (`tempoChanges()`, optional, gives [{ beat, bpm }] for a song whose tempo changes; the notes are then placed with the tempo map), `instrument()` the
 * program and channel the notes use when they have none, `from` the start in seconds. At `loop` the position returns to `loopStart`, independently of `from`.
 * Returns { stop, time() }: time() is the position in the song in seconds (for the playhead). onStop is called once when it ends.
 */
export function startLive({ rows, bpm, tempoChanges = null, ppq, instrument, from = 0, loop = 0, loopStart = 0, end: clipEnd = Infinity, renderEvent, onStop = () => {} }) {
  const ctx = audio();
  ctx.resume();
  const master = ctx.createDynamicsCompressor();
  const level = ctx.createGain();
  level.gain.value = PLAYBACK_LEVEL;   // audition (note-audition.js) is capped by the same level
  master.connect(level).connect(playbackOutput(ctx));
  const lead = 0.06;
  const origin = ctx.currentTime + lead - from;     // the audio clock time at which song second 0 would be
  const span = loop > loopStart ? loop - loopStart : 0;
  let scheduledTo = from;                           // how far (in unwrapped seconds, counted as if the song never looped) notes are scheduled
  let stopped = false;
  const position = () => ctx.currentTime - origin;
  // the notes of the unwrapped window [u0, u1): split at the end of each lap, and played at the lap they fall in
  const schedule = (u0, u1) => {
    const { program, channel } = instrument();
    while (u0 < u1) {
      const lap = span > 0 ? Math.max(0, Math.floor((u0 - loopStart) / span)) : 0;
      const lapEnd = span > 0 ? loopStart + (lap + 1) * span : clipEnd;
      const end = Math.min(u1, lapEnd);
      if (end <= u0) break;
      for (const e of eventsInWindow(rows(), { ppq, bpm: bpm(), from: u0 - lap * span, to: end - lap * span, program, channel, map: tempoChanges ? tempoMap(tempoChanges(), bpm()) : null })) {
        const at = origin + e.start + lap * span;
        if (at < ctx.currentTime) continue;
        const event = span ? { ...e, duration: Math.min(e.duration, lapEnd - lap * span - e.start) } : e;
        const voiceOut = ctx.createGain(); voiceOut.connect(master);
        if (!renderEvent?.(ctx, voiceOut, event, at)) {
          if (event.drum) playDrum(ctx, voiceOut, event, at); else playTone(ctx, voiceOut, event, at);
        }
        if (span) { voiceOut.gain.setValueAtTime(1, origin + lapEnd - 0.003); voiceOut.gain.linearRampToValueAtTime(0, origin + lapEnd); }
        setTimeout(() => voiceOut.disconnect(), Math.max(0, (at - ctx.currentTime + event.duration + (span ? 0.05 : 12) + (event.tail || 0)) * 1000));
      }
      u0 = end;
    }
  };
  const round = () => {
    if (stopped) return;
    if (!span && position() >= clipEnd) { player.stop(); return; }
    const horizon = position() + AHEAD;
    if (horizon > scheduledTo) { schedule(scheduledTo, horizon); scheduledTo = horizon; }
  };
  const timer = setInterval(round, TICK);
  const player = {
    time: () => rollPosition(position(), from, loopStart, loop),
    stop() {
      if (stopped) return;
      stopped = true; clearInterval(timer);
      level.gain.setTargetAtTime(0.0001, ctx.currentTime, 0.03);
      setTimeout(() => master.disconnect(), 400);
      onStop();
    },
  };
  round();
  return player;
}

/** Unlock the shared audition context synchronously from a user gesture. */
export function liveAudioContext() { const ctx = audio(); resumeOnGesture(); return ctx; }

/** Plays one sketch-voice event (from eventsInWindow) at audio time `at` into `out`: the never-silent fallback of the note audition. */
export function playSketchEvent(ctx, out, event, at) {
  audio();
  if (event.drum) playDrum(ctx, out, event, at); else playTone(ctx, out, event, at);
}

/** Resume the shared context on the first user gesture of the page, so the first key press is already instant. */
let armed = false;
export function armAudioUnlock() {
  if (armed || typeof window === 'undefined') return;
  armed = true;
  armGestureResume();
}

/** Reported audio latency of the shared context in ms (base, output), when the browser says. */
export function audioLatencyInfo() { const ctx = audio(); return { base: Math.round((ctx.baseLatency || 0) * 1000), output: Math.round((ctx.outputLatency || 0) * 1000), state: ctx.state }; }
