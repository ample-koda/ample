// What a delay really waits: the echo time in milliseconds and in quarter notes, from Time or, when Sync is on, from the note value at the tempo.
// Pure, so node --test checks it.

/** The length of a note value in quarter notes: 1/4 is one, 1/8 half of one, 1/1 four; a t is a triplet (two thirds of it). null for off or a text that is no value. */
export function syncQuarters(sync) {
  const m = /^\s*"?1\/(\d+)(t?)"?\s*$/i.exec(String(sync ?? ''));
  if (!m || Number(m[1]) < 1) return null;
  return (4 / Number(m[1])) * (m[2] ? 2 / 3 : 1);
}

/**
 * @returns {{ ms: number, quarters: number, synced: boolean }} the effective echo time; synced says that Sync decides and Time is not used
 */
export function delayTiming(timeMs, sync, tempo) {
  const beatMs = 60000 / Math.max(1, Number(tempo) || 120);
  const locked = syncQuarters(sync);
  if (locked !== null) return { ms: locked * beatMs, quarters: locked, synced: true };
  const ms = Math.max(0, Number(timeMs) || 0);
  return { ms, quarters: ms / beatMs, synced: false };
}

/** The sentence under the delay: the effective time, its length in quarter notes, and which of Time and Sync decides. */
export function delayTimingText(timeMs, sync, tempo) {
  const { ms, quarters, synced } = delayTiming(timeMs, sync, tempo);
  const bpm = Number(tempo) || 120;
  const notes = `${Math.round(quarters * 100) / 100} quarter note${Math.round(quarters * 100) / 100 === 1 ? '' : 's'}`;
  return `${Math.round(ms)} ms · ${notes} at ${bpm} BPM` + (synced ? ` · Sync ${String(sync).replace(/"/g, '')} decides, Time is not used` : '');
}
