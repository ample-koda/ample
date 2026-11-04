// Riff machine and pattern fill (PR20): generates a short seeded rhythm/riff, always deterministic for the same seed, so re-rolling the same
// seed always gives the same result (and two different seeds reliably differ). The roll writes the result with pasteNotes (new notes only,
// nothing existing is touched), so it is one ordinary undoable edit like every other gesture.

function seededRandom(seed) { const x = Math.sin(seed * 999.123) * 10000; return x - Math.floor(x); }

/**
 * Generates `bars` bars of a riff at `stepsPerBar` resolution, starting at tick `at0`. `density` (0-100) is the chance a step sounds at all;
 * `accentEvery` (for example 4) raises the velocity of every Nth step (0 disables accents). Pitches are chosen within +-`spread` semitones of
 * `rootPitch`; when `snapToScale` is given (a function `pitch => nearestPitch`, e.g. built from web/lib/scales.mjs's nearestScaleTone), every
 * chosen pitch is snapped through it. Returns plain `{ at, length, pitch, velocity }` notes (ticks), ready for pasteNotes.
 */
export function generateRiff({ at0 = 0, bars = 1, barTicks, stepsPerBar = 8, density = 60, accentEvery = 4, rootPitch = 60, spread = 7, gatePercent = 80, seed = 1, snapToScale = null } = {}) {
  const totalSteps = Math.max(1, Math.round(bars * stepsPerBar));
  const stepTicks = barTicks / stepsPerBar;
  const notes = [];
  for (let i = 0; i < totalSteps; i++) {
    if (seededRandom(seed + i * 3) * 100 > Math.max(0, Math.min(100, density))) continue;
    const accented = accentEvery > 0 && i % accentEvery === 0;
    const velocity = Math.max(1, Math.min(127, Math.round((accented ? 100 : 72) + seededRandom(seed + i * 3 + 1) * 25)));
    let pitch = Math.round(rootPitch + (seededRandom(seed + i * 3 + 2) * 2 - 1) * spread);
    pitch = Math.max(0, Math.min(127, pitch));
    if (snapToScale) pitch = snapToScale(pitch);
    const length = Math.max(1, Math.round(stepTicks * (Math.max(1, Math.min(100, gatePercent)) / 100)));
    notes.push({ at: at0 + i * stepTicks, length, pitch, velocity });
  }
  return notes;
}
