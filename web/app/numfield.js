// A number field for the properties panel: typing, arrows and the focus slider share one commit.
// Typing, the arrows and the focus slider all end in the same commit.
// @ample:number-fields

import { el, toast } from './ui.js';
import { readNumber, sliderPlacement } from '../lib/numfield-model.js';

/**
 * @param {object} o
 * @param {string} o.value the current value as text
 * @param {number[]|null} o.range [min, max, step] or null (then there is no slider and no clamping)
 * @param {boolean} o.disabled
 * @param {string} [o.title]
 * @param {(text: string) => Promise<boolean>} o.onCommit saves the value; false means it was refused and the field goes back
 */
export function numberField({ value, range, disabled, title, onCommit }) {
  const wrap = el('div', { className: 'numf' });
  let good = String(value ?? '');
  const input = el('input', { type: 'number', value: good, disabled, title: title || (range ? `${range[0]} to ${range[1]}; type it, use the arrows or the focus slider` : 'type it or use the arrows'), step: range ? range[2] : 'any' });
  if (range) { input.min = String(range[0]); input.max = String(range[1]); }
  input.setAttribute('inputmode', 'decimal');
  let slider = null;
  const commit = async () => {
    const r = readNumber(input.value, range);
    if (r.error) { toast(r.error); input.value = good; if (slider) slider.value = good; return; }
    if (r.clamped) toast(`Moved into the range ${range[0]} to ${range[1]}.`);
    input.value = r.text;
    if (slider) slider.value = r.text;
    if (r.text === good) return;
    const ok = await onCommit(r.text);
    if (ok === false) { input.value = good; if (slider) slider.value = good; } else good = r.text;
  };
  input.onchange = commit;
  input.onfocus = () => { if (slider) { wrap.classList.add('open'); placeSlider(); } };
  input.onblur = (event) => { if (event.relatedTarget !== slider) wrap.classList.remove('open', 'above'); };
  input.onkeydown = (event) => { if (event.key === 'Escape') { wrap.classList.remove('open', 'above'); input.blur(); } };
  wrap.append(input);
  if (range) {
    slider = el('input', { type: 'range', min: range[0], max: range[1], step: range[2], value: good === '' ? range[0] : good, disabled, className: 'numslider', tabIndex: -1, title: 'Slider (the same value; you can also type it)' });
    slider.onblur = () => wrap.classList.remove('open', 'above');
    slider.onkeydown = (event) => { if (event.key === 'Escape') { wrap.classList.remove('open', 'above'); slider.blur(); } };
    slider.oninput = () => { input.value = slider.value; };
    slider.onchange = () => { input.value = slider.value; commit(); };
    wrap.append(slider);
  }

  function placeSlider() {
    if (!slider) return;
    const rect = input.getBoundingClientRect();
    const pane = wrap.closest('aside')?.getBoundingClientRect();
    const bounds = pane ? { top: pane.top, bottom: pane.bottom } : { top: 0, bottom: window.innerHeight };
    wrap.classList.toggle('above', sliderPlacement(rect, bounds, 48) === 'above');
  }

  return wrap;
}
