// The before/after view of one block of the live chain: a log-frequency spectrum (before gray, after accent, peak hold) or a scope. Opened from a block chip of the live chain bar,
// drawn by one requestAnimationFrame loop that runs only while the view is visible and the tab is shown. @ample:live-spectrum
import { el } from './ui.js';
import { chainSpectrum, chainScope, onChain } from './live-chain.js';
import { flat } from './store.js';
import { bandCentres, logBands, peakHold, dbHeight, scopeStart } from '../lib/spectrum-math.mjs';

const CENTRES = bandCentres(120);
const FLOOR = -90;
let panel = null, canvas = null, block = null, mode = 'spectrum', frame = 0, held = null;

const color = (name, fallback) => getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;

/** Opens (or closes, when it is the same block) the view for a block of the chain. */
export function toggleSpectrum(id) {
  if (block === id) { closeSpectrum(); return; }
  block = id; held = null;
  mount();
}

export function closeSpectrum() { block = null; cancelAnimationFrame(frame); panel?.remove(); panel = null; canvas = null; }
export const spectrumBlock = () => block;

function mount() {
  const host = document.querySelector('.livechain');
  if (!host) return;
  panel?.remove();
  const title = flat().nodes.find((n) => n.id === block)?.title || block;
  canvas = el('canvas', { className: 'lcspec-canvas', width: 560, height: 140, role: 'img', 'aria-label': `Spectrum before and after ${title}` });
  const modeButton = el('button', { type: 'button', className: 'lcspec-mode', textContent: mode === 'spectrum' ? 'Show scope' : 'Show spectrum', onclick: () => { mode = mode === 'spectrum' ? 'scope' : 'spectrum'; mount(); } });
  const close = el('button', { type: 'button', textContent: 'Close', onclick: closeSpectrum });
  panel = el('div', { className: 'lcspec', 'data-block': block }, [
    el('div', { className: 'lcspec-head' }, [el('strong', { textContent: `${title}: before (gray) and after (accent)` }), modeButton, close]), canvas,
  ]);
  host.append(panel);
  cancelAnimationFrame(frame);
  const tick = () => {
    if (!panel) return;
    if (!document.hidden) draw();
    frame = requestAnimationFrame(tick);
  };
  frame = requestAnimationFrame(tick);
}

function draw() {
  const ctx = canvas.getContext('2d'), w = canvas.width, h = canvas.height;
  ctx.clearRect(0, 0, w, h);
  ctx.fillStyle = color('--panel3', '#222'); ctx.fillRect(0, 0, w, h);
  const accent = color('--acc', '#4af'), gray = color('--muted', '#888');
  if (mode === 'scope') {
    const scope = chainScope(block);
    if (!scope) return;
    for (const [data, stroke] of [[scope.pre, gray], [scope.post, accent]]) {
      const start = scopeStart(data), length = Math.min(512, data.length - start);
      ctx.strokeStyle = stroke; ctx.lineWidth = 1.5; ctx.beginPath();
      for (let index = 0; index < length; index++) { const x = index / (length - 1) * w, y = h / 2 - Math.max(-1, Math.min(1, data[start + index])) * h / 2; index ? ctx.lineTo(x, y) : ctx.moveTo(x, y); }
      ctx.stroke();
    }
    return;
  }
  const spectrum = chainSpectrum(block);
  if (!spectrum) return;
  const pre = logBands(spectrum.pre, spectrum.rate, CENTRES), post = logBands(spectrum.post, spectrum.rate, CENTRES);
  held = peakHold(held, post);
  const trace = (values, stroke, width) => {
    ctx.strokeStyle = stroke; ctx.lineWidth = width; ctx.beginPath();
    values.forEach((db, index) => { const x = index / (values.length - 1) * w, y = h - dbHeight(db, FLOOR) * h; index ? ctx.lineTo(x, y) : ctx.moveTo(x, y); });
    ctx.stroke();
  };
  ctx.globalAlpha = .45; trace(held, accent, 1); ctx.globalAlpha = 1;
  trace(pre, gray, 1.5); trace(post, accent, 2);
  ctx.fillStyle = gray; ctx.font = '10px sans-serif';
  for (const hz of [100, 1000, 10000]) { const x = Math.log(hz / 20) / Math.log(1000) * w; ctx.fillText(hz >= 1000 ? `${hz / 1000}k` : String(hz), x + 2, h - 3); }
}

onChain((state) => { if (!state.active || (block && !state.items.some((item) => item.id === block))) closeSpectrum(); else if (block && panel && !panel.isConnected) mount(); });
