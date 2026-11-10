// The notes of a row of a sequence pattern, written as "bar:beat:length:pitch:velocity" entries (bar from 1, beat and length in beats).
// Pure helpers for the sequencer workspace: how many notes a row has, and a small picture of them.

const NOTE = /(\d+):([\d.]+):([\d.]+):(\d+):(\d+)/g;

/** The notes of a row as plain numbers. Anything that is not a note entry (a formula, steps of a drum) gives an empty list. */
export function notesOf(text) {
  if (typeof text !== 'string' || text.trim().startsWith('=')) return [];
  return [...text.matchAll(NOTE)].map((m) => ({ bar: Number(m[1]), beat: Number(m[2]), length: Number(m[3]), pitch: Number(m[4]), velocity: Number(m[5]) }));
}

/** How many notes a row has. */
export const notesIn = (text) => notesOf(text).length;

/**
 * Where the notes of a row go in a small picture, as fractions between 0 and 1: x and width along the pattern, y from the top (the highest pitch of the row is at the top).
 * `bars` is the length of the pattern; `beatsPerBar` is 4 unless the song says otherwise.
 */
export function thumbRects(text, bars, beatsPerBar = 4) {
  const notes = notesOf(text), span = Math.max(1, bars) * beatsPerBar;
  if (!notes.length) return [];
  const low = Math.min(...notes.map((n) => n.pitch)), high = Math.max(...notes.map((n) => n.pitch)), range = Math.max(1, high - low);
  return notes.map((n) => {
    const x = Math.min(1, Math.max(0, ((n.bar - 1) * beatsPerBar + n.beat) / span));
    const w = Math.min(1 - x, Math.max(0.004, n.length / span));
    return { x, w, y: high === low ? 0.5 : (high - n.pitch) / range, velocity: n.velocity };
  });
}
