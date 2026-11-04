import { el } from '../../ui.js';
import { control, controls, number, transferCurve } from './helpers.js';
import { registerStudioPanel } from './registry.js';

function masterPanel(context) {
  const params = { limiter: '-1 dB', target: '-14 LUFS', ...context.params };
  let ceiling = number(params, 'limiter', -1), target = number(params, 'target', -14), levels = null;
  const curve = transferCurve('Master limiter ceiling', (input) => Math.min(input, ceiling));
  const input = el('output', { className: 'note', textContent: 'Input: play the local preview to measure.' });
  const output = el('output', { className: 'note', textContent: 'Output: not measured yet.' });
  const comparison = el('output', { className: 'note', textContent: 'Target is a loudness reference; it does not change gain.' });
  const format = (value, unit) => Number.isFinite(value) ? `${value.toFixed(1)} ${unit}` : 'not measurable';
  const paint = () => {
    if (!levels) return;
    input.textContent = `Input: ${format(levels.input.peakDbfs, 'dBFS peak')} · ${format(levels.input.lufs, 'LUFS')}`;
    output.textContent = `Output: ${format(levels.output.peakDbfs, 'dBFS peak')} · ${format(levels.output.lufs, 'LUFS')}`;
    comparison.textContent = Number.isFinite(levels.output.lufs) ? `${(levels.output.lufs - target).toFixed(1)} LU relative to target ${target} LUFS. Target does not change gain.` : 'Loudness is not measurable for this audio. Target does not change gain.';
  };
  const slice = (name, label) => {
    const field = el('input', { type: 'number', min: 1, step: 1, value: params[name] || '', disabled: context.readOnly, 'aria-label': label, placeholder: name === 'slice_from' ? 'Beginning' : 'End' });
    field.onchange = () => context.commit(name, field.value);
    return el('label', { className: 'studio-panel-control' }, [el('span', { textContent: label }), field]);
  };
  const channels = el('select', { disabled: context.readOnly, 'aria-label': 'Song channels' }, ['auto', 'mono', 'stereo'].map((value) => el('option', { value, textContent: value, selected: value === (params.channels || 'auto') })));
  channels.onchange = () => context.commit('channels', channels.value);
  const panel = el('section', { className: 'studio-effect-panel studio-master-panel', 'aria-label': 'Master controls' }, [curve, controls([
    control(context, params, 'limiter', 'Ceiling', -12, 0, 'dB', 'linear', 112, (value) => { ceiling = value; curve.redraw(); }),
    control(context, params, 'target', 'Loudness reference', -36, -5, 'LUFS', 'linear', 112, (value) => { target = value; paint(); }),
    slice('slice_from', 'First bar'), slice('slice_to', 'Last bar'),
    el('label', { className: 'studio-panel-control' }, [el('span', { textContent: 'Song channels' }), channels]),
  ]), el('div', { className: 'studio-master-levels', 'aria-live': 'polite', style: 'display:grid;gap:6px;margin-top:12px' }, [input, output, comparison]), el('p', { className: 'note', textContent: 'Local look-ahead limiter and bar slice; approximate. A/B compares the output with the dry input. Channel policy applies upstream: rebuild the input after changing it.' })]);
  panel.setMasterLevels = (next) => { levels = next; paint(); };
  return panel;
}

registerStudioPanel('master', masterPanel);
