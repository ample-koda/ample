// Lyrics file format and checks. Pure, no DOM. @ample:lyrics-editor
//
// One line per row. A timed line is "[bar 9] words". A section tag is a line of its own, "[Verse]", with no bar: it is not sung, it tells ACE
// what comes next, and in the editor it is a heading row that sits before the timed line(s) it introduces.

import { barBeats } from '../lib/meter.mjs';
const TAG = /^\s*\[([^\]]+)\]\s*$/;

export const isTag = (r) => !!r.tag;

/** The tags offered in the editor. */
export const TAGS = ['[Intro]', '[Verse]', '[Pre-Chorus]', '[Chorus]', '[Bridge]', '[Instrumental]', '[Outro]'];

export function parseLyrics(text) {
  const rows = [], bad = [];
  text.split('\n').forEach((l, i) => {
    if (!l.trim()) return;
    const m = /^\[bar\s+([\d.]+)\]\s*(.*)$/.exec(l);
    if (m) rows.push({ bar: Number(m[1]), text: m[2] });
    else if (TAG.test(l) && !/^\s*\[bar\b/i.test(l)) rows.push({ tag: true, text: `[${TAG.exec(l)[1].trim()}]` });
    else bad.push(i + 1);
  });
  return { rows, bad };
}

export const lyricsText = (rows) => `${rows.map((r) => (r.tag ? r.text : `[bar ${r.bar}] ${r.text}`)).join('\n')}\n`;

/** The timed rows only, each with its index in the full list. */
export const timedRows = (rows) => rows.map((r, i) => ({ r, i })).filter((x) => !x.r.tag);

/** True when the timed rows are in time order (tags do not count). */
export const isSorted = (rows) => timedRows(rows).every((x, k, a) => !k || x.r.bar >= a[k - 1].r.bar);

/** Sorts the timed rows by bar; a tag stays in front of the timed line that follows it (a tag at the end stays at the end). */
export function sortByTime(rows) {
  const groups = [];
  let pending = [];
  rows.forEach((r) => {
    if (r.tag) pending.push(r);
    else { groups.push({ bar: r.bar, items: [...pending, r] }); pending = []; }
  });
  groups.sort((a, b) => a.bar - b.bar);
  return [...groups.flatMap((g) => g.items), ...pending];
}

export function lyricChecks(rows, node, tempoBpm) {
  const out = [];
  const seg = node && node.parts && node.parts[0];
  const start = seg ? seg.at : 1;
  const end = seg ? seg.at + seg.length : null;
  const first5 = node && node.params && node.params.first5s === 'check';
  const sec = (b) => ((b - 1) * barBeats() * 60) / tempoBpm;
  const timed = timedRows(rows);
  timed.forEach(({ r, i }, k) => {
    const prev = k ? timed[k - 1].r : null;
    const next = timed[k + 1] ? timed[k + 1].r : null;
    if (!r.text.trim()) out.push({ i, sev: 'warning', msg: `line ${i + 1} has no text` });
    if (prev && r.bar < prev.bar) out.push({ i, sev: 'error', msg: `line ${i + 1} (bar ${r.bar}) comes before the line above it (bar ${prev.bar})` });
    else if (prev && r.bar === prev.bar) out.push({ i, sev: 'info', msg: `lines ${timed[k - 1].i + 1} and ${i + 1} start on the same bar` });
    if (r.bar < start) out.push({ i, sev: 'warning', msg: `line ${i + 1} starts before the lyrics part (bar ${start})` });
    if (end != null && r.bar >= end) out.push({ i, sev: 'warning', msg: `line ${i + 1} starts after the lyrics part ends (bar ${end})` });
    if (r.text.length > 60) out.push({ i, sev: 'info', msg: `line ${i + 1} is long (${r.text.length} characters): hard to sing in one go` });
    const words = r.text.trim().split(/\s+/).filter(Boolean).length;
    if (next && next.bar > r.bar) {
      const dt = sec(next.bar) - sec(r.bar);
      if (words / dt > 4) out.push({ i, sev: 'warning', msg: `line ${i + 1}: ${words} words in ${dt.toFixed(1)} s is more than 4 words per second` });
    }
  });
  rows.forEach((r, i) => { if (r.tag && !r.text.replace(/[[\]\s]/g, '')) out.push({ i, sev: 'warning', msg: `line ${i + 1} is an empty tag` }); });
  if (first5 && timed.length) {
    const s = sec(timed[0].r.bar) - sec(start);
    out.push(s > 5 ? { i: timed[0].i, sev: 'warning', msg: `first5s check: the first line starts ${s.toFixed(1)} s into the part (limit 5 s)` } : { i: timed[0].i, sev: 'info', msg: `first5s check passed (${s.toFixed(1)} s)` });
  }
  if (!timed.length) out.push({ i: -1, sev: 'warning', msg: 'there are no lyric lines yet' });
  return out;
}
