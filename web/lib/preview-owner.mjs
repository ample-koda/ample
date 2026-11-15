// Which window owns the preview that is playing. Several advanced windows can be open at once, but the player is shared: only the window that started the
// preview may update, seek, loop or stop it, and a window that starts one takes the preview over (issue 161). Pure, so node --test checks it.

export function previewOwnership() {
  let owner = null;
  return {
    /** Makes `next` the owner and gives the window that had the preview before (or null when there was none, or it was `next`). */
    claim(next) {
      const previous = owner;
      owner = next;
      return previous !== null && previous !== next ? previous : null;
    },
    /** Whether `window` owns the preview. */
    owns: (window) => owner !== null && owner === window,
    /** Gives the preview up; only the owner can. */
    release(window) {
      if (owner === window) owner = null;
    },
  };
}
