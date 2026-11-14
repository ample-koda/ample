// Opt-in Performance workspace: quantized clip launching on named lanes.
// The engine is pure (injected clock and player), so node --test can check it.
// Nothing here touches the arrangement; it only holds a separate performance layout.
// Nothing runs at import time: no storage, timers or audio are touched until a function is called.
//
// Units: every time is in seconds on ONE clock (the AudioContext clock, `now()`), tempo is in beats per
// minute, a bar is `beatsPerBar` beats. Clips are identified by `clip.id` inside a lane; a lane by its name.

export const PERFORMANCE_SETTING_KEY = 'ample.performance.enabled';
export const QUANTIZE = { beat: 'beat', bar: 'bar' };

/** Whether the opt-in setting is on (off unless the stored value is exactly '1'). `storage` is any object with getItem. */
export function performanceEnabled(storage) {
  try { return storage?.getItem(PERFORMANCE_SETTING_KEY) === '1'; } catch { return false; }
}
export function setPerformanceEnabled(storage, on) {
  try { storage?.setItem(PERFORMANCE_SETTING_KEY, on ? '1' : '0'); } catch { /* storage may be blocked */ }
}

/** Next grid time at or after `now` for a clock that started at `origin`. A time before the origin gives the origin. */
export function nextBoundary(now, origin, beatSec, beatsPerBar, quantize) {
  if (![now, origin, beatSec, beatsPerBar].every(Number.isFinite) || beatSec <= 0 || beatsPerBar < 1) throw new RangeError('nextBoundary needs finite times, a positive beat length and at least one beat per bar');
  const step = quantize === QUANTIZE.bar ? beatSec * beatsPerBar : beatSec;
  const n = Math.ceil((now - origin) / step - 1e-9);
  return origin + Math.max(0, n) * step;
}

const checkTempo = (bpm, beatsPerBar, quantize) => {
  if (!Number.isFinite(bpm) || bpm <= 0) throw new RangeError(`bpm must be a positive number, got ${bpm}`);
  if (!Number.isInteger(beatsPerBar) || beatsPerBar < 1) throw new RangeError(`beatsPerBar must be a positive integer, got ${beatsPerBar}`);
  if (quantize !== QUANTIZE.beat && quantize !== QUANTIZE.bar) throw new RangeError(`unknown quantize "${quantize}"`);
};

/**
 * @param {{now:()=>number, play:(clip:object, when:number, lane:string)=>object, bpm?:number, beatsPerBar?:number, quantize?:string, startLead?:number}} o
 * play() starts a prepared clip at an AudioContext timestamp `when` (a time not after now means "at once") and
 * returns a handle: stop(when?) (no argument: at once), cancel() (before it started), and, for a clip that
 * can be replaced, cancelStop() (takes back a stop(when) that has not happened yet).
 * startLead: when the grid starts (first launch after idle / Stop all) it starts this many seconds ahead, so
 * launches a few milliseconds apart (Drums A, then Bass A) land on the same first downbeat.
 */
export function createPerformance({ now, play, bpm = 120, beatsPerBar = 4, quantize = QUANTIZE.bar, startLead = 0.1 }) {
  checkTempo(bpm, beatsPerBar, quantize);
  if (!Number.isFinite(startLead) || startLead < 0) throw new RangeError('startLead must be a number of seconds >= 0');
  const lanes = new Map(); // name -> { clips: Map(id->clip), playing, queued }
  let origin = null;
  let beatSec = 60 / bpm;

  const clock = () => {
    const t = now();
    if (!Number.isFinite(t)) throw new RangeError(`the clock gave ${t}`);
    return t;
  };
  const getLane = (name) => {
    const l = lanes.get(name);
    if (!l) throw new Error(`unknown lane ${name}`);
    return l;
  };
  const ensureOrigin = () => { if (origin === null) origin = clock() + startLead; };
  const boundary = (q) => { ensureOrigin(); return nextBoundary(clock(), origin, beatSec, beatsPerBar, q); };
  const abort = (h) => { if (typeof h.cancel === 'function') h.cancel(); else h.stop(); };
  /** Moves what has started to playing and what has ended away. Call from a UI timer; every other call does it first. */
  const tick = () => {
    const t = clock();
    for (const l of lanes.values()) {
      if (l.playing && l.playing.stopAt !== null && l.playing.stopAt <= t) l.playing = null;
      if (l.queued && l.queued.when <= t) { l.playing = { clipId: l.queued.clipId, handle: l.queued.handle, stopAt: null }; l.queued = null; }
    }
  };
  /** Queues `clip` on a lane at `when`; the clip that plays now stops at the same `when`. */
  const schedule = (l, laneName, clip, when) => {
    const replaces = l.playing && l.playing.stopAt === null;
    if (replaces && typeof l.playing.handle.cancelStop !== 'function') throw new TypeError('play() handles must have cancelStop() so that cancelling a queued replacement keeps the old clip playing');
    const handle = play(clip, when, laneName);
    if (replaces) { l.playing.handle.stop(when); l.playing.stopAt = when; } // the same boundary: no gap, no overlap
    l.queued = { clipId: clip.id, when, handle, replaced: !!replaces };
  };
  /** The clip of a lane plays on, apart from a stop that only a queued replacement scheduled. */
  const holds = (l, clipId) => l.playing?.clipId === clipId && (l.playing.stopAt === null || (l.queued?.replaced && l.playing.stopAt === l.queued.when));
  /** Drops the queued launch of a lane; the clip it was going to replace keeps playing. */
  const unqueue = (l) => {
    abort(l.queued.handle);
    if (l.queued.replaced && l.playing && l.playing.stopAt === l.queued.when) { l.playing.handle.cancelStop(); l.playing.stopAt = null; }
    l.queued = null;
  };

  const api = {
    now: clock,
    addClip(laneName, clip) {
      if (!clip || clip.id === undefined || clip.id === null) throw new Error('a clip needs an id');
      if (!lanes.has(laneName)) lanes.set(laneName, { clips: new Map(), playing: null, queued: null });
      lanes.get(laneName).clips.set(clip.id, clip);
    },
    /** The grid time of the next bar line (what a scene launches on), whatever the launch quantize is. */
    nextBar() { return boundary(QUANTIZE.bar); },
    /** Queues a clip at the next beat or bar. Returns the time it starts, or null when it already plays and nothing else is queued. */
    launch(laneName, clipId) {
      tick();
      const l = getLane(laneName);
      const clip = l.clips.get(clipId);
      if (!clip) throw new Error(`unknown clip ${clipId} in lane ${laneName}`);
      if (l.queued?.clipId === clipId) return l.queued.when; // double launch: already queued
      if (holds(l, clipId)) { // launching what plays: take back a queued replacement
        if (l.queued) unqueue(l);
        return null;
      }
      const when = boundary(quantize);
      if (l.queued) unqueue(l);
      schedule(l, laneName, clip, when);
      return when;
    },
    /**
     * One batch on one timestamp (a scene): [{ lane, action: 'start'|'stop'|'keep', clip? }] at `at`. Everything is
     * checked first, so a bad entry changes nothing.
     */
    launchBatch(at, batch) {
      tick();
      if (!Number.isFinite(at) || at < clock()) throw new RangeError(`the batch time ${at} is not a time from now on`);
      const items = batch.map((b) => {
        const l = getLane(b.lane);
        if (b.action === 'start') {
          const clip = l.clips.get(b.clip);
          if (!clip) throw new Error(`unknown clip ${b.clip} in lane ${b.lane}`);
          return { l, lane: b.lane, action: 'start', clip };
        }
        if (b.action === 'stop' || b.action === 'keep') return { l, lane: b.lane, action: b.action };
        throw new Error(`unknown action ${b.action}`);
      });
      for (const { l, lane, action, clip } of items) {
        if (action === 'keep') continue;
        if (action === 'start' && holds(l, clip.id)) { if (l.queued) unqueue(l); continue; }
        if (l.queued) unqueue(l);
        if (action === 'start') schedule(l, lane, clip, at);
        else if (l.playing && l.playing.stopAt === null) { l.playing.handle.stop(at); l.playing.stopAt = at; }
      }
      return at;
    },
    cancelQueued(laneName) {
      tick(); // a launch whose time has come is playing, not queued
      const l = lanes.get(laneName);
      if (!l?.queued) return false;
      unqueue(l);
      return true;
    },
    /** Immediate: stops everything playing and clears every queued launch. */
    stopAll() {
      tick();
      for (const l of lanes.values()) {
        if (l.queued) { abort(l.queued.handle); l.queued = null; }
        if (l.playing) { l.playing.handle.stop(); l.playing = null; }
      }
      origin = null;
    },
    /**
     * Changes the tempo, keeping the beat position of the grid. Refused while a launch or stop is scheduled on
     * the old grid (cancel or wait first). Clips that play keep their own tempo handling.
     */
    setTempo(newBpm) {
      tick();
      checkTempo(newBpm, beatsPerBar, quantize);
      for (const l of lanes.values()) if (l.queued || (l.playing && l.playing.stopAt !== null)) throw new Error('cannot change the tempo while a launch or stop is scheduled');
      if (origin !== null) {
        const t = clock();
        const beats = Math.max(0, (t - origin) / beatSec);
        beatSec = 60 / newBpm;
        origin = t - beats * beatSec;
      } else beatSec = 60 / newBpm;
      bpm = newBpm;
    },
    get bpm() { return bpm; },
    /** ready / queued / playing per clip, for display. A new object on every call. */
    state() {
      tick();
      const out = {};
      for (const [name, l] of lanes) {
        out[name] = [...l.clips.keys()].map((id) => ({
          id, status: l.playing?.clipId === id ? 'playing' : l.queued?.clipId === id ? 'queued' : 'ready',
        }));
      }
      return out;
    },
  };
  return api;
}
