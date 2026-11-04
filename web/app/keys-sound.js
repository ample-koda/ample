// The own sound of the keyboard dock: when there is no window to play and no block selected (or when it is asked for) the dock plays a real instrument itself, a piano from the General MIDI pack by
// default. The instrument is a General MIDI program and a SoundFont: the pack that comes with AMPLE, or a .sf2 file of the project. Every note is rendered once by the server with that SoundFont and kept
// (a note that was not heard before takes a moment; the notes of the keys on the screen are rendered in the background as soon as the sound is chosen), and plays as many notes at once as you like.
// @ample:virtual-keyboard
import { api } from './api.js';
import { liveAudioContext } from './live-play.js';
import { playbackOutput } from './playback-volume.js';
import { gmProgramName } from '../lib/gm-programs.mjs';

const STORE = 'ample.keys.sound', PACK = 'pack:musescore-general', NOTE_TICKS = 1920, PPQ = 480;

const read = () => { try { return JSON.parse(localStorage.getItem(STORE) || '{}'); } catch { return {}; } };

function createOwnSound() {
  const saved = read();
  const state = { file: typeof saved.file === 'string' && saved.file ? saved.file : PACK, bank: Number(saved.bank) || 0, program: Number.isInteger(saved.program) ? saved.program : 0 };
  const cache = new Map();          // key -> Promise of an AudioBuffer
  const listeners = new Set();
  const progress = { total: 0, done: 0 };
  let running = 0, master = null;
  const queue = [];
  const tell = () => { for (const listener of listeners) listener({ ...progress }); };
  const keyOf = (pitch, program = state.program) => `${state.file}|${state.bank}:${program}|${pitch}`;
  const drain = () => { while (running < 3 && queue.length) { running++; queue.shift()().finally(() => { running--; drain(); }); } };
  const render = (pitch, program) => new Promise((resolve, reject) => {
    queue.push(async () => {
      try {
        const result = await api.gmAudition({ file: state.file, bank: state.bank, program, channel: 0, gain: 0, limiter: 'soft', notes: [{ bar: 1, tick: 0, ticks: NOTE_TICKS, ppq: PPQ, pitch, velocity: 100 }] });
        resolve(await liveAudioContext().decodeAudioData(await result.blob.arrayBuffer()));
      } catch (error) { reject(error); }
    });
    drain();
  });
  const bufferFor = (pitch) => {
    const key = keyOf(pitch);
    if (!cache.has(key)) {
      progress.total++; tell();
      cache.set(key, render(pitch, state.program).catch((error) => { cache.delete(key); throw error; }).finally(() => { progress.done++; if (progress.done >= progress.total) { progress.done = 0; progress.total = 0; } tell(); }));
    }
    return cache.get(key);
  };
  // a compressor keeps chords from clipping (every note is a full recording on its own)
  const output = (ctx) => {
    if (!master) {
      const compressor = ctx.createDynamicsCompressor();
      compressor.threshold.value = -14; compressor.knee.value = 12; compressor.ratio.value = 6; compressor.attack.value = 0.004; compressor.release.value = 0.2;
      const level = ctx.createGain(); level.gain.value = 0.6;
      master = compressor; compressor.connect(level).connect(playbackOutput(ctx));
    }
    return master;
  };
  const persist = () => { try { localStorage.setItem(STORE, JSON.stringify(state)); } catch { /* storage can be blocked */ } };

  return {
    get state() { return { ...state }; },
    /** The name of what is heard, e.g. "Acoustic Grand Piano · General MIDI pack". */
    name() { return `${gmProgramName(state.program) || 'Program ' + state.program} · ${state.file === PACK ? 'General MIDI pack' : state.file.split('/').pop()}`; },
    isPack: () => state.file === PACK,
    set(patch) {
      if (patch.file !== undefined) state.file = patch.file || PACK;
      if (patch.program !== undefined) state.program = Math.max(0, Math.min(127, Math.round(patch.program)));
      if (patch.bank !== undefined) state.bank = Math.max(0, Math.round(patch.bank));
      persist(); tell();
    },
    /** Hears when the rendering of the notes goes on: { total, done }. Returns the way to stop. */
    onProgress(listener) { listeners.add(listener); return () => listeners.delete(listener); },
    /** Renders these pitches in the background (the keys that are on the screen), so they sound at once. */
    warm(first, last) { for (let pitch = first; pitch <= last; pitch++) bufferFor(pitch).catch(() => {}); },
    /** Starts a note; resolves to the way to end it (null when it could not be rendered). */
    async play(pitch, velocity = 100) {
      let buffer;
      try { buffer = await bufferFor(pitch); } catch { return null; }
      const ctx = liveAudioContext(), source = ctx.createBufferSource(), gain = ctx.createGain();
      source.buffer = buffer;
      const peak = 0.25 + 0.75 * Math.min(1, Math.max(0, velocity / 127)), now = ctx.currentTime;
      gain.gain.setValueAtTime(0, now); gain.gain.linearRampToValueAtTime(peak, now + 0.004);   // no click at the start
      source.connect(gain).connect(output(ctx));
      source.start();
      return () => { const t = ctx.currentTime; try { gain.gain.cancelScheduledValues(t); gain.gain.setValueAtTime(gain.gain.value, t); gain.gain.setTargetAtTime(0, t, 0.06); source.stop(t + 0.4); } catch { /* already stopped */ } };
    },
    /** A short phrase on another program (the list of instruments lets you hear them); resolves to the way to stop it. */
    async hear(program) {
      const low = (program >= 32 && program <= 39), base = low ? 36 : 60;
      const notes = [0, 4, 7, 12].map((interval, index) => ({ bar: 1, tick: index * 240, ticks: index === 3 ? 960 : 240, ppq: PPQ, pitch: base + interval, velocity: 90 }));
      const result = await api.gmAudition({ file: state.file, bank: state.bank, program, channel: 0, gain: 0, limiter: 'soft', notes });
      const ctx = liveAudioContext(), buffer = await ctx.decodeAudioData(await result.blob.arrayBuffer()), source = ctx.createBufferSource();
      source.buffer = buffer; source.connect(output(ctx)); source.start();
      return () => { try { source.stop(); } catch { /* already stopped */ } };
    },
  };
}

export const ownSound = createOwnSound();
export const OWN_PACK = PACK;
