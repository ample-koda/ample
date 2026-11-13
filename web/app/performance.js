// Opt-in Performance workspace: quantized clip launching on named lanes.
// The engine is pure (injected clock and player), so node --test can check it.
// Nothing here touches the arrangement; it only holds a separate performance layout.

export const PERFORMANCE_SETTING_KEY = 'ample.performance.enabled';
export const QUANTIZE = { beat: 'beat', bar: 'bar' };

/** Whether the opt-in setting is on. `storage` is any object with getItem. */
export function performanceEnabled(storage) {
  try { return storage?.getItem(PERFORMANCE_SETTING_KEY) === '1'; } catch { return false; }
}
export function setPerformanceEnabled(storage, on) {
  try { storage?.setItem(PERFORMANCE_SETTING_KEY, on ? '1' : '0'); } catch { /* storage may be blocked */ }
}

/** Next grid time at or after `now` for a clock that started at `origin`. */
export function nextBoundary(now, origin, beatSec, beatsPerBar, quantize) {
  const step = quantize === QUANTIZE.bar ? beatSec * beatsPerBar : beatSec;
  const n = Math.ceil((now - origin) / step - 1e-9);
  return origin + Math.max(0, n) * step;
}

/**
 * @param {{now:()=>number, play:(clip:object, when:number, lane:string)=>object, bpm?:number, beatsPerBar?:number, quantize?:string}} o
 * play() starts a prepared clip at an AudioContext timestamp and returns a handle with stop(when?).
 */
export function createPerformance({ now, play, bpm = 120, beatsPerBar = 4, quantize = QUANTIZE.bar }) {
  const lanes = new Map(); // name -> { clips: Map(id->clip), playing, queued }
  let origin = null;
  const beatSec = 60 / bpm;

  const lane = (name) => {
    if (!lanes.has(name)) lanes.set(name, { clips: new Map(), playing: null, queued: null });
    return lanes.get(name);
  };
  const boundary = () => {
    if (origin === null) origin = now();
    return nextBoundary(now(), origin, beatSec, beatsPerBar, quantize);
  };
  /** Moves queued launches whose time has come to playing. Call from a UI timer. */
  const tick = () => {
    const t = now();
    for (const l of lanes.values()) {
      if (l.queued && l.queued.when <= t) { l.playing = l.queued; l.queued = null; }
    }
  };

  return {
    addClip(laneName, clip) { lane(laneName).clips.set(clip.id, clip); },
    launch(laneName, clipId) {
      tick();
      const l = lane(laneName);
      const clip = l.clips.get(clipId);
      if (!clip) throw new Error(`unknown clip ${clipId} in lane ${laneName}`);
      if (l.queued) { l.queued.handle.cancel?.(); l.queued = null; }
      const when = boundary();
      const handle = play(clip, when, laneName);
      if (l.playing) l.playing.handle.stop(when); // replace at the same boundary
      l.queued = { clipId, when, handle };
      return when;
    },
    cancelQueued(laneName) {
      const l = lane(laneName);
      if (!l.queued) return false;
      l.queued.handle.cancel?.() ?? l.queued.handle.stop();
      l.queued = null;
      return true;
    },
    /** Immediate: stops everything playing and clears every queued launch. */
    stopAll() {
      for (const l of lanes.values()) {
        if (l.queued) { l.queued.handle.cancel?.() ?? l.queued.handle.stop(); l.queued = null; }
        if (l.playing) { l.playing.handle.stop(); l.playing = null; }
      }
      origin = null;
    },
    /** ready / queued / playing per clip, for display. */
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
}
