// The fade editor (plan fades F04): fade in or out, where it starts, how long it takes (bars or seconds) and the shape, with a picture of the curve.
// It writes the same fadein(...) / fadeout(...) text the source holds. @ample:fades

import { el, ask } from './ui.js';
import { FADE_SHAPES, fadeAmountAt, formatFade, parseFade } from '../lib/fade-model.mjs';

const ns = 'http://www.w3.org/2000/svg';
const node = (tag, attrs = {}) => { const n = document.createElementNS(ns, tag); Object.entries(attrs).forEach(([k, v]) => n.setAttribute(k, String(v))); return n; };
const SHAPE_LABEL = { lin: 'straight (lin)', equal: 'equal power (equal)', exp: 'slow start (exp)' };

/**
 * Opens the editor for a gain or a mix. Resolves to the new text (a fadein(...) or fadeout(...) text), '' for "back to a plain value", or undefined when cancelled.
 * @param {{ title: string, text: string, bars: number, tempo: number }} options
 */
export async function openFadeEditor({ title, text, bars, tempo }) {
  const current = parseFade(text) || { fadeIn: false, start: { value: 5, seconds: false }, length: { value: 2, seconds: false }, shape: 'lin' };
  const secondsPerBar = (60 / Math.max(20, tempo || 100)) * 4;
  const shownBars = Math.max(16, bars || 16);
  const W = 680, H = 190, left = 58, bottom = 26;
  const direction = el('select', { 'aria-label': 'Fade in or out' }, [el('option', { value: 'in', textContent: 'Fade in (silent, then full)' }), el('option', { value: 'out', textContent: 'Fade out (full, then silent)' })]);
  direction.value = current.fadeIn ? 'in' : 'out';
  const unitSelect = (seconds) => { const s = el('select', { 'aria-label': 'Unit' }, [el('option', { value: 'bars', textContent: 'bars' }), el('option', { value: 's', textContent: 'seconds' })]); s.value = seconds ? 's' : 'bars'; return s; };
  const startValue = el('input', { type: 'text', value: String(current.start.value), 'aria-label': 'Start', size: 7 });
  const startUnit = unitSelect(current.start.seconds);
  const lengthValue = el('input', { type: 'text', value: String(current.length.value), 'aria-label': 'Length', size: 7 });
  const lengthUnit = unitSelect(current.length.seconds);
  const shape = el('select', { 'aria-label': 'Shape' }, FADE_SHAPES.map((s) => el('option', { value: s, textContent: SHAPE_LABEL[s] })));
  shape.value = current.shape;
  const svg = node('svg', { viewBox: `0 0 ${W} ${H}`, class: 'automation-plot', role: 'img', 'aria-label': `${title} fade over the bars` });
  // the same neon look as the automation editor: a faint line at every bar, the level at the top and the bottom, a curve over a soft fill
  const defs = node('defs');
  const stroke = node('linearGradient', { id: 'fade-stroke', x1: 0, x2: 1, y1: 0, y2: 0 });
  stroke.append(node('stop', { offset: 0, 'stop-color': '#2de2ff' }), node('stop', { offset: 1, 'stop-color': '#ff4fd8' }));
  const fill = node('linearGradient', { id: 'fade-fill', x1: 0, x2: 0, y1: 0, y2: 1 });
  fill.append(node('stop', { offset: 0, 'stop-color': '#2de2ff', 'stop-opacity': 0.28 }), node('stop', { offset: 1, 'stop-color': '#ff4fd8', 'stop-opacity': 0.02 }));
  defs.append(stroke, fill);
  const grid = node('g');
  grid.append(node('rect', { x: left, y: 10, width: W - left - 10, height: H - bottom - 10, rx: 6, class: 'auto-area' }));
  const barX = (bar) => left + ((bar - 1) / Math.max(1, shownBars - 1)) * (W - left - 10);
  const labelStep = [1, 2, 4, 8, 16].find((s) => s * ((W - left - 10) / Math.max(1, shownBars - 1)) >= 34) ?? 32;
  for (let bar = 1; bar <= shownBars; bar++) {
    const strong = (bar - 1) % labelStep === 0;
    grid.append(node('line', { x1: barX(bar), x2: barX(bar), y1: 10, y2: H - bottom, class: strong ? 'auto-grid strong' : 'auto-grid' }));
    if (strong) { const t = node('text', { x: barX(bar), y: H - 8, class: 'auto-label', 'text-anchor': 'middle' }); t.textContent = String(bar); grid.append(t); }
  }
  [['full', 10], ['silent', H - bottom]].forEach(([label, y]) => {
    grid.append(node('line', { x1: left, x2: W - 10, y1: y, y2: y, class: 'auto-grid value' }));
    const t = node('text', { x: left - 8, y: y + 4, class: 'auto-label', 'text-anchor': 'end' }); t.textContent = label; grid.append(t);
  });
  const area = node('path', { class: 'auto-fill', style: 'fill:url(#fade-fill)' });
  const line = node('path', { class: 'studio-response-line auto-line', fill: 'none', style: 'stroke:url(#fade-stroke)' });
  svg.append(defs, grid, area, line);
  const source = el('code', { className: 'auto-source', title: 'The text this writes into the song' });
  const readout = el('p', { className: 'auto-hint' });
  const spec = () => {
    const pos = (v, u) => `${v.value.trim()}${u.value === 's' ? 's' : ''}`;
    const text2 = `${direction.value === 'in' ? 'fadein' : 'fadeout'}(${pos(startValue, startUnit)}, ${pos(lengthValue, lengthUnit)}, ${shape.value})`;
    return { text: text2, spec: parseFade(text2) };
  };
  const draw = () => {
    const { text: written, spec: parsed } = spec();
    if (!parsed) { line.setAttribute('d', ''); area.setAttribute('d', ''); source.textContent = written; readout.textContent = 'Not a fade yet: the start is 1 or more (bars) or seconds (12s), and the length is above zero.'; return; }
    const total = shownBars * secondsPerBar, steps = 160, d = [];
    for (let i = 0; i <= steps; i++) {
      const t = (i / steps) * total;
      d.push(`${i === 0 ? 'M' : 'L'}${left + (i / steps) * (W - left - 10)} ${10 + (1 - fadeAmountAt(parsed, t, secondsPerBar)) * (H - bottom - 10)}`);
    }
    line.setAttribute('d', d.join(' '));
    area.setAttribute('d', `${d.join(' ')} L${W - 10} ${H - bottom} L${left} ${H - bottom} Z`);
    source.textContent = formatFade(parsed);
    readout.textContent = `Bars count from bar 1 (5.5 is half way through bar 5); seconds count from the start of the song. The picture shows ${shownBars} bars at ${Math.round(tempo || 100)} BPM.`;
  };
  [direction, startValue, startUnit, lengthValue, lengthUnit, shape].forEach((control) => { control.oninput = draw; control.onchange = draw; });
  draw();
  const field = (label, ...controls) => el('label', { className: 'auto-field' }, [el('span', { textContent: label }), controls.length > 1 ? el('span', { className: 'auto-pair' }, controls) : controls[0]]);
  const wrap = el('div', { className: 'auto-editor' }, [
    el('div', { className: 'auto-fields' }, [field('Direction', direction), field('Shape', shape), field('Starts at', startValue, startUnit), field('Lasts', lengthValue, lengthUnit)]),
    svg,
    el('div', { className: 'auto-foot' }, [readout, el('span', { className: 'auto-source-label', textContent: 'Source' }), source]),
  ]);
  const dialog = document.getElementById('dlg');
  dialog.classList.add('automation-dialog');
  let result;
  try {
    result = await ask(`Fade ${title}`, '', [
      { label: 'Cancel', value: false }, { label: 'Back to a plain value', value: 'clear' }, { label: 'Apply', value: true, primary: true },
    ], [wrap]);
  } finally { dialog.classList.remove('automation-dialog'); }
  if (result === 'clear') return '';
  if (result !== true) return undefined;
  const { spec: parsed } = spec();
  return parsed ? formatFade(parsed) : undefined;
}
