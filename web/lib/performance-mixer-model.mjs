// Session override layer of the Performance mixer (issue 276): gain, pan, mute, solo and live effect moves belong to the performance session, never to the song,
// until the performer chooses Apply. Pure: no DOM, no store. Overrides are keyed by stable block id and parameter name; a mixer or bus input is its own override ("mix1#gains#2"),
// so Apply merges into the song as it is THEN and never overwrites the other inputs with stale values. Mute and solo are flags, not gain edits: unmuting gives the fader value back.
export const keyOf = (block, name) => `${block}#${name}`;
export const MUTED_DB = -60;
export const GAIN_MIN = -60, GAIN_MAX = 12;
const PANS = new Set(['l', 'c', 'r']);
const LIST = new Set(['gains', 'pan']);
const MAX_INPUTS = 16;

/** "a b c" or a bare list to its parts; an empty or missing value gives the fallback. */
export const parts = (value, fallback) => {
  const list = String(value ?? '').replace(/^"|"$/g, '').trim().split(/\s+/).filter(Boolean);
  return list.length ? list : String(fallback).split(/\s+/);
};
export const quote = (list) => `"${list.join(' ')}"`;
const padded = (list, fill) => { const out = [...list]; while (out.length < 4) out.push(fill); return out; };

/** Clamps the number at the start of a value to { min, max } and keeps what follows it ("30 %", "-6 dB"). Not a number with a range: the text as it is. null for an empty or non-finite value. */
export function clampValue(value, range) {
  if (value === undefined || value === null) return null;
  const text = String(value).trim();
  if (!text) return null;
  if (!range || (range.min === undefined && range.max === undefined)) return text;
  const m = /^(-?\d+(?:\.\d+)?|-?\.\d+)(.*)$/.exec(text);
  if (!m) return text;
  let n = Number(m[1]);
  if (!Number.isFinite(n)) return null;
  if (range.min !== undefined) n = Math.max(range.min, n);
  if (range.max !== undefined) n = Math.min(range.max, n);
  return String(n) + m[2];
}

export function createSession() {
  const over = new Map();   // key -> { block, name, index?, value, seq }; flags: name 'mute' / 'solo' with value 'on'
  const listeners = new Set();
  let seq = 0;
  const emit = () => { for (const f of listeners) f(); };
  const put = (key, entry) => { over.set(key, { ...entry, seq: ++seq }); emit(); return over.get(key); };
  const inputKey = (block, name, i) => `${block}#${name}#${i}`;
  const okIndex = (i) => Number.isInteger(i) && i >= 0 && i < MAX_INPUTS;
  const flag = (e) => e.index !== undefined && (e.name === 'mute' || e.name === 'solo');   // a per-input flag (the bus block also has a plain `solo` parameter: that one has no index)
  const listName = (e) => (flag(e) ? 'gains' : e.name);
  const api = {
    /** A single input of a mixer or bus: gains in dB (clamped to -60..12), pan l, c or r. Anything else is refused (null) and changes nothing. */
    setInput(block, name, i, value) {
      if (!block || !LIST.has(name) || !okIndex(i)) return null;
      let v;
      if (name === 'gains') { const n = Number(String(value ?? '').replace(/\s*dB$/i, '').trim()); if (!String(value ?? '').trim() || !Number.isFinite(n)) return null; v = String(Math.min(GAIN_MAX, Math.max(GAIN_MIN, n))); }
      else { v = String(value ?? '').trim().toLowerCase(); if (!PANS.has(v)) return null; }
      return put(inputKey(block, name, i), { block, name, index: i, value: v });
    },
    /** One dispatcher for mouse, touch and MIDI CC: set a value for the session only. The song is not touched. A whole gains or pan list becomes per-input overrides. */
    set(block, name, value, range, saved) {
      if (!block || !name) return null;
      if (LIST.has(name)) {
        // a whole list (MIDI CC, snapshot recall): only the inputs that differ from what is heard become overrides, so Apply never pins the inputs nobody touched
        const cur = parts(api.read(block, name, saved), '');
        const same = (a, b) => (name === 'gains' ? Number(a) === Number(b) : String(a).toLowerCase() === String(b).toLowerCase());
        let last = null;
        parts(value, '').forEach((p, i) => { if (!over.has(inputKey(block, name, i)) && cur[i] !== undefined && same(cur[i], String(p).replace(/s*dB$/i, ''))) { last = last || {}; return; } last = api.setInput(block, name, i, p) || last; });
        return last;
      }
      const v = clampValue(value, range);
      if (v === null) return null;
      return put(keyOf(block, name), { block, name, value: v });
    },
    /** The value the sound and the readouts use: the override when there is one, else the saved value of the song. A gains or pan list is the fader/pan values (mute and solo are not in it). */
    read(block, name, saved) {
      if (LIST.has(name)) { const list = padded(parts(saved, name === 'gains' ? '0 0 0 0' : 'c c c c'), name === 'gains' ? '0' : 'c'); for (const e of over.values()) if (e.block === block && e.name === name && e.index !== undefined) list[e.index] = e.value; return quote(list); }
      return over.get(keyOf(block, name))?.value ?? saved;
    },
    has(block, name) { return LIST.has(name) ? [...over.values()].some((e) => e.block === block && e.name === name) : over.has(keyOf(block, name)); },
    setMute(block, i, on) { if (!block || !okIndex(i)) return false; if (on) put(inputKey(block, 'mute', i), { block, name: 'mute', index: i, value: 'on' }); else if (over.delete(inputKey(block, 'mute', i))) emit(); return true; },
    setSolo(block, i, on) { if (!block || !okIndex(i)) return false; if (on) put(inputKey(block, 'solo', i), { block, name: 'solo', index: i, value: 'on' }); else if (over.delete(inputKey(block, 'solo', i))) emit(); return true; },
    isMuted: (block, i) => over.has(inputKey(block, 'mute', i)),
    isSoloed: (block, i) => over.has(inputKey(block, 'solo', i)),
    /**
     * The gains that are heard for a mixer or bus: the saved ones with the session's fader moves, then mute and solo. Mute wins over solo; while any input is soloed every input that is
     * not soloed is silent. Muted is -60 dB; an unmuted input is back at its fader value.
     */
    effectiveGains(block, savedGains) {
      const base = padded(parts(savedGains, '0 0 0 0'), '0');
      for (const e of over.values()) if (e.block === block && e.name === 'gains' && e.index !== undefined) base[e.index] = e.value;
      const anySolo = [...over.values()].some((e) => e.block === block && flag(e) && e.name === 'solo');
      return base.map((g, i) => (api.isMuted(block, i) || (anySolo && !api.isSoloed(block, i)) ? String(MUTED_DB) : g));
    },
    get size() { return over.size; },
    /** The overrides as { block, name, index?, value }. */
    entries() { return [...over.values()].map(({ block, name, index, value }) => ({ block, name, ...(index === undefined ? {} : { index }), value })); },
    /** The distinct { block, name } parameters the session has changed ("gains" for gains, mute and solo; "pan" for pan): what Reset has to put back in the sound. */
    touched() {
      const seen = new Map();
      for (const e of over.values()) { const name = listName(e); seen.set(keyOf(e.block, name), { block: e.block, name }); }
      return [...seen.values()];
    },
    /** Reset: drop every override, the saved mix is back. */
    reset() { over.clear(); emit(); },
    /** Drops the overrides of blocks that are gone from the song (renamed or deleted meanwhile). Returns the dropped blocks. */
    prune(exists) { const gone = new Set(); for (const [k, e] of [...over]) if (!exists(e.block)) { over.delete(k); gone.add(e.block); } if (gone.size) emit(); return [...gone]; },
    /** What Apply is about to write: marks the state so a move made while Apply runs is not lost. */
    snapshot() { return new Map([...over].map(([k, e]) => [k, e.seq])); },
    /** After a successful Apply: drops what was applied; an override moved since the snapshot stays. */
    applied(snapshot) { if (!snapshot) over.clear(); else for (const [k, s] of snapshot) if (over.get(k)?.seq === s) over.delete(k); emit(); },
    /**
     * The setParameter edits that Apply sends, as ONE batch (one undo step), built against the song as it is now (nodes = [{ id, params }]): a gains or pan list is the song's current list
     * with the session's inputs (and for gains the mute and solo that are heard) put in; unchanged values and blocks that are gone are left out. { edits, skipped }.
     */
    compose(nodes) {
      const edits = [], skipped = [], blocks = new Set([...over.values()].map((e) => e.block));
      for (const block of blocks) {
        const node = nodes.find((n) => n.id === block);
        if (!node) { skipped.push(block); continue; }
        const names = new Set([...over.values()].filter((e) => e.block === block).map(listName));
        for (const name of names) {
          let value, saved;
          if (name === 'gains') { saved = node.params?.gains; value = quote(api.effectiveGains(block, saved)); }
          else if (name === 'pan') { saved = node.params?.pan; value = api.read(block, 'pan', saved); }
          else { saved = node.params?.[name]; value = over.get(keyOf(block, name)).value; }
          const same = LIST.has(name) ? padded(parts(saved, name === 'gains' ? '0 0 0 0' : 'c c c c'), name === 'gains' ? '0' : 'c').join(' ') === parts(value, '').join(' ') : String(saved ?? '') === value;
          if (!same) edits.push({ type: 'setParameter', block, name, value });
        }
      }
      return { edits, skipped };
    },
    subscribe(f) { listeners.add(f); return () => listeners.delete(f); },
  };
  return api;
}

/**
 * The performance mixer without DOM or store: deps = { nodes() -> [{ id, params }], preview(block, name, value) (the live chain), revert(block, name) (optional: the song's value plays again),
 * edit(ops) -> Promise<boolean> }. performSet is heard through preview at once; the song is edited only by performApply, as one edit() call.
 */
export function createPerformMixer({ session = createSession(), nodes, preview, revert = null, edit }) {
  const node = (id) => nodes().find((n) => n.id === id);
  const savedOf = (block, name) => node(block)?.params?.[name];
  const heard = (block, name) => {
    if (name === 'gains') return quote(session.effectiveGains(block, savedOf(block, 'gains')));
    if (name === 'pan') return session.read(block, 'pan', savedOf(block, 'pan'));
    return session.read(block, name, savedOf(block, name));
  };
  const push = (block, name) => preview(block, name, heard(block, name));
  let applying = false;
  return {
    session,
    /** Mouse, touch, MIDI CC and snapshot recall all call this. Clamped, kept in the session, heard at once; false when the value was refused. */
    performSet(block, name, value, range) {
      const e = session.set(block, name, value, range, savedOf(block, name));
      if (!e) return false;
      push(block, name);
      return true;
    },
    performMute(block, i, on = !session.isMuted(block, i)) { if (!session.setMute(block, i, on)) return false; push(block, 'gains'); return true; },
    performSolo(block, i, on = !session.isSoloed(block, i)) { if (!session.setSolo(block, i, on)) return false; push(block, 'gains'); return true; },
    /** The sound follows the session again after the song changed under it (the live chain drops its preview of a block whose song values changed; pushed twice because the first push meets that). */
    resync() {
      session.prune((id) => Boolean(node(id)));
      for (let pass = 0; pass < 2; pass++) for (const { block, name } of session.touched()) push(block, name);
    },
    /** Reset: the saved mix is back, in the sound too (also for a value the song does not write: its default plays). */
    performReset() {
      const touched = session.touched();
      session.reset();
      for (const { block, name } of touched) {
        if (revert) revert(block, name);
        else { const saved = savedOf(block, name); if (saved !== undefined) preview(block, name, saved); }
      }
    },
    /** Apply to project: ONE edit() call (one undo step). The overrides are dropped only when it succeeded; a failed Apply keeps them. Returns { applied, skipped, ok }. */
    async performApply() {
      if (applying) return { applied: 0, skipped: [], ok: false, busy: true };
      applying = true;
      try {
        const snap = session.snapshot();
        const { edits, skipped } = session.compose(nodes());
        if (!edits.length) { session.applied(snap); return { applied: 0, skipped, ok: true }; }
        let ok = false;
        try { ok = (await edit(edits)) === true; } catch { ok = false; }
        if (ok) { session.applied(snap); session.prune((id) => Boolean(node(id))); }
        return { applied: ok ? edits.length : 0, skipped, ok };
      } finally { applying = false; }
    },
  };
}
