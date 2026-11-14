// Which document an edit belongs to: the song, or the module that is open inside it.
// The identity is captured when the edit is asked for and checked when it runs, so a tab switch never moves it. @ample:edit-target

/** The document the editor shows now: the open module when the person is inside one, otherwise the song. */
export function documentTarget(state) {
  const inModule = Array.isArray(state.moduleStack) && state.moduleStack.length > 0 && !!state.modulePath;
  if (inModule) return { kind: 'module', path: state.modulePath, songPath: state.path || '' };
  return { kind: 'song', path: state.path || '', songPath: state.path || '' };
}

/** True when both targets are the same document (same kind, same file, same song around it). */
export function sameTarget(a, b) {
  if (!a || !b) return false;
  return a.kind === b.kind && a.path === b.path && a.songPath === b.songPath && a.path !== '';
}

/** Text for the person when an edit is dropped because the document it was made in is no longer the open one. */
export function staleTargetMessage(target) {
  const what = target?.kind === 'module' ? 'module' : 'song';
  return `The ${what} ${target?.path || ''} is no longer open, so the change made in it was not applied.`;
}

/** A stable key for a window or an edit that belongs to one block of one document. */
export function blockTargetKey(target, block) {
  return JSON.stringify([target.kind, target.songPath, target.path, block]);
}
