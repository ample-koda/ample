// Pure model for Performance-mode launch input (issue 277): bindings from a MIDI note or a computer key to a launch
// action (clip, scene or stop), learn mode, the keyboard focus rules, and the router
// that ties them together (no DOM or MIDI objects in here, so it is tested in node).
// Sources are strings: 'note:36' (any channel; the MIDI hub filters the channel) or 'key:KeyQ'. Actions are { kind:
// 'clip' | 'scene' | 'stop', id? }.
export const noteSource = (pitch) => `note:${pitch}`;
export const keySource = (code) => `key:${code}`;
export const actionKey = (a) => `${a.kind}:${a.id ?? ''}`;
export const sourceLabel = (s) => (s.startsWith('note:')
  ? `Note ${s.slice(5)}`
  : s.startsWith('key:') ? s.slice(4).replace(/^Key|^Digit/, '') : s);

const KINDS = new Set(['clip', 'scene', 'stop']);
const SOURCE_RE = /^(?:note:(\d{1,3})|key:([A-Za-z0-9]+))$/;
/** Keys that can never be bound: modifiers, focus/browser keys and Escape (which cancels learn). */
// long-lines-ok: one regular expression that cannot be split
const RESERVED_KEY = /^(?:(?:Shift|Control|Alt|Meta)(?:Left|Right)|Tab|CapsLock|Escape|ContextMenu|NumLock|ScrollLock|Pause|PrintScreen|F\d{1,2}|OS(?:Left|Right))$/;
export const keyBindable = (code) => typeof code === 'string' && /^[A-Za-z0-9]+$/.test(code) && !RESERVED_KEY.test(code);

function cleanAction(a) {
  if (!a || typeof a !== 'object' || !KINDS.has(a.kind)) return null;
  if (a.kind === 'stop') return { kind: 'stop' };
  return typeof a.id === 'string' && a.id ? { kind: a.kind, id: a.id } : null;
}
function cleanSource(s) {
  const m = typeof s === 'string' ? SOURCE_RE.exec(s) : null;
  if (!m) return null;
  if (m[1] !== undefined) return Number(m[1]) <= 127 ? noteSource(Number(m[1])) : null;
  return keyBindable(m[2]) ? s : null;
}
const device = (s) => s.split(':')[0];

/**
 * True when a key event must not launch anything: repeat, modifiers, composition, an event someone else already
 * handled, or focus in a text field.
 */
export function keyIgnored(e) {
  if (e.repeat || e.ctrlKey || e.metaKey || e.altKey || e.shiftKey || e.isComposing || e.defaultPrevented) return true;
  const t = e.target, tag = (t?.tagName || '').toUpperCase();
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || Boolean(t?.isContentEditable);
}

export function createInputState(json = null) {
  const bindings = new Map();   // source -> action
  const extras = new Map();     // entries this version does not understand (other kind, odd source): kept as they are, never launched
  let waiting = null;           // action waiting for the next source
  const sameSlot = (source, action) => {
    for (const [s, a] of bindings) {
      if (actionKey(a) === actionKey(action) && device(s) === device(source)) return true;
    }
    return false;
  };
  const api = {
    bindings,
    get waiting() { return waiting; },
    listen(action) { const a = cleanAction(action); waiting = a; return Boolean(a); },
    cancel() { waiting = null; },
    /**
     * Offers a source: binds it when an action waits (one source per action and device type, one action per source) and
     * returns the action; otherwise null.
     */
    learn(source) {
      const src = cleanSource(source);
      if (!waiting || !src) return null;
      const action = waiting; waiting = null;
      for (const [s, a] of [...bindings]) if (actionKey(a) === actionKey(action) && device(s) === device(src)) bindings.delete(s);
      extras.delete(src);
      bindings.set(src, action);
      return action;
    },
    lookup(source) { return bindings.get(source) ?? null; },
    clear(source) { const had = bindings.delete(source); return extras.delete(source) || had; },
    list() { return [...bindings].map(([source, action]) => ({ source, action, label: sourceLabel(source) })); },
    /** Adds saved bindings without overriding what is already bound (a load that finishes after the user has learned something). */
    merge(text) {
      let data;
      try { data = JSON.parse(text); } catch { return; }
      if (!data || typeof data !== 'object' || Array.isArray(data)) return;
      for (const [s, raw] of Object.entries(data)) {
        const src = cleanSource(s), action = cleanAction(raw);
        if (src && action) { if (!bindings.has(src) && !sameSlot(src, action)) { bindings.set(src, action); extras.delete(src); } }
        else if (!bindings.has(s) && !extras.has(s)) extras.set(s, raw);
      }
    },
    serialize() { return JSON.stringify({ ...Object.fromEntries(extras), ...Object.fromEntries(bindings) }); },
  };
  if (json) api.merge(json);
  return api;
}

/**
 * One dispatcher for keys and pads. hooks: { state, launch(action), active(), persist(), onChange(), deviceCount() }.
 * Keys and pads act, and learn, only while active() is true. handleKey/handleNote return true when they used the event.
 */
export function createInputRouter({ state, launch, active, persist = () => {}, onChange = () => {}, deviceCount = () => 0 }) {
  let hadDevices = false;
  const isActive = () => { try { return Boolean(active()); } catch { return false; } };
  const learned = (source) => { if (state.learn(source)) { persist(); onChange(); return true; } return false; };
  const router = {
    learnAction(action) { if (!state.listen(action)) return false; hadDevices = deviceCount() > 0; onChange(); return true; },
    cancelLearn() { if (state.waiting) { state.cancel(); onChange(); } },
    handleKey(e) {
      if (state.waiting && e.code === 'Escape' && !e.repeat) { router.cancelLearn(); return false; }
      if (!e.code || keyIgnored(e) || !isActive()) return false;
      const source = keySource(e.code);
      let hit = false;
      if (state.waiting) hit = keyBindable(e.code) && learned(source);
      else { const a = state.lookup(source); if (a) { launch(a); hit = true; } }
      if (hit) { e.preventDefault?.(); e.stopPropagation?.(); }
      return hit;
    },
    /** A MIDI note as the hub reports it: type 'on' | 'off'. A note on with velocity 0 is a note off. */
    handleNote(type, pitch, velocity) {
      if (type !== 'on' || velocity === 0 || !Number.isInteger(pitch) || !isActive()) return false;
      const source = noteSource(pitch);
      if (state.waiting) return learned(source);
      const a = state.lookup(source);
      if (!a) return false;
      launch(a);
      return true;
    },
    /** A controller that disconnects (or MIDI switched off) cancels a pending learn that had a device when it started. */
    handleMidiState(on, names) {
      const count = on === false ? 0 : (names?.length ?? 0);
      if (count > 0) hadDevices = true;
      else if (state.waiting && hadDevices) { hadDevices = false; router.cancelLearn(); }
    },
    /**
     * True when this pad is (or, while learning, may become) a launch pad and the workspace is showing: the Jam note
     * listener should skip it.
     */
    isMappedPad(pitch) { return isActive() && Boolean(state.waiting || state.lookup(noteSource(pitch))); },
  };
  return router;
}
