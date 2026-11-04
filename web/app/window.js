// A window in the look of the AMPLE Jam window (the metal frame and title bar of skin.css): dragged by its title, one or several at a time, optionally modal, and a close that asks what to do
// with changes that are not saved (Save, Don't save, Cancel), like a desktop window. @ample:window
import { el } from './ui.js';

const spots = new Map();
const sizes = new Map();   // the size a resizable window was last given, by its id   // the last place of a window by its id, for this page
let top = 40;

/**
 * options: id (remembers the place), title, content (a node), modal (false), width and height in px, buttons [{ label, value, primary, onclick }] for the footer (a click closes the window with its value
 * unless onclick returns false), dirty (a flag or a function: are there changes that are not saved), save (async function for the Save of the close question; false keeps the window open), saveLabel, question ({ title, text, discard }: the words of the close question), onClose(reason).
 * Returns { element, body, footer, closed (a promise of the reason), close(reason), attemptClose(), setTitle(text), setDirty(flag) }.
 * The reason is the value of the footer button, 'saved', 'discarded' or 'closed'.
 */
export function openWindow(options) {
  const { id = '', modal = false, width = 640, height = 440 } = options;
  let dirtyFlag = typeof options.dirty === 'boolean' ? options.dirty : false;
  const isDirty = () => (typeof options.dirty === 'function' ? !!options.dirty() : dirtyFlag);
  let settle;
  const closed = new Promise((resolve) => { settle = resolve; });

  const dialog = el('dialog', { className: `ample-win metal-window${modal ? ' is-modal' : ''}`, 'aria-label': options.title || 'Window' });
  const titleEl = el('span', { className: 'aw-title', textContent: options.title || '' });
  const closeButton = el('button', { type: 'button', className: 'aw-close', textContent: '✕', title: 'Close', 'aria-label': 'Close' });
  const bar = el('div', { className: 'aw-bar metal-title' }, [titleEl, closeButton]);
  const body = el('div', { className: 'aw-body' }, options.content ? [options.content] : []);
  const footer = options.buttons?.length ? el('div', { className: 'aw-foot' }) : null;
  const sheet = el('div', { className: 'aw-sheet', hidden: true });
  dialog.append(bar, body, ...(footer ? [footer] : []), sheet);

  let finished = false;
  const api = {
    element: dialog, body, footer, closed,
    setTitle(text) { titleEl.textContent = text; dialog.setAttribute('aria-label', text); },
    setDirty(flag) { dirtyFlag = !!flag; },
    close(reason = 'closed') {
      if (finished) return;
      finished = true;
      if (modal || dialog.open) dialog.close();
      const spot = dialog.getBoundingClientRect();
      if (id) spots.set(id, { left: spot.left, top: spot.top });
      dialog.remove();
      try { options.onClose?.(reason); } finally { settle(reason); }
    },
    attemptClose() {
      if (finished) return;
      if (!isDirty()) { api.close('closed'); return; }
      askSave();
    },
  };

  for (const b of options.buttons || []) {
    const button = el('button', { type: 'button', className: b.primary ? 'primary' : '', textContent: b.label });
    button.onclick = async () => { if ((await b.onclick?.(api)) === false) return; api.close(b.value ?? b.label); };
    footer.append(button);
  }

  // the close question: Save, Don't save, Cancel (Enter saves, Esc cancels)
  function askSave() {
    const save = el('button', { type: 'button', className: 'primary', textContent: options.saveLabel || 'Save' });
    const discard = el('button', { type: 'button', textContent: options.question?.discard || "Don't save" });
    const cancel = el('button', { type: 'button', textContent: 'Cancel' });
    const hide = () => { sheet.hidden = true; sheet.replaceChildren(); dialog.querySelector('.aw-body')?.focus?.(); };
    save.onclick = async () => { save.disabled = true; const ok = options.save ? await options.save(api) : true; save.disabled = false; if (ok === false) { hide(); return; } api.close('saved'); };
    discard.onclick = () => api.close('discarded');
    cancel.onclick = hide;
    sheet.replaceChildren(el('div', { className: 'aw-card', role: 'alertdialog', 'aria-label': 'Unsaved changes' }, [
      el('b', { textContent: options.question?.title || `Save changes to ${titleEl.textContent || 'this window'}?` }),
      el('p', { textContent: options.question?.text || 'The changes are not saved yet.' }),
      el('div', { className: 'aw-card-buttons' }, [cancel, discard, save]),
    ]));
    sheet.hidden = false;
    sheet.onkeydown = (event) => { if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); hide(); } };
    save.focus();
  }

  closeButton.onclick = () => api.attemptClose();
  dialog.addEventListener('cancel', (event) => { event.preventDefault(); if (!sheet.hidden) { sheet.hidden = true; return; } api.attemptClose(); });   // Esc
  // a click beside a modal window is a try to close it
  if (modal) dialog.addEventListener('mousedown', (event) => { if (event.target === dialog) api.attemptClose(); });
  dialog.addEventListener('pointerdown', () => { dialog.style.zIndex = String(++top); }, true);

  document.body.append(dialog);
  if (modal) dialog.showModal(); else dialog.show();
  place(dialog, id, sizes.get(id)?.width ?? width, sizes.get(id)?.height ?? height);
  drag(dialog, bar);
  if (options.resizable) grow(dialog, id);
  (footer?.querySelector('button.primary') || body.querySelector('input,select,textarea,button') || closeButton).focus();
  return api;
}

/** A grip in the bottom right corner makes the window bigger or smaller (not below its minimum, not beyond the screen); the size is kept for the next time. */
/** A resize grip in the lower right corner of a dialog (the one of Jam and the other windows); the size is remembered by id for this page. */
export function resizeGrip(dialog, id) { grow(dialog, id); }

/** The size a resizable window was last given (null when it was never resized). */
export const rememberedSize = (id) => sizes.get(id) || null;

function grow(dialog, id) {
  const grip = el('div', { className: 'aw-grip', title: 'Drag to resize', 'aria-hidden': 'true' });
  grip.style.touchAction = 'none';
  dialog.append(grip);
  grip.addEventListener('pointerdown', (event) => {
    event.preventDefault();
    grip.setPointerCapture(event.pointerId);
    const start = { x: event.clientX, y: event.clientY, w: dialog.offsetWidth, h: dialog.offsetHeight };
    const move = (e) => {
      const w = Math.max(320, Math.min(start.w + e.clientX - start.x, innerWidth - dialog.offsetLeft - 8));
      const h = Math.max(180, Math.min(start.h + e.clientY - start.y, innerHeight - dialog.offsetTop - 8));
      dialog.style.width = w + 'px';
      dialog.style.height = h + 'px';
      if (id) sizes.set(id, { width: w, height: h });
    };
    const end = () => { grip.removeEventListener('pointermove', move); grip.removeEventListener('pointerup', end); grip.removeEventListener('pointercancel', end); };
    grip.addEventListener('pointermove', move);
    grip.addEventListener('pointerup', end);
    grip.addEventListener('pointercancel', end);
  });
}

/** The first place: where this window was last, else the middle (a modal one) or a little aside from the last window (the others). */
function place(dialog, id, width, height) {
  const vw = innerWidth, vh = innerHeight;
  const w = Math.min(width, vw - 16), h = Math.min(height, vh - 16);
  dialog.style.width = `${w}px`;
  dialog.style.height = `${h}px`;
  const remembered = id ? spots.get(id) : null;
  const others = document.querySelectorAll('dialog.ample-win[open]').length - 1;
  const left = remembered?.left ?? Math.max(8, (vw - w) / 2 + others * 28);
  const topPx = remembered?.top ?? Math.max(8, (vh - h) / 3 + others * 28);
  dialog.style.left = `${Math.min(Math.max(8, left), Math.max(8, vw - w - 8))}px`;
  dialog.style.top = `${Math.min(Math.max(8, topPx), Math.max(8, vh - h - 8))}px`;
  dialog.style.zIndex = String(++top);
}

/** The title bar moves the window; the window stays inside the screen with its title bar reachable. A phone shows it full screen (css), so no drag there. */
function drag(dialog, bar) {
  bar.addEventListener('pointerdown', (event) => {
    if (event.target.closest('button') || matchMedia('(max-width: 700px)').matches) return;
    bar.setPointerCapture(event.pointerId);
    const start = { x: event.clientX, y: event.clientY, left: dialog.offsetLeft, top: dialog.offsetTop };
    const move = (e) => {
      const w = dialog.offsetWidth;
      dialog.style.left = `${Math.min(Math.max(start.left + e.clientX - start.x, 80 - w), innerWidth - 80)}px`;
      dialog.style.top = `${Math.min(Math.max(start.top + e.clientY - start.y, 0), innerHeight - 40)}px`;
    };
    const end = () => { bar.removeEventListener('pointermove', move); bar.removeEventListener('pointerup', end); bar.removeEventListener('pointercancel', end); };
    bar.addEventListener('pointermove', move);
    bar.addEventListener('pointerup', end);
    bar.addEventListener('pointercancel', end);
  });
}
