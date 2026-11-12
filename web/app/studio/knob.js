import { clamp, denormalize, formatValue, knobDragValue, normalize, snap } from '../../lib/studio-math.js';
import { el } from '../ui.js';

const ns = 'http://www.w3.org/2000/svg';
let knobId = 0;

export function knob(options) {
  let value = clamp(Number(options.value), options.min, options.max);
  let startValue = value;
  let dragPosition = 0;
  let startX = 0;
  let startY = 0;
  let active = false;
  let bubble = null;   // the value next to the knob while it is dragged
  const size = options.size || 82;
  const decimals = options.decimals || 0;
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', '0 0 100 100'); svg.setAttribute('width', String(size)); svg.setAttribute('height', String(size));
  svg.setAttribute('role', 'slider'); svg.setAttribute('tabindex', options.disabled ? '-1' : '0'); svg.setAttribute('aria-disabled', String(Boolean(options.disabled))); svg.setAttribute('aria-orientation', 'vertical');
  svg.setAttribute('aria-label', options.label); svg.setAttribute('aria-valuemin', String(options.min)); svg.setAttribute('aria-valuemax', String(options.max));
  svg.style.setProperty('--knob-accent', options.accent || 'var(--acc)');
  const add = (tag, attrs) => { const node = document.createElementNS(ns, tag); Object.entries(attrs).forEach(([key, item]) => node.setAttribute(key, String(item))); svg.append(node); return node; };
  const defs = document.createElementNS(ns, 'defs');
  const gradient = document.createElementNS(ns, 'linearGradient'); gradient.id = `studio-knob-gradient-${knobId++}`; gradient.setAttribute('x1', '0'); gradient.setAttribute('y1', '0'); gradient.setAttribute('x2', '1'); gradient.setAttribute('y2', '1');
  [['0%', 'var(--panel2)'], ['55%', 'var(--bg)'], ['100%', 'var(--panel)']].forEach(([offset, color]) => { const stop = document.createElementNS(ns, 'stop'); stop.setAttribute('offset', offset); stop.setAttribute('stop-color', color); gradient.append(stop); });
  defs.append(gradient); svg.append(defs);
  add('circle', { cx: 50, cy: 50, r: 43, class: 'studio-knob-bezel' });
  const body = add('circle', { cx: 50, cy: 50, r: 36, class: 'studio-knob-body' }); body.setAttribute('fill', `url(#${gradient.id})`);
  const arc = add('path', { class: 'studio-knob-arc' });
  const indicator = add('path', { class: 'studio-knob-indicator' });
  const ticks = document.createElementNS(ns, 'g'); ticks.setAttribute('class', 'studio-knob-ticks');
  for (let index = 0; index <= 10; index++) {
    const angle = (-135 + index * 27) * Math.PI / 180;
    const line = document.createElementNS(ns, 'line');
    line.setAttribute('x1', String(50 + Math.cos(angle) * 39)); line.setAttribute('y1', String(50 + Math.sin(angle) * 39));
    line.setAttribute('x2', String(50 + Math.cos(angle) * 42)); line.setAttribute('y2', String(50 + Math.sin(angle) * 42)); ticks.append(line);
  }
  svg.append(ticks);
  const readout = el('span', { className: 'studio-knob-value', textContent: '' });
  const root = el('div', { className: `studio-knob studio-knob-${options.sizeName || 'm'}` }, [svg, el('span', { className: 'studio-knob-label', textContent: options.label }), readout]);
  const point = (angle, radius) => [50 + Math.cos(angle * Math.PI / 180) * radius, 50 + Math.sin(angle * Math.PI / 180) * radius];
  const path = (from, to) => { const [x1, y1] = point(from, 30); const [x2, y2] = point(to, 30); return `M${x1} ${y1} A30 30 0 ${to - from > 180 ? 1 : 0} 1 ${x2} ${y2}`; };
  const draw = () => {
    const p = normalize(value, options.min, options.max, options.scale);
    const angle = -135 + p * 270;
    arc.setAttribute('d', path(-135, angle));
    const [x1, y1] = point(angle, 17); const [x2, y2] = point(angle, 29);
    indicator.setAttribute('d', `M${x1} ${y1}L${x2} ${y2}`);
    const formatted = options.format ? options.format(value) : formatValue(value, decimals, options.unit || '');
    readout.textContent = formatted;
    if (bubble) bubble.textContent = formatted;
    svg.setAttribute('aria-valuenow', String(value)); svg.setAttribute('aria-valuetext', formatted);
  };
  const preview = (next) => { value = snap(clamp(next, options.min, options.max), options.step || 0, options.min, options.max); draw(); if (options.onPreview) options.onPreview(value); };
  const commit = () => { if (options.onCommit && !options.disabled) options.onCommit(value); };
  // the drag ends on the window too: a knob that is drawn again while it is dragged (a card of a block that redraws itself) loses its pointer events, and without this the hidden cursor stays hidden
  const end = () => { window.removeEventListener('pointerup', end); window.removeEventListener('pointercancel', end); window.removeEventListener('blur', end); if (!active) return; const gone = !svg.isConnected; clearDrag(); if (gone && value !== startValue) commit(); };
  const clearDrag = () => { if (!active) return; bubble?.remove(); bubble = null; active = false; document.body.classList.remove('studio-dragging'); };
  svg.onpointerdown = (event) => {
    if (options.disabled || event.button !== 0) return;
    if (event.altKey) { event.preventDefault(); preview(options.defaultValue); commit(); return; }   // Alt+click: back to the default
    active = true; startValue = value; dragPosition = normalize(value, options.min, options.max, options.scale); startX = event.clientX; startY = event.clientY;
    svg.setPointerCapture(event.pointerId); document.body.classList.add('studio-dragging'); event.preventDefault();
    bubble = el('span', { className: 'studio-knob-bubble', role: 'status', textContent: readout.textContent }); root.append(bubble);
    window.addEventListener('pointerup', end); window.addEventListener('pointercancel', end);   // bubbling: the knob's own handler commits first window.addEventListener('blur', end);
  };
  svg.onpointermove = (event) => {
    if (!active) return;
    const delta = (event.clientX - startX) - (event.clientY - startY);
    startX = event.clientX; startY = event.clientY;
    dragPosition = knobDragValue(dragPosition, delta, 0, 1, event.shiftKey);
    preview(denormalize(dragPosition, options.min, options.max, options.scale));
  };
  svg.onpointerup = () => { if (!active) return; clearDrag(); if (value !== startValue) commit(); };   // a click that moved nothing writes nothing (it would redraw the knob under a double click)
  svg.onpointercancel = () => { if (!active) return; preview(startValue); clearDrag(); };
  svg.onlostpointercapture = () => clearDrag();
  svg.onkeydown = (event) => {
    if (options.disabled) return;
    if (event.key === 'Escape' && active) { preview(startValue); clearDrag(); event.preventDefault(); return; }
    const amount = event.shiftKey ? .001 : .01;
    // a knob that moves in whole steps (bits, hold, milliseconds) must move by at least one step per key press, or a small range would swallow the key
    const move = (sign) => { const target = denormalize(normalize(value, options.min, options.max, options.scale) + sign * amount, options.min, options.max, options.scale); return options.step && Math.abs(target - value) < options.step ? value + sign * options.step : target; };
    if (event.key === 'ArrowRight' || event.key === 'ArrowUp') preview(move(1));
    else if (event.key === 'ArrowLeft' || event.key === 'ArrowDown') preview(move(-1));
    else if (event.key === 'PageUp') preview(move(10));   // a coarse step: ten times the arrow key (WAI-ARIA slider)
    else if (event.key === 'PageDown') preview(move(-10));
    else if (event.key === 'Home') preview(options.min);
    else if (event.key === 'End') preview(options.max);
    else return;
    event.preventDefault(); commit();
  };
  svg.onwheel = (event) => { if (options.disabled) return; event.preventDefault(); const delta = (options.step || (options.max - options.min) / 100) * (event.deltaY < 0 ? 1 : -1); preview(options.scale === 'log' ? denormalize(normalize(value, options.min, options.max, options.scale) + (event.deltaY < 0 ? .01 : -.01), options.min, options.max, options.scale) : value + delta); commit(); };
  // a double click opens a small field to type the value yourself (Enter applies, Esc cancels); Alt+click gives the default value back
  svg.ondblclick = (event) => { event.stopPropagation(); if (options.disabled) return; enterValue(); };
  // typing a value: a small numeric field in place of the readout (Enter on the knob or a right-click); Enter applies, Esc or leaving cancels
  const enterValue = () => {
    if (options.disabled || root.querySelector('.studio-knob-entry')) return;
    const input = el('input', { type: 'text', inputMode: 'decimal', className: 'studio-knob-entry', value: String(value), 'aria-label': `${options.label} value` });
    let done = false;
    const finish = (apply) => {
      if (done) return; done = true;
      const text = input.value.trim();
      input.replaceWith(readout);
      if (apply && text !== '' && Number.isFinite(Number(text))) { preview(Number(text)); commit(); }
      svg.focus({ preventScroll: true });
    };
    input.onkeydown = (event) => { event.stopPropagation(); if (event.key === 'Enter') { event.preventDefault(); finish(true); } else if (event.key === 'Escape') { event.preventDefault(); finish(false); } };
    input.onblur = () => finish(false);
    readout.replaceWith(input); input.focus(); input.select();
  };
  svg.oncontextmenu = (event) => { event.preventDefault(); enterValue(); };
  svg.addEventListener('keydown', (event) => { if (event.key === 'Enter' && !options.disabled) { event.preventDefault(); enterValue(); } });
  draw();
  return root;
}
