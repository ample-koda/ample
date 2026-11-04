// What Codex looks at while it answers Koda: the blocks of the song that are named in the new lines of the task's log (tools it called, commands it ran) and in its latest thought. Only block names
// are taken out; nothing else of the log is kept or shown. Pure. @ample:koda

const escape = (s) => String(s).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** The block names (from ids) that a piece of text names as a word. */
export function namedBlocks(text, ids) {
  const t = String(text ?? '');
  return ids.filter((id) => id && new RegExp(`(?<![\\w-])${escape(id)}(?![\\w-])`).test(t));
}

/** From the log of a task: the names of blocks in the entries after seq (tool and command entries), in order and without repeats, and the newest seq. */
export function glanced(log, ids, afterSeq = 0, thought = '') {
  const found = [];
  let seq = afterSeq;
  for (const entry of log ?? []) {
    if ((entry.seq ?? 0) <= afterSeq) continue;
    seq = Math.max(seq, entry.seq ?? 0);
    if (entry.kind !== 'tool' && entry.kind !== 'command') continue;
    const text = `${entry.text ?? ''} ${entry.details ? JSON.stringify(entry.details.arguments ?? {}).slice(0, 1500) : ''}`;
    for (const id of namedBlocks(text, ids)) if (!found.includes(id)) found.push(id);
  }
  for (const id of namedBlocks(thought, ids)) if (!found.includes(id)) found.push(id);
  return { blocks: found, seq };
}
