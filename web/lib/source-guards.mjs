// Small decisions of the Source editor that must not depend on timing or on the DOM, so they are tested in node.

export const FORMAT_STALE_MESSAGE = 'The text changed while it was being formatted, so the formatting was not applied. Press Format again.';

/**
 * A formatting answer belongs to the text that was sent. It may only be applied when the editor still shows exactly that text;
 * otherwise it would replace what was typed after the request.
 */
export const formatStillApplies = (sentText, currentText) => typeof sentText === 'string' && sentText === currentText;

/**
 * What Save all does when the Source editor holds typed text.
 * - typed: undefined/null = the editor had nothing typed; a string = the path whose typed text was applied; false = it could not be applied
 * - dirty: the paths with unsaved changes once the typed text was applied
 * @returns {{ action: 'nothing' | 'stop' | 'saved-typed-only' | 'ask', message?: string }}
 */
export function planSaveAll({ typed = null, dirty = [] } = {}) {
  if (typed === false) {
    return { action: 'stop', message: 'Nothing was saved: the text typed in the Source editor could not be applied. Resolve that first.' };
  }
  if (!dirty.length && typed) return { action: 'saved-typed-only', message: `Saved ${typed.split('/').pop()}.` };
  if (!dirty.length) return { action: 'nothing', message: 'Nothing to save.' };
  return { action: 'ask' };
}
