// What the history views draw, derived from GET /api/history/graph. Pure, no DOM. @ample:history @ample:branches

/** Orders lines: studio, garage, then the others by name. */
const order = (a, b) => (a.name === 'studio' ? -1 : b.name === 'studio' ? 1 : a.name === 'garage' ? -1 : b.name === 'garage' ? 1 : a.name.localeCompare(b.name));

export function histModel(h) {
  if (!h) return null;
  const byId = new Map(h.nodes.map((n) => [n.nodeId, n]));
  const lines = [...h.lines].sort(order);
  const lineOf = new Map();
  for (const l of lines) {
    let id = l.head;
    while (id && !lineOf.has(id)) {
      lineOf.set(id, l.name);
      const n = byId.get(id);
      id = n ? n.parentId : null;
    }
  }
  const tagsAt = new Map();
  h.tags.forEach((t) => tagsAt.set(t.nodeId, [...(tagsAt.get(t.nodeId) || []), t.name]));
  const headOf = (name) => (h.lines.find((l) => l.name === name) || {}).head;
  const archived = new Set(h.lines.filter((l) => l.archived).map((l) => l.name));
  return { h, byId, lines, lineOf, tagsAt, archived, hereId: headOf(h.current), studioId: headOf('studio'), headOf };
}

/** The revisions to draw: everything in advanced view, otherwise the recent ones and what matters. */
export function visibleNodes(m, advanced) {
  const nodes = m.h.nodes.filter((n) => advanced || !m.archived.has(m.lineOf.get(n.nodeId)));
  if (advanced) return nodes;
  const tips = new Set(m.lines.filter((l) => !l.archived).map((l) => l.head));
  return nodes.filter((n, i) => i >= nodes.length - 14 || tips.has(n.nodeId) || n.nodeId === m.studioId || n.nodeId === m.hereId || m.tagsAt.has(n.nodeId) || n.parentId === null);
}

/** The line a line was forked from (the line of the parent of its first revision), or null. */
export function lineParent(m, name) {
  const mine = m.h.nodes.filter((n) => m.lineOf.get(n.nodeId) === name);
  const first = mine[0];
  if (!first) {
    // A line that has no revision of its own yet starts at a revision that belongs to another line.
    const head = m.h.lines.find((l) => l.name === name);
    const owner = head ? m.lineOf.get(head.head) : null;
    return owner && owner !== name ? owner : null;
  }
  if (!first.parentId) return null;
  const pl = m.lineOf.get(first.parentId);
  return pl && pl !== name ? pl : null;
}

/** From the root line down to the given line. */
export function lineChain(m, name) {
  const chain = [name];
  let p = lineParent(m, name);
  while (p && !chain.includes(p)) { chain.unshift(p); p = lineParent(m, p); }
  return chain;
}

/** Lines forked directly from the given line. */
export function lineChildren(m, name, withArchived) {
  return m.lines.filter((l) => (withArchived || !l.archived) && lineParent(m, l.name) === name).map((l) => l.name);
}

export const shortId = (id) => (id ? id.replace(/^node:/, '').slice(0, 8) : '-');
export const fmtTime = (t) => (t ? String(t).slice(0, 16).replace('T', ' ') : '-');

/** Recent compare choices plus any older revision supplied by the action that opened Compare. */
export function compareRefs(m, refA, refB) {
  const refs = [...m.lines.map((l) => [l.name, `line ${l.name}`]), ...m.h.tags.map((t) => [t.name, `tag ${t.name}`]), ...m.h.nodes.slice(-20).map((n) => [n.nodeId, `${shortId(n.nodeId)} ${n.message}`])];
  for (const ref of [refA, refB]) if (ref && !refs.some(([value]) => value === ref)) {
    const node = m.byId.get(ref);
    refs.push([ref, node ? `${shortId(node.nodeId)} ${node.message}` : ref]);
  }
  return refs;
}
