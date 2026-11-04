import { el } from '../ui.js';

export function scrubBar(options) {
  let position = options.position || 0; let dragging = false; let initial = position; let loopAnchor = position; let loop = options.loop ? [...options.loop] : null; let initialLoop = null; let choosingLoop = false;
  const canvas = el('canvas', { className: 'studio-scrub', role: 'slider', tabindex: options.disabled ? '-1' : '0', 'aria-label': options.label, title: 'Drag to seek · Shift-drag to set loop region', 'aria-valuemin': '0', 'aria-valuemax': '1000', 'aria-valuenow': String(Math.round(position * 1000)) });
  const draw = () => {
    const bounds = canvas.getBoundingClientRect(); const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.max(1, Math.round(bounds.width * dpr)); canvas.height = Math.max(1, Math.round(bounds.height * dpr));
    const ctx = canvas.getContext('2d'); ctx.scale(dpr, dpr); const style = getComputedStyle(document.documentElement);
    ctx.clearRect(0, 0, bounds.width, bounds.height);
    if (loop) { const left = Math.min(...loop) * bounds.width, right = Math.max(...loop) * bounds.width; ctx.fillStyle = style.getPropertyValue('--fx-dyn').trim(); ctx.globalAlpha = .18; ctx.fillRect(left, 0, right - left, bounds.height); ctx.globalAlpha = 1; ctx.strokeStyle = style.getPropertyValue('--fx-dyn').trim(); ctx.strokeRect(left, 1, right - left, bounds.height - 2); }
    ctx.strokeStyle = style.getPropertyValue('--fx-dyn').trim(); ctx.beginPath();
    const peaks = options.peaks || [];
    if (options.peaksArePairs) {
      for (let index = 0; index < peaks.length; index += 2) { const x = index / Math.max(2, peaks.length - 2) * bounds.width; ctx.moveTo(x, bounds.height / 2 - peaks[index + 1] * bounds.height / 2); ctx.lineTo(x, bounds.height / 2 - peaks[index] * bounds.height / 2); }
    } else peaks.forEach((sample, index) => { const x = index / Math.max(1, peaks.length - 1) * bounds.width; ctx.moveTo(x, bounds.height / 2 - sample * bounds.height / 2); ctx.lineTo(x, bounds.height / 2 + sample * bounds.height / 2); });
    ctx.stroke(); ctx.fillStyle = style.getPropertyValue('--hi').trim(); ctx.fillRect(position * bounds.width, 0, 2, bounds.height);
    canvas.setAttribute('aria-valuenow', String(Math.round(position * 1000))); canvas.setAttribute('aria-valuetext', `${Math.round(position * 100)}%${loop ? ` · loop ${Math.round(loop[0] * 100)} to ${Math.round(loop[1] * 100)}%` : ''}`);
  };
  const set = (event) => { const rect = canvas.getBoundingClientRect(), next = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width)); if (choosingLoop) { loop = [loopAnchor, next].sort((a, b) => a - b); options.onLoopPreview?.(loop); } else { position = next; options.onPreview(position); } draw(); };
  canvas.onpointerdown = (event) => { if (options.disabled) return; dragging = true; initial = position; initialLoop = loop ? [...loop] : null; choosingLoop = event.shiftKey; if (choosingLoop) loopAnchor = Math.max(0, Math.min(1, (event.clientX - canvas.getBoundingClientRect().left) / canvas.getBoundingClientRect().width)); canvas.setPointerCapture(event.pointerId); set(event); event.preventDefault(); };
  canvas.onpointermove = (event) => { if (dragging) set(event); };
  canvas.onpointerup = () => { if (!dragging) return; dragging = false; if (choosingLoop) options.onLoopCommit?.(loop); else options.onCommit(position); choosingLoop = false; initialLoop = null; };
  canvas.onpointercancel = () => { if (!dragging) return; dragging = false; position = initial; loop = initialLoop; choosingLoop = false; initialLoop = null; draw(); options.onPreview(position); if (loop) options.onLoopPreview?.(loop); };
  canvas.onkeydown = (event) => {
    if (options.disabled) return;
    if (event.key === 'Escape' && (dragging || initialLoop)) { position = initial; loop = initialLoop; dragging = false; choosingLoop = false; initialLoop = null; draw(); options.onPreview(position); if (loop) options.onLoopPreview?.(loop); event.preventDefault(); return; }
    if (event.key === 'ArrowRight' || event.key === 'ArrowUp') position = Math.min(1, position + (event.shiftKey ? .001 : .01)); else if (event.key === 'ArrowLeft' || event.key === 'ArrowDown') position = Math.max(0, position - (event.shiftKey ? .001 : .01)); else if (event.key === 'Home') position = 0; else if (event.key === 'End') position = 1; else return;
    draw(); options.onPreview(position); options.onCommit(position); event.preventDefault();
  };
  canvas.setScrubPosition = (next) => { if (dragging) return; position = Math.max(0, Math.min(1, Number(next))); draw(); };
  canvas.setScrubLoop = (next) => { loop = next ? [...next].sort((a, b) => a - b).map((value) => Math.max(0, Math.min(1, value))) : null; draw(); };
  draw(); new ResizeObserver(draw).observe(canvas); return canvas;
}
