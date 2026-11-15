// The mix of the lanes of a performance, kept for the session (issue 276): a gain, a pan, a mute, a solo and a filter per lane. It is heard at once, belongs to the performance and not to the song, goes back to
// what the layout keeps with Reset, and is written into the layout only with an explicit Keep. Pure, so node --test checks it; the audio side (performance-audio.mjs) only reads `settings`.

export const GAIN_DB = { min: -60, max: 12 };
/** The filter is a low pass; at the top of the range it is open, which is the same as no filter. */
export const CUTOFF = { min: 40, max: 20000 };

const clamp = (value, min, max) => Math.min(max, Math.max(min, value));
const number = (value, fallback) => { const n = Number(value); return Number.isFinite(n) ? n : fallback; };

export const emptyMix = () => ({ gainDb: 0, pan: 0, cutoff: CUTOFF.max, mute: false, solo: false });

/** A mix read from anything: unknown or missing values are the neutral ones, numbers are clamped. */
export function normalizeMix(value) {
  const m = value && typeof value === 'object' ? value : {};
  return {
    gainDb: clamp(number(m.gainDb, 0), GAIN_DB.min, GAIN_DB.max),
    pan: clamp(number(m.pan, 0), -100, 100),
    cutoff: clamp(number(m.cutoff, CUTOFF.max), CUTOFF.min, CUTOFF.max),
    mute: m.mute === true,
    solo: m.solo === true,
  };
}

export const dbToGain = (db) => (db <= GAIN_DB.min ? 0 : 10 ** (db / 20));

/** A slider position 0..1 on a log scale between the lowest cutoff and the open filter, and back. */
export const cutoffAt = (position) => CUTOFF.min * (CUTOFF.max / CUTOFF.min) ** clamp(number(position, 1), 0, 1);
export const positionOfCutoff = (hz) => Math.log(clamp(number(hz, CUTOFF.max), CUTOFF.min, CUTOFF.max) / CUTOFF.min) / Math.log(CUTOFF.max / CUTOFF.min);

export const isNeutral = (mix) => { const m = normalizeMix(mix); return m.gainDb === 0 && m.pan === 0 && m.cutoff === CUTOFF.max && !m.mute && !m.solo; };

/**
 * The session of the lane mix. `base` is what the layout keeps (lane name to mix); the session changes lie on top of it.
 * settings(lanes) is what the sound uses: a gain (0 when the lane is muted, or another lane has solo), a pan from -1 to 1 and the cutoff, or null for no filter (open, or bypassed).
 */
export function createLaneMix(base = new Map()) {
  let kept = new Map([...base].map(([lane, mix]) => [lane, normalizeMix(mix)]));
  const over = new Map();
  let filtersBypassed = false;
  const listeners = new Set();
  const changed = () => listeners.forEach((fn) => fn());
  const read = (lane) => normalizeMix({ ...(kept.get(lane) ?? emptyMix()), ...(over.get(lane) ?? {}) });
  return {
    get(lane) { return read(lane); },
    /** Changes one or more values of a lane; the value is clamped. Returns the lane mix as it is heard. */
    set(lane, patch) {
      over.set(lane, { ...(over.get(lane) ?? {}), ...normalizeMix({ ...read(lane), ...patch }) });
      changed();
      return read(lane);
    },
    /** Back to what the layout keeps (a bypass of the filters is lifted too). */
    reset() { over.clear(); filtersBypassed = false; changed(); },
    /** The lanes whose mix is not the one the layout keeps. */
    touched() { return [...over.keys()].filter((lane) => JSON.stringify(read(lane)) !== JSON.stringify(kept.get(lane) ?? emptyMix())); },
    get size() { return this.touched().length; },
    /** The lanes that have a filter closing them (what a fallback under load can bypass). */
    filtered(lanes) { return lanes.filter((lane) => read(lane).cutoff < CUTOFF.max); },
    bypassFilters(on) { if (filtersBypassed !== on) { filtersBypassed = on; changed(); } },
    get filtersBypassed() { return filtersBypassed; },
    /** What the layout keeps from now on is what is heard now (the session starts again from there). */
    keep(lanes) { kept = new Map(lanes.map((lane) => [lane, read(lane)])); over.clear(); changed(); return kept; },
    settings(lanes) {
      const anySolo = lanes.some((lane) => read(lane).solo);
      return new Map(lanes.map((lane) => {
        const m = read(lane);
        const silent = m.mute || (anySolo && !m.solo);
        return [lane, { gain: silent ? 0 : dbToGain(m.gainDb), pan: m.pan / 100, cutoff: filtersBypassed || m.cutoff >= CUTOFF.max ? null : m.cutoff }];
      }));
    },
    subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); },
  };
}

/**
 * The guard of a fallback under load: it is fed the peak load of the audio thread (0..1) of every update; a load above the threshold for `updates` in a row says `bypass`, a load below
 * `release` for twice as many says `restore`; in between nothing changes. It is the same rule whether or not the browser can measure; without measurements it is never fed.
 */
export function createLoadGuard({ threshold = 0.85, release = 0.6, updates = 4 } = {}) {
  let high = 0, low = 0, bypassed = false;
  return {
    get bypassed() { return bypassed; },
    feed(peak) {
      const p = clamp(number(peak, 0), 0, 1);
      high = p > threshold ? high + 1 : 0;
      low = p < release ? low + 1 : 0;
      if (!bypassed && high >= updates) { bypassed = true; high = 0; return 'bypass'; }
      if (bypassed && low >= updates * 2) { bypassed = false; low = 0; return 'restore'; }
      return 'ok';
    },
  };
}
