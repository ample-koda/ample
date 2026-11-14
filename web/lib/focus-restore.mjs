/**
 * Keeps the keyboard on "the same control" when a view is redrawn. A control is known by what it is (tag, type, id, label, title, data-*) plus its
 * place among the controls that look the same, not by its index among all controls, so a control that appears or disappears before it does not move the focus
 * to a neighbour.
 */
export const FOCUSABLE = 'button,input,select,textarea,[tabindex]';

const signatureOf = (node) => JSON.stringify([
  String(node.tagName || '').toLowerCase(), node.type || '', node.id || '', node.getAttribute?.('aria-label') || '', node.getAttribute?.('title') || '', node.getAttribute?.('name') || '',
  Object.entries(node.dataset || {}).sort(([a], [b]) => (a < b ? -1 : 1)),
]);
const usable = (node) => !node.disabled && node.getAttribute?.('tabindex') !== '-1';

/** The identity of `active` inside `root`: { signature, nth } (nth counts the usable controls with the same signature before it), or null. */
export function focusKeyOf(root, active) {
  if (!active || !root.contains(active) || !usable(active)) return null;
  const signature = signatureOf(active);
  const same = [...root.querySelectorAll(FOCUSABLE)].filter((node) => usable(node) && signatureOf(node) === signature);
  const nth = same.indexOf(active);
  return nth < 0 ? null : { signature, nth };
}

/** The control a key points at in the redrawn `root`: the same place among the same kind; when there are fewer now, the last one of that kind. null when none is left. */
export function findByFocusKey(root, key) {
  if (!key) return null;
  const same = [...root.querySelectorAll(FOCUSABLE)].filter((node) => usable(node) && signatureOf(node) === key.signature);
  return same[Math.min(key.nth, same.length - 1)] ?? null;
}

/** The key of the control Tab (step 1) or Shift+Tab (step -1) would reach from `active`, so a redraw during the move can put the focus where the move was going. */
export function neighbourKey(root, active, step) {
  const list = [...root.querySelectorAll(FOCUSABLE)].filter((node) => usable(node) || node === active), at = list.indexOf(active);
  const target = at < 0 ? null : list.slice(at + step, at + step + 1).find(usable);
  return target ? focusKeyOf(root, target) : null;
}
