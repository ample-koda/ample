import { delayTapGain, distortionSample, estimatedRt60 } from '../../../lib/studio-math.js';
import { el } from '../../ui.js';
import { knob } from '../knob.js';
import { selector } from '../selector.js';
import { registerStudioPanel } from './registry.js';
import { compareAudioBuffers, envelopePath } from '../preview/analysis.js';

const ns = 'http://www.w3.org/2000/svg';
const value = (params, key, fallback) => { const parsed = Number.parseFloat(String(params[key] ?? '').replace(/[a-z %]/gi, '')); return Number.isFinite(parsed) ? parsed : fallback; };
const svg = (tag, attrs) => { const node = document.createElementNS(ns, tag); Object.entries(attrs).forEach(([name, item]) => node.setAttribute(name, String(item))); return node; };
const plot = (label, draw) => { const graph = svg('svg', { viewBox: '0 0 520 220', class: 'studio-effect-plot', role: 'img', 'aria-label': label }); draw(graph); return graph; };
const controls = (items) => el('div', { className: 'studio-eq-controls' }, items);
const knobControl = (context, params, name, label, min, max, unit, scale = 'linear', size = 82, onPreview = null) => el('label', { className: 'studio-panel-control' }, [el('span', { textContent: label }), knob({ label, value: value(params, name, min), min, max, unit, scale, size, disabled: context.readOnly, onPreview: (next) => { context.preview?.(name, next); onPreview?.(next); }, onCommit: (next) => context.commit(name, `${Number(next.toFixed(unit === 'dB' ? 1 : 0))}${unit ? ` ${unit}` : ''}`) })]);
const choice = (context, params, name, label, values) => el('label', { className: 'studio-panel-control' }, [el('span', { textContent: label }), selector({ label, value: params[name] || values[0], disabled: context.readOnly, choices: values.map((item) => ({ value: item, label: item[0].toUpperCase() + item.slice(1) })), onCommit: (next) => context.commit(name, next) })]);
const baseline = (graph) => graph.append(svg('line', { x1: 20, x2: 500, y1: 110, y2: 110, class: 'studio-graph-zero' }));

function distortionPanel(context) {
  const params = context.params, drive = value(params, 'drive', 12), mode = params.mode || 'soft'; let liveDrive = drive;
  const graph = plot('Distortion input and output transfer curve', (root) => {
    baseline(root);
    const response = svg('path', { class: 'studio-response-line' });
    const draw = () => { const d = []; for (let index = 0; index <= 100; index++) { const input = index / 50 - 1; d.push(`${index ? 'L' : 'M'}${20 + index * 4.8} ${110 - distortionSample(input, liveDrive, mode) * 90}`); } response.setAttribute('d', d.join(' ')); };
    draw(); root.append(response); root.updateDrive = (next) => { liveDrive = next; draw(); };
  });
  const waveform = svg('svg', { viewBox: '0 0 640 180', class: 'studio-compressor-waveform', role: 'img', 'aria-label': 'Distortion input and output waveform envelopes' });
  const waveStatus = el('p', { className: 'note', textContent: context.renderedBuffer ? 'Exact engine before/after waveform appears after analysis.' : 'Build the upstream input to enable waveform analysis.' });
  const analyze = el('button', { type: 'button', textContent: 'Analyze exact preview', disabled: !context.renderedBuffer, title: context.renderedBuffer ? 'Render this bounded preview through the integer effect engine' : 'Build the upstream input first' });
  analyze.onclick = async () => {
    analyze.disabled = true; waveStatus.textContent = 'Rendering the bounded exact preview…';
    try {
      const [input, output] = await Promise.all([context.inputBuffer?.(), context.renderedBuffer(context.params)]);
      if (!input || !output) throw new Error('Build the upstream audio input first.');
      const comparison = compareAudioBuffers(input.getChannelData(0), output.getChannelData(0));
      waveform.replaceChildren(svg('line', { x1: 0, x2: 640, y1: 90, y2: 90, class: 'studio-graph-zero' }), svg('path', { d: envelopePath(comparison.input, 90, 76, 640), class: 'studio-response-line' }), svg('path', { d: envelopePath(comparison.output, 90, 76, 640), class: 'studio-analysis-output' }));
      const averageGain = comparison.gainDb.reduce((sum, gain) => sum + gain, 0) / comparison.gainDb.length;
      waveStatus.textContent = `Exact engine analysis · average output change ${averageGain >= 0 ? '+' : ''}${averageGain.toFixed(1)} dB. Cyan: input · violet: output.`;
    } catch (error) { waveStatus.textContent = error.message; }
    finally { analyze.disabled = !context.renderedBuffer; }
  };
  const waveformPanel = el('section', { className: 'studio-compressor-analysis', 'aria-label': 'Distortion input and output waveform comparison' }, [el('div', { className: 'studio-analysis-heading' }, [el('strong', { textContent: 'BEFORE / AFTER' }), analyze]), waveform, waveStatus]);
  return el('section', { className: 'studio-effect-panel studio-distortion-panel', 'aria-label': 'Distortion controls' }, [graph, controls([
    knobControl(context, params, 'drive', 'Drive', 0, 36, 'dB', 'linear', 112, (next) => graph.updateDrive(next)), choice(context, params, 'mode', 'Mode', ['soft', 'hard']),
    knobControl(context, params, 'tone', 'Tone', 500, 16000, 'Hz', 'log'), knobControl(context, params, 'level', 'Level', -24, 12, 'dB'), knobControl(context, params, 'mix', 'Mix', 0, 100, '%'),
  ]), waveformPanel, el('p', { className: 'note', textContent: 'The transfer curve is an approximation; waveform analysis uses the exact bounded integer renderer.' })]);
}

function delayPanel(context) {
  const params = context.params, feedback = value(params, 'feedback', 35), time = value(params, 'time', 250); let liveFeedback = feedback;
  const tapLines = [];
  const graph = plot('Delay echo tap levels', (root) => {
    baseline(root);
    for (let tap = 0; tap < 7; tap++) { const line = svg('line', { x1: 38 + tap * 70, x2: 38 + tap * 70, y1: 110 - delayTapGain(liveFeedback, tap) * 90, y2: 110, class: 'studio-delay-tap' }); tapLines.push(line); root.append(line); }
  });
  const bpm = Number(context.tempo) || 120, beatMs = 60000 / bpm, quarters = beatMs / time;
  const timing = el('p', { className: 'note', textContent: `${time} ms · ${quarters >= 1 ? `${quarters.toFixed(2)} quarter notes` : `${(quarters * 2).toFixed(2)} eighth notes`} at ${bpm} BPM` });
  const updateFeedback = (next) => { liveFeedback = next; tapLines.forEach((line, index) => line.setAttribute('y1', String(110 - delayTapGain(liveFeedback, index) * 90))); };
  const updateTime = (next) => { const count = beatMs / next; timing.textContent = `${Math.round(next)} ms · ${count >= 1 ? `${count.toFixed(2)} quarter notes` : `${(count * 2).toFixed(2)} eighth notes`} at ${bpm} BPM`; };
  return el('section', { className: 'studio-effect-panel studio-delay-panel', 'aria-label': 'Delay controls' }, [graph, controls([
    knobControl(context, params, 'time', 'Time', 1, 2000, 'ms', 'log', 112, updateTime), knobControl(context, params, 'feedback', 'Feedback', 0, 90, '%', 'linear', 82, updateFeedback), knobControl(context, params, 'mix', 'Mix', 0, 100, '%'),
  ]), timing]);
}

function reverbPanel(context) {
  const params = context.params, size = value(params, 'size', 50), decay = value(params, 'decay', 60); let liveSize = size, liveDecay = decay;
  let roomShape, tail;
  const graph = plot('Reverb room and decay tail estimate', (root) => {
    roomShape = svg('rect', { rx: 8, class: 'studio-room-shape' }); tail = svg('path', { class: 'studio-response-line' });
    const draw = () => { const width = 70 + liveSize * 3, height = 45 + liveSize * 0.7; roomShape.setAttribute('x', String(260 - width / 2)); roomShape.setAttribute('y', String(165 - height)); roomShape.setAttribute('width', String(width)); roomShape.setAttribute('height', String(height)); const points = []; for (let index = 0; index <= 80; index++) points.push(`${index ? 'L' : 'M'}${20 + index * 6} ${30 + 160 * (1 - Math.exp(-index / Math.max(2, liveDecay / 8)))}`); tail.setAttribute('d', points.join(' ')); };
    draw(); root.append(roomShape, tail); root.updateRoom = (next) => { liveSize = next; draw(); }; root.updateDecay = (next) => { liveDecay = next; draw(); };
  });
  const estimate = el('p', { className: 'note', textContent: `Estimated RT60: ${estimatedRt60(size, decay).toFixed(1)} s` });
  const updateEstimate = () => { estimate.textContent = `Estimated RT60: ${estimatedRt60(liveSize, liveDecay).toFixed(1)} s`; };
  return el('section', { className: 'studio-effect-panel studio-reverb-panel', 'aria-label': 'Reverb controls' }, [graph, controls([
    knobControl(context, params, 'size', 'Room size', 1, 100, '%', 'linear', 112, (next) => { graph.updateRoom(next); updateEstimate(); }), knobControl(context, params, 'decay', 'Decay', 0, 95, '%', 'linear', 82, (next) => { graph.updateDecay(next); updateEstimate(); }), knobControl(context, params, 'mix', 'Mix', 0, 100, '%'),
  ]), estimate]);
}

registerStudioPanel('distortion', distortionPanel);
registerStudioPanel('delay', delayPanel);
registerStudioPanel('reverb', reverbPanel);
