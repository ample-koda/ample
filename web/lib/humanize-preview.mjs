// PR12: a JS preview of the engine's midi-humanize block (src/Ample.Audio/Engine/MidiEffects.cs, Humanize()), using the verified RNG port
// (stable-rng.mjs) so the roll can show (and "bake") the same drift the engine would apply, without a build.
//
// Limitation (documented, not silently wrong): this derives the block's seed the normal way (SeedDeriver on the project's master seed and
// the block's id). If the owner has locked/overridden that block's seed through the project's seed-management UI, or a recipe/control
// override is in play, the preview will not match - there is no client-side way to read that override's value. The common case (an
// ordinary, unlocked block) matches bit-for-bit; verified against spec-tests/prng/vectors.json.
import { StableRng, deriveSeed } from './stable-rng.mjs';
import { barBeats } from './meter.mjs';

const HUMANIZE_DOMAIN = 0x48554D414E495A45n;   // "HUMANIZE", XORed into the block's own derived seed, same as the engine

/**
 * Computes the same per-note (velocity, start) drift the engine's midi-humanize block would, for a clip's notes (in the clip's own text
 * order, matching how the engine reads them). Returns a Map from note index (in `model.notes`) to `{ velocity, start }` (ticks, clamped to
 * one bar, same as the engine). `masterSeed` and `humanizeBlockId` must be BigInt/string from the project and the block as built.
 */
export function previewHumanizeDrift(model, { masterSeed, seed: explicitSeed, humanizeBlockId, velocityRange, timingThousandths }) {
  const seed = (explicitSeed === undefined ? deriveSeed(BigInt(masterSeed), ['block', humanizeBlockId, 'seed', 'seed']) : BigInt(explicitSeed)) ^ HUMANIZE_DOMAIN;
  const rng = new StableRng(BigInt.asUintN(64, seed));
  const vel = Math.max(0, Math.min(60, velocityRange)), tim = Math.max(0, Math.min(200, timingThousandths));
  const barTicks = barBeats() * model.ppq;
  const drift = new Map();
  model.notes.forEach((item, index) => {
    const dv = Number(rng.next() >> 40n) % (2 * vel + 1) - vel;
    const dt = Number(rng.next() >> 40n) % (2 * tim + 1) - tim;
    const n = item.note;
    const velocity = Math.max(1, Math.min(127, n.velocity + dv));
    const start = Math.max(0, Math.min(barTicks - 1, n.start + Math.round((dt / 1000) * model.ppq)));
    drift.set(index, { velocity, start });
  });
  return drift;
}
