// The built-in drums of the live chain as single hits (plan local-preview F2): every hit of the pattern is a short buffer (rendered once, exactly, by web/lib/drum-core.mjs) that is
// started on the audio clock a moment ahead of its time, so a knob that changes the pattern is heard from the next hit on, with no re-render of the loop and no swap.
// Looks like a looping source to the chain (start, stop), but it is a scheduler. @ample:live-drums
import { hitsInWindow } from '../lib/drum-schedule.mjs';

const LOOKAHEAD = .12, INTERVAL = 25;

/**
 * context, destination: where the hits go; clock: { span() -> { start, length } of the loop in the take, positionAt(contextTime) -> position in the take }.
 * Returns { start(when), stop(), set(plan), reset(), hitCount }.
 */
export function createDrumVoice(context, destination, clock) {
  const buffers = new WeakMap();
  let plan = { hits: [] }, horizon = 0, timer = 0, started = false, scheduled = [];
  const audioOf = (samples) => {
    if (!buffers.has(samples)) { const buffer = context.createBuffer(1, samples.length, context.sampleRate); buffer.copyToChannel(samples, 0); buffers.set(samples, buffer); }
    return buffers.get(samples);
  };
  const tick = () => {
    if (!started) return;
    const now = context.currentTime, from = Math.max(horizon, now), to = now + LOOKAHEAD;
    scheduled = scheduled.filter((entry) => entry.when + entry.length > now);
    if (to <= from) return;
    for (const { hit, when } of hitsInWindow(plan.hits, { span: clock.span(), positionAt: clock.positionAt, from, to })) {
      const node = context.createBufferSource(), buffer = audioOf(hit.samples);
      node.buffer = buffer; node.connect(destination); node.start(when);
      scheduled.push({ node, when, length: buffer.duration });
    }
    horizon = to;
  };
  const cancelFuture = () => {
    const now = context.currentTime;
    for (const entry of scheduled) if (entry.when > now) { try { entry.node.stop(); } catch { /* gone */ } }
    scheduled = scheduled.filter((entry) => entry.when <= now);
  };
  return {
    start(when) { horizon = when; started = true; timer = setInterval(tick, INTERVAL); tick(); },
    stop() { started = false; clearInterval(timer); for (const entry of scheduled) { try { entry.node.stop(); } catch { /* gone */ } } scheduled = []; },
    /** A new pattern: what was queued but has not started is dropped, the new hits are queued at once. */
    set(next) { plan = next; if (started) { cancelFuture(); horizon = context.currentTime; tick(); } },
    /** The loop range moved: queue again from now. */
    reset() { if (started) { cancelFuture(); horizon = context.currentTime; tick(); } },
    get hitCount() { return plan.hits.length; },
    // the chain treats a source like a buffer node (loop points of a region): nothing to do for a scheduler
    buffer: null, loopStart: 0, loopEnd: 0,
  };
}
