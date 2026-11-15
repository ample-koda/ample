// The gestures of the canvas are shown when the canvas is first used and then on demand: a ? button next to the zoom controls shows them again (issue 129).
import { $ } from './ui.js';

const KEY = 'ample.canvas-hint.seen';

export function initCanvasHint() {
  const hint = $('#hint'), button = $('#z-help');
  if (!hint || !button) return;
  let timer = 0;
  const show = (on, forMs = 0) => {
    clearTimeout(timer);
    hint.hidden = !on;
    button.setAttribute('aria-expanded', String(on));
    if (on && forMs) timer = setTimeout(() => show(false), forMs);
  };
  let seen = false;
  try { seen = localStorage.getItem(KEY) === '1'; } catch { /* storage may be blocked: the hint then shows each time */ }
  show(!seen, 12000);
  try { localStorage.setItem(KEY, '1'); } catch { /* storage may be blocked */ }
  button.onclick = () => show(hint.hidden, 15000);
  $('#cv')?.addEventListener('wheel', () => { if (!hint.hidden) show(false); }, { passive: true, once: true });
}
