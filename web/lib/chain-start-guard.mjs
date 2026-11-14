// The decision not to try a song state again that already failed to start (kept free of the audio and DOM imports so it can be tested alone).

/** Whether the start of `target` for the song state `signature` already failed: the old chain plays on, no new try and no new notice until the song changes again. */
export const failedAlready = (failed, target, signature) => Boolean(failed) && failed.target === target && failed.signature === signature;
