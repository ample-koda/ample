// Pure model of the Grid (step-sequencer) view of the Lite roll "AMPLE Jam" (plan MLS10, decision 15): which pitches are rows, how long a step is,
// which notes a cell holds and how a velocity is shaded. Same note text as the Roll view: a cell is a note of one step at that pitch, nothing new is stored.
// No DOM. @ample:pianoroll-lite

/** General MIDI percussion names for the rows of a clip that has no notes yet (kick, snare, hats, clap ...), highest row first. */
export const GM_DRUM_DEFAULTS = [49, 51, 46, 42, 39, 38, 36];
const GM_NAMES = { 35: 'Kick 2', 36: 'Kick', 37: 'Side stick', 38: 'Snare', 39: 'Clap', 40: 'Snare 2', 41: 'Low tom', 42: 'Closed hat', 43: 'Floor tom', 44: 'Pedal hat', 45: 'Tom', 46: 'Open hat', 47: 'Mid tom', 48: 'High tom', 49: 'Crash', 50: 'High tom 2', 51: 'Ride', 52: 'China', 53: 'Ride bell', 54: 'Tambourine', 55: 'Splash', 56: 'Cowbell', 57: 'Crash 2', 59: 'Ride 2', 60: 'Bongo', 61: 'Bongo low', 62: 'Conga mute', 63: 'Conga', 64: 'Conga low', 69: 'Cabasa', 70: 'Maracas', 75: 'Claves', 76: 'Wood block', 77: 'Wood block low' };
const NOTE_NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];

/** The label of a row: the GM drum name when there is one, else the note name (C4 = 60). */
export const pitchLabel = (pitch) => `${GM_NAMES[pitch] ?? `${NOTE_NAMES[pitch % 12]}${Math.floor(pitch / 12) - 1}`}`;
export const noteLabel = (pitch) => `${NOTE_NAMES[pitch % 12]}${Math.floor(pitch / 12) - 1}`;
export const drumChoices = () => Object.keys(GM_NAMES).map(Number).sort((a, b) => b - a).map((pitch) => ({ pitch, label: GM_NAMES[pitch] }));

/**
 * The rows of the Grid view, highest pitch first: the distinct pitches present in the clip plus the `pins` (rows the view already showed or the user added, so
 * a row does not vanish when its last step is cleared). An empty clip with no pins gets the GM drum defaults.
 */
export function gridPitches(presentPitches, pins = []) {
  const set = new Set([...presentPitches, ...pins].filter((p) => Number.isInteger(p) && p >= 0 && p <= 127));
  if (!set.size) for (const p of GM_DRUM_DEFAULTS) set.add(p);
  return [...set].sort((a, b) => b - a);
}

/** The step length in ticks: the snap grid, or a 1/16 when snap is off or free (decision 13's list gives `snapBeats`; 0 means no snap). */
export const stepTicks = (snapBeatsValue, ppq) => Math.max(1, Math.round((snapBeatsValue > 0 ? snapBeatsValue : 0.25) * ppq));

/** The step column that holds `tick` and the tick where that column starts. */
export const columnOf = (tick, step) => Math.floor(Math.max(0, tick) / step);
export const columnStart = (column, step) => column * step;

/** The (tie-merged) rows that start inside the cell [column*step, (column+1)*step) at `pitch`: the notes the cell shows. */
export const cellRows = (rows, column, pitch, step) => rows.filter((r) => r.pitch === pitch && r.at >= column * step && r.at < (column + 1) * step);

/** The (tie-merged) rows at `pitch` that sound anywhere inside the cell: the ones starting in it and the ones that began earlier and run into it (a note longer than a step fills several cells). */
export const coveringRows = (rows, column, pitch, step) => rows.filter((r) => r.pitch === pitch && r.at < (column + 1) * step && r.at + Math.max(1, r.length) > column * step);

/** What a cell shows: the rows covering it and whether it is the cell the note starts in (`head`) or a later cell of a longer note (`tail`). `last` marks the final cell of such a note. */
export function cellInfo(rows, column, pitch, step) {
  const covering = coveringRows(rows, column, pitch, step);
  if (!covering.length) return { rows: [], head: false, tail: false, last: false };
  const head = covering.some((r) => r.at >= column * step), lastCol = (r) => columnOf(Math.max(r.at, r.at + r.length - 1), step);
  return { rows: covering, head, tail: !head, last: covering.some((r) => lastCol(r) === column) };
}

/** The row height after a zoom step, kept inside the Grid limits (14-56 px). */
export const zoomGridRows = (rowH, factor) => Math.round(Math.max(14, Math.min(56, rowH * factor)) * 100) / 100;

/** How strongly a filled cell is drawn for a velocity: soft = light, loud = saturated (screenshot 4 of the plan). Monotonic, 0.3 to 1. */
export const cellShade = (velocity) => Math.round((0.3 + 0.7 * Math.max(0, Math.min(127, velocity)) / 127) * 1000) / 1000;

/** The row index (0 = top) at grid pixel `y`, or -1 outside the rows. */
export const rowAt = (y, scrollY, rowH, count) => { const r = Math.floor((y + scrollY) / rowH); return r >= 0 && r < count ? r : -1; };

/** The "volume" of one grid row: the average velocity of its notes (0 when the row is empty). */
export function rowVolume(rows, pitch) {
  let sum = 0, count = 0;
  for (const row of rows) if (row.pitch === pitch) { sum += row.velocity; count++; }
  return count ? Math.round(sum / count) : 0;
}

/** Dragging a row's knob up makes every step louder, down softer: 90 px is a doubling or a halving (never below a tenth or above four times). */
export const knobFactor = (dy) => Math.max(0.1, Math.min(4, 2 ** (-dy / 90)));

/** A velocity scaled by a factor, kept inside what a note can have (1 to 127). */
export const scaleVelocity = (velocity, factor) => Math.max(1, Math.min(127, Math.round(velocity * factor)));
