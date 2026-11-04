// Studio panels of the limiter, the pan and the width effects (plan X03).
import { el } from '../../ui.js';
import { baseline, control, controls, number, plot, svg, transferCurve } from './helpers.js';
import { registerStudioPanel } from './registry.js';

function limiterPanel(context) {
  const params = context.params;
  let ceiling = number(params, 'ceiling', -1);
  const graph = transferCurve('Limiter input and output level curve', (input) => Math.min(input, ceiling));
  const controlsRow = controls([
    control(context, params, 'ceiling', 'Ceiling', -24, 0, 'dB', 'linear', 112, (next) => { ceiling = next; graph.redraw(); }),
    control(context, params, 'release', 'Release', 10, 1000, 'ms', 'log'),
    control(context, params, 'lookahead', 'Look-ahead', 1, 20, 'ms'),
  ]);
  return el('section', { className: 'studio-effect-panel studio-limiter-panel', 'aria-label': 'Limiter controls' }, [graph, controlsRow, el('p', { className: 'note', textContent: 'Nothing passes the ceiling. The sound is delayed by the look-ahead inside the block and realigned, so the timing stays the same.' })]);
}

function panPanel(context) {
  const params = context.params;
  let pan = number(params, 'pan', 0);
  const graph = plot('Pan position', (root) => {
    baseline(root);
    const marker = svg('circle', { cy: 110, r: 14, class: 'studio-response-handle' });
    const draw = () => marker.setAttribute('cx', String(260 + (pan / 32) * 220));
    draw(); root.append(svg('text', { x: 20, y: 60, class: 'studio-graph-label' }), marker); root.redraw = draw;
  });
  return el('section', { className: 'studio-effect-panel studio-pan-panel', 'aria-label': 'Pan controls' }, [graph, controls([control(context, params, 'pan', 'Pan', -32, 32, '', 'linear', 112, (next) => { pan = next; graph.redraw(); })]), el('p', { className: 'note', textContent: '-32 is fully left, 32 fully right. A mono sound becomes stereo; a stereo sound is balanced. Type auto(1:-32, 5:32) or lfo(4, -20, 20) in the field to move it over the song.' })]);
}

function widthPanel(context) {
  const params = context.params;
  let width = number(params, 'width', 100);
  const graph = plot('Stereo width', (root) => {
    const left = svg('ellipse', { cx: 260, cy: 110, ry: 70, class: 'studio-room-shape' });
    const draw = () => left.setAttribute('rx', String(10 + (width / 200) * 200));
    draw(); root.append(left); root.redraw = draw;
  });
  return el('section', { className: 'studio-effect-panel studio-width-panel', 'aria-label': 'Width controls' }, [graph, controls([control(context, params, 'width', 'Width', 0, 200, '%', 'linear', 112, (next) => { width = next; graph.redraw(); })]), el('p', { className: 'note', textContent: '0 folds the sound to mono, 100 keeps the image, 200 doubles the side signal. A mono source stays mono.' })]);
}

registerStudioPanel('limiter', limiterPanel);
registerStudioPanel('pan', panPanel);
registerStudioPanel('width', widthPanel);
