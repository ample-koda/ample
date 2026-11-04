import { el } from '../../ui.js';
import { knob } from '../knob.js';
import { registerStudioPanel } from './registry.js';

const parts = (value, fallback) => String(value || fallback).trim().split(/\s+/);
function mixerPanel(context) {
  const gains = parts(context.params.gains, '0 0 0 0'), pans = parts(context.params.pan, 'c c c c');
  const root = el('section', { className: 'studio-effect-panel studio-mixer-panel', 'aria-label': 'Mixer channels' });
  const strips = el('div', { className: 'studio-mixer-strips' });
  for (let index = 0; index < 4; index++) {
    const range = el('input', { type: 'range', min: '-60', max: '48', step: '1', value: String(Number(gains[index]) || 0), disabled: context.readOnly, 'aria-label': `Input ${index + 1} gain`, title: 'Input gain in decibels' });
    const readout = el('output', { textContent: `${range.value} dB` });
    range.oninput = () => { readout.value = `${range.value} dB`; readout.textContent = readout.value; };
    range.onchange = () => { gains[index] = range.value; context.commit('gains', gains.join(' ')); };
    const panValue = (pans[index] || 'c').toLowerCase() === 'l' ? -100 : (pans[index] || 'c').toLowerCase() === 'r' ? 100 : 0;
    const pan = knob({ label: `Input ${index + 1} pan`, value: panValue, min: -100, max: 100, step: 100, disabled: context.readOnly, format: (next) => next < 0 ? 'Left' : next > 0 ? 'Right' : 'Centre', onCommit: (next) => { pans[index] = next < 0 ? 'l' : next > 0 ? 'r' : 'c'; context.commit('pan', pans.join(' ')); } });
    strips.append(el('div', { className: 'studio-mixer-strip' }, [el('strong', { textContent: `Input ${index + 1}` }), range, readout, pan, el('span', { className: 'studio-mixer-meter', role: 'img', 'aria-label': 'Meter available during audio preview' })]));
  }
  root.append(strips, el('p', { className: 'note', textContent: 'Each fader and pan edit is journaled. Live channel meters appear when preview analysis is connected.' }));
  return root;
}

registerStudioPanel('mixer', mixerPanel);
registerStudioPanel('bus', mixerPanel);
