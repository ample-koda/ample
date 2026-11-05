// The arithmetic of editing the parts of a block (\`at 1 for 8 as intro, 9 for 8\`): snap, clamp, overlap, and the new list of parts for a move, a resize, a split,
// a repeat and a delete. Pure and tested without a DOM; the Playlist and the Arrangement use the same rules (the limits of Edits.SetParts: a part starts at bar 1
// or later, lasts at least one bar, and ends by bar 256). @ample:part-editing

export const LAST_BAR = 256;

/** Rounds a bar to the grid (1 = every bar, 4 = every 4 bars; the first bar of a grid step is 1, 5, 9, ...). */
export const snapBar = (bar, step = 1) => (step <= 1 ? Math.round(bar) : Math.round((bar - 1) / step) * step + 1);

/** The reason a list of parts is not allowed, or null. */
export function partsProblem(parts) {
  for (const p of parts) {
    if (!Number.isInteger(p.at) || p.at < 1) return 'A part starts at bar 1 or later.';
    if (!Number.isInteger(p.length) || p.length < 1) return 'A part lasts at least one bar.';
    if (p.at + p.length - 1 > LAST_BAR) return `A part ends by bar ${LAST_BAR}.`;
  }
  const sorted = [...parts].sort((a, b) => a.at - b.at);
  for (let i = 1; i < sorted.length; i++) if (sorted[i].at < sorted[i - 1].at + sorted[i - 1].length) return 'Two parts of one block may not overlap.';
  return null;
}

const finish = (parts) => { const problem = partsProblem(parts); return problem ? { problem } : { parts: [...parts].sort((a, b) => a.at - b.at) }; };
const copy = (parts) => parts.map((p) => ({ at: p.at, length: p.length, name: p.name || '' }));

/** { parts } or { problem }. The new place of part `index` (the first bar). */
export function movePart(parts, index, at) { const next = copy(parts); next[index].at = at; return finish(next); }
/** The new length of part `index`. */
export function resizePart(parts, index, length) { const next = copy(parts); next[index].length = length; return finish(next); }
/** Moves several parts (of one block) by `delta` bars; one refused part refuses all. */
export function movePartsBy(parts, indexes, delta) { const next = copy(parts); for (const i of indexes) next[i].at += delta; return finish(next); }
/** Splits part `index` at a bar (the first bar of the second piece); both pieces are at least one bar. */
export function splitPart(parts, index, bar) {
  const p = parts[index];
  if (!(bar > p.at && bar < p.at + p.length)) return { problem: 'Split inside the clip, not at its edge.' };
  const next = copy(parts);
  next.splice(index, 1, { at: p.at, length: bar - p.at, name: p.name || '' }, { at: bar, length: p.at + p.length - bar, name: '' });
  return finish(next);
}
/** Adds a part after part `index`, of the same length (the "Repeat" action); starts where the part ends. */
export function repeatPart(parts, index) {
  const p = parts[index];
  const next = copy(parts);
  next.push({ at: p.at + p.length, length: p.length, name: '' });
  return finish(next);
}
/** Adds a new part (a clip drawn on an empty stretch). */
export function addPart(parts, at, length) { return finish([...copy(parts), { at, length, name: '' }]); }
/** Takes part `index` out; null when it was the only one (the caller decides what to do with a block that has no part left). */
export function removePart(parts, index) { return parts.length <= 1 ? null : finish(copy(parts).filter((_, i) => i !== index)); }

/** The first bar of the grid at a pixel position, for a timeline with `bw` pixels per bar. */
export const barAt = (x, bw) => Math.floor(x / bw) + 1;
