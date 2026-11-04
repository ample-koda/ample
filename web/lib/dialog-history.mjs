// Phone dialogs and the system Back button (mobile-ui MUI05): every dialog that opens full screen gets one history entry,
// so Back (Android gesture, browser button) closes the top dialog instead of leaving the app. Pure bookkeeping, no DOM.
// opened(d) -> true when the caller must pushState; closed(d) -> true when the caller must history.back();
// popstate() -> the dialog to close for a real Back press (or null when the event is one we caused ourselves).
export function createDialogHistory() {
  const stack = [];
  let ignore = 0;
  return {
    opened(d) {
      if (stack.includes(d)) return false;
      stack.push(d);
      return true;
    },
    closed(d) {
      const at = stack.indexOf(d);
      if (at < 0) return false;
      stack.splice(at, 1);
      ignore++;
      return true;
    },
    popstate() {
      if (ignore > 0) { ignore--; return null; }
      return stack.pop() ?? null;
    },
    get depth() { return stack.length; },
  };
}

/** What a Back button does to a dialog: the same as Escape (a cancel event the dialog may refuse), then close. */
export function backAction(dialog) {
  const event = new Event('cancel', { cancelable: true });
  dialog.dispatchEvent(event);
  if (!event.defaultPrevented && dialog.open) dialog.close();
  return !dialog.open;
}
