import { StableRng } from './stable-rng.mjs';

const integer = (value, fallback) => Number.isFinite(Number.parseInt(value, 10)) ? Number.parseInt(value, 10) : fallback;

/** Applies AMPLE's seeded humanize transform using the exact effective block seed returned by the engine. */
export function humanizeMidiEvents(input, params, seed) {
  const root = JSON.parse(JSON.stringify(typeof input === 'string' ? JSON.parse(input) : input));
  if (params.enabled === 'off') return root;
  const velocity = Math.max(0, Math.min(60, integer(params.velocity, 10))), timing = Math.max(0, Math.min(200, integer(params.timing, 20)));
  const rng = new StableRng(BigInt.asUintN(64, BigInt(seed)) ^ 0x48554D414E495A45n);
  for (const note of root.notes || []) {
    const dv = Number(rng.next() >> 40n) % ((2 * velocity) + 1) - velocity;
    const dt = Number(rng.next() >> 40n) % ((2 * timing) + 1) - timing;
    note.velocity = Math.max(1, Math.min(127, integer(note.velocity, 96) + dv));
    note.beat = Math.max(0, Math.min(3.99, Number(note.beat || 0) + (dt / 1000)));
    delete note.tick;
  }
  return root;
}
