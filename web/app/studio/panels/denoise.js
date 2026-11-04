// The denoise panel: the waveform of the input, and a stretch to drag over the part that holds only noise (a pause between phrases).
// The stretch is written as noise start, learn_from and learn; reduction and sensitivity are two sliders.

import { el } from '../../ui.js';
import { registerStudioPanel } from './registry.js';

const WIDTH = 640, HEIGHT = 110;

function number(value, fallback) {
  const n = Number.parseFloat(value);
  return Number.isFinite(n) ? n : fallback;
}

function denoisePanel(context) {
  const root = el('section', { className: 'studio-effect-panel studio-denoise-panel', 'aria-label': 'Denoise' });
  const canvas = el('canvas', { width: WIDTH, height: HEIGHT, className: 'denoise-wave', role: 'img', 'aria-label': 'Waveform of the input: drag over the stretch that holds only noise' });
  const readout = el('output', { className: 'denoise-readout' });
  let duration = 0;
  let peaks = null;
  let from = number(context.params.learn_from, 0);
  let length = number(context.params.learn, 0.5);
  const learnsFromStart = () => (context.params.noise || 'auto') === 'start';

  const say = () => {
    readout.textContent = learnsFromStart() ? `Noise: ${from.toFixed(1)} s to ${(from + length).toFixed(1)} s` : 'Noise: found by itself (the quietest moments). Drag over a pause to choose it yourself.';
  };

  const draw = () => {
    const g = canvas.getContext('2d');
    g.clearRect(0, 0, WIDTH, HEIGHT);
    const styles = getComputedStyle(document.documentElement);
    g.fillStyle = styles.getPropertyValue('--panel2').trim() || '#222';
    g.fillRect(0, 0, WIDTH, HEIGHT);
    if (peaks) {
      g.fillStyle = styles.getPropertyValue('--acc').trim() || '#6cf';
      for (let x = 0; x < WIDTH; x++) {
        const p = peaks[Math.floor((x / WIDTH) * peaks.length)] || 0;
        const h = Math.max(1, p * (HEIGHT - 6));
        g.fillRect(x, (HEIGHT - h) / 2, 1, h);
      }
    }

    if (learnsFromStart() && duration > 0) {
      g.fillStyle = 'rgba(229, 83, 75, 0.35)';
      g.fillRect((from / duration) * WIDTH, 0, Math.max(2, (length / duration) * WIDTH), HEIGHT);
    }
  };

  const commit = (items) => {
    if (context.commitMany) context.commitMany(items);
    else items.forEach((item) => context.commit(item.name, item.value));
  };

  // the waveform comes from the decoded input of the block (when it is built)
  Promise.resolve(context.inputBuffer?.()).then((buffer) => {
    if (!buffer) { say(); return; }
    duration = buffer.duration;
    const data = buffer.getChannelData(0);
    const step = Math.max(1, Math.floor(data.length / WIDTH));
    peaks = new Float32Array(WIDTH);
    for (let x = 0; x < WIDTH; x++) {
      let max = 0;
      for (let i = x * step; i < Math.min(data.length, (x + 1) * step); i += 8) max = Math.max(max, Math.abs(data[i]));
      peaks[x] = max;
    }

    draw();
    say();
  }).catch(() => say());

  if (!context.readOnly) {
    let anchor = null;
    const at = (event) => {
      const box = canvas.getBoundingClientRect();
      return Math.min(1, Math.max(0, (event.clientX - box.left) / box.width)) * (duration || 1);
    };

    canvas.onpointerdown = (event) => { if (!duration) return; canvas.setPointerCapture(event.pointerId); anchor = at(event); from = anchor; length = 0.1; context.params.noise = 'start'; say(); draw(); };
    canvas.onpointermove = (event) => {
      if (anchor === null) return;
      const now = at(event);
      from = Math.min(anchor, now);
      length = Math.max(0.1, Math.abs(now - anchor));
      say();
      draw();
    };
    canvas.onpointerup = () => {
      if (anchor === null) return;
      anchor = null;
      const learnFrom = Math.round(from * 10) / 10, learn = Math.round(length * 10) / 10;
      context.params.learn_from = String(learnFrom);
      context.params.learn = String(learn);
      commit([{ name: 'noise', value: 'start' }, { name: 'learn_from', value: String(learnFrom) }, { name: 'learn', value: String(learn) }]);
    };
  }

  const slider = (name, label, min, max, fallback, unit) => {
    const input = el('input', { type: 'range', min: String(min), max: String(max), step: '1', value: String(number(context.params[name], fallback)), disabled: context.readOnly, 'aria-label': label });
    const value = el('output', { textContent: `${input.value} ${unit}` });
    input.oninput = () => { value.textContent = `${input.value} ${unit}`; };
    input.onchange = () => commit([{ name, value: input.value }]);
    return el('label', { className: 'denoise-slider' }, [`${label} `, input, ' ', value]);
  };

  const automatic = el('button', { type: 'button', textContent: 'Find the noise by itself', disabled: context.readOnly, onclick: () => { context.params.noise = 'auto'; say(); draw(); commit([{ name: 'noise', value: 'auto' }]); } });

  // "optimize" is an execution-policy parameter (spec/templates/denoise.json marks it affectsOutput:false): every value renders byte-identical
  // audio, so it is shown apart from the musical controls above rather than as another sound knob (see ExtraDenoise.cs for the policy itself).
  const execution = el('div', { className: 'denoise-execution' }, [el('span', { className: 'note' }, ['Execution (never changes the sound):'])]);
  const optimizeButtons = [
    ['auto', 'Optimize: auto', 'AMPLE picks speed or memory by itself, based on how big the noise-floor scan of a long sound would get (default).'],
    ['speed', 'Optimize: speed', 'Faster on long sounds: keeps the noise-floor scan in memory instead of redoing it while cleaning. Same result either way.'],
    ['memory', 'Optimize: memory', 'Lower memory use: redoes the scan while cleaning instead of keeping it.'],
  ].map(([value, label, title]) => {
    const button = el('button', { type: 'button', textContent: label, title, disabled: context.readOnly });
    button.onclick = () => { context.params.optimize = value; syncOptimize(); commit([{ name: 'optimize', value }]); };
    return Object.assign(button, { optimizeValue: value });
  });
  const syncOptimize = () => {
    const active = String(context.params.optimize || 'auto');
    for (const button of optimizeButtons) { const on = button.optimizeValue === active; button.classList.toggle('on', on); button.setAttribute('aria-pressed', String(on)); }
  };
  execution.append(...optimizeButtons);
  syncOptimize();

  root.append(canvas, readout, slider('reduction', 'Reduction', 0, 40, 12, 'dB'), slider('sensitivity', 'Sensitivity', 0, 100, 50, '%'), automatic, execution);
  say();
  draw();
  return root;
}

registerStudioPanel('denoise', denoisePanel);
