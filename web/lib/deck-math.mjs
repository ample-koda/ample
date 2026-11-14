// Pure maths for the two-deck DJ cueing of the Performance workspace (issue 278). No audio here, so it is testable in node.
// Decks hold material that is already at the set tempo and aligned to a declared downbeat; there is no beat detection or warping.

const EPS = 1e-9;

/** Equal-power crossfader. pos 0 = only deck A, 1 = only deck B. Returns {a, b} gains with a*a + b*b = 1 (exact 1/0 at the ends). */
export function crossfadeGains(pos) {
  const p = Number.isNaN(pos) || typeof pos !== 'number' ? 0.5 : Math.min(1, Math.max(0, pos));
  if (p === 0) return { a: 1, b: 0 };
  if (p === 1) return { a: 0, b: 1 };
  return { a: Math.cos(p * Math.PI / 2), b: Math.sin(p * Math.PI / 2) };
}

/** Length of one beat in seconds at a tempo (fractional tempos are fine). */
export function beatSeconds(bpm) {
  if (!(bpm > 0) || !Number.isFinite(bpm)) throw new RangeError('bpm must be positive');
  return 60 / bpm;
}

/**
 * Phase of a deck inside its loop, in beats, counted from the declared downbeat.
 * elapsedSec = seconds since the deck started playing; loopBeats = loop length in beats.
 * A value within rounding noise of the loop length is reported as 0, never as 3.9999999.
 */
export function deckPhaseBeats(elapsedSec, bpm, loopBeats, downbeatBeats = 0) {
  if (!(loopBeats > 0)) throw new RangeError('loopBeats must be positive');
  const beats = elapsedSec / beatSeconds(bpm) - downbeatBeats;
  const r = ((beats % loopBeats) + loopBeats) % loopBeats;
  return loopBeats - r < EPS ? 0 : r;
}

/** Next time on the shared clock (seconds) at which a bar begins, at or after nowSec. The clock started at clockStartSec. */
export function nextBarStart(nowSec, clockStartSec, bpm, beatsPerBar = 4) {
  if (!(beatsPerBar > 0)) throw new RangeError('beatsPerBar must be positive');
  const bar = beatSeconds(bpm) * beatsPerBar;
  const n = Math.ceil((nowSec - clockStartSec) / bar - EPS);
  return clockStartSec + Math.max(0, n) * bar;
}

/** Signed offset in beats of deck B against deck A, wrapped to (-loop/2, loop/2]; 0 means in phase. */
export function phaseDriftBeats(phaseA, phaseB, loopBeats) {
  if (!(loopBeats > 0)) throw new RangeError('loopBeats must be positive');
  let d = (phaseB - phaseA) % loopBeats;
  if (d > loopBeats / 2) d -= loopBeats;
  if (d <= -loopBeats / 2) d += loopBeats;
  return Math.abs(d) < EPS ? 0 : d;
}

const norm = (id) => (id == null || id === '' || id === 'default' ? 'default' : String(id));

/**
 * Output routing. Cue is private: it never reaches the room.
 * devices = {canSplit: bool, cueDeviceId: string|null, roomDeviceId: string|null, error?: string}.
 * The room context always plays on the system default output unless roomDeviceId says otherwise, so an unknown room
 * device counts as "default" and a cue device "default" is the same device. error = opening the cue device failed.
 * Returns {mode, cueToRoom, message}; cue is only audible in mode "split".
 */
export function routingPlan(devices) {
  const d = devices || {};
  if (!d.canSplit) return { mode: 'unavailable', cueToRoom: false, message: 'Independent cue output is not available on this device: cue is off, so nothing private can reach the room.' };
  if (!d.cueDeviceId) return { mode: 'no-cue-device', cueToRoom: false, message: 'Pick a headphone output for cue. Until then cue stays silent.' };
  if (norm(d.cueDeviceId) === norm(d.roomDeviceId)) return { mode: 'same-device', cueToRoom: false, message: 'Cue and room use the same output; cue is held silent so it cannot reach the audience.' };
  if (d.error) return { mode: 'failed', cueToRoom: false, message: 'The headphone output could not be opened (' + d.error + '); cue stays silent.' };
  return { mode: 'split', cueToRoom: false, message: 'Cue plays on the headphone output only.' };
}
