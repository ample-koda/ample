import { el } from '../../ui.js';
import { onPlayer } from '../../player.js';
import { fader } from '../fader.js';
import { meter } from '../meter.js';
import { registerStudioPanel } from './registry.js';

/** The volume block takes -60 to +48 dB (template volume; the engine's wide gain table). */
const MAX_DB = 48;

export function volumeControls(context, meterTake) {
  const panel = el('section', { className: 'studio-live-panel volume-controls', 'aria-label': 'Volume and stereo channels' });
  const values = { ...context.params };
  const gains = { left_gain: Number.parseFloat(values.left_gain) || 0, right_gain: Number.parseFloat(values.right_gain) || 0 };
  const lanes = el('div', { className: 'volume-lanes' }), widgets = {};
  const sync = () => Object.entries(widgets).forEach(([name, widget]) => { const input = widget.querySelector('input'); input.value = String(gains[name]); input.setAttribute('aria-valuetext', gains[name] + ' dB'); widget.querySelector('output').textContent = gains[name] + ' dB'; });
  const changes = (name, value) => {
    const other = name === 'left_gain' ? 'right_gain' : 'left_gain';
    let delta = value - gains[name];
    if (values.linked !== 'off') delta = Math.max(-60 - Math.min(gains[name], gains[other]), Math.min(MAX_DB - Math.max(gains[name], gains[other]), delta));
    gains[name] += delta;
    const result = [{ name, value: gains[name] + ' dB' }];
    if (values.linked !== 'off') { gains[other] += delta; result.push({ name: other, value: gains[other] + ' dB' }); }
    sync(); result.forEach((item) => context.preview?.(item.name, item.value));
    return result;
  };
  ['left', 'right'].forEach((channel) => {
    const name = channel + '_gain', label = channel === 'left' ? 'L' : 'R';
    let pending = null;
    const control = fader({ label, value: gains[name], min: -60, max: MAX_DB, step: 1, unit: 'dB', orientation: 'vertical', disabled: context.readOnly, onPreview: (value) => { pending = changes(name, value); }, onCommit: (value) => { const items = pending || changes(name, value); pending = null; if (context.commitMany) context.commitMany(items); else items.forEach((item) => context.commit(item.name, item.value)); } });
    widgets[name] = control;
    const level = meter({ label: label + ' output level', min: -60, max: 6, value: -60, peak: -60, unit: 'dB', decimals: 1 });
    level.dataset.channel = channel;
    lanes.append(el('div', { className: 'volume-lane' }, [control, level]));
  });
  const toggles = el('div', { className: 'volume-toggles' });
  ['linked', 'mono', 'left', 'right'].forEach((name) => {
    const mute = name === 'left' || name === 'right';
    const input = el('input', { type: 'checkbox', checked: mute ? values[name] === 'off' : name === 'linked' ? values[name] !== 'off' : values[name] === 'on', disabled: context.readOnly });
    input.onchange = () => { values[name] = (mute ? !input.checked : input.checked) ? 'on' : 'off'; context.preview?.(name, values[name]); context.commit(name, values[name]); };
    toggles.append(el('label', {}, [input, mute ? 'Mute ' + (name === 'left' ? 'L' : 'R') : name === 'linked' ? 'Link' : 'Mono']));
  });
  panel.onpointerdown = (event) => event.stopPropagation();
  panel.onclick = (event) => event.stopPropagation();
  panel.ondblclick = (event) => event.stopPropagation();
  panel.dispose = onPlayer((state) => {
    const audible = meterTake ? state.take === meterTake() || state.take === 'audition' && state.auditionBlock === context.block : state.take === 'audition' && state.auditionBlock === context.block;
    const decibels = (value) => value > 0 ? 20 * Math.log10(value) : -60;
    lanes.querySelectorAll('[data-channel]').forEach((level) => level.setMeterValue(decibels(audible && state.playing ? state[level.dataset.channel + 'Level'] || 0 : 0), decibels(audible && state.playing ? state[level.dataset.channel + 'Peak'] || 0 : 0)));
  });
  panel.append(lanes, toggles);
  return panel;
}

registerStudioPanel('volume', (context) => volumeControls(context, null));
