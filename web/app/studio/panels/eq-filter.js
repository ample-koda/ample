import { biquadMagnitude, filterPasses, clamp, equalizerResponse, normalize, denormalize, spectrumBins } from '../../../lib/studio-math.js';
import { el } from '../../ui.js';
import { knob } from '../knob.js';
import { selector } from '../selector.js';
import { onPlayer, playerSpectrum } from '../../player.js';
import { registerStudioPanel } from './registry.js';

const svgNs = 'http://www.w3.org/2000/svg';
const number = (params, name, fallback) => { const value = Number.parseFloat(String(params[name] ?? '').replace(/[a-z ]/gi, '')); return Number.isFinite(value) ? value : fallback; };
const line = (label, control) => el('label', { className: 'studio-panel-control' }, [el('span', { textContent: label }), control]);
const commit = (context, name, value, unit) => context.commit(name, `${Number(value.toFixed(unit === 'dB' ? 1 : 0))}${unit ? ` ${unit}` : ''}`);
const commitBatch = (context, values) => {
  const changes = values.map(([name, value, unit]) => ({ name, value: `${Number(value.toFixed(unit === 'dB' ? 1 : 0))}${unit ? ` ${unit}` : ''}` }));
  if (context.commitMany) context.commitMany(changes);
  else changes.forEach(({ name, value }) => context.commit(name, value));
};
const svgElement = (tag, attrs = {}) => { const node = document.createElementNS(svgNs, tag); Object.entries(attrs).forEach(([key, value]) => node.setAttribute(key, String(value))); return node; };

function responseGraph(kind, params, context) {
  const graph = svgElement('svg', { viewBox: '0 0 760 240', class: 'studio-response-graph', role: 'img', 'aria-label': kind === 'eq' ? 'Equalizer frequency response, 20 Hz to 20 kHz' : 'Filter frequency response, 20 Hz to 20 kHz' });
  const xAt = (frequency) => 38 + normalize(frequency, 20, 20000, 'log') * 700;
  const yAt = (db) => 116 - clamp(db, -24, 24) * 3.5;
  [-18, -12, -6, 0, 6, 12, 18].forEach((db) => graph.append(svgElement('line', { x1: 38, x2: 738, y1: yAt(db), y2: yAt(db), class: db === 0 ? 'studio-graph-zero' : 'studio-graph-grid' })));
  [20, 100, 1000, 10000, 20000].forEach((frequency) => { const label = svgElement('text', { x: xAt(frequency), y: 232, 'text-anchor': 'middle', class: 'studio-graph-label' }); label.textContent = frequency >= 1000 ? `${frequency / 1000}k` : String(frequency); graph.append(label); });
  const low = number(params, 'low_hz', 250), high = number(params, 'high_hz', 3000), cutoff = number(params, 'cutoff', 1000);
  const type = params.type || 'lowpass';
  const liveParams = { ...params };
  const response = svgElement('path', { class: 'studio-response-line' });
  const drawResponse = () => {
    const values = [];
    for (let index = 0; index <= 140; index++) {
      const frequency = 20 * (1000 ** (index / 140));
      const db = kind === 'eq' ? equalizerResponse(frequency, liveParams) : biquadMagnitude(liveParams.type || type, frequency, number(liveParams, 'cutoff', cutoff)) * filterPasses(liveParams.slope);
      values.push(`${index ? 'L' : 'M'}${xAt(frequency).toFixed(2)} ${yAt(db).toFixed(2)}`);
    }
    response.setAttribute('d', values.join(' '));
  };
  if (kind === 'eq') {
    const spectrum = svgElement('path', { class: 'studio-spectrum-line', 'aria-label': 'Approximate input spectrum' });
    const liveSpectrum = svgElement('path', { class: 'studio-live-spectrum', 'aria-label': 'Live playback spectrum' });
    graph.append(spectrum);
    context.inputBuffer?.().then((buffer) => {
      if (!graph.isConnected) return;
      const bins = spectrumBins(buffer.getChannelData(0), buffer.sampleRate, 96);
      spectrum.setAttribute('d', bins.map((bin, index) => `${index ? 'L' : 'M'}${xAt(bin.frequency).toFixed(2)} ${yAt(bin.db).toFixed(2)}`).join(' '));
    }).catch(() => {});
    let lastReducedUpdate = 0;
    const unsubscribe = onPlayer((player) => {
      if (!graph.isConnected) { unsubscribe(); return; }
      if (!player.playing) { liveSpectrum.removeAttribute('d'); return; }
      if (matchMedia('(prefers-reduced-motion: reduce)').matches && player.time - lastReducedUpdate < 1) return;
      lastReducedUpdate = player.time;
      const bins = playerSpectrum(96);
      liveSpectrum.setAttribute('d', bins.map((bin, index) => `${index ? 'L' : 'M'}${xAt(bin.frequency).toFixed(2)} ${yAt(bin.db).toFixed(2)}`).join(' '));
      if (!liveSpectrum.isConnected) graph.append(liveSpectrum);
    });
    graph.dispose = unsubscribe;
  }
  drawResponse(); graph.append(response);
  if (kind === 'eq') {
    const handles = [
      { name: 'low', hz: low, gain: number(params, 'low', 0), label: 'Low band gain', min: -12, max: 12, scale: 'linear', unit: 'dB' },
      { name: 'mid', hz: Math.sqrt(low * high), gain: number(params, 'mid', 0), label: 'Mid band gain', min: -12, max: 12, scale: 'linear', unit: 'dB' },
      { name: 'high', hz: high, gain: number(params, 'high', 0), label: 'High band gain', min: -12, max: 12, scale: 'linear', unit: 'dB' },
    ];
    handles.forEach((item) => {
      const handle = svgElement('circle', { cx: xAt(item.hz), cy: yAt(item.gain), r: 9, class: 'studio-response-handle', tabindex: context.readOnly ? -1 : 0, role: 'slider', 'aria-label': item.label, 'aria-valuemin': item.min, 'aria-valuemax': item.max, 'aria-valuenow': item.gain });
      let startX = 0, startY = 0, active = false, nextHz = item.hz, nextGain = item.gain;
      handle.onpointerdown = (event) => { if (context.readOnly || event.button !== 0) return; active = true; startX = event.clientX; startY = event.clientY; nextHz = item.hz; nextGain = item.gain; handle.setPointerCapture(event.pointerId); document.body.classList.add('studio-dragging'); event.preventDefault(); };
      handle.onpointermove = (event) => {
        if (!active) return;
        const box = graph.getBoundingClientRect();
        const px = clamp((event.clientX - box.left) / box.width * 760, 38, 738);
        if (item.name === 'low') nextHz = denormalize((px - 38) / 700, 40, 2000, 'log');
        else if (item.name === 'high') nextHz = denormalize((px - 38) / 700, 1000, 12000, 'log');
        nextGain = clamp(item.gain - (event.clientY - startY) / Math.max(1, box.height) * 48, -12, 12);
        liveParams[item.name] = nextGain;
        context.preview?.(item.name, nextGain);
        if (item.name === 'low') liveParams.low_hz = nextHz;
        if (item.name === 'high') liveParams.high_hz = nextHz;
        if (item.name === 'low') context.preview?.('low_hz', nextHz);
        if (item.name === 'high') context.preview?.('high_hz', nextHz);
        drawResponse();
        handle.setAttribute('cx', String(item.name === 'mid' ? xAt(Math.sqrt(low * high)) : xAt(nextHz)));
        handle.setAttribute('cy', String(yAt(nextGain)));
      };
      handle.onpointerup = () => {
        if (!active) return;
        active = false; document.body.classList.remove('studio-dragging');
        const changes = [[item.name, nextGain, 'dB']];
        if (item.name === 'low') changes.push(['low_hz', nextHz, 'Hz']);
        if (item.name === 'high') changes.push(['high_hz', nextHz, 'Hz']);
        commitBatch(context, changes);
      };
      handle.onpointercancel = () => { active = false; document.body.classList.remove('studio-dragging'); };
      handle.onkeydown = (event) => {
        const delta = event.shiftKey ? 10 : 1;
        if (event.key === 'ArrowUp' || event.key === 'ArrowRight') nextGain = clamp(item.gain + delta, -12, 12);
        else if (event.key === 'ArrowDown' || event.key === 'ArrowLeft') nextGain = clamp(item.gain - delta, -12, 12);
        else return;
        event.preventDefault(); commit(context, item.name, nextGain, 'dB');
      };
      graph.append(handle);
    });
  } else {
    const marker = svgElement('line', { x1: xAt(cutoff), x2: xAt(cutoff), y1: 24, y2: 210, class: 'studio-response-marker' });
    graph.append(marker);
  }
  graph.updateParameter = (name, value) => {
    liveParams[name] = value; drawResponse();
    if (kind === 'filter') { const marker = graph.querySelector('.studio-response-marker'); marker.setAttribute('x1', String(xAt(number(liveParams, 'cutoff', cutoff)))); marker.setAttribute('x2', marker.getAttribute('x1')); }
    else graph.querySelectorAll('.studio-response-handle').forEach((handle, index) => { const name = ['low', 'mid', 'high'][index], lowHz = number(liveParams, 'low_hz', low), highHz = number(liveParams, 'high_hz', high); handle.setAttribute('cx', String(xAt(index === 0 ? lowHz : index === 2 ? highHz : Math.sqrt(lowHz * highHz)))); handle.setAttribute('cy', String(yAt(number(liveParams, name, 0)))); });
  };
  return graph;
}

function eqPanel(context) {
  const params = context.params;
  const root = el('section', { className: 'studio-effect-panel studio-eq-panel', 'aria-label': 'Equalizer controls' });
  const graph = responseGraph('eq', params, context);
  root.dispose = () => graph.dispose?.();
  root.append(graph);
  const controls = el('div', { className: 'studio-eq-controls' });
  [['low', 'Low', -12, 12, 'dB'], ['mid', 'Mid', -12, 12, 'dB'], ['high', 'High', -12, 12, 'dB'], ['low_hz', 'Low corner', 40, 2000, 'Hz'], ['high_hz', 'High corner', 1000, 12000, 'Hz']].forEach(([name, label, min, max, unit]) => {
    const value = number(params, name, name === 'low_hz' ? 250 : name === 'high_hz' ? 3000 : 0);
    controls.append(line(label, knob({ learn: context.block ? { block: context.block, name } : null, label, value, min, max, unit, scale: unit === 'Hz' ? 'log' : 'linear', disabled: context.readOnly, onPreview: (next) => { graph.updateParameter(name, next); context.preview?.(name, next); }, onCommit: (next) => commit(context, name, next, unit) })));
  });
  root.append(controls, el('p', { className: 'note', textContent: context.inputBuffer ? 'Input spectrum is an approximate estimate from the built input; the response curve is a model.' : 'Build the upstream input to show its approximate spectrum. The response curve is a model.' }));
  return root;
}

function filterPanel(context) {
  const params = context.params;
  const root = el('section', { className: 'studio-effect-panel studio-filter-panel', 'aria-label': 'Filter controls' });
  const cutoff = number(params, 'cutoff', 1000);
  const graph = responseGraph('filter', params, context);
  root.append(graph, el('div', { className: 'studio-filter-controls' }, [
    knob({ learn: context.block ? { block: context.block, name: 'cutoff' } : null, label: 'Cutoff', value: cutoff, min: 20, max: 20000, unit: 'Hz', scale: 'log', size: 112, disabled: context.readOnly, onPreview: (next) => { graph.updateParameter('cutoff', next); context.preview?.('cutoff', next); }, onCommit: (next) => commit(context, 'cutoff', next, 'Hz') }),
    line('Slope', selector({ label: 'Filter slope', value: String(params.slope || '12'), disabled: context.readOnly, choices: ['12', '24', '36', '48'].map((value) => ({ value, label: `${value} dB/oct` })), onCommit: (next) => { graph.updateParameter('slope', next); context.commit('slope', next); } })),
    line('Type', selector({ label: 'Filter type', value: params.type || 'lowpass', disabled: context.readOnly, choices: [{ value: 'lowpass', label: 'Low pass' }, { value: 'highpass', label: 'High pass' }, { value: 'bandpass', label: 'Band pass' }, { value: 'notch', label: 'Notch' }], onCommit: (next) => context.commit('type', next) })),
  ]));
  return root;
}

registerStudioPanel('eq', eqPanel);
registerStudioPanel('filter', filterPanel);
