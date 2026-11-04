import { el } from '../ui.js';
import { formatValue } from '../../lib/studio-math.js';

export function readout(options) {
  return el('output', { className: 'studio-readout', 'aria-label': options.label, textContent: options.format ? options.format(options.value) : formatValue(options.value, options.decimals || 0, options.unit || '') });
}
