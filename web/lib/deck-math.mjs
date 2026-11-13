// Pure maths for the two-deck DJ cueing of the Performance workspace (issue 278). No audio here, so it is testable in node.
// Decks hold material that is already at the set tempo and aligned to a declared downbeat; there is no beat detection or warping.

/** Equal-power crossfader. pos 0 = only deck A, 1 = only deck B. Returns {a, b} gains. */
export function crossfadeGains(pos) {
  const p = Math.min(1, Math.max(0, Number.isFinite(pos) ? pos : 0.5));
  return { a: Math.cos(p * Math.PI / 2), b: Math.sin(p * Math.PI / 2) };
}

/** Length of one beat in seconds at a tempo. */
export function beatSeconds(bpm) {
  if (!(bpm > 0)) throw new RangeError('bpm must be positive');
  return 60 / bpm;
}

/**
 * Phase of a deck inside its loop, in beats, counted from the declared downbeat.
 * elapsedSec = seconds since the deck started playing; loopBeats = loop length in beats.
 */
export function deckPhaseBeats(elapsedSec, bpm, loopBeats, downbeatBeats = 0) {
  if (!(loopBeats > 0)) throw new RangeError('loopBeats must be positive');
  const beats = elapsedSec / beatSeconds(bpm) - downbeatBeats;
  return ((beats % loopBeats) + loopBeats) % loopBeats;
}

/** Next time on the shared clock (seconds) at which a bar begins, at or after nowSec. The clock started at clockStartSec. */
export function nextBarStart(nowSec, clockStartSec, bpm, beatsPerBar = 4) {
  const bar = beatSeconds(bpm) * beatsPerBar;
  const n = Math.ceil((nowSec - clockStartSec) / bar - 1e-9);
  return clockStartSec + Math.max(0, n) * bar;
}

/** Signed offset in beats of deck B against deck A, wrapped to (-loop/2, loop/2]; 0 means in phase. */
export function phaseDriftBeats(phaseA, phaseB, loopBeats) {
  let d = (phaseB - phaseA) % loopBeats;
  if (d > loopBeats / 2) d -= loopBeats;
  if (d <= -loopBeats / 2) d += loopBeats;
  return d;
}

/**
 * Output routing. Cue is private: it never reaches the room.
 * devices = {canSplit: bool, cueDeviceId: string|null, roomDeviceId: string|null}.
 * Returns {mode, cueToRoom, message}; cue is only audible in mode "split".
 */
export function routingPlan(devices) {
  const d = devices || {};
  if (!d.canSplit) return { mode: 'unavailable', cueToRoom: false, message: 'Independent cue output is not available on this device: cue is off, so nothing private can reach the room.' };
  if (!d.cueDeviceId) return { mode: 'no-cue-device', cueToRoom: false, message: 'Pick a headphone output for cue. Until then cue stays silent.' };
  if (d.cueDeviceId === d.roomDeviceId) return { mode: 'same-device', cueToRoom: false, message: 'Cue and room use the same output; cue is held silent so it cannot reach the audience.' };
  return { mode: 'split', cueToRoom: false, message: 'Cue plays on the headphone output only.' };
}
