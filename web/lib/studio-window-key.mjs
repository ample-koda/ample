// Which document an Advanced window belongs to. Pure (no DOM, no store), so it is tested in node.
// The windows used to be keyed by the block id alone, so the block `fx` of one song raised and edited
// the window of the block `fx` of another song.

/** The key of a window: the song, the module that is open (or none) and the block. */
export const windowKey = (songPath, modulePath, block) => JSON.stringify([songPath || '', modulePath || '', String(block ?? '')]);

/** Whether a window key belongs to the song and module that are open now. A key that cannot be read belongs to nothing. */
export function keyBelongsTo(key, songPath, modulePath) {
  try {
    const [song, module] = JSON.parse(key);
    return song === (songPath || '') && module === (modulePath || '');
  } catch { return false; }
}

/** The keys of the windows that do not belong to the open song and module any more: they are to be closed. */
export const foreignKeys = (keys, songPath, modulePath) => [...keys].filter((key) => !keyBelongsTo(key, songPath, modulePath));
