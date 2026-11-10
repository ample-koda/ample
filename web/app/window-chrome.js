// Window management for a floating panel (AMPLE Jam): a collapse button and a double-click on the title bar roll the panel up to its title bar,
// a maximize button makes it a locked-in full-screen window (a backdrop behind it, Esc or the restore button leaves) and the previous place and size come back.
// The state lives in panel.dataset.window ('' | 'collapsed' | 'max'); the CSS in pianoroll-lite.css does the layout, so the saved left/top/width/height are never touched.
import { el } from './ui.js';

/** The small AMPLE Jam icon (the AMPLE logo with a J where the A is, and its eighth note) that stands in front of the AMPLE Jam name. */
export const jamIcon = () => el('img', { className: 'jam-icon', src: 'assets/icon/jam-small.svg', alt: '', width: 16, height: 16, decoding: 'async' });
/** Gives a button the icon and the name: <img> AMPLE Jam. */
export function withJamIcon(button) { button.replaceChildren(jamIcon(), ' AMPLE Jam'); return button; }

// the windows that are open, back to front: a click, a drag or the focus inside one brings it to the front
const stack = [];
function raise(panel) {
  const at = stack.indexOf(panel);
  if (at >= 0) stack.splice(at, 1);
  stack.push(panel);
  stack.forEach((window, index) => { window.style.zIndex = String(30 + index); });
  setActive(panel);
}

// the window that was used last is the active one; a click anywhere else on the page makes every window look inactive (a greyer title bar), like in Windows
function setActive(active) { for (const window of stack) window.toggleAttribute('data-inactive', window !== active); }
let outsideWired = false;
function wireOutside() {
  if (outsideWired) return;
  outsideWired = true;
  document.addEventListener('pointerdown', (event) => { if (!stack.some((window) => window.contains(event.target))) setActive(null); }, true);
}

export function addWindowChrome(panel, title, before, shift = () => {}) {
  wireOutside();
  raise(panel);
  // a bigger handle in the corner than the browser's own (which is too small to catch)
  const grip = el('div', { className: 'mls-grip', role: 'separator', 'aria-label': 'Resize the window', title: 'Drag to resize' });
  panel.append(grip);
  grip.addEventListener('pointerdown', (event) => {
    if (panel.dataset.window) return;
    event.preventDefault(); grip.setPointerCapture(event.pointerId);
    const r = panel.getBoundingClientRect(), sx = event.clientX, sy = event.clientY;
    const move = (e) => { panel.style.width = `${Math.round(Math.max(520, Math.min(window.innerWidth - r.left - 4, r.width + e.clientX - sx)))}px`; panel.style.height = `${Math.round(Math.max(340, Math.min(window.innerHeight - r.top - 4, r.height + e.clientY - sy)))}px`; };
    const up = () => { grip.removeEventListener('pointermove', move); grip.removeEventListener('pointerup', up); grip.removeEventListener('pointercancel', up); };
    grip.addEventListener('pointermove', move); grip.addEventListener('pointerup', up); grip.addEventListener('pointercancel', up);
  });
  panel.addEventListener('pointerdown', () => raise(panel), true);
  panel.addEventListener('focusin', () => raise(panel));
  let backdrop = null;
  const collapse = el('button', { type: 'button', className: 'mls-wbtn mls-collapse', textContent: '–', title: 'Collapse to the title bar (double-click the title bar does the same)', 'aria-label': 'Collapse', 'aria-expanded': 'true' });
  const maximize = el('button', { type: 'button', className: 'mls-wbtn mls-maximize', textContent: '▢', title: 'Maximize: a full-screen window you leave with Esc or Restore', 'aria-label': 'Maximize' });
  title.insertBefore(collapse, before);
  title.insertBefore(maximize, before);
  const mode = () => panel.dataset.window || '';
  let memo = { rel: 40 };
  function setCollapsed(on, anchor) {
    if (mode() === 'max') return;
    const before = panel.getBoundingClientRect();
    if (on) memo = { rel: anchor ? anchor.x - before.left : 40, left: before.left, collapsedLeft: before.left };
    if (on) panel.dataset.window = 'collapsed'; else delete panel.dataset.window;
    collapse.textContent = on ? '▁' : '–';
    if (anchor) {
      // the window changes width: slide it sideways so the mouse stays where it was and the same click (or double-click) works again without moving it.
      // Every step is "where it should be minus where it is", so it can run again once the new layout has settled (a frame later) and correct what is left.
      const restore = !on && Math.abs(before.left - memo.collapsedLeft) < 2;   // not moved since it was rolled up: back to exactly where it was
      const place = () => {
        const r = panel.getBoundingClientRect();
        if (restore) shift(memo.left - r.left);
        else if (anchor.button) { const b = collapse.getBoundingClientRect(); shift(anchor.x - (b.left + b.width / 2)); }
        else {
          const stop = collapse.getBoundingClientRect().left - r.left - 8;
          shift(anchor.x - (r.left + (on ? Math.max(12, Math.min(memo.rel, stop)) : memo.rel)));
        }
      };
      place();
      requestAnimationFrame(() => { place(); if (on) memo.collapsedLeft = panel.getBoundingClientRect().left; });
      if (on) memo.collapsedLeft = panel.getBoundingClientRect().left;
    }
    collapse.setAttribute('aria-expanded', String(!on));
    collapse.setAttribute('aria-label', on ? 'Expand' : 'Collapse');
    collapse.title = on ? 'Expand the window again (double-click the title bar does the same)' : 'Collapse to the title bar (double-click the title bar does the same)';
  }
  function setMaximized(on) {
    if (on === (mode() === 'max')) return;
    if (on) {
      delete panel.dataset.window; panel.dataset.window = 'max';
      backdrop = el('div', { className: 'mls-backdrop' });
      document.body.insertBefore(backdrop, panel);
      document.addEventListener('keydown', escape, true);
    } else {
      delete panel.dataset.window;
      backdrop?.remove(); backdrop = null;
      document.removeEventListener('keydown', escape, true);
    }
    maximize.textContent = on ? '❐' : '▢';
    maximize.setAttribute('aria-label', on ? 'Restore' : 'Maximize');
    maximize.title = on ? 'Back to the previous size and place (Esc does the same)' : 'Maximize: a full-screen window you leave with Esc or Restore';
    collapse.setAttribute('aria-expanded', 'true');
  }
  function escape(event) {
    if (event.key !== 'Escape' || mode() !== 'max') return;
    event.preventDefault(); event.stopPropagation();
    setMaximized(false);
  }
  collapse.addEventListener('click', (event) => setCollapsed(mode() !== 'collapsed', event.detail === 0 ? null : { x: event.clientX, button: true }));
  maximize.addEventListener('click', () => setMaximized(mode() !== 'max'));
  title.addEventListener('dblclick', (event) => { if (event.target.closest('button')) return; if (mode() === 'max') { setMaximized(false); return; } setCollapsed(mode() !== 'collapsed', { x: event.clientX, button: false }); });   // maximized: a double-click gives the normal window back
  return { dispose() { setMaximized(false); const at = stack.indexOf(panel); if (at >= 0) stack.splice(at, 1); } };
}
