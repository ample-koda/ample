// Pure model for Performance-mode launch input (issue 277): bindings from a MIDI note or a computer key to a launch action (clip, scene or stop), learn mode, and the keyboard focus rules.
// Sources are strings: 'note:36' (any channel) or 'key:KeyQ'. Actions are { kind: 'clip' | 'scene' | 'stop', id? }.
export const noteSource = (pitch) => `note:${pitch}`;
export const keySource = (code) => `key:${code}`;
export const actionKey = (a) => `${a.kind}:${a.id ?? ''}`;
export const sourceLabel = (s) => s.startsWith('note:') ? `Note ${s.slice(5)}` : s.startsWith('key:') ? s.slice(4).replace(/^Key|^Digit/, '') : s;

/** True when a key event must not launch anything: repeat, modifiers, or focus in a text field. */
export function keyIgnored(e) {
  if (e.repeat || e.ctrlKey || e.metaKey || e.altKey) return true;
  const t = e.target, tag = (t?.tagName || '').toUpperCase();
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || Boolean(t?.isContentEditable);
}

export function createInputState(json = null) {
  const bindings = new Map();   // source -> action
  let waiting = null;           // action waiting for the next source
  if (json) { try { for (const [s, a] of Object.entries(JSON.parse(json))) if (a && typeof a.kind === 'string') bindings.set(s, a); } catch { /* ignore bad data */ } }
  return {
    bindings,
    get waiting() { return waiting; },
    listen(action) { waiting = action; },
    cancel() { waiting = null; },
    /** Offers a source: binds it when an action waits (one source per action and device type, one action per source) and returns the action; otherwise null. */
    learn(source) {
      if (!waiting) return null;
      const action = waiting; waiting = null;
      for (const [s, a] of [...bindings]) if (actionKey(a) === actionKey(action) && s.split(':')[0] === source.split(':')[0]) bindings.delete(s);
      bindings.set(source, action);
      return action;
    },
    lookup(source) { return bindings.get(source) ?? null; },
    clear(source) { return bindings.delete(source); },
    list() { return [...bindings].map(([source, action]) => ({ source, action, label: sourceLabel(source) })); },
    serialize() { return JSON.stringify(Object.fromEntries(bindings)); },
  };
}
