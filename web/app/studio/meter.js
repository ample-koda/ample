import { el } from '../ui.js';

export function meter(options) {
  let value = options.value, peak = options.peak ?? options.value, heldPeak = peak, holdUntil = 0, lastFrame = 0, frame = 0;
  const canvas = el('canvas', { className: `studio-meter${options.mode === 'reduction' ? ' reduction' : ''}`, role: 'meter', 'aria-label': options.label, 'aria-valuemin': String(options.min), 'aria-valuemax': String(options.max), 'aria-valuenow': String(options.value), 'aria-valuetext': `${options.value}${options.unit ? ` ${options.unit}` : ''}` });
  const draw = (now = performance.now()) => {
    const rect = canvas.getBoundingClientRect(); const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.max(1, Math.round(rect.width * dpr)); canvas.height = Math.max(1, Math.round(rect.height * dpr));
    const context = canvas.getContext('2d'); context.scale(dpr, dpr);
    const style = getComputedStyle(document.documentElement); const width = rect.width; const height = rect.height;
    const ratio = Math.max(0, Math.min(1, (value - options.min) / (options.max - options.min)));
    const inputPeak = Math.max(value, peak);
    if (inputPeak >= heldPeak) { heldPeak = inputPeak; holdUntil = now + 900; }
    else if (now > holdUntil) heldPeak = Math.max(inputPeak, heldPeak - Math.max(0, now - lastFrame) * (options.max - options.min) / 2600);
    lastFrame = now;
    const peakRatio = Math.max(0, Math.min(1, (heldPeak - options.min) / (options.max - options.min)));
    context.fillStyle = style.getPropertyValue('--panel3').trim(); context.fillRect(0, 0, width, height);
    const gradient = context.createLinearGradient(0, height, 0, 0);
    gradient.addColorStop(0, style.getPropertyValue('--ok').trim()); gradient.addColorStop(.78, style.getPropertyValue('--warn').trim()); gradient.addColorStop(1, style.getPropertyValue('--bad').trim());
    context.fillStyle = gradient; context.fillRect(0, height * (1 - ratio), width, height * ratio);
    context.fillStyle = style.getPropertyValue('--hi').trim(); context.fillRect(0, height * (1 - peakRatio), width, 2);
    canvas.setAttribute('aria-valuenow', String(value)); canvas.setAttribute('aria-valuetext', `${Number(value).toFixed(options.decimals || 0)}${options.unit ? ` ${options.unit}` : ''}`);
    if (options.animate && document.visibilityState !== 'hidden' && !matchMedia('(prefers-reduced-motion: reduce)').matches) frame = requestAnimationFrame(draw);
  };
  canvas.setMeterValue = (next, nextPeak = next) => { value = Math.max(options.min, Math.min(options.max, Number(next))); peak = Math.max(value, Math.min(options.max, Number(nextPeak))); if (frame) cancelAnimationFrame(frame); draw(); };
  draw();
  new ResizeObserver(() => { if (frame) cancelAnimationFrame(frame); draw(); }).observe(canvas);
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible' && options.animate) { if (frame) cancelAnimationFrame(frame); draw(); } });
  return canvas;
}
