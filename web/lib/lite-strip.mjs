// Pure model of the neighbouring-clips strip of the Lite roll (plan MLS11, decision 16): which other clips sit near the open clip in the song, as
// small chips with a stable colour; the back stack for "open the neighbour in place" (Alt+Left returns). Read-mostly: nothing here edits a clip. No DOM.
// AMPLE draws one lane per block, so "on the same track" is read as "in time": every other placed midi-clip near the open one, whatever its lane (an unplaced clip,
// one with no part, has no position and is left out). @ample:pianoroll-lite

export const STRIP_WINDOW_BARS = 8;

/** A stable hue (0-359) for a block id, so a chip keeps its colour across redraws and sessions. */
export function hueOf(id) {
  let h = 0;
  for (const ch of String(id)) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return h % 360;
}

/**
 * Chips for the clip `currentId`: one per part of every other placed midi-clip whose bars fall within `windowBars` before the current clip's first
 * part or after its last, plus the current clip itself (flagged). `nodes` are { id, title, tpl, parts: [{ at, length }] } (at is a 1-based bar), `links` is accepted for
 * later use (clips feeding the same instrument) and ignored today. Sorted by start bar. Returns [] when the current clip is unknown.
 */
export function neighbourChips(nodes, _links, currentId, windowBars = STRIP_WINDOW_BARS) {
  const current = nodes.find((n) => n.id === currentId);
  if (!current) return [];
  const parts = (n) => (n.parts?.length ? n.parts : [{ at: 1, length: 1 }]);
  const mine = parts(current), from = Math.min(...mine.map((p) => p.at)), to = Math.max(...mine.map((p) => p.at + p.length - 1));
  const chips = [];
  for (const n of nodes) {
    if (n.tpl !== 'midi-clip') continue;
    const isCurrent = n.id === currentId;
    if (!isCurrent && !n.parts?.length) continue;
    for (const p of parts(n)) {
      const end = p.at + p.length - 1;
      if (!isCurrent && (end < from - windowBars || p.at > to + windowBars)) continue;
      chips.push({ id: n.id, title: n.title || n.id, hue: hueOf(n.id), at: p.at, length: p.length, current: isCurrent });
    }
  }
  return chips.sort((a, b) => a.at - b.at || Number(b.current) - Number(a.current) || a.id.localeCompare(b.id));
}

/** The text a chip shows and its accessible name. */
export const chipLabel = (chip) => `${chip.title} · bar ${chip.at}${chip.length > 1 ? `-${chip.at + chip.length - 1}` : ''}`;

/** A signature of the chips (changes when a neighbour is renamed or moved, so the strip redraws only then). */
export const chipsSignature = (chips) => chips.map((c) => `${c.id}|${c.title}|${c.at}|${c.length}|${c.current ? 1 : 0}`).join(';');

/** The back stack: push the clip we leave, pop to go back; never holds the same id twice in a row, capped at 20. */
export function pushBack(stack, id) { return stack[stack.length - 1] === id ? stack : [...stack, id].slice(-20); }
export function popBack(stack) { return stack.length ? { id: stack[stack.length - 1], stack: stack.slice(0, -1) } : { id: null, stack }; }
