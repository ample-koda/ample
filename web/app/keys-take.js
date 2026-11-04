// The ad-hoc take of the keyboard dock: press record, play (the click keeps going while it waits and starts over at your first note), press record again, listen to it, choose the bars to keep, and save
// it as a new MIDI clip in the song or throw it away. It listens to what is played (keys-play.js), so the keyboard, a MIDI input and the pitch wheel all count. @ample:virtual-keyboard
import { S, edit, flat, notify, select } from './store.js';
import { liveAudioContext } from './live-play.js';
import { playbackOutput } from './playback-volume.js';
import { player } from './keys-play.js';
import { format, parse, pasteNotes } from '../lib/notes-model.mjs';
import { splitAtBars, takeToItems } from '../lib/virtual-keys.mjs';
import { barBeats } from '../lib/meter.mjs';

export const TAKE_PPQ = 1_000_000;   // the resolution a clip without its own ppq has: its notes text then reads as beats with decimals
const tempo = () => Number(S.doc?.project?.tempo) || 120;

/** A click at the tempo of the song: beat one of a bar is higher. start() begins on a beat one at once. */
function createClick() {
  let timer = 0, beat = 0;
  const tick = () => {
    const ctx = liveAudioContext(), now = ctx.currentTime, osc = ctx.createOscillator(), gain = ctx.createGain();
    osc.frequency.value = beat % 4 === 0 ? 1500 : 1000;
    gain.gain.setValueAtTime(0.0001, now); gain.gain.linearRampToValueAtTime(0.2, now + 0.002); gain.gain.exponentialRampToValueAtTime(0.0001, now + 0.04);
    osc.connect(gain).connect(playbackOutput(ctx)); osc.start(now); osc.stop(now + 0.06);
    beat++;
  };
  const stop = () => { clearInterval(timer); timer = 0; beat = 0; };
  return { start() { stop(); tick(); timer = setInterval(tick, 60000 / tempo()); }, stop, get running() { return timer !== 0; } };
}

/**
 * @param {{ grid: () => number, click: () => boolean, onChange: () => void }} options  grid: beats to quantise to (0 for none); click: whether the click sounds; onChange: the state changed
 */
export function createAdhocTake({ grid, click, onChange }) {
  const metronome = createClick();
  const state = { on: false, waiting: false, t0: 0, open: new Map(), notes: [], wheel: 0 };
  const seconds = () => (performance.now() - state.t0) / 1000;
  const stopTap = player.tap((type, pitch, value) => {
    if (!state.on) return;
    if (type === 'on') {
      if (state.waiting) { state.waiting = false; state.t0 = performance.now(); if (click()) metronome.start(); onChange(); }   // the first note is beat one of the take
      state.open.set(pitch, { at: seconds(), velocity: value, bend: Math.round(state.wheel * 100) });
    } else if (type === 'off') {
      const held = state.open.get(pitch);
      if (!held) return;
      state.open.delete(pitch);
      const end = seconds();
      state.notes.push({ at: held.at, end: end > held.at ? end : held.at + 0.25, pitch, velocity: held.velocity, bend: held.bend });
    } else if (type === 'bend') {
      state.wheel = value;
      for (const held of state.open.values()) if (Math.abs(value * 100) > Math.abs(held.bend)) held.bend = Math.round(value * 100);
    }
  });
  const reset = () => { state.on = false; state.waiting = false; state.open.clear(); state.notes = []; state.wheel = 0; metronome.stop(); };
  return {
    get active() { return state.on; },
    get waiting() { return state.waiting; },
    /** Starts waiting for the first note (the click ticks meanwhile). */
    arm() { reset(); state.on = true; state.waiting = true; if (click()) metronome.start(); onChange(); },
    cancel() { reset(); onChange(); },
    /** Ends the take: the notes on the grid as rows { at, length, pitch, velocity, bend } in ticks of TAKE_PPQ, or null when nothing was played. */
    finish() {
      const end = state.t0 ? seconds() : 0;
      for (const [pitch, held] of state.open) state.notes.push({ at: held.at, end, pitch, velocity: held.velocity, bend: held.bend });
      const notes = [...state.notes].sort((a, b) => a.at - b.at);
      reset(); onChange();
      return notes.length ? takeToItems(notes, tempo(), TAKE_PPQ, grid()) : null;
    },
    dispose() { stopTap(); reset(); },
  };
}

/** Plays rows of a take (ticks of TAKE_PPQ) on what the keyboard plays; returns the way to stop. */
export function previewRows(rows, bars, done) {
  const perTick = 60000 / tempo() / TAKE_PPQ, timers = [];
  const stops = new Set();
  for (const row of rows) {
    timers.push(setTimeout(async () => {
      const stop = await player.sound(row.pitch, row.velocity);
      if (!stop) return;
      stops.add(stop);
      timers.push(setTimeout(() => { stops.delete(stop); stop(); }, Math.max(60, row.length * perTick)));
    }, row.at * perTick));
  }
  timers.push(setTimeout(done, bars * barBeats() * TAKE_PPQ * perTick + 400));
  return () => { for (const timer of timers) clearTimeout(timer); for (const stop of stops) stop(); stops.clear(); };
}

/** Writes the chosen rows into a new MIDI clip of the song (take, take2, ...), as long as the bars chosen, and selects it. */
export async function saveTakeAsClip(rows, bars) {
  const items = rows.flatMap((r) => splitAtBars(r.at, r.length, r.pitch, r.velocity, TAKE_PPQ, r.bend ? { bend: r.bend } : {}));
  const text = format(pasteNotes(parse('', TAKE_PPQ), items, 0, 0)).trim();
  const used = new Set(flat().nodes.map((node) => node.id));
  let name = 'take', n = 1;
  while (used.has(n === 1 ? name : `${name}${n}`)) n++;
  name = n === 1 ? name : `${name}${n}`;
  const ok = await edit([{ type: 'addBlock', blockType: 'midi-clip', name }, { type: 'setParameter', block: name, name: 'notes', value: /\s/.test(text) ? `"${text}"` : text }, { type: 'setParts', block: name, parts: [{ at: 1, length: Math.max(1, bars), name: '' }] }]);
  if (ok) { select(name); notify(); }
  return ok ? name : null;
}
