import { el } from '../ui.js';
import { insertCurvePoint, moveCurvePoint, removeCurvePoint } from '../../lib/curve-editor-model.mjs';

export function curveEditor(options) {
  let points = options.points.map((point) => ({ ...point }));
  let initial = null;
  let activeIndex = -1;
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', `0 0 ${options.width} ${options.height}`); svg.setAttribute('role', 'application'); svg.setAttribute('aria-label', options.label);
  const draw = () => {
    svg.replaceChildren();
    for (let x = 0; x < options.width; x += options.grid || 32) { const line = document.createElementNS(svg.namespaceURI, 'line'); line.setAttribute('x1', String(x)); line.setAttribute('x2', String(x)); line.setAttribute('y1', '0'); line.setAttribute('y2', String(options.height)); line.setAttribute('class', 'studio-curve-grid'); svg.append(line); }
    const line = document.createElementNS(svg.namespaceURI, 'polyline'); line.setAttribute('points', points.map((point) => `${point.x},${options.height - point.y}`).join(' ')); line.setAttribute('class', 'studio-curve-line'); svg.append(line);
    points.forEach((point, index) => {
      const handle = document.createElementNS(svg.namespaceURI, 'circle'); handle.setAttribute('cx', String(point.x)); handle.setAttribute('cy', String(options.height - point.y)); handle.setAttribute('r', '6'); handle.setAttribute('tabindex', options.disabled ? '-1' : '0'); handle.setAttribute('role', 'slider'); handle.setAttribute('aria-label', `${options.label} point ${index + 1}`); handle.setAttribute('aria-valuemin', '0'); handle.setAttribute('aria-valuemax', String(options.height)); handle.setAttribute('aria-valuenow', String(point.y)); handle.setAttribute('aria-valuetext', `Point ${index + 1}, ${Math.round(point.y)}`); handle.setAttribute('class', 'studio-curve-point');
      handle.onpointerdown = (event) => { if (options.disabled) return; activeIndex = index; initial = points.map((entry) => ({ ...entry })); svg.setPointerCapture(event.pointerId); event.preventDefault(); };
      handle.onkeydown = (event) => {
        if (options.disabled) return;
        if (event.key === 'Delete' || event.key === 'Backspace') { event.preventDefault(); points = removeCurvePoint(points, index); draw(); options.onCommit(points); return; }
        const step = (options.snap || 1) * (event.shiftKey ? 0.2 : 1);
        const x = point.x + (event.key === 'ArrowRight' ? step : event.key === 'ArrowLeft' ? -step : 0);
        const y = point.y + (event.key === 'ArrowUp' ? step : event.key === 'ArrowDown' ? -step : event.key === 'Home' ? -options.height : event.key === 'End' ? options.height : 0);
        if (x === point.x && y === point.y) return;
        event.preventDefault(); points = moveCurvePoint(points, index, x, y, options.width, options.height, options.snap || 1, options.minGap || 8); draw(); options.onPreview(points); options.onCommit(points); svg.querySelectorAll('.studio-curve-point')[index]?.focus();
      };
      svg.append(handle);
    });
  };
  draw();
  svg.onpointermove = (event) => {
    if (activeIndex < 0 || !initial) return;
    const bounds = svg.getBoundingClientRect(); const snap = options.snap || 1;
    points = moveCurvePoint(points, activeIndex, (event.clientX - bounds.left) / bounds.width * options.width, (options.height - (event.clientY - bounds.top) / bounds.height * options.height), options.width, options.height, snap, options.minGap || 8);
    draw(); options.onPreview(points);
  };
  svg.onpointerup = () => { if (activeIndex < 0 || !initial) return; activeIndex = -1; initial = null; options.onCommit(points); };
  svg.onpointercancel = () => { if (!initial) return; points = initial; initial = null; activeIndex = -1; draw(); options.onPreview(points); };
  svg.onlostpointercapture = () => { if (activeIndex < 0 || !initial) return; points = initial; initial = null; activeIndex = -1; draw(); options.onPreview(points); };
  svg.onkeydown = (event) => { if (event.key === 'Escape' && initial) { event.preventDefault(); event.stopPropagation(); points = initial; initial = null; activeIndex = -1; draw(); options.onPreview(points); } };
  svg.ondblclick = (event) => {
    if (options.disabled) return;
    const bounds = svg.getBoundingClientRect(), x = (event.clientX - bounds.left) / bounds.width * options.width, y = options.height - (event.clientY - bounds.top) / bounds.height * options.height;
    points = insertCurvePoint(points, x, y, options.width, options.height, options.snap || 1, options.minGap || 8); draw(); options.onCommit(points);
    const index = points.findIndex((point) => point.x === Math.round(x / (options.snap || 1)) * (options.snap || 1)); svg.querySelectorAll('.studio-curve-point')[index]?.focus();
  };
  svg.oncontextmenu = (event) => {
    event.preventDefault(); if (options.disabled || points.length <= 2) return;
    const bounds = svg.getBoundingClientRect(), x = (event.clientX - bounds.left) / bounds.width * options.width, y = options.height - (event.clientY - bounds.top) / bounds.height * options.height;
    const index = points.reduce((best, point, i) => Math.hypot(point.x - x, point.y - y) < Math.hypot(points[best].x - x, points[best].y - y) ? i : best, 0);
    if (index <= 0 || index >= points.length - 1) return;
    points = removeCurvePoint(points, index); draw(); options.onCommit(points);
  };
  return el('div', { className: 'studio-curve' }, [svg]);
}
