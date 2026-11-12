// MIDI-learn for knobs (local-preview chunk 6.1). Shift + right-click on a knob (or a long press, or the L key while it has focus) makes it wait for a controller: the next CC that arrives
// is bound to it, the knob pulses while it waits. A bound knob follows its controller (heard at once; one write, one undo step, when the movement stops). Shift + right-click on a bound
// knob clears it. The bindings are remembered per viewer in this browser (localStorage), not in the song: the song stays a pure text of sound. Only knobs that are on screen move.
// @ample:midi-learn
import { onMidiControl, setMidi, midiActive } from './midi-hub.js';
import { whyNot } from '../lib/caps.mjs';
import { toast } from './ui.js';
import { loadUiState, getUiKey, setUiKey } from './ui-state.js';
import { createLearnState, ccToNorm, ccLabel, targetKey } from '../lib/midi-learn-model.mjs';

// the bindings live in the project (ui-state.json), so they travel with it; values from this browser's old localStorage are moved over once (ui-state.js)
const state = createLearnState(null);
const save = () => setUiKey('learn', JSON.parse(state.serialize()));
let loaded = false;
const loadBindings = () => { if (loaded) return; loaded = true; loadUiState().then(() => { const saved = getUiKey('learn'); if (saved) { for (const [key, target] of createLearnState(JSON.stringify(saved)).bindings) state.bindings.set(key, target); paintAll(); } }); };   // started by the first learnable knob, never at import (the token is not there yet)

const live = new Map();   // target key -> Set of { element, move, commit, isConnected }
const timers = new Map();
const COMMIT_MS = 400;

const paint = (element, target, root = element.closest?.('.studio-knob')) => {
  if (root) { const c = state.controllerOf(target); if (c) root.setAttribute('data-midi', ccLabel(c)); else root.removeAttribute('data-midi'); }
  const controller = state.controllerOf(target), waiting = state.waiting && targetKey(state.waiting) === targetKey(target);
  element.classList.toggle('midi-learning', Boolean(waiting));
  element.classList.toggle('midi-learned', Boolean(controller));
  if (controller) element.setAttribute('data-midi', ccLabel(controller)); else element.removeAttribute('data-midi');
  element.title = waiting ? 'Move a knob or fader on your MIDI controller to bind it (Esc cancels)' : controller ? `${ccLabel(controller)}: Shift + right-click clears it` : element.dataset.baseTitle ?? '';
};
const paintAll = () => { for (const set of live.values()) for (const entry of [...set]) { if (!entry.isConnected()) { set.delete(entry); continue; } paint(entry.element, entry.target); } };

onMidiControl((cc, value, channel) => {
  const learned = state.hear(channel, cc);
  if (learned) { save(); paintAll(); toast(`Bound ${ccLabel(`${channel}:${cc}`)} to ${learned.name} of ${learned.block}.`); return; }
  const target = state.lookup(channel, cc);
  if (!target) return;
  const key = targetKey(target), entries = [...(live.get(key) || [])].filter((entry) => entry.isConnected());
  if (!entries.length) return;   // its window is closed: nothing to move
  for (const entry of entries) entry.move(ccToNorm(value));
  clearTimeout(timers.get(key));
  timers.set(key, setTimeout(() => { for (const entry of entries) entry.commit(); }, COMMIT_MS));
});

/** Waits for a controller for this target; switches the MIDI input on when it is off. */
async function startLearning(target) {
  if (!midiActive()) {
    try { await setMidi(true); } catch (error) { toast(error?.message || whyNot('midi')); return; }
  }
  state.listen(target);
  paintAll();
}

/** Called by a knob that has a `learn` option ({ block, name }). `hooks`: move(norm 0..1), commit(), isConnected(). */
export function attachLearn(root, svg, target, hooks) {
  loadBindings();
  const entry = { element: svg, target, ...hooks };
  const key = targetKey(target);
  if (!live.has(key)) live.set(key, new Set());
  live.get(key).add(entry);
  svg.dataset.baseTitle = svg.getAttribute('title') ?? '';
  paint(svg, target);
  const toggle = () => {
    if (state.controllerOf(target)) { state.clear(target); save(); paintAll(); toast(`Cleared the controller of ${target.name}.`); return; }
    if (state.waiting && targetKey(state.waiting) === key) { state.cancel(); paintAll(); return; }
    startLearning(target);
  };
  svg.addEventListener('contextmenu', (event) => { if (event.shiftKey) { event.preventDefault(); toggle(); } });
  svg.addEventListener('keydown', (event) => {
    if (event.key === 'l' || event.key === 'L') { event.preventDefault(); toggle(); }
    else if (event.key === 'Escape' && state.waiting) { state.cancel(); paintAll(); }
  });
  let press = 0;
  svg.addEventListener('pointerdown', (event) => { if (event.pointerType === 'touch') press = setTimeout(() => { press = 0; toggle(); }, 700); });
  for (const type of ['pointerup', 'pointercancel', 'pointermove']) svg.addEventListener(type, () => { if (press) { clearTimeout(press); press = 0; } });
}

/** Test and status helpers. */
export const learnState = () => ({ bindings: Object.fromEntries(state.bindings), waiting: state.waiting });
export function forgetAllBindings() { state.bindings.clear(); state.cancel(); save(); paintAll(); }
