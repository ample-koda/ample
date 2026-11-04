// The pure part of the section timeline (plan patterns P07): where the blocks of a drums block's song are drawn, which colour a pattern name gets, and what a drag means.
// The edits themselves (setBars, moveSection, removeSection, addSection) are in pattern-model.mjs. @ample:patterns

/** How many colours the timeline uses; the colours themselves are CSS variables (--sectl-0 ... --sectl-7). */
export const COLOURS = 8;

/** The colour number of a pattern name: always the same for the same name, in every block, so repeats look alike. */
export function colourOf(name) {
  let hash = 5381;
  for (const ch of String(name)) hash = ((hash << 5) + hash + ch.charCodeAt(0)) >>> 0;
  return hash % COLOURS;
}

/** The width of one bar in pixels: the whole song fits the width when it can (never narrower than `min`; then the timeline scrolls). */
export const pxPerBar = (totalBars, width, min = 22, max = 56) => Math.max(min, Math.min(max, Math.floor(width / Math.max(1, totalBars))));

/** The left edge and width of every section for a bar width. */
export function rects(entries, px) {
  let x = 0;
  return entries.map((e, index) => { const rect = { index, x, width: e.bars * px }; x += rect.width; return rect; });
}

/** The bars a section gets when its right edge is dragged `dx` pixels from where it started (whole bars, 1 to 999). */
export const barsFromDrag = (startBars, dx, px) => Math.max(1, Math.min(999, Math.round(startBars + dx / px)));

/** Where a dragged section lands: the index it has in the list after it is taken out, for a pointer at `x` (the other sections keep their places while it moves). */
export function dropIndex(entries, from, x, px) {
  const others = entries.filter((_, i) => i !== from);
  let at = 0, index = 0;
  for (const e of others) {
    const middle = at + (e.bars * px) / 2;
    if (x > middle) index++;
    at += e.bars * px;
  }
  return index;
}

/** The x of the line that shows where a dropped section would land (the left edge of the section after it, or the end). */
export function dropLineX(entries, from, to, px) {
  const others = entries.filter((_, i) => i !== from);
  let x = 0;
  for (let i = 0; i < Math.min(to, others.length); i++) x += others[i].bars * px;
  return x;
}

/** A copy of the section placed right after it. */
export function duplicateSection(entries, index) {
  if (index < 0 || index >= entries.length) return entries;
  const list = [...entries];
  list.splice(index + 1, 0, { ...entries[index] });
  return list;
}

/** The bar numbers written on the ruler: every `step` bars so that two numbers are never closer than `gap` pixels. Returns [{ bar, x }] (bar 1 is the first). */
export function rulerMarks(totalBars, px, gap = 28) {
  const step = [1, 2, 4, 8, 16, 32, 64].find((s) => s * px >= gap) ?? 128;
  const marks = [];
  for (let bar = 1; bar <= totalBars; bar += step) marks.push({ bar, x: (bar - 1) * px });
  return marks;
}

/** The text of the hover tip of a section: its name, the bars it covers and how many. */
export function tipOf(range) {
  const last = range.start + range.bars - 1;
  return `${range.name === 'pattern' ? 'main' : range.name}: ${range.bars === 1 ? `bar ${range.start}` : `bars ${range.start} to ${last} (${range.bars} bars)`}`;
}
