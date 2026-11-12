// Step-pattern effects (duck, tremolo gate) for the live preview, scheduled on the clock of the audio context instead of a timer that sets the gain every 15 ms.
// A small timer only fills a look-ahead window: the gain events are queued in the audio thread ahead of time, so a busy main thread cannot make the pumping jitter.
// The step plans are pure functions (tested in node); the scheduler needs an AudioParam and a way to ask where the playing take is.
import { gateOpenness } from './trance-gate.mjs';

const clamp = (value, min, max) => Math.max(min, Math.min(max, value));

/** Gain events of one duck step, relative to the step start: an x dips to the floor and comes back over `releaseSeconds`; anything else is open. */
export function duckStepEvents(pattern, k, stepSeconds, depthDb, releaseSeconds) {
  const steps = String(pattern || 'x...x...x...x...').padEnd(16, '.').slice(0, 16);
  if (steps[((k % 16) + 16) % 16] !== 'x') return [{ dt: 0, value: 1, jump: true }];
  const floor = 10 ** (-clamp(depthDb, 0, 40) / 20), release = Math.max(.001, releaseSeconds);
  return [{ dt: 0, value: floor, jump: true }, { dt: Math.min(release, stepSeconds), value: floor + (1 - floor) * Math.min(1, stepSeconds / release), jump: false }];
}

/** Gain events of one tremolo-gate step: a short fade (2 ms) from the previous step's gain to this one's, then hold. depth is 0..1. */
export function gateStepEvents(pattern, k, stepSeconds, depth) {
  const text = String(pattern ?? 'x.x.x.x.x.x.x.x.').replace(/^"|"$/g, '');
  if (!text.length) return [{ dt: 0, value: 1, jump: true }];
  const gain = (step) => 1 - clamp(depth, 0, 1) * (1 - gateOpenness(text[((step % text.length) + text.length) % text.length])) ;
  const ramp = Math.min(.002, stepSeconds / 2);
  return [{ dt: 0, value: gain(k - 1), jump: true }, { dt: ramp, value: gain(k), jump: false }];
}

/** Turns the events of a step that starts at context time `start` into AudioParam calls; the part before `now` collapses into the value it has at `now`. */
export function planStep(events, start, now) {
  const ops = [];
  let past = null;   // the last event that is already behind us
  for (const event of events) {
    const time = start + event.dt;
    if (time <= now) { past = event; continue; }
    if (!ops.length && past) {
      // the step is already running: begin at the value it has right now
      const from = start + past.dt, span = time - from;
      ops.push({ op: 'set', value: event.jump || span <= 0 ? past.value : past.value + (event.value - past.value) * ((now - from) / span), time: now });
    }
    ops.push({ op: event.jump ? 'set' : 'ramp', value: event.value, time });
  }
  if (!ops.length && past) ops.push({ op: 'set', value: past.value, time: now });
  return ops;
}

/**
 * Keeps `param` following `stepEvents(k)` while the take plays. clock() gives { position, duration, playing } of the take (seconds); stepSeconds() the current
 * step length. Events are queued up to `lookahead` seconds ahead of the audio clock and the plan is thrown away (reset) when the take jumps or a knob changes.
 */
export function createClockGate(context, param, { clock, stepSeconds, stepEvents, lookahead = .3, interval = 50, map = null, stepBeats = 0 }) {
  // With `map` (a tempoMap of audio-clock.mjs) and `stepBeats` the steps sit on the beat grid of the song: step k starts where beat k*stepBeats is, also after a tempo change.
  // The take position and the song position are the same seconds (a take is rendered from song second 0), so no separate offset is needed.
  const beats = () => (typeof stepBeats === 'function' ? stepBeats() : stepBeats);   // a function when the step length is a knob
  const timed = () => !!(map && beats() > 0);
  const stepAt = (k, fallback) => { const b = beats(); return timed() ? { start: map.secondsAt(k * b), length: map.secondsAt((k + 1) * b) - map.secondsAt(k * b) } : { start: k * fallback, length: fallback }; };
  let anchor = null, seg = 0, k = 0, timer = null;
  const reset = () => { anchor = null; try { param.cancelScheduledValues(context.currentTime); } catch { /* closed */ } };
  const tick = () => {
    const now = context.currentTime, state = clock(), step = stepSeconds();
    if (!state.playing || !(step > 0)) { anchor = null; return; }
    const duration = state.duration > 0 ? state.duration : 0;
    if (anchor) {
      const u = anchor.position + (now - anchor.time), predicted = duration ? ((u % duration) + duration) % duration : u;
      let diff = Math.abs(predicted - state.position);
      if (duration) diff = Math.min(diff, duration - diff);
      if (diff > .08 || anchor.step !== step) { param.cancelScheduledValues(now); anchor = null; }
    }
    if (!anchor) {
      anchor = { position: state.position, time: now, step, duration }; seg = 0; k = timed() ? Math.max(0, Math.floor(map.beatAt(state.position) / beats() + 1e-9)) : Math.floor(state.position / step);
      param.cancelScheduledValues(now);
    }
    for (let guard = 0; guard < 64; guard++) {
      const at = stepAt(k, step), base = seg * (anchor.duration || 0), start = anchor.time + (base + at.start - anchor.position);
      if (start > now + lookahead) break;
      for (const { op, value, time } of planStep(stepEvents(k, at.length), start, now)) {
        if (op === 'set') param.setValueAtTime(value, time); else param.linearRampToValueAtTime(value, time);
      }
      k++;
      if (anchor.duration && stepAt(k, step).start >= anchor.duration - 1e-6) { seg++; k = 0; }
    }
  };
  tick();
  timer = setInterval(tick, interval);
  return { reset, tick, dispose: () => { clearInterval(timer); timer = null; } };
}
