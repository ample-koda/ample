// Studio panels of the voice cleanup and colour effects: gate, de-esser, chorus, flanger, phaser and bit crusher (plan X04).
import { el } from '../../ui.js';
import { baseline, control, controls, number, plot, svg, transferCurve } from './helpers.js';
import { registerStudioPanel } from './registry.js';

function gatePanel(context) {
  const params = context.params;
  let threshold = number(params, 'threshold', -50);
  const graph = transferCurve('Gate input and output level curve', (input) => (input < threshold ? -60 : input));
  return el('section', { className: 'studio-effect-panel studio-gate-panel', 'aria-label': 'Gate controls' }, [graph, controls([
    control(context, params, 'threshold', 'Threshold', -80, 0, 'dB', 'linear', 112, (next) => { threshold = next; graph.redraw(); }),
    control(context, params, 'attack', 'Attack', 1, 200, 'ms', 'log'),
    control(context, params, 'hold', 'Hold', 0, 1000, 'ms'),
    control(context, params, 'release', 'Release', 5, 2000, 'ms', 'log'),
  ]), el('p', { className: 'note', textContent: 'Below the threshold the sound fades out after the hold time; use it on a recorded voice to remove the noise between phrases.' })]);
}

function deesserPanel(context) {
  const params = context.params;
  let frequency = number(params, 'frequency', 6000);
  const graph = plot('De-esser band', (root) => {
    baseline(root, 200);
    const band = svg('rect', { y: 30, height: 170, class: 'studio-room-shape' });
    const label = svg('text', { y: 24, class: 'studio-graph-label' });
    const draw = () => { const x = 20 + (Math.log(frequency / 2000) / Math.log(12000 / 2000)) * 480; band.setAttribute('x', String(x)); band.setAttribute('width', String(Math.max(4, 500 - x))); label.setAttribute('x', String(x)); label.textContent = `${Math.round(frequency)} Hz and up`; };
    draw(); root.append(band, label); root.redraw = draw;
  });
  return el('section', { className: 'studio-effect-panel studio-deesser-panel', 'aria-label': 'De-esser controls' }, [graph, controls([
    control(context, params, 'frequency', 'Frequency', 2000, 12000, 'Hz', 'log', 112, (next) => { frequency = next; graph.redraw(); }),
    control(context, params, 'threshold', 'Threshold', -60, 0, 'dB'),
    control(context, params, 'ratio', 'Ratio', 1, 20, ''),
  ]), el('p', { className: 'note', textContent: 'Only the sound above the frequency is turned down when it gets loud, so s sounds soften and the rest stays as it is.' })]);
}

function lfoPlot(label, getPeriod, getDepth) {
  return plot(label, (root) => {
    baseline(root);
    const line = svg('path', { class: 'studio-response-line' });
    const draw = () => {
      const period = Math.max(100, getPeriod()), cycles = Math.min(6, 4000 / period), d = [];
      for (let i = 0; i <= 120; i++) { const t = (i / 120) * cycles; const tri = Math.abs(((t % 1) * 2) - 1); d.push(`${i ? 'L' : 'M'}${20 + (i / 120) * 480} ${110 - (tri - 0.5) * 2 * getDepth() * 90}`); }
      line.setAttribute('d', d.join(' '));
    };
    draw(); root.append(line); root.redraw = draw;
  });
}

function modulationPanel(kind, defaults) {
  return (context) => {
    const params = context.params;
    let period = number(params, 'period', defaults.period), depth = number(params, 'depth', defaults.depth ?? 1);
    const graph = lfoPlot(`${kind} movement`, () => period, () => Math.min(1, depth / (defaults.depthMax ?? 1)));
    const list = [control(context, params, 'period', 'Period', 100, 10000, 'ms', 'log', 112, (next) => { period = next; graph.redraw(); })];
    if (kind !== 'phaser') list.push(control(context, params, 'depth', 'Depth', 1, defaults.depthMax, 'ms', 'linear', 82, (next) => { depth = next; graph.redraw(); }));
    if (kind === 'phaser') list.push(control(context, params, 'stages', 'Stages', 2, 8, ''));
    if (kind === 'flanger') list.push(control(context, params, 'feedback', 'Feedback', 0, 90, '%'));
    list.push(control(context, params, 'mix', 'Mix', 0, 100, '%'));
    return el('section', { className: `studio-effect-panel studio-${kind}-panel`, 'aria-label': `${kind} controls` }, [graph, controls(list), el('p', { className: 'note', textContent: 'The curve shows how the moving part travels. The mix can be automated: type auto(1:0, 5:60) or lfo(4, 20, 80) in its field.' })]);
  };
}

function bitcrusherPanel(context) {
  const params = context.params;
  let bits = number(params, 'bits', 8), hold = number(params, 'downsample', 1);
  const graph = plot('Bit crusher staircase', (root) => {
    baseline(root);
    const line = svg('path', { class: 'studio-response-line' });
    const draw = () => {
      const steps = 2 ** Math.min(6, Math.max(2, bits - 2)), d = [];
      for (let i = 0; i <= 160; i++) {
        const held = Math.floor(i / Math.max(1, Math.min(16, hold)) ) * Math.max(1, Math.min(16, hold));
        const x = Math.sin((held / 160) * Math.PI * 4), q = Math.round(x * steps) / steps;
        d.push(`${i ? 'L' : 'M'}${20 + (i / 160) * 480} ${110 - q * 90}`);
      }
      line.setAttribute('d', d.join(' '));
    };
    draw(); root.append(line); root.redraw = draw;
  });
  return el('section', { className: 'studio-effect-panel studio-bitcrusher-panel', 'aria-label': 'Bit crusher controls' }, [graph, controls([
    control(context, params, 'bits', 'Bits', 2, 16, '', 'linear', 112, (next) => { bits = next; graph.redraw(); }),
    control(context, params, 'downsample', 'Hold', 1, 64, '', 'log', 82, (next) => { hold = next; graph.redraw(); }),
    control(context, params, 'mix', 'Mix', 0, 100, '%'),
  ])]);
}

registerStudioPanel('gate', gatePanel);
registerStudioPanel('deesser', deesserPanel);
registerStudioPanel('chorus', modulationPanel('chorus', { period: 1500, depth: 5, depthMax: 15 }));
registerStudioPanel('flanger', modulationPanel('flanger', { period: 2000, depth: 2, depthMax: 3 }));
registerStudioPanel('phaser', modulationPanel('phaser', { period: 2500 }));
registerStudioPanel('bitcrusher', bitcrusherPanel);
