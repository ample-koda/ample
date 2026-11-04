import { el } from '../ui.js';

export function selector(options) {
  const select = el('select', { className: 'studio-selector', disabled: Boolean(options.disabled), 'aria-label': options.label });
  options.choices.forEach((choice) => select.append(el('option', { value: choice.value, textContent: choice.label, selected: choice.value === options.value })));
  select.onchange = () => options.onCommit(select.value);
  return select;
}
