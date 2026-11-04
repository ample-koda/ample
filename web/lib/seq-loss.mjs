// What a new version of a sequence takes away from the one that is in the editor: channels, patterns, clips and filled rows. Pure: no DOM. @ample:koda

/** { channels, patterns, rows, clips } of what the old sequence has and the new one has not (names of channels and patterns, the number of filled rows and of clips). */
export function sequenceLoss(before, after) {
  const names = (list) => new Set((list ?? []).map((x) => x.name));
  const gone = (a, b) => (a ?? []).filter((x) => !names(b).has(x.name)).map((x) => x.name);
  let rows = 0;
  for (const pattern of before.patterns ?? []) {
    const next = (after.patterns ?? []).find((p) => p.name === pattern.name);
    if (!next) continue;
    for (const [channel, text] of Object.entries(pattern.rows ?? {})) if (String(text).trim() && !String(next.rows?.[channel] ?? '').trim()) rows++;
  }
  return { channels: gone(before.channels, after.channels), patterns: gone(before.patterns, after.patterns), rows, clips: Math.max(0, (before.clips?.length ?? 0) - (after.clips?.length ?? 0)) };
}

export const lostAnything = (loss) => loss.channels.length > 0 || loss.patterns.length > 0 || loss.rows > 0 || loss.clips > 0;

/** The loss in words: "channels snare, hat; 2 filled rows". */
export const lossText = (loss) => [loss.channels.length ? `channels ${loss.channels.join(', ')}` : '', loss.patterns.length ? `patterns ${loss.patterns.join(', ')}` : '', loss.rows ? `${loss.rows} filled row${loss.rows === 1 ? '' : 's'}` : '', loss.clips ? `${loss.clips} clip${loss.clips === 1 ? '' : 's'} of the playlist` : ''].filter(Boolean).join('; ');

/** The new sequence with what it took away put back: channels and patterns that are gone, and the rows that were filled and are empty now (the playlist stays as the new one has it). A new copy. */
export function restoreLost(before, after) {
  const out = JSON.parse(JSON.stringify(after));
  const have = (list, name) => (list ?? []).some((x) => x.name === name);
  for (const channel of before.channels ?? []) if (!have(out.channels, channel.name)) out.channels.push(JSON.parse(JSON.stringify(channel)));
  for (const pattern of before.patterns ?? []) {
    const next = out.patterns.find((p) => p.name === pattern.name);
    if (!next) { out.patterns.push(JSON.parse(JSON.stringify(pattern))); continue; }
    for (const [channel, text] of Object.entries(pattern.rows ?? {})) {
      if (String(text).trim() && !String(next.rows?.[channel] ?? '').trim()) {
        next.rows = { ...(next.rows ?? {}), [channel]: text };
        if (pattern.rowNotes?.[channel] !== undefined) next.rowNotes = { ...(next.rowNotes ?? {}), [channel]: pattern.rowNotes[channel] };
      }
    }
  }
  return out;
}
