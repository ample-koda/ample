// The hi-hat roll chopper (PR10): slices a note into N equal pieces with exact tick arithmetic (the pieces always sum to the original length).
export const CHOP_DIVISIONS = ['1/2', '1/4', '1/8', '1/16', '1/32', '1/4t', '1/8t', '1/16t'];
const COUNT = { '1/2': 2, '1/4': 4, '1/8': 8, '1/16': 16, '1/32': 32, '1/4t': 3, '1/8t': 6, '1/16t': 12 };

function seededRandom(seed) { const x = Math.sin(seed * 999.123) * 10000; return x - Math.floor(x); }
const clampVelocity = (v) => Math.max(1, Math.min(127, Math.round(v)));

function rampVelocity(base, i, count, ramp, seed) {
  if (count <= 1) return base;
  const t = i / (count - 1);
  if (ramp === 'rising') return clampVelocity(base * (0.5 + 0.5 * t));
  if (ramp === 'falling') return clampVelocity(base * (1 - 0.5 * t));
  if (ramp === 'humanised') return clampVelocity(base * (0.8 + seededRandom(seed + i) * 0.4));
  return base;
}

/**
 * Slices one note (`{ at, length, pitch, velocity }`, ticks) into `division`'s equal pieces, same pitch, a velocity ramp and a gate length.
 * The pieces' lengths always sum to exactly `note.length` (the first pieces absorb the remainder of the integer division, never drifting).
 * `gatePercent` (1-100) shortens each piece's sounding length without changing its start (a gap before the next piece); `ramp` is
 * 'flat' | 'rising' | 'falling' | 'humanised' (seeded, so the same seed always chops the same way). Returns `[note]` unchanged for an
 * unknown division or a note shorter than the division count.
 */
export function chop(note, division, { ramp = 'flat', gatePercent = 100, seed = 1 } = {}) {
  const count = COUNT[division];
  if (!count || note.length < count) return [note];
  const base = Math.floor(note.length / count), extra = note.length - base * count;
  const out = [];
  let at = note.at;
  for (let i = 0; i < count; i++) {
    const full = base + (i < extra ? 1 : 0);
    const length = Math.max(1, Math.round(full * (Math.max(1, Math.min(100, gatePercent)) / 100)));
    out.push({ at, length, pitch: note.pitch, velocity: rampVelocity(note.velocity, i, count, ramp, seed) });
    at += full;
  }
  return out;
}
