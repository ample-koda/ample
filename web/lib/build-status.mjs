// What the main build progress says to assistive technology: a value for the progress bar, and a short sentence when the state changes
// (starting, finishing, failing), not on every tick of the progress. Pure, so node --test checks it.

/** 'working' while a build or a preview runs, 'failed' when it stopped with failed blocks, otherwise 'ready'. */
export const buildState = ({ running = false, previewRunning = false, failed = 0 } = {}) => (running || previewRunning ? 'working' : failed > 0 ? 'failed' : 'ready');

/** The percentage (0 to 100) for a done count out of a total; 0 when there is nothing to count. */
export const percentOf = (done, total) => (total > 0 ? Math.max(0, Math.min(100, Math.round((done / total) * 100))) : 0);

/**
 * The sentence for a change of state, or '' when nothing is to be said (the first state seen, the same state again, or a quiet end).
 * @param {'working' | 'failed' | 'ready' | null} before the state last announced (null: none yet)
 */
export function announcement(before, now, failed = 0) {
  if (before === null || before === now) return '';
  if (now === 'working') return 'Building';
  if (now === 'failed') return failed === 1 ? 'Build finished: 1 block failed' : `Build finished: ${failed} blocks failed`;
  if (now === 'ready' && before === 'working') return 'Build finished';
  return '';
}
