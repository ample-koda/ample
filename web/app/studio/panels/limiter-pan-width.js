// Studio panels of the limiter, the pan and the width effects (plan X03).
import { infoNote } from './info.js';
import { el } from '../../ui.js';
import { control, controls, number, transferCurve } from './helpers.js';
import { registerStudioPanel } from './registry.js';

/** One horizontal slider with its value and named marks under it (Left, Centre, Right; mono, as recorded, wide): the compact editor of a one-parameter effect. */
function strip(context, params, { name, label, min, max, step, unit, fallback, format, marks }) {
  const value = number(params, name, fallback);
  const input = el('input', { type: 'range', min: String(min), max: String(max), step: String(step), value: String(value), 'aria-label': label, className: 'studio-strip-range', disabled: context.readOnly });
  const out = el('output', { className: 'studio-strip-value', textContent: format(value), 'aria-live': 'polite' });
  input.oninput = () => { out.textContent = format(Number(input.value)); context.preview?.(name, Number(input.value)); };
  input.onchange = () => context.commit(name, `${Number(input.value)}${unit ? ` ${unit}` : ''}`);
  const markRow = el('div', { className: 'studio-strip-marks', 'aria-hidden': 'true' }, marks.map((mark) => el('span', { textContent: mark.label, style: `left:${((mark.at - min) / (max - min)) * 100}%` })));
  return el('div', { className: 'studio-strip' }, [el('span', { className: 'studio-strip-label', textContent: label }), el('div', { className: 'studio-strip-track' }, [input, markRow]), out]);
}

function limiterPanel(context) {
  const params = context.params;
  let ceiling = number(params, 'ceiling', -1);
  const graph = transferCurve('Limiter input and output level curve', (input) => Math.min(input, ceiling), { reference: () => ({ db: ceiling, label: 'ceiling' }) });
  const controlsRow = controls([
    control(context, params, 'ceiling', 'Ceiling', -24, 0, 'dB', 'linear', 82, (next) => { ceiling = next; graph.redraw(); }),
    control(context, params, 'release', 'Release', 10, 1000, 'ms', 'log'),
    control(context, params, 'lookahead', 'Look-ahead', 1, 20, 'ms'),
  ]);
  return el('section', { className: 'studio-effect-panel studio-limiter-panel', 'aria-label': 'Limiter controls' }, [graph, controlsRow, infoNote('Nothing passes the ceiling. The sound is delayed by the look-ahead inside the block and realigned, so the timing stays the same.')]);
}

function panPanel(context) {
  const params = context.params;
  // one horizontal control with Left, Centre and Right at its marks: a compact edit, no large plot (issue 185)
  const side = (value) => (value < 0 ? `${-value} left` : value > 0 ? `${value} right` : 'centre');
  return el('section', { className: 'studio-effect-panel studio-pan-panel', 'aria-label': 'Pan controls' }, [strip(context, params, { name: 'pan', label: 'Pan', min: -32, max: 32, step: 1, unit: '', fallback: 0, format: side, marks: [{ at: -32, label: 'L' }, { at: 0, label: 'C' }, { at: 32, label: 'R' }] }), el('p', { className: 'note', textContent: '-32 is fully left, 32 fully right. A mono sound becomes stereo; a stereo sound is balanced. Type auto(1:-32, 5:32) or lfo(4, -20, 20) in the field to move it over the song.' })]);
}

function widthPanel(context) {
  const params = context.params;
  // 0 % is mono, 100 % is the stereo as recorded, 200 % is twice as wide: a compact control with those references (issue 185)
  return el('section', { className: 'studio-effect-panel studio-width-panel', 'aria-label': 'Width controls' }, [strip(context, params, { name: 'width', label: 'Width', min: 0, max: 200, step: 1, unit: '%', fallback: 100, format: (value) => `${value} %`, marks: [{ at: 0, label: 'mono' }, { at: 100, label: 'as recorded' }, { at: 200, label: 'wide' }] }), el('p', { className: 'note', textContent: '0 folds the sound to mono, 100 keeps the image, 200 doubles the side signal. A mono source stays mono.' })]);
}

registerStudioPanel('limiter', limiterPanel);
registerStudioPanel('pan', panPanel);
registerStudioPanel('width', widthPanel);
