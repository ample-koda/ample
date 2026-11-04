// Quick search for the file switcher: a few letters of a path find it (plan project-panel PP06). Pure.

/**
 * How well a query matches a path: every letter of the query must appear in order (case does not matter); consecutive letters, a start of the
 * file name or of a word, and a short path score higher. -1 when it does not match.
 */
export function fuzzyScore(query, text) {
  const q = String(query || '').toLowerCase().replace(/\s+/g, '');
  if (!q) return 0;
  const t = String(text).toLowerCase();
  const nameStart = t.lastIndexOf('/') + 1;
  let ti = 0, score = 0, run = 0;
  for (let qi = 0; qi < q.length; qi++) {
    const at = t.indexOf(q[qi], ti);
    if (at < 0) return -1;
    run = at === ti && qi > 0 ? run + 1 : 0;
    score += 1 + run * 2;
    if (at === nameStart || at === 0 || /[/._ -]/.test(t[at - 1])) score += 3;
    if (at >= nameStart) score += 1;
    ti = at + 1;
  }
  return score * 10 - t.length * 0.1;
}

/** The paths that match, best first; with an empty query the recent files come first (in the order given), then the rest alphabetically. */
export function rankFiles(paths, query, recent = [], limit = 30) {
  const q = String(query || '').trim();
  if (!q) {
    const known = new Set(paths);
    const head = recent.filter((p) => known.has(p));
    const rest = paths.filter((p) => !head.includes(p)).sort((a, b) => a.localeCompare(b));
    return [...head, ...rest].slice(0, limit);
  }
  return paths
    .map((p) => ({ p, score: fuzzyScore(q, p) + (recent.includes(p) ? 5 : 0) }))
    .filter((x) => x.score >= 0 || fuzzyScore(q, x.p) >= 0)
    .sort((a, b) => b.score - a.score || a.p.localeCompare(b.p))
    .slice(0, limit)
    .map((x) => x.p);
}
