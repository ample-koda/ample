import { StableRng } from './stable-rng.mjs';

const integer = (value, fallback) => Number.isFinite(Number.parseInt(value, 10)) ? Number.parseInt(value, 10) : fallback;
const upDown = (index, count) => { const cycle = Math.max(1, (count * 2) - 2), position = index % cycle; return position < count ? position : cycle - position; };

/** Applies AMPLE's deterministic arpeggiator. Random mode requires the exact effective block seed. */
export function arpeggiateMidiEvents(input, params, seed) {
  const root = JSON.parse(JSON.stringify(typeof input === 'string' ? JSON.parse(input) : input));
  if (params.enabled === 'off') return root;
  const step = ({ '1/4': 1, '1/8': 0.5, '1/32': 0.125 })[params.rate] || 0.25;
  const octaves = Math.max(1, Math.min(4, integer(params.octaves, 1))), gate = Math.max(10, Math.min(100, integer(params.gate, 80))), pattern = params.pattern || 'up';
  const rng = pattern === 'random' ? new StableRng(BigInt.asUintN(64, BigInt(seed)) ^ 0x415250454747494Fn) : null;
  const groups = new Map();
  for (const note of root.notes || []) {
    const bar = integer(note.bar, 1), beat = Number(note.beat || 0), key = `${bar}:${beat}`;
    if (!groups.has(key)) groups.set(key, { bar, beat, notes: [] });
    groups.get(key).notes.push(note);
  }
  const output = [];
  for (const group of [...groups.values()].sort((a, b) => a.bar - b.bar || a.beat - b.beat)) {
    const pitches = [...new Set(group.notes.map((note) => integer(note.pitch, 60)))].sort((a, b) => a - b);
    const pool = Array.from({ length: octaves }, (_, octave) => pitches.map((pitch) => pitch + (12 * octave))).flat(), template = group.notes[0];
    const total = Number(template.beats ?? 4), count = Math.floor(total / step);
    for (let index = 0; index < count && group.beat + (index * step) < 4; index++) {
      if (output.length >= 20000) throw new RangeError('The local arpeggiator preview exceeds 20,000 notes.');
      const pitchIndex = pattern === 'down' ? pool.length - 1 - (index % pool.length)
        : pattern === 'updown' ? upDown(index, pool.length)
          : pattern === 'random' ? Number(rng.next() >> 40n) % pool.length : index % pool.length;
      const note = { ...template, pitch: Math.max(0, Math.min(127, pool[pitchIndex])), beat: group.beat + (index * step), beats: Math.round(step * gate / 100 * 1000) / 1000 };
      delete note.tick; delete note.ticks; output.push(note);
    }
  }
  return { ...root, notes: output };
}
