// Performance-mode launch input (issue 277): MIDI notes and computer keys launch clips, scenes and Stop through ONE
// dispatcher, with learnable, remappable bindings kept in the project (ui-state).
// HOOK for the Performance workspace (issue 266): call attachPerformanceInput({ launch(action), active() }) once.
// `launch` is the same function the pointer uses (so timing/quantize rules are shared),
// `active()` says whether the Performance workspace is showing (keys and pads only act, and learn, then). Use
// bindingsList()/learnAction()/clearBinding() to draw the mapping list.
// Detaching cancels a pending learn. The rules live in ../lib/performance-input-model.mjs (createInputRouter). @ample:performance-input
import { onMidiNote, onMidiState, midiNames } from './midi-hub.js';
import { loadUiState, getUiKey, setUiKey } from './ui-state.js';
import { createInputState, createInputRouter } from '../lib/performance-input-model.mjs';

const state = createInputState();
const changeListeners = new Set();
let host = null, loaded = false, stops = [];
const changed = () => { for (const l of changeListeners) l(); };
const router = createInputRouter({
  state,
  launch: (action) => host?.launch(action),
  active: () => host?.active(),
  persist: () => setUiKey('performanceInput', JSON.parse(state.serialize())),
  onChange: changed,
  deviceCount: () => midiNames().length,
});

// ---- public API ----
export const onBindingsChange = (l) => { changeListeners.add(l); return () => changeListeners.delete(l); };
export const bindingsList = () => state.list();
export const learnAction = (action) => router.learnAction(action);
export const cancelLearn = () => router.cancelLearn();
export const waitingAction = () => state.waiting;
export const clearBinding = (source) => {
  if (state.clear(source)) { setUiKey('performanceInput', JSON.parse(state.serialize())); changed(); }
};

export function attachPerformanceInput(hooks) {
  detachPerformanceInput();
  host = hooks;
  if (!loaded) {
    loaded = true;
    loadUiState().then(() => {
      const saved = getUiKey('performanceInput');
      if (saved && typeof saved === 'object') { state.merge(JSON.stringify(saved)); changed(); }
    }).catch(() => { loaded = false; });
  }
  const onKey = (e) => { router.handleKey(e); };
  window.addEventListener('keydown', onKey, true);   // capture: a launch key is used here before the Jam keyboard sees it
  const offNote = onMidiNote((type, pitch, velocity) => { router.handleNote(type, pitch, velocity); });
  const offState = onMidiState((on, names) => router.handleMidiState(on, names));
  stops = [() => window.removeEventListener('keydown', onKey, true), offNote, offState];
}

export function detachPerformanceInput() { for (const stop of stops) stop(); stops = []; router.cancelLearn(); host = null; }

/**
 * True when this pitch is bound to a launch action (or a pad is being learned) and the Performance workspace is
 * showing: the Jam note listener should skip it.
 */
export const isMappedPad = (pitch) => router.isMappedPad(pitch);
