// Performance-mode launch input (issue 277): MIDI notes and computer keys launch clips, scenes and Stop through ONE dispatcher, with learnable, remappable bindings kept in the project (ui-state).
// HOOK for the Performance workspace (issue 266): call attachPerformanceInput({ launch(action), active() }) once. `launch` is the same function the pointer uses (so timing/quantize rules are shared),
// `active()` says whether the Performance workspace is showing (keys and pads only act then). Use bindingsList()/learnAction()/clearBinding() to draw the mapping list. @ample:performance-input
import { onMidiNote, onMidiState, midiNames } from './midi-hub.js';
import { loadUiState, getUiKey, setUiKey } from './ui-state.js';
import { createInputState, noteSource, keySource, keyIgnored } from '../lib/performance-input-model.mjs';

const state = createInputState();
const changeListeners = new Set();
let host = null, loaded = false, stops = [];
const changed = () => { for (const l of changeListeners) l(); };
const save = () => { setUiKey('performanceInput', JSON.parse(state.serialize())); changed(); };

// ---- public API ----
export const onBindingsChange = (l) => { changeListeners.add(l); return () => changeListeners.delete(l); };
export const bindingsList = () => state.list();
export const learnAction = (action) => { state.listen(action); changed(); };
export const cancelLearn = () => { state.cancel(); changed(); };
export const waitingAction = () => state.waiting;
export const clearBinding = (source) => { if (state.clear(source)) save(); };

function dispatch(source) {
  const action = state.lookup(source);
  if (!action || !host?.active()) return false;
  host.launch(action);
  return true;
}

export function attachPerformanceInput(hooks) {
  detachPerformanceInput();
  host = hooks;
  if (!loaded) { loaded = true; loadUiState().then(() => { const saved = getUiKey('performanceInput'); if (saved) { for (const [s, a] of createInputState(JSON.stringify(saved)).bindings) state.bindings.set(s, a); changed(); } }); }
  const onKey = (e) => {
    if (keyIgnored(e)) return;
    const source = keySource(e.code);
    if (state.waiting) { if (e.code === 'Escape') { cancelLearn(); return; } if (state.learn(source)) { save(); e.preventDefault(); } return; }
    if (dispatch(source)) e.preventDefault();
  };
  window.addEventListener('keydown', onKey);
  const offNote = onMidiNote((type, pitch) => {
    if (type !== 'on') return;
    const source = noteSource(pitch);
    if (state.waiting) { if (state.learn(source)) save(); return; }
    dispatch(source);
  });
  // a controller that disconnects cancels a pending learn so the UI never waits for a device that is gone
  const offState = onMidiState(() => { if (state.waiting && !midiNames().length) cancelLearn(); });
  stops = [() => window.removeEventListener('keydown', onKey), offNote, offState];
}

export function detachPerformanceInput() { for (const stop of stops) stop(); stops = []; host = null; }

/** True when this pitch is bound to a launch action and the Performance workspace is showing: the Jam note listener should skip it. */
export const isMappedPad = (pitch) => Boolean(host?.active() && state.lookup(noteSource(pitch)));
