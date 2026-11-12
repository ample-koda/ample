// A SoundFont voice in the browser (plan local-preview 4.8): SpessaSynth (web/vendor/spessasynth, Apache-2.0) renders a General MIDI note from the pack SoundFont that the server hands out
// (GET /api/soundfont/general-midi), so a note needs no round trip. Rendering is offline and deterministic for a note: noteOn, process blocks, noteOff, process the tail.
import { SoundBankLoader, SpessaSynthProcessor } from '../vendor/spessasynth/spessasynth_core.js';

/** Loads a SoundFont (ArrayBuffer) into a synthesizer at `rate`; reverb and chorus effects off, as the server's render has none. Returns { synth, rate }. */
export function createSf2Synth(arrayBuffer, rate = 44100) {
  const synth = new SpessaSynthProcessor(rate, { enableEventSystem: false, effectsEnabled: true, eventsEnabled: false });
  synth.soundBankManager.addSoundBank(SoundBankLoader.fromArrayBuffer(arrayBuffer), 'main');
  return { synth, rate };
}

/** Renders one note: { program, bank, channel, pitch, velocity, seconds (held), tail (release seconds) } -> [left, right] Float32Arrays. */
export function renderSf2Note({ synth, rate }, { program = 0, bank = 0, channel = 0, pitch = 60, velocity = 100, seconds = 0.5, tail = 1, gainDb = 0, normalize = true }) {
  const ch = channel === 9 ? 9 : 0;
  synth.resetAllControllers?.();
  if (bank) synth.controllerChange(ch, 0, bank & 127);
  synth.programChange(ch, program);
  const frames = Math.round((seconds + tail) * rate), left = new Float32Array(frames), right = new Float32Array(frames), block = 128;
  const off = Math.round(seconds * rate);
  let started = false, ended = false;
  for (let at = 0; at < frames; at += block) {
    if (!started) { synth.noteOn(ch, pitch, velocity); started = true; }
    if (!ended && at >= off) { synth.noteOff(ch, pitch); ended = true; }
    synth.process(left, right, at, Math.min(block, frames - at));
  }
  synth.stopAllChannels?.(true);
  if (normalize) {
    // as the server (SamplerRenderers.cs, 'presets differ a lot in level'): the loudest sample of the render goes to -6 dBFS at unity gain and the gain parameter trims from there
    let peak = 0;
    for (let i = 0; i < frames; i++) peak = Math.max(peak, Math.abs(left[i]), Math.abs(right[i]));
    if (peak > 0) { const k = 10 ** ((-6 + gainDb) / 20) / peak; for (let i = 0; i < frames; i++) { left[i] *= k; right[i] *= k; } }
  }
  return [left, right];
}

/**
 * Renders a list of notes ({ start, end (seconds), pitch, velocity, channel, bank, program }) into `seconds` + `tail` seconds of stereo, one MIDI channel per note channel (programs set once per
 * channel change); the loudest sample goes to -6 dBFS plus gainDb like the server's GM render. Notes must be sorted by start.
 */
export function renderSf2Notes({ synth, rate }, notes, seconds, { tail = 1.5, gainDb = 0, normalize = true } = {}) {
  const frames = Math.round((seconds + tail) * rate), left = new Float32Array(frames), right = new Float32Array(frames), block = 128;
  const events = [];
  for (const n of notes) { events.push({ at: Math.round(n.start * rate), on: true, n }); events.push({ at: Math.round(n.end * rate), on: false, n }); }
  events.sort((a, b) => a.at - b.at || (a.on ? 1 : -1));
  synth.resetAllControllers?.();
  const programs = new Map();
  let e = 0;
  for (let at = 0; at < frames; at += block) {
    for (; e < events.length && events[e].at < at + block; e++) {
      const { on, n } = events[e], ch = Math.min(15, Math.max(0, n.channel ?? 0));
      if (on) {
        const key = `${n.bank ?? 0}:${n.program ?? 0}`;
        if (programs.get(ch) !== key) { programs.set(ch, key); if (ch !== 9) { synth.controllerChange(ch, 0, (n.bank ?? 0) & 127); synth.programChange(ch, n.program ?? 0); } }
        synth.noteOn(ch, n.pitch, n.velocity);
      } else synth.noteOff(ch, n.pitch);
    }
    synth.process(left, right, at, Math.min(block, frames - at));
  }
  synth.stopAllChannels?.(true);
  if (normalize) {
    let peak = 0;
    for (let i = 0; i < frames; i++) peak = Math.max(peak, Math.abs(left[i]), Math.abs(right[i]));
    if (peak > 0) { const k = 10 ** ((-6 + gainDb) / 20) / peak; for (let i = 0; i < frames; i++) { left[i] *= k; right[i] *= k; } }
  }
  return [left, right];
}
