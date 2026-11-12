import { clamp, compressorOutput, duckGain } from '../../../lib/studio-math.js';
import { el } from '../../ui.js';
import { knob } from '../knob.js';
import { registerStudioPanel } from './registry.js';
import { compareAudioBuffers, envelopePath } from '../preview/analysis.js';
import { onPlayer } from '../../player.js';
import { barBeats } from '../../../lib/meter.mjs';

const ns = 'http://www.w3.org/2000/svg';
const valueOf = (params, key, fallback) => { const value = Number.parseFloat(String(params[key] ?? '').replace(/[a-z ]/gi, '')); return Number.isFinite(value) ? value : fallback; };
const control = (label, widget) => el('label', { className: 'studio-panel-control' }, [el('span', { textContent: label }), widget]);
const write = (context, name, value, unit = '') => context.commit(name, `${typeof value === 'number' ? Number(value.toFixed(unit === 'dB' ? 1 : 0)) : value}${unit ? ` ${unit}` : ''}`);
const svg = (tag, attributes) => { const node = document.createElementNS(ns, tag); Object.entries(attributes).forEach(([name, value]) => node.setAttribute(name, String(value))); return node; };

function compressorPanel(context) {
  const params = context.params;
  const threshold = valueOf(params, 'threshold', -18), ratio = valueOf(params, 'ratio', 4);
  const graph = svg('svg', { viewBox: '0 0 520 320', class: 'studio-dynamics-curve', role: 'img', 'aria-label': 'Compressor input and output transfer curve' });
  const x = (db) => 44 + (db + 60) / 60 * 430, y = (db) => 276 - (db + 60) / 60 * 248;
  [-60, -45, -30, -15, 0].forEach((db) => { graph.append(svg('line', { x1: x(db), x2: x(db), y1: 28, y2: 276, class: 'studio-graph-grid' }), svg('line', { x1: 44, x2: 474, y1: y(db), y2: y(db), class: 'studio-graph-grid' })); });
  let liveThreshold = threshold, liveRatio = ratio;
  const response = svg('path', { class: 'studio-response-line' });
  const drawResponse = () => {
    const points = [];
    for (let db = -60; db <= 0; db += 1) points.push(`${db === -60 ? 'M' : 'L'}${x(db)} ${y(compressorOutput(db, liveThreshold, liveRatio))}`);
    response.setAttribute('d', points.join(' '));
  };
  drawResponse(); graph.append(response);
  const thresholdHandle = svg('circle', { cx: x(threshold), cy: y(threshold), r: 9, class: 'studio-response-handle', tabindex: context.readOnly ? -1 : 0, role: 'slider', 'aria-label': 'Threshold', 'aria-valuemin': -60, 'aria-valuemax': 0, 'aria-valuenow': threshold });
  let dragging = false, nextThreshold = threshold;
  thresholdHandle.onpointerdown = (event) => { if (context.readOnly) return; dragging = true; thresholdHandle.setPointerCapture(event.pointerId); event.preventDefault(); };
  thresholdHandle.onpointermove = (event) => { if (!dragging) return; const rect = graph.getBoundingClientRect(); nextThreshold = clamp(((event.clientX - rect.left) / rect.width * 520 - 44) / 430 * 60 - 60, -60, 0); liveThreshold = nextThreshold; thresholdHandle.setAttribute('cx', String(x(nextThreshold))); thresholdHandle.setAttribute('cy', String(y(nextThreshold))); thresholdHandle.setAttribute('aria-valuenow', String(nextThreshold)); ratioHandle.setAttribute('cy', String(y(compressorOutput(0, liveThreshold, liveRatio)))); drawResponse(); };
  thresholdHandle.onpointerup = () => { if (dragging) { dragging = false; thresholdHandle.setAttribute('aria-valuenow', String(nextThreshold)); write(context, 'threshold', nextThreshold, 'dB'); } };
  const ratioOutput = compressorOutput(0, threshold, ratio);
  const ratioHandle = svg('circle', { cx: x(0), cy: y(ratioOutput), r: 9, class: 'studio-response-handle', tabindex: context.readOnly ? -1 : 0, role: 'slider', 'aria-label': 'Ratio', 'aria-valuemin': 1, 'aria-valuemax': 20, 'aria-valuenow': ratio });
  let ratioDrag = false, nextRatio = ratio;
  ratioHandle.onpointerdown = (event) => { if (context.readOnly) return; ratioDrag = true; ratioHandle.setPointerCapture(event.pointerId); event.preventDefault(); };
  ratioHandle.onpointermove = (event) => {
    if (!ratioDrag) return;
    const rect = graph.getBoundingClientRect(), viewY = (event.clientY - rect.top) / rect.height * 320, output = clamp((276 - viewY) / 248 * 60 - 60, liveThreshold, 0);
    nextRatio = clamp((0 - liveThreshold) / Math.max(0.001, output - liveThreshold), 1, 20);
    liveRatio = nextRatio; drawResponse();
    ratioHandle.setAttribute('aria-valuenow', String(nextRatio));
    ratioHandle.setAttribute('cy', String(y(compressorOutput(0, threshold, nextRatio))));
  };
  ratioHandle.onpointerup = () => { if (ratioDrag) { ratioDrag = false; ratioHandle.setAttribute('aria-valuenow', String(nextRatio)); write(context, 'ratio', nextRatio); } };
  thresholdHandle.onkeydown = (event) => {
    if (!['ArrowLeft', 'ArrowRight', 'ArrowDown', 'ArrowUp'].includes(event.key)) return;
    event.preventDefault(); nextThreshold = clamp(threshold + (event.key === 'ArrowRight' || event.key === 'ArrowUp' ? 1 : -1), -60, 0);
    liveThreshold = nextThreshold; thresholdHandle.setAttribute('cx', String(x(nextThreshold))); thresholdHandle.setAttribute('cy', String(y(nextThreshold))); ratioHandle.setAttribute('cy', String(y(compressorOutput(0, liveThreshold, liveRatio)))); drawResponse(); write(context, 'threshold', nextThreshold, 'dB');
  };
  ratioHandle.onkeydown = (event) => {
    if (!['ArrowLeft', 'ArrowRight', 'ArrowDown', 'ArrowUp'].includes(event.key)) return;
    event.preventDefault(); nextRatio = clamp(ratio + (event.key === 'ArrowRight' || event.key === 'ArrowUp' ? 0.5 : -0.5), 1, 20);
    liveRatio = nextRatio; ratioHandle.setAttribute('cy', String(y(compressorOutput(0, liveThreshold, liveRatio)))); drawResponse(); write(context, 'ratio', nextRatio);
  };
  graph.append(thresholdHandle, ratioHandle);
  const controls = el('div', { className: 'studio-eq-controls' });
  [['threshold', 'Threshold', -60, 0, 'dB'], ['ratio', 'Ratio', 1, 20, ''], ['attack', 'Attack', 1, 500, 'ms'], ['release', 'Release', 5, 2000, 'ms'], ['makeup', 'Makeup', 0, 12, 'dB'], ['keyFilter', 'Key filter', 0, 8000, 'Hz']].forEach(([name, label, min, max, unit]) => controls.append(control(label, knob({ learn: context.block ? { block: context.block, name } : null, label, value: valueOf(params, name, name === 'threshold' ? -18 : name === 'ratio' ? 4 : name === 'attack' ? 10 : name === 'release' ? 120 : 0), min, max, unit, scale: name === 'release' || name === 'attack' ? 'log' : 'linear', disabled: context.readOnly, onPreview: (next) => context.preview?.(name, next), onCommit: (next) => write(context, name, next, unit) }))));
  const analysis = el('section', { className: 'studio-compressor-analysis', 'aria-label': 'Compressor input, output and gain reduction analysis' });
  const analyze = el('button', { type: 'button', textContent: 'Analyze exact preview', disabled: !context.renderedBuffer, title: context.renderedBuffer ? 'Render this bounded preview through the integer effect engine' : 'Build the upstream input first' });
  const analysisStatus = el('p', { className: 'note', textContent: context.renderedBuffer ? 'Input/output waveform and gain reduction appear after an exact preview analysis.' : 'Build the upstream input to enable exact preview analysis.' });
  const waveform = svg('svg', { viewBox: '0 0 640 180', class: 'studio-compressor-waveform', role: 'img', 'aria-label': 'Compressor input and output waveform envelope' });
  analyze.onclick = async () => {
    analyze.disabled = true; analysisStatus.textContent = 'Rendering the bounded exact preview…';
    try {
      const [input, output] = await Promise.all([context.inputBuffer?.(), context.renderedBuffer(context.params)]);
      if (!input || !output) throw new Error('Build the upstream audio input first.');
      const result = compareAudioBuffers(input.getChannelData(0), output.getChannelData(0));
      waveform.replaceChildren(svg('line', { x1: 0, x2: 640, y1: 90, y2: 90, class: 'studio-graph-zero' }), svg('path', { d: envelopePath(result.input, 90, 76, 640), class: 'studio-response-line' }), svg('path', { d: envelopePath(result.output, 90, 76, 640), class: 'studio-analysis-output' }));
      const reductions = result.gainDb.map((gain) => Math.max(0, -gain)), peak = Math.max(...reductions), average = reductions.reduce((sum, item) => sum + item, 0) / reductions.length;
      analysisStatus.textContent = `Exact engine analysis · peak reduction ${peak.toFixed(1)} dB · average ${average.toFixed(1)} dB. Blue: input · violet: output.`;
    } catch (error) { analysisStatus.textContent = error.message; }
    finally { analyze.disabled = !context.renderedBuffer; }
  };
  analysis.append(el('div', { className: 'studio-analysis-heading' }, [el('strong', { textContent: 'INPUT / OUTPUT · GAIN REDUCTION' }), analyze]), waveform, analysisStatus);
  return el('section', { className: 'studio-effect-panel studio-compressor-panel', 'aria-label': 'Compressor controls' }, [graph, controls, analysis, el('p', { className: 'note', textContent: 'Transfer curve is a display model. Preview analysis measures the exact integer renderer output over a bounded audio range.' }), el('p', { className: 'note', textContent: 'Sidechain: connect a sound to the key port of this block and that sound, not the input, drives the reduction (for example the kick ducking a bass). Key filter keeps only the key above that frequency; 0 is off. The threshold can be automated: type auto(1:-30, 9:-18) in the source.' })]);
}

function duckPanel(context) {
  const params = context.params;
  let pattern = String(params.pattern || 'x...x...x...x...').padEnd(16, '.').slice(0, 16).replace(/[^x.]/g, '.');
  const depth = valueOf(params, 'depth', 10), release = valueOf(params, 'release', 160);
  const steps = el('div', { className: 'studio-duck-steps', role: 'group', 'aria-label': 'Sixteen step duck pattern' });
  let painting = false, paintValue = 'x', suppressClick = false;
  const buttons = [];
  const setStep = (index, value) => { pattern = `${pattern.slice(0, index)}${value}${pattern.slice(index + 1)}`; buttons[index].textContent = value === 'x' ? '●' : '·'; buttons[index].setAttribute('aria-pressed', String(value === 'x')); };
  for (let index = 0; index < 16; index++) {
    const button = el('button', { type: 'button', className: 'studio-duck-step', textContent: pattern[index] === 'x' ? '●' : '·', 'aria-label': `Step ${index + 1}`, 'aria-pressed': String(pattern[index] === 'x'), disabled: context.readOnly });
    button.dataset.step = String(index);
    buttons.push(button);
    button.onclick = () => { if (painting) return; if (suppressClick) { suppressClick = false; return; } setStep(index, pattern[index] === 'x' ? '.' : 'x'); write(context, 'pattern', pattern); };
    button.onpointerdown = (event) => { if (context.readOnly) return; painting = true; paintValue = pattern[index] === 'x' ? '.' : 'x'; button.setPointerCapture(event.pointerId); setStep(index, paintValue); event.preventDefault(); };
    button.onpointerenter = () => { if (painting) setStep(index, paintValue); };
    button.onpointermove = (event) => {
      if (!painting) return;
      const target = document.elementFromPoint(event.clientX, event.clientY)?.closest('[data-step]');
      if (target) setStep(Number(target.dataset.step), paintValue);
    };
    button.onpointerup = () => { if (!painting) return; painting = false; suppressClick = true; write(context, 'pattern', pattern); };
    button.onpointercancel = () => { painting = false; suppressClick = true; };
    button.onkeydown = (event) => { if (event.key === ' ' || event.key === 'Enter') { event.preventDefault(); button.click(); } };
    steps.append(button);
  }
  const curve = svg('svg', { viewBox: '0 0 640 130', class: 'studio-duck-curve', role: 'img', 'aria-label': 'Duck gain envelope over one bar' });
  const releaseSteps = Math.max(1, release / 40), points = [];
  for (let index = 0; index <= 160; index++) points.push(`${index ? 'L' : 'M'}${index * 4} ${110 - duckGain(index / 10, pattern, depth, releaseSteps) * 90}`);
  curve.append(svg('path', { d: points.join(' '), class: 'studio-response-line' }));
  const playhead = svg('line', { x1: 0, x2: 0, y1: 4, y2: 126, class: 'studio-duck-playhead', 'aria-hidden': 'true' });
  curve.append(playhead);
  const bpm = Number(context.tempo) || 120, secondsPerBar = (barBeats() * 60) / bpm;
  let lastReducedUpdate = 0;
  const unsubscribe = onPlayer((player) => {
    if (!curve.isConnected) { unsubscribe(); return; }
    if (!player.playing) { playhead.removeAttribute('x1'); playhead.removeAttribute('x2'); return; }
    if (matchMedia('(prefers-reduced-motion: reduce)').matches && player.time - lastReducedUpdate < 1) return;
    lastReducedUpdate = player.time;
    const x = ((player.time % secondsPerBar) / secondsPerBar) * 640;
    playhead.setAttribute('x1', String(x)); playhead.setAttribute('x2', String(x));
  });
  const controls = el('div', { className: 'studio-filter-controls' }, [
    knob({ learn: context.block ? { block: context.block, name: 'depth' } : null, label: 'Depth', value: depth, min: 0, max: 40, unit: 'dB', disabled: context.readOnly, onPreview: (next) => context.preview?.('depth', next), onCommit: (next) => write(context, 'depth', next, 'dB') }),
    knob({ learn: context.block ? { block: context.block, name: 'release' } : null, label: 'Release', value: release, min: 10, max: 2000, unit: 'ms', scale: 'log', disabled: context.readOnly, onPreview: (next) => context.preview?.('release', next), onCommit: (next) => write(context, 'release', next, 'ms') }),
  ]);
  const root = el('section', { className: 'studio-effect-panel studio-duck-panel studio-live-panel', 'aria-label': 'Duck controls' }, [steps, curve, controls, el('p', { className: 'note', textContent: `16 steps per bar · project tempo ${context.tempo || '—'} BPM` })]);
  root.dispose = unsubscribe;
  return root;
}

registerStudioPanel('compressor', compressorPanel);
registerStudioPanel('duck', duckPanel);
