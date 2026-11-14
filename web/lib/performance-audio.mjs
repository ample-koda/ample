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

export function createClipPlayer({ ctx, output, bufferOf, timers = { set: setTimeout, clear: clearTimeout } }) {
  if (!ctx || !output || typeof bufferOf !== 'function') {
    throw new TypeError('createClipPlayer needs a context, an output and bufferOf(clip)');
  }
  const live = new Set();

  function play(clip, when, lane) {
    const buffer = bufferOf(clip, lane);
    if (!buffer) throw new Error(`"${clip?.label ?? clip?.id}" is not loaded yet.`);
    const source = ctx.createBufferSource();
    source.buffer = buffer;
    source.loop = true;
    const gain = ctx.createGain();
    gain.gain.value = 1;
    source.connect(gain);
    gain.connect(output);
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
    return handle;
  }

  /** Releases every source that is still around (leaving the workspace). */
  const releaseAll = () => { for (const handle of [...live]) handle.release(); };
  return { play, releaseAll, get count() { return live.size; } };
}
