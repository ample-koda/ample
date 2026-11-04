// The window of the assistant's browser, for a person who is not at this computer. While that window waits for a human step (a sign-in, a check that asks for a human)
// a small panel shows it as a picture that refreshes a few times per second; a click, a scroll and typing in the panel go to the page. Nothing shows when there is no such step.
import { api } from './api.js';
import { el } from './ui.js';

let watching = false, panel = null, timer = 0, busy = false;

/** Starts looking (once) whether the assistant's browser waits for a person. */
export function watchBrowserView() {
  if (watching) return;
  watching = true;
  const look = async () => {
    if (busy) return;
    busy = true;
    try {
      const view = await api.get('/api/ai/browser/view');
      if (view.active && !view.busy) { if (!panel) openPanel(); paint(view); } else if (!view.active && panel) closePanel();
    } catch { /* not reachable now: try again */ } finally { busy = false; }
  };
  timer = setInterval(look, 1200);
  look();
}

function openPanel() {
  const canvas = el('canvas', { className: 'bv-canvas', tabIndex: 0 });
  const title = el('b', { className: 'bv-title', textContent: 'Browser of the assistant' });
  const typing = el('input', { type: 'text', className: 'bv-type', placeholder: 'Type here, Enter sends it to the page' });
  const close = el('button', { type: 'button', className: 'quiet', textContent: 'Hide', onclick: () => closePanel(true) });
  panel = el('div', { id: 'browser-view', className: 'bv', role: 'dialog', 'aria-label': 'Browser of the assistant' }, [el('div', { className: 'bv-head' }, [title, close]), canvas, typing, el('small', { className: 'bv-note', textContent: 'The assistant waits for you here (a sign-in or a human check). Click and type like on the page; it goes on when you are done.' })]);
  panel.canvas = canvas; panel.title = title; panel.size = { width: 1, height: 1 };
  const at = (e) => { const r = canvas.getBoundingClientRect(); return { x: Math.round((e.clientX - r.left) / r.width * panel.size.width), y: Math.round((e.clientY - r.top) / r.height * panel.size.height) }; };
  const send = (body) => api.post('/api/ai/browser/input', body).catch(() => {});
  canvas.addEventListener('click', (e) => { canvas.focus(); send({ type: 'click', ...at(e) }); });
  canvas.addEventListener('wheel', (e) => { e.preventDefault(); send({ type: 'scroll', ...at(e), dy: Math.max(-600, Math.min(600, e.deltaY)) }); }, { passive: false });
  canvas.addEventListener('keydown', (e) => {
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    if (e.key.length === 1) { e.preventDefault(); send({ type: 'type', text: e.key }); } else if (['Enter', 'Backspace', 'Tab', 'Escape', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Delete'].includes(e.key)) { e.preventDefault(); send({ type: 'key', text: e.key }); }
  });
  typing.addEventListener('keydown', async (e) => { if (e.key !== 'Enter') return; e.preventDefault(); const text = typing.value; typing.value = ''; if (text) await send({ type: 'type', text }); });   // a phone keyboard types here
  document.body.append(panel);
}

function paint(view) {
  if (!panel || panel.hidden) return;
  const image = new Image();
  image.onload = () => {
    const canvas = panel.canvas;
    panel.size = { width: view.width, height: view.height };
    canvas.width = image.naturalWidth; canvas.height = image.naturalHeight;
    canvas.getContext('2d').drawImage(image, 0, 0);
  };
  image.src = 'data:image/jpeg;base64,' + view.image;
  panel.title.textContent = view.title || 'Browser of the assistant';
}

function closePanel(hideOnly = false) {
  if (!panel) return;
  if (hideOnly) { panel.hidden = true; setTimeout(() => { panel?.remove(); panel = null; }, 60000); return; }
  panel.remove(); panel = null;
}
