// Plays prepared clips for the Performance workspace (issue 266) on one Web Audio clock.
// A clip is a decoded take that loops until it is stopped; the launch engine (web/app/performance.js) decides WHEN,
// this file only starts and stops sources at the timestamps it is given.
// The context is passed in, so node --test can use a fake one.
//
// createClipPlayer({ ctx, output, bufferOf }).play(clip, when, lane) returns the handle the launch engine needs:
//   stop(when?)   no argument: now; with a time: the clip is silent from that time and its source is released shortly after
//   cancelStop()  takes back a stop(when) that has not happened yet (a cancelled queued replacement)
//   cancel()      before it started: nothing is heard
// A clip whose buffer is not ready is refused with an error, so a launch never schedules silence.

const RELEASE_MS = 150;   // after the scheduled silence the source is stopped and disconnected

export function createClipPlayer({ ctx, output, bufferOf, outputFor = null, timers = { set: setTimeout, clear: clearTimeout } }) {
  if (!ctx || !output || typeof bufferOf !== 'function') {
    throw new TypeError('createClipPlayer needs a context, an output and bufferOf(clip)');
  }
  const live = new Set();

  function play(clip, when, lane) {
    const buffer = bufferOf(clip, lane);
    if (!buffer) throw new Error(`"${clip?.label ?? clip?.id}" is not loaded yet.`);
    const source = ctx.createBufferSource();
    source.buffer = buffer;
    source.loop = clip?.loop !== false;   // a clip of an accent lane plays once (issue 280)
    const gain = ctx.createGain();
    gain.gain.value = 1;
    source.connect(gain);
    gain.connect(outputFor ? outputFor(lane) : output);   // a lane has its own bus when the session mix is on (issue 276)
    source.start(Math.max(when, ctx.currentTime));
    let released = false, timer = null;
    const handle = {
      clip: clip.id, lane,
      release() {
        if (released) return;
        released = true;
        if (timer !== null) { timers.clear(timer); timer = null; }
        try { source.stop(); } catch { /* it never started, or stopped already */ }
        try { source.disconnect(); gain.disconnect(); } catch { /* disconnected already */ }
        live.delete(handle);
      },
      stop(at) {
        if (released) return;
        if (at === undefined || at <= ctx.currentTime) { handle.release(); return; }
        gain.gain.cancelScheduledValues(ctx.currentTime);
        gain.gain.setValueAtTime(1, ctx.currentTime);
        gain.gain.setValueAtTime(0, at);
        if (timer !== null) timers.clear(timer);
        timer = timers.set(() => { timer = null; handle.release(); }, Math.max(0, (at - ctx.currentTime) * 1000) + RELEASE_MS);
      },
      cancelStop() {
        if (released) return;
        if (timer !== null) { timers.clear(timer); timer = null; }
        gain.gain.cancelScheduledValues(ctx.currentTime);
        gain.gain.setValueAtTime(1, ctx.currentTime);
      },
      cancel() { handle.release(); },
    };
    live.add(handle);
    if (!source.loop) source.onended = () => { handle.ended = true; handle.release(); };
    return handle;
  }

  /** Releases every source that is still around (leaving the workspace). */
  const releaseAll = () => { for (const handle of [...live]) handle.release(); };
  return { play, releaseAll, get count() { return live.size; } };
}

/**
 * One bus per lane between the clips of the lane and the output: a low pass filter (only on the path while it closes the sound), a pan and a gain, all set at once from the session mix
 * (performance-lanemix.mjs settings). Nodes the browser does not have (a stereo panner, a biquad) are left out; the gain always works (issue 276).
 */
export function createLaneBuses({ ctx, output }) {
  const buses = new Map();
  const smooth = (param, value) => {
    if (!param) return;
    if (typeof param.setTargetAtTime === 'function') { param.cancelScheduledValues?.(ctx.currentTime); param.setTargetAtTime(value, ctx.currentTime, 0.01); } else param.value = value;
  };
  const make = () => {
    const input = ctx.createGain(), gain = ctx.createGain();
    const filter = typeof ctx.createBiquadFilter === 'function' ? ctx.createBiquadFilter() : null;
    const panner = typeof ctx.createStereoPanner === 'function' ? ctx.createStereoPanner() : null;
    if (filter) { filter.type = 'lowpass'; filter.frequency.value = 20000; }
    const bus = { input, gain, filter, panner, filtering: false };
    // the path: input, then the filter when it is used, then the pan, then the gain, then the output
    bus.route = (filtering) => {
      input.disconnect?.();
      const after = panner ?? gain;
      input.connect(filtering && filter ? filter : after);
      if (filter) { filter.disconnect?.(); filter.connect(after); }
      if (panner) { panner.disconnect?.(); panner.connect(gain); }
      bus.filtering = Boolean(filtering && filter);
    };
    gain.connect(output);
    bus.route(false);
    return bus;
  };
  const bus = (lane) => { if (!buses.has(lane)) buses.set(lane, make()); return buses.get(lane); };
  return {
    /** Where the clips of a lane connect to. */
    input: (lane) => bus(lane).input,
    /** Sets every lane: gain 0..1, pan -1..1, cutoff in Hz or null for no filter. */
    apply(settings) {
      for (const [lane, s] of settings) {
        const b = bus(lane);
        smooth(b.gain.gain, s.gain);
        if (b.panner) smooth(b.panner.pan, s.pan);
        const filtering = s.cutoff !== null && s.cutoff !== undefined;
        if (filtering !== b.filtering) b.route(filtering);
        if (filtering && b.filter) smooth(b.filter.frequency, s.cutoff);
      }
    },
    /** Lets go of every bus (leaving the workspace). */
    releaseAll() { for (const b of buses.values()) { for (const n of [b.input, b.filter, b.panner, b.gain]) { try { n?.disconnect?.(); } catch { /* disconnected already */ } } } buses.clear(); },
    get count() { return buses.size; },
  };
}
