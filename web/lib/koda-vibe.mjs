// Koda moves with the music: from the level of what is playing (a number per frame) it finds the beats by itself (a rise above the running average), keeps a feel for the time between them, and tells how
// much Koda should move now (amp), the pulse of the last beat (pulse, 1 at the beat and fading) and which way to lean (lean, -1..1, flips each beat). No tempo is needed from the song, so it also follows
// a preview, a sample or a recording. When no beat is found for a while but sound goes on, it carries on at the last pace, softer. Pure. @ample:koda

export const createVibe = () => ({ slow: 0, prev: 0, loud: 0, quiet: 0, active: false, amp: 0, pulse: 0, lean: 0, dir: 1, last: -1e9, period: 520, count: 0, t: 0, onset: false, started: false, stopped: false });

const LOUD = 0.012;       // below this it is silence
const STOP_AFTER = 900;   // ms of silence before the music counts as stopped
const START_AFTER = 120;  // ms of sound before it counts as started

/** One frame: level is the peak of the output (0..1), dt the milliseconds since the last frame. Changes and returns s. */
export function stepVibe(s, level, dt) {
  const k = (tau) => 1 - Math.exp(-dt / tau);
  s.t += dt;
  s.onset = s.started = s.stopped = false;
  s.slow += (level - s.slow) * k(900);
  const rising = level > s.slow * 1.4 + 0.03 && level > s.prev + 0.01 && s.t - s.last > 170;
  s.prev = level;
  if (level > LOUD) { s.loud += dt; s.quiet = 0; } else { s.quiet += dt; s.loud = 0; }
  if (!s.active && s.loud >= START_AFTER) { s.active = true; s.started = true; s.pulse = 1; s.last = s.t; }
  else if (s.active && s.quiet >= STOP_AFTER) { s.active = false; s.stopped = true; }
  if (s.active) {
    if (rising) {
      const gap = s.t - s.last;
      if (gap > 200 && gap < 1300) s.period += (gap - s.period) * 0.35;
      s.last = s.t; s.pulse = 1; s.onset = true;
      // at a fast tempo the body leans every second beat only: it keeps up without flapping
      if (s.period > 330 || ++s.count % 2 === 0) s.dir = -s.dir;
    } else if (s.t - s.last > Math.max(1000, s.period * 2) && s.t - s.last >= s.period) {
      // sound without a clear beat: keep to the pace, softer
      s.last = s.t; s.pulse = Math.max(s.pulse, 0.55); s.dir = -s.dir;
    }
  }
  s.pulse *= Math.exp(-dt / 150);
  s.lean += (s.dir - s.lean) * k(130);
  s.amp += ((s.active ? 1 : 0) - s.amp) * k(s.active ? 120 : 350);
  return s;
}

/** 1 for a relaxed beat, less for a fast one (a period of 250 ms, 240 bpm, gives about half). */
export const easeFast = (period) => Math.min(1, Math.max(0.5, period / 480));

/** The movement of Koda for a frame of the vibe: how far up (px at a body of 140 px), the squash and the lean in degrees. mode 'full' for a Koda that does nothing, 'soft' for one that is busy. */
export function vibePose(s, mode = 'full') {
  // the faster the beat, the smaller each bounce (a fast song is not a frantic Koda)
  const a = s.amp * (mode === 'soft' ? 0.22 : 1) * easeFast(s.period);
  const p = s.pulse * a;
  return { lift: -18 * p, squash: 0.06 * p, lean: s.lean * (mode === 'soft' ? 2 : 4) * s.amp };
}
