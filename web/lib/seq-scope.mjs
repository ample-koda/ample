// What the Sequencer transport tells the person: which sound is heard (a pattern, the sequencer, a range of bars), whether it repeats only for the audition or the
// sequence has a loop of its own, and which version of the edits reaches the speakers. Pure, so node --test checks it.

/**
 * The line next to Play: the source, the audition loop and the saved loop.
 * @param {{ mode: 'pattern' | 'song', pattern?: string | null, range?: { at: number, bars: number } | null, repeat: boolean, savedLoop?: { at: number, bars: number } | null }} o
 */
export function scopeText({ mode, pattern = null, range = null, repeat, savedLoop = null }) {
  const source = range
    ? `bars ${range.at} to ${range.at + range.bars - 1} of the sequencer`
    : mode === 'pattern' ? `the pattern ${pattern ?? ''}`.trim() : 'the whole sequencer (without the effects after it)';
  const loop = repeat ? 'repeats (only while you audition)' : 'plays once';
  const saved = savedLoop ? ` · saved loop of the sequence: bar ${savedLoop.at} for ${savedLoop.bars}` : '';
  return `Hears ${source} · ${loop}${saved}`;
}

/**
 * Which version is audible, as one short word group.
 * @param {{ playing: boolean, preparing: boolean, pending: boolean, stale: boolean }} s preparing: a build runs; pending: a new take waits for the next return of the loop; stale: edits were made that are not built yet
 */
export function versionState({ playing, preparing, pending, stale }) {
  if (preparing) return 'Preparing the new version';
  if (pending) return 'Playing the previous version: the new one comes in at the next return';
  if (stale) return playing ? 'Playing the previous version: your last edits are not built yet' : 'Edits not built yet';
  return playing ? 'Playing the latest version' : '';
}
