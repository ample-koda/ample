import { el } from '../ui.js';
import { formatValue } from '../../lib/studio-math.js';

let faderId = 0;

export function fader(options) {
  const valueText = () => options.format ? options.format(Number(input.value)) : formatValue(input.value, options.decimals || 0, options.unit || '');
  const input = el('input', { type: 'range', min: String(options.min), max: String(options.max), step: String(options.step || 'any'), value: String(options.value), disabled: Boolean(options.disabled), 'aria-label': options.label, 'aria-orientation': options.orientation === 'horizontal' ? 'horizontal' : 'vertical', 'aria-valuetext': formatValue(options.value, options.decimals || 0, options.unit || '') });
  input.className = `studio-fader${options.orientation === 'horizontal' ? ' horizontal' : ' vertical'}`;
  input.oninput = () => { readout.textContent = valueText(); input.setAttribute('aria-valuetext', valueText()); if (!options.disabled) options.onPreview(Number(input.value)); };
  input.onchange = () => { if (!options.disabled) options.onCommit(Number(input.value)); };
  const readout = el('output', { className: 'studio-fader-value', textContent: valueText() });
  const control = el('div', { className: `studio-fader-track${options.orientation === 'horizontal' ? ' horizontal' : ' vertical'}` }, [input]);
  if (options.ticks?.length) {
    const listId = `studio-fader-ticks-${faderId++}`;
    const list = el('datalist', { id: listId }, options.ticks.map((tick) => el('option', { value: String(tick) })));
    input.setAttribute('list', listId); control.append(list);
  }
  return el('label', { className: `studio-fader-wrap${options.orientation === 'horizontal' ? ' horizontal' : ''}` }, [el('span', { textContent: options.label }), control, readout]);
}
