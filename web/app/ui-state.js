// Per-project Studio state that travels with the project (GET/POST /api/project/ui-state, kept in .ample/ui-state.json): MIDI-learn bindings and parameter snapshots.
// Values saved earlier in this browser's localStorage are moved into it once. Reads are synchronous after loadUiState() has resolved; writes are batched. @ample:ui-state
import { api } from './api.js';

let state = null, loading = null, timer = 0;
const OLD = { learn: 'ample.midi.learn', snapshots: 'ample.snapshots' };

/** Loads the state (once) and migrates the old localStorage values. Resolves to the object. */
export function loadUiState() {
  loading ||= (async () => {
    let server = {};
    try { server = (await api.get('/api/project/ui-state')) || {}; } catch { /* none yet */ }
    state = { ...server, ...(state || {}) };
    let moved = false;
    for (const [key, old] of Object.entries(OLD)) {
      try {
        const text = localStorage.getItem(old);
        if (text && state[key] === undefined) { state[key] = JSON.parse(text); moved = true; }
        if (text) localStorage.removeItem(old);
      } catch { /* unreadable or blocked: nothing to move */ }
    }
    if (moved) save();
    return state;
  })();
  return loading;
}
/** The value of a key (undefined before the load has finished). */
export const getUiKey = (key) => state?.[key];
/** Sets a key and writes the whole state soon (a burst of changes is one write). */
export function setUiKey(key, value) {
  state ||= {};
  state[key] = value;
  save();
}
function save() { clearTimeout(timer); timer = setTimeout(() => { api.post('/api/project/ui-state', state).catch(() => {}); }, 400); }
export const flushUiState = () => { clearTimeout(timer); return state ? api.post('/api/project/ui-state', state).catch(() => {}) : Promise.resolve(); };
