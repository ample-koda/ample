export { knob } from './knob.js';
export { fader } from './fader.js';
export { led } from './led.js';
export { selector } from './selector.js';
export { readout } from './readout.js';
export { meter } from './meter.js';
export { curveEditor } from './curve.js';
export { scrubBar } from './scrub.js';
export { xyPad } from './xy.js';

import { knob } from './knob.js';
import { fader } from './fader.js';
import { led } from './led.js';
import { selector } from './selector.js';
import { readout } from './readout.js';
import { meter } from './meter.js';
import { curveEditor } from './curve.js';
import { scrubBar } from './scrub.js';
import { xyPad } from './xy.js';
import { el } from '../ui.js';

export function mountStudioGallery() {
  if (new URLSearchParams(location.search).get('gallery') !== '1') return;
  const host = el('main', { id: 'studio-gallery' });
  host.append(el('h1', { textContent: 'AMPLE studio control gallery' }));
  const knobPreview = el('p', { className: 'studio-readout' });
  host.append(el('section', {}, [el('h2', { textContent: 'Knob' }), knob({ label: 'Cutoff', min: 20, max: 20000, value: 1000, defaultValue: 1000, scale: 'log', unit: 'Hz', sizeName: 'l', onPreview: (value) => { knobPreview.textContent = String(value); }, onCommit: () => {} }), knobPreview]));
  host.append(el('section', {}, [el('h2', { textContent: 'Fader' }), fader({ label: 'Output', min: -60, max: 6, value: -6, unit: 'dB', onPreview: () => {}, onCommit: () => {} })]));
  host.append(el('section', {}, [el('h2', { textContent: 'LED and selector' }), led({ label: 'Enabled', value: true, onCommit: () => {} }), selector({ label: 'Filter mode', value: 'low', choices: [{ value: 'low', label: 'Low pass 12' }, { value: 'high', label: 'High pass 12' }], onCommit: () => {} }), readout({ label: 'Frequency', value: 1800, unit: 'Hz' })]));
  const outputMeter = meter({ label: 'Output meter', min: 0, max: 1, value: .62, peak: .8 });
  outputMeter.onclick = () => outputMeter.setMeterValue(.91, .96);
  const loopReadout = el('output', { className: 'note', textContent: 'Shift-drag to select a loop region.' });
  const scrub = scrubBar({ label: 'Preview scrub', position: .3, peaks: [.2, .5, .8, .4, .7], onPreview: () => {}, onCommit: () => {}, onLoopPreview: (range) => { loopReadout.textContent = `Loop ${Math.round(range[0] * 100)}–${Math.round(range[1] * 100)}%`; }, onLoopCommit: (range) => { loopReadout.textContent = `Loop set: ${Math.round(range[0] * 100)}–${Math.round(range[1] * 100)}%`; } });
  host.append(el('section', {}, [el('h2', { textContent: 'Meter and scrub' }), outputMeter, scrub, loopReadout]));
  host.append(el('section', {}, [el('h2', { textContent: 'Curve and XY pad' }), curveEditor({ label: 'Response curve', width: 240, height: 140, grid: 24, snap: 4, points: [{ x: 0, y: 15 }, { x: 80, y: 110 }, { x: 160, y: 85 }, { x: 240, y: 120 }], onPreview: () => {}, onCommit: () => {} }), xyPad({ label: 'Filter XY', xMin: 0, xMax: 100, x: 50, yMin: 0, yMax: 100, y: 50, onPreview: () => {}, onCommit: () => {} })]));
  document.body.append(host);
}
