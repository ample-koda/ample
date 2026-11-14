// What closing the Jam window does with a take that is still being recorded. Pure (no DOM, no store), so it is tested in node.
// Closing the window used to stop the playback like a press on Stop, which finished the recording and added the notes to the clip,
// although the person only closed the window.

/**
 * 'discard' when the window is closed while a recording is on and something was played; otherwise 'none'.
 * An unfinished take is never added to the clip by closing the window: the person stops the take to keep it.
 */
export function onCloseTake({ recording = false, played = 0 } = {}) {
  return recording && played > 0 ? 'discard' : 'none';
}

/** The number of notes of a take that is not finished: the notes played and the ones still held. */
export const playedCount = ({ notes = [], open = new Map() } = {}) => notes.length + (open?.size ?? 0);

/** What the person is told. */
export const discardedMessage = (count) => `The window was closed during the recording, so the ${count} unfinished note${count === 1 ? ' was' : 's were'} not added to the clip.`;
