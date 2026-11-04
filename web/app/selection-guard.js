// By default the text of labels, buttons and panels cannot be selected by accident (a drag or a double-click that selects half a heading is never what was meant).
// The text that is there to be read or copied stays selectable: fields, the source editor, logs, errors, code and the assistant's conversation (see body.no-select in app.css).
// The switch is in the help menu; switched off, the page behaves as a plain web page.
const KEY = 'ample.protectSelection';

export const selectionGuardOn = () => { try { return localStorage.getItem(KEY) !== '0'; } catch { return true; } };

export function setSelectionGuard(on) {
  document.body.classList.toggle('no-select', on);
  try { localStorage.setItem(KEY, on ? '1' : '0'); } catch { /* storage may be blocked */ }
  document.querySelectorAll('[data-select-guard],[data-select-guard-state]').forEach((item) => item.setAttribute('aria-checked', String(on)));
}

export function initSelectionGuard() {
  setSelectionGuard(selectionGuardOn());
  document.querySelectorAll('[data-select-guard]').forEach((item) => item.addEventListener('click', () => setSelectionGuard(!selectionGuardOn())));
}
