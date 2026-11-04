const integer = (value, fallback) => Number.isFinite(Number.parseInt(value, 10)) ? Number.parseInt(value, 10) : fallback;
const unitsPerBeat = 3_000_000;
const roundAway = (value) => value < 0 ? Math.ceil(value - 0.5) : Math.floor(value + 0.5);
const units = (value, fallback) => Number.isFinite(Number(value)) ? roundAway(Number(value) * unitsPerBeat) : fallback;
const grids = { '1/4': unitsPerBeat, '1/8': unitsPerBeat / 2, '1/32': unitsPerBeat / 8, '1/8t': unitsPerBeat / 3, '1/16': unitsPerBeat / 4 };
const roundThousandth = (value) => roundAway(value / 3000) / 1000;

/** Applies AMPLE's quantize grid, strength, decimal rounding and tick-field removal to parsed MIDI events. */
export function quantizeMidiEvents(input, params) {
  const root = JSON.parse(JSON.stringify(typeof input === 'string' ? JSON.parse(input) : input));
  if (params.enabled === 'off') return root;
  const grid = grids[params.grid] || grids['1/16'], strength = Math.max(0, Math.min(100, integer(params.strength, 100)));
  for (const note of root.notes || []) {
    const beat = units(note.beat, 0), snapped = roundAway(beat / grid) * grid;
    note.beat = Math.max(0, Math.min(3.999, roundThousandth(beat + roundAway((snapped - beat) * strength / 100))));
    delete note.tick;
    if (params.lengths === 'on') {
      const length = units(note.beats, unitsPerBeat), snappedLength = Math.max(grid, roundAway(length / grid) * grid);
      note.beats = roundThousandth(length + roundAway((snappedLength - length) * strength / 100));
      delete note.ticks;
    }
  }
  return root;
}
