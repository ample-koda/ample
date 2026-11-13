// Session override layer of the Performance mixer (issue 276): gain, pan, mute, solo and live effect moves belong to the performance session, never to the song,
// until the performer chooses Apply. Pure: no DOM, no store. Overrides are keyed by stable block id + parameter name ("mix1#gains", "reverb1#mix").
export const keyOf = (block, name) => `${block}#${name}`;
const MUTED_DB = -60;

export function createSession() {
  const over = new Map();   // key -> { block, name, value }
  const listeners = new Set();
  const emit = () => { for (const f of listeners) f(); };
  return {
    /** One dispatcher for mouse, touch and MIDI CC: set a value for the session only. The song is not touched. */
    set(block, name, value) { over.set(keyOf(block, name), { block, name, value: String(value) }); emit(); return over.get(keyOf(block, name)); },
    /** The value the sound and the readouts use: the override when there is one, else the saved value of the song. */
    read(block, name, saved) { return over.get(keyOf(block, name))?.value ?? saved; },
    has: (block, name) => over.has(keyOf(block, name)),
    get size() { return over.size; },
    /** The overrides as { block, name, value } (used by Reset to restore the sound). */
    entries() { return [...over.values()]; },
    /** Reset: drop every override, the saved mix is back. */
    reset() { over.clear(); emit(); },
    /** The setParameter edits that Apply sends to the song, as ONE batch (one undo step). */
    applyEdits() { return [...over.values()].map(({ block, name, value }) => ({ type: 'setParameter', block, name, value })); },
    /** After a successful Apply the song holds the values: the overrides are dropped. */
    applied() { over.clear(); emit(); },
    subscribe(f) { listeners.add(f); return () => listeners.delete(f); },
  };
}

export const muteGains = (gains, index) => gains.map((g, i) => (i === index ? String(MUTED_DB) : g));
export const soloGains = (gains, index) => gains.map((g, i) => (i === index ? g : String(MUTED_DB)));
export const quote = (list) => `"${list.join(' ')}"`;
