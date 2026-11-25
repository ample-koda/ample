// Small building blocks shared by the studio panels of the newer effects (limiter, pan, width, gate, de-esser, modulation, bit crusher).
import { el } from '../../ui.js';
import { knob } from '../knob.js';
import { knobPrecision, wholeDb } from '../../../lib/studio-math.js';
import { knobStart } from '../../../lib/knob-start.mjs';

const ns = 'http://www.w3.org/2000/svg';

export const number = (params, key, fallback) => { const parsed = Number.parseFloat(String(params[key] ?? '').replace(/[a-z %]/gi, '')); return Number.isFinite(parsed) ? parsed : fallback; };
/** The MIDI-learn target of a knob of a panel: the block and the parameter (null when the panel has no block). */
export const learnOf = (context, name) => (context.block ? { block: context.block, name } : null);
export const automated = (text) => /^\s*(auto|lfo|env|fadein|fadeout)\(/.test(String(text ?? ''));
export const svg = (tag, attrs = {}) => { const node = document.createElementNS(ns, tag); Object.entries(attrs).forEach(([name, item]) => node.setAttribute(name, String(item))); return node; };
export const plot = (label, draw) => { const graph = svg('svg', { viewBox: '0 0 520 220', class: 'studio-effect-plot', role: 'img', 'aria-label': label }); draw(graph); return graph; };
/** The label next to a Play button that says what is heard, with the same words as the shared transport of the Advanced windows: Dry input, Processed (live) or Rendered effect (issue 187). */
export const AUDITION_SOURCES = { dry: 'Dry input', live: 'Processed (live)', rendered: 'Rendered effect' };
export const auditionLabel = (mode, detail = '') => el('span', {
  className: 'studio-hear-label note', role: 'status', 'data-mode': mode,
  textContent: `Plays: ${AUDITION_SOURCES[mode] ?? mode}${detail ? ` · ${detail}` : ''}`,
  title: `What the Play button plays: ${AUDITION_SOURCES[mode] ?? mode}. The build is the truth; this is made in the browser.`,
});
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

  const templateStep = context.definitions?.find?.((definition) => definition.name === name)?.step;
  const precision = knobPrecision(unit, min, wholeDb(context.type, name), templateStep);
  return el('div', { className: 'studio-panel-control' }, [knob({   // the knob draws its own name: no second label outside it (issue 188)
    label, value: knobStart(params, name, context.definitions, min), min, max, unit, scale, size,
    step: precision.step, decimals: precision.display, disabled: context.readOnly, learn: learnOf(context, name),
    onPreview: (next) => { context.preview?.(name, next); onPreview?.(next); },
    onCommit: (next) => context.commit(name, `${Number(next.toFixed(precision.commit))}${unit ? ` ${unit}` : ''}`),
  })]);
}

/**
 * A transfer curve: input level on the x axis, output level on the y axis, both in dB from `floor` (-60 unless the range of the control goes lower) to 0, with tick values, axis names
 * and, when `reference` is given, a marked line at the value that the control sets (the threshold or the ceiling) (issue 189).
 * @param {string} label the name of the plot for assistive technology
 * @param {(input: number) => number} outputDb the output level for an input level
 * @param {{ floor?: number, reference?: () => ({ db: number, label: string } | null) }} [options]
 */
export function transferCurve(label, outputDb, { floor = -60, reference = null } = {}) {
  const span = -floor;
  const x = (db) => 20 + ((db - floor) / span) * 480;
  const y = (db) => 200 - ((db - floor) / span) * 180;
  return plot(label, (root) => {
    baseline(root, 190);
    root.append(svg('line', { x1: 20, x2: 500, y1: 200, y2: 20, class: 'studio-graph-zero', 'stroke-dasharray': '4 4' }));
    for (const db of [0, -10, -20, -40, -60, -80].filter((value) => value >= floor)) {
      root.append(svg('line', { x1: x(db), x2: x(db), y1: 20, y2: 200, class: 'studio-grid' }), svg('line', { x1: 20, x2: 500, y1: y(db), y2: y(db), class: 'studio-grid' }));
      const below = svg('text', { x: x(db), y: 212, class: 'studio-axis', 'text-anchor': 'middle' }); below.textContent = String(db);
      const left = svg('text', { x: 16, y: y(db) + 3, class: 'studio-axis', 'text-anchor': 'end' }); left.textContent = String(db);
      root.append(below, left);
    }

    const xName = svg('text', { x: 500, y: 218, class: 'studio-axis studio-axis-name', 'text-anchor': 'end' }); xName.textContent = 'input dB';
    const yName = svg('text', { x: 24, y: 14, class: 'studio-axis studio-axis-name' }); yName.textContent = 'output dB';
    root.append(xName, yName);
    const mark = svg('line', { class: 'studio-reference', 'stroke-dasharray': '3 3' });
    const markName = svg('text', { class: 'studio-axis studio-axis-name', 'text-anchor': 'start' });
    root.append(mark, markName);
    const line = svg('path', { class: 'studio-response-line' });
    const draw = () => {
      const d = [];
      for (let i = 0; i <= span; i++) {
        const input = floor + i;
        const out = Math.max(floor, Math.min(0, outputDb(input)));
        d.push(`${i ? 'L' : 'M'}${x(input)} ${y(out)}`);
      }

      line.setAttribute('d', d.join(' '));
      const ref = reference?.();
      const shown = ref && ref.db >= floor && ref.db <= 0;
      mark.style.display = markName.style.display = shown ? '' : 'none';
      if (shown) {
        mark.setAttribute('x1', String(x(ref.db))); mark.setAttribute('x2', String(x(ref.db))); mark.setAttribute('y1', '20'); mark.setAttribute('y2', '200');
        markName.setAttribute('x', String(Math.min(x(ref.db) + 4, 440))); markName.setAttribute('y', '32');
        markName.textContent = `${ref.label} ${Math.round(ref.db * 10) / 10} dB`;
      }
    };
    draw(); root.append(line); root.redraw = draw;
  });
}
