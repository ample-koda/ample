// The controls of a component in a window of their own (the layout of the properties panel, with room): opened from the card or the panel, stays in step with the song.
import { edit, flat, readOnly, S, subscribe } from './store.js';
import { ask, el } from './ui.js';
import { renderComponentUi } from './component-ui.js';
import { liveOptions } from './component-live-ui.js';

/** The items the popup shows: the layout of the panel, else the card layout (so a component that only has a card block still has a popup). */
export const popupItems = (ui) => (ui?.panel?.length ? ui.panel : ui?.card || []);

/** Opens the controls of a block that is an instance of a component with a layout. */
export async function openComponentPopup(blockId) {
  const find = () => flat().nodes.find((x) => x.id === blockId);
  const template = S.tpl[find()?.tpl];
  if (!template?.ui || !popupItems(template.ui).length) return;
  const holder = el('div', { className: 'cui-popup-holder' });
  const draw = () => {
    const node = find();
    if (!node) return;
    const values = Object.fromEntries((template.parameters || []).map((p) => [p.name, node.params[p.name] ?? p.default]));
    const save = (name, text) => edit([{ type: 'setParameter', block: blockId, name, value: text }]);
    const saveMany = (items) => edit(items.map((i) => ({ type: 'setParameter', block: blockId, name: i.name, value: i.text })));
    holder.replaceChildren(renderComponentUi(template, values, popupItems(template.ui), 'popup', { disabled: readOnly(), save, saveMany, ...liveOptions(template, blockId) }).element);
  };
  draw();
  const stop = subscribe(draw);
  try {
    await ask(`${template.title} · ${blockId}`, '', [{ label: 'Close', value: true, primary: true }], [holder]);
  } finally {
    stop();
  }
}
