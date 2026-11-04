// Small building blocks shared by the studio panels of the newer effects (limiter, pan, width, gate, de-esser, modulation, bit crusher).
import { el } from '../../ui.js';
import { knob } from '../knob.js';

const ns = 'http://www.w3.org/2000/svg';

export const number = (params, key, fallback) => { const parsed = Number.parseFloat(String(params[key] ?? '').replace(/[a-z %]/gi, '')); return Number.isFinite(parsed) ? parsed : fallback; };
export const automated = (text) => /^\s*(auto|lfo|env|fadein|fadeout)\(/.test(String(text ?? ''));
export const svg = (tag, attrs = {}) => { const node = document.createElementNS(ns, tag); Object.entries(attrs).forEach(([name, item]) => node.setAttribute(name, String(item))); return node; };
export const plot = (label, draw) => { const graph = svg('svg', { viewBox: '0 0 520 220', class: 'studio-effect-plot', role: 'img', 'aria-label': label }); draw(graph); return graph; };
export const controls = (items) => el('div', { className: 'studio-eq-controls' }, items);
export const baseline = (graph, y = 110) => graph.append(svg('line', { x1: 20, x2: 500, y1: y, y2: y, class: 'studio-graph-zero' }));

/**
 * A knob for a numeric parameter. When the value is an automation (auto(...) or lfo(...)) it is shown as a text field instead, because a knob
 * would write one number over the automation; the text stays the source.
 */
export function control(context, params, name, label, min, max, unit, scale = 'linear', size = 82, onPreview = null) {
  if (automated(params[name])) {
    const input = el('input', { type: 'text', value: params[name], disabled: context.readOnly, 'aria-label': `${label} automation`, title: 'An automation: auto(bar:value, ...) or lfo(period, low, high). Clear it to get the knob back.' });
    input.onchange = () => context.commit(name, input.value.trim());
    return el('label', { className: 'studio-panel-control' }, [el('span', { textContent: `${label} (automated)` }), input]);
  }

  return el('label', { className: 'studio-panel-control' }, [el('span', { textContent: label }), knob({
    label, value: number(params, name, min), min, max, unit, scale, size, step: unit === 'dB' ? undefined : 1, disabled: context.readOnly,
    onPreview: (next) => { context.preview?.(name, next); onPreview?.(next); },
    onCommit: (next) => context.commit(name, `${Number(next.toFixed(unit === 'dB' ? 1 : 0))}${unit ? ` ${unit}` : ''}`),
  })]);
}

/** A transfer curve: input level on the x axis, output level on the y axis, both in dB from -60 to 0. */
export function transferCurve(label, outputDb) {
  return plot(label, (root) => {
    baseline(root, 190);
    root.append(svg('line', { x1: 20, x2: 500, y1: 200, y2: 20, class: 'studio-graph-zero', 'stroke-dasharray': '4 4' }));
    const line = svg('path', { class: 'studio-response-line' });
    const draw = () => {
      const d = [];
      for (let i = 0; i <= 60; i++) { const input = -60 + i; const out = Math.max(-60, Math.min(0, outputDb(input))); d.push(`${i ? 'L' : 'M'}${20 + (i / 60) * 480} ${200 - ((out + 60) / 60) * 180}`); }
      line.setAttribute('d', d.join(' '));
    };
    draw(); root.append(line); root.redraw = draw;
  });
}
