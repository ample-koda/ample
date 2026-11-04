import { bandOf, clamp, normalize, denormalize, peqResponse } from '../../../lib/studio-math.js';
import { el } from '../../ui.js';
import { knob } from '../knob.js';
import { selector } from '../selector.js';
import { registerStudioPanel } from './registry.js';

// The parametric EQ (block peq): six bands, each with a type, a frequency, a gain and a Q. The graph shows the sum of the six bands and has one draggable handle per band
// (left and right: frequency, up and down: gain); below it every band has its own type, frequency, gain and Q controls.
const svgNs = 'http://www.w3.org/2000/svg';
const BANDS = [1, 2, 3, 4, 5, 6];
const TYPES = [{ value: 'peak', label: 'Peak' }, { value: 'lowshelf', label: 'Low shelf' }, { value: 'highshelf', label: 'High shelf' }, { value: 'notch', label: 'Notch' }, { value: 'lowcut', label: 'Low cut' }, { value: 'highcut', label: 'High cut' }];
const isCut = (type) => type === 'lowcut' || type === 'highcut';
const svgElement = (tag, attrs = {}) => { const node = document.createElementNS(svgNs, tag); Object.entries(attrs).forEach(([key, value]) => node.setAttribute(key, String(value))); return node; };
const text = (value, unit) => `${Number(value.toFixed(unit === 'Hz' ? 0 : 2))}${unit ? ` ${unit}` : ''}`;

function graphOf(context, live) {
  const graph = svgElement('svg', { viewBox: '0 0 760 240', class: 'studio-response-graph', role: 'img', 'aria-label': 'Parametric equalizer frequency response, 20 Hz to 20 kHz' });
  const xAt = (hz) => 38 + normalize(hz, 20, 20000, 'log') * 700;
  const yAt = (db) => 116 - clamp(db, -24, 24) * 3.5;
  [-18, -12, -6, 0, 6, 12, 18].forEach((db) => graph.append(svgElement('line', { x1: 38, x2: 738, y1: yAt(db), y2: yAt(db), class: db === 0 ? 'studio-graph-zero' : 'studio-graph-grid' })));
  [20, 100, 1000, 10000, 20000].forEach((hz) => {
    const label = svgElement('text', { x: xAt(hz), y: 232, 'text-anchor': 'middle', class: 'studio-graph-label' });
    label.textContent = hz >= 1000 ? `${hz / 1000} kHz` : `${hz} Hz`;
    graph.append(label);
  });
  const response = svgElement('path', { class: 'studio-response-line' });
  const draw = () => {
    const values = [];
    for (let i = 0; i <= 140; i++) {
      const hz = 20 * (1000 ** (i / 140));
      values.push(`${i ? 'L' : 'M'}${xAt(hz).toFixed(2)} ${yAt(peqResponse(hz, live)).toFixed(2)}`);
    }
    response.setAttribute('d', values.join(' '));
  };
  draw();
  graph.append(response);
  const places = [];
  for (const index of BANDS) {
    const start = bandOf(live, index);
    const handle = svgElement('circle', { cx: xAt(start.hz), cy: yAt(start.gain), r: 9, class: 'studio-response-handle', tabindex: context.readOnly ? -1 : 0, role: 'slider', 'aria-label': `Band ${index}: frequency and gain` });
    const place = () => {
      const b = bandOf(live, index);
      handle.setAttribute('cx', String(xAt(b.hz)));
      handle.setAttribute('cy', String(yAt(b.gain)));
      handle.setAttribute('aria-valuetext', `${Math.round(b.hz)} Hz, ${b.gain.toFixed(1)} dB`);
    };
    let active = false;
    let next = start;
    handle.onpointerdown = (event) => {
      if (context.readOnly || event.button !== 0) return;
      active = true;
      handle.setPointerCapture?.(event.pointerId);
      document.body.classList.add('studio-dragging');
    };
    handle.onpointermove = (event) => {
      if (!active) return;
      const box = graph.getBoundingClientRect();
      const px = clamp((event.clientX - box.left) / box.width * 760, 38, 738);
      const py = clamp((event.clientY - box.top) / box.height * 240, 0, 240);
      const before = bandOf(live, index);
      // a low cut or high cut has no gain: the handle only moves sideways
      next = { ...before, hz: denormalize((px - 38) / 700, 20, 20000, 'log'), gain: isCut(before.type) ? before.gain : clamp((116 - py) / 3.5, -24, 24) };
      live[`band${index}_hz`] = next.hz;
      context.preview?.(`band${index}_hz`, next.hz);
      if (!isCut(before.type)) { live[`band${index}_gain`] = next.gain; context.preview?.(`band${index}_gain`, next.gain); }
      draw();
      place();
    };
    handle.onpointerup = () => {
      if (!active) return;
      active = false;
      document.body.classList.remove('studio-dragging');
      const changes = [{ name: `band${index}_hz`, value: text(next.hz, 'Hz') }, ...(isCut(next.type) ? [] : [{ name: `band${index}_gain`, value: text(next.gain, 'dB') }])];
      if (context.commitMany) context.commitMany(changes);
      else changes.forEach((c) => context.commit(c.name, c.value));
    };
    handle.onpointercancel = () => { active = false; document.body.classList.remove('studio-dragging'); };
    handle.onkeydown = (event) => {
      const step = event.shiftKey ? 3 : 0.5;
      const b = bandOf(live, index);
      if (event.key === 'ArrowUp') live[`band${index}_gain`] = clamp(b.gain + step, -24, 24);
      else if (event.key === 'ArrowDown') live[`band${index}_gain`] = clamp(b.gain - step, -24, 24);
      else if (event.key === 'ArrowRight') live[`band${index}_hz`] = clamp(b.hz * (event.shiftKey ? 1.26 : 1.06), 20, 20000);
      else if (event.key === 'ArrowLeft') live[`band${index}_hz`] = clamp(b.hz / (event.shiftKey ? 1.26 : 1.06), 20, 20000);
      else return;
      event.preventDefault();
      draw();
      place();
      const now = bandOf(live, index);
      const changes = [{ name: `band${index}_gain`, value: text(now.gain, 'dB') }, { name: `band${index}_hz`, value: text(now.hz, 'Hz') }];
      if (context.commitMany) context.commitMany(changes);
      else changes.forEach((c) => context.commit(c.name, c.value));
    };
    place();
    places.push(place);
    graph.append(handle);
  }
  graph.updateParameter = (name, value) => { live[name] = value; draw(); places.forEach((place) => place()); };
  return graph;
}

/** The on/off switch of one band (parameter bandN_enabled): an off band keeps its settings but is left out of the sound and of the curve. */
function enableToggle(context, graph, index, enabled) {
  const button = el('button', { type: 'button', className: enabled ? 'on' : '', textContent: enabled ? 'On' : 'Off', title: `Switch band ${index} on or off`, 'aria-pressed': String(enabled), disabled: Boolean(context.readOnly) });
  button.setAttribute('aria-label', `Band ${index} enabled`);
  button.onclick = () => {
    const next = button.getAttribute('aria-pressed') !== 'true';
    button.setAttribute('aria-pressed', String(next)); button.classList.toggle('on', next); button.textContent = next ? 'On' : 'Off';
    const value = next ? 'on' : 'off';
    graph.updateParameter(`band${index}_enabled`, value); context.preview?.(`band${index}_enabled`, value); context.commit(`band${index}_enabled`, value);
  };
  return button;
}

function peqPanel(context) {
  const live = { ...context.params };
  const root = el('section', { className: 'studio-effect-panel studio-peq-panel', 'aria-label': 'Parametric equalizer controls' });
  const graph = graphOf(context, live);
  root.append(graph);
  const grid = el('div', { className: 'studio-peq-bands' });
  for (const index of BANDS) {
    const b = bandOf(context.params, index);
    const change = (name, value) => { graph.updateParameter(name, value); context.preview?.(name, value); };
    grid.append(el('div', { className: 'studio-peq-band' }, [
      el('strong', { textContent: `Band ${index}` }),
      enableToggle(context, graph, index, b.enabled),
      selector({ label: `Band ${index} type`, value: b.type, disabled: context.readOnly, choices: TYPES, onCommit: (value) => { graph.updateParameter(`band${index}_type`, value); context.commit(`band${index}_type`, value); } }),
      knob({ label: 'Frequency', value: b.hz, min: 20, max: 20000, unit: 'Hz', scale: 'log', disabled: context.readOnly, onPreview: (v) => change(`band${index}_hz`, v), onCommit: (v) => context.commit(`band${index}_hz`, text(v, 'Hz')) }),
      knob({ label: 'Gain', value: b.gain, min: -24, max: 24, unit: 'dB', scale: 'linear', disabled: context.readOnly, onPreview: (v) => change(`band${index}_gain`, v), onCommit: (v) => context.commit(`band${index}_gain`, text(v, 'dB')) }),
      knob({ label: 'Q', value: b.q, min: 0.1, max: 18, unit: '', scale: 'log', disabled: context.readOnly, onPreview: (v) => change(`band${index}_q`, v), onCommit: (v) => context.commit(`band${index}_q`, text(v, '')) }),
    ]));
  }
  root.append(grid, el('p', { className: 'note', textContent: 'Drag a point to move a band; the curve is a model of the six bands together. With every gain at 0 dB the sound passes unchanged.' }));
  return root;
}

registerStudioPanel('peq', peqPanel);
