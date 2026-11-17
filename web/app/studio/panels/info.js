// The (i) toggle of an Advanced window: an explanation that is there when you want it and out of the way (collapsed) when you do not.
import { el } from '../../ui.js';

/** A collapsed (i) with an explanation behind it. `content` is text or a node (or a list of nodes). */
export function infoToggle(content, className = '', label = 'More about this window') {
  const body = Array.isArray(content) ? content : [typeof content === 'string' ? el('p', { className: 'note', textContent: content }) : content];
  return el('details', { className: `studio-info ${className}`.trim() }, [el('summary', { 'aria-label': label, title: label, textContent: 'i' }), el('div', { className: 'studio-info-body' }, body)]);
}
/** The same for one sentence. */
export const infoNote = (text, label) => infoToggle(text, '', label);
