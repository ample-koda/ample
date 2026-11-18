// Live Preview: the users of a source (step 5 of plan/live-preview/note-origin.md). Several chains that read one melody each get a colour, a number and a shape,
// so that the notes of the source that sound at the same time can be told apart. Pure; the page draws the marks and the "used by" list.
import { blockSpan, notesLines } from './listen-highlight.mjs';

export const MAX_USERS = 4, MAX_CHAINS = 12;   // MAX_CHAINS: the chain clips whose origins are asked for in one build (the servers answer no more)   // more colours cannot be told apart: the rest share a neutral tint and are counted

const hashOf = (name) => { let h = 2166136261; for (const c of name) { h ^= c.codePointAt(0); h = Math.imul(h, 16777619) >>> 0; } return h; };

/**
 * The slot (0..3) of every user, or null for the neutral ones. A slot depends on the names only (not on the order they were found or on what plays), so a
 * user keeps its colour while the others come and go; it can only move when a user with a lower hash is added or removed.
 * @param {string[]} users
 * @returns {Map<string, number|null>}
 */
export function slotsFor(users) {
  const sorted = [...new Set(users)].sort((a, b) => hashOf(a) - hashOf(b) || (a < b ? -1 : 1));
  return new Map(sorted.map((name, i) => [name, i < MAX_USERS ? i : null]));
}

export const slotLabel = (slot) => (slot === null || slot === undefined ? '+' : String(slot + 1));
export const slotClass = (slot) => (slot === null || slot === undefined ? 'lp-user-n' : `lp-user-${slot + 1}`);

/** The marks of a source with their colour: a mark that carries `user` gets the class, the number and the shape of that user's slot; other marks stay as they are. */
export function colourMarks(marks, slots) {
  return marks.map((m) => {
    if (!m.user) return m;
    const slot = slots.get(m.user) ?? null;
    return { ...m, cls: `${m.cls} ${slotClass(slot)}`, attrs: { 'data-lp-user': slotLabel(slot), 'data-lp-name': m.user } };
  });
}

/** The clips among `names` whose notes are a chain (a pipeline, a call or a component, not literal notes), at most MAX_CHAINS: the clips to ask the origins of. One rule for Studio and Remix. */
export function chainClips(text, names) {
  const isChain = (id) => { const span = blockSpan(text, id); return span?.type === 'midi-clip' && notesLines(text, span).some((l) => !l.literal); };
  return names.filter(isChain).slice(0, MAX_CHAINS);
}

/** What a chain does to its source, as written: the part of the notes line after the first reference ("notes theme.notes |> transpose(+12)" gives "+12"-ish text). */
export function chainText(text, user) {
  const span = blockSpan(text, user);
  if (!span) return '';
  const line = notesLines(text, span).find((l) => !l.literal);
  if (!line) return '';
  const value = text.slice(line.from, line.to).replace(/^notes\s*/, '');
  const at = value.indexOf('|>');
  return at < 0 ? '' : value.slice(at + 2).trim().replace(/\s*\|>\s*/g, ' > ');
}

/**
 * The "used by" list: for every source that is read by chains, its users in slot order with their text, and how many more there are than colours.
 * `origins` is { chainClip: [{ source, ... }] } as the build brought it.
 * @returns {{ source: string, users: { user: string, slot: number|null, label: string, chain: string }[], extra: number }[]}
 */
export function usersBySource(text, origins) {
  const bySource = new Map();
  for (const [user, list] of Object.entries(origins ?? {})) {
    for (const o of list ?? []) {
      if (o.source == null) continue;
      if (!bySource.has(o.source)) bySource.set(o.source, new Set());
      bySource.get(o.source).add(user);
    }
  }
  const slots = slotsFor([...bySource.values()].flatMap((s) => [...s]));
  return [...bySource].map(([source, users]) => {
    const ordered = [...users].sort((a, b) => (slots.get(a) ?? 99) - (slots.get(b) ?? 99) || (a < b ? -1 : 1));
    const shown = ordered.map((user) => ({ user, slot: slots.get(user) ?? null, label: slotLabel(slots.get(user) ?? null), chain: chainText(text, user) }));
    return { source, users: shown, extra: shown.filter((u) => u.slot === null).length };
  }).sort((a, b) => (a.source < b.source ? -1 : 1));
}
