// MIDI-learn for knobs (local-preview chunk 6.1): the pure part. A controller (channel + CC number) is bound to a parameter of a block; the state serializes to one small JSON text.

export const ccKey = (channel, cc) => `${channel}:${cc}`;
export const targetKey = (target) => `${target.block}.${target.name}`;

/** A CC value 0..127 as 0..1. */
export const ccToNorm = (value) => Math.max(0, Math.min(127, Number(value) || 0)) / 127;

/** The state: bindings by controller, and at most one target waiting for its controller. */
export function createLearnState(saved = null) {
  const bindings = new Map();
  let waiting = null;
  try { for (const [key, target] of Object.entries(JSON.parse(saved || '{}'))) if (target?.block && target?.name) bindings.set(key, { block: String(target.block), name: String(target.name) }); } catch { /* an unreadable saved text starts empty */ }
  return {
    bindings,
    get waiting() { return waiting; },
    /** Starts waiting for a controller for this target (replacing any other wait). */
    listen(target) { waiting = { block: target.block, name: target.name }; },
    cancel() { waiting = null; },
    /** Binds the controller to the waiting target (the target loses its old controller; the controller loses its old target). Returns the target or null. */
    hear(channel, cc) {
      if (!waiting) return null;
      const target = waiting;
      waiting = null;
      for (const [key, value] of bindings) if (targetKey(value) === targetKey(target)) bindings.delete(key);
      bindings.set(ccKey(channel, cc), target);
      return target;
    },
    lookup(channel, cc) { return bindings.get(ccKey(channel, cc)) || null; },
    controllerOf(target) { for (const [key, value] of bindings) if (targetKey(value) === targetKey(target)) return key; return null; },
    clear(target) { for (const [key, value] of bindings) if (targetKey(value) === targetKey(target)) bindings.delete(key); },
    serialize() { return JSON.stringify(Object.fromEntries(bindings)); },
  };
}

/** A controller key such as "1:21" as text for a label: "CC 21" (channel 1 is the default, others get "ch 3"). */
export function ccLabel(key) { const [channel, cc] = String(key).split(':'); return Number(channel) > 1 ? `CC ${cc} ch ${channel}` : `CC ${cc}`; }
