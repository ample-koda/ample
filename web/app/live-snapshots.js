// Parameter snapshots A to D and a morph between two of them, in the Advanced window of a block (local-preview chunk 6.2). Click an empty slot to store the current values, a stored slot
// to recall it (one undo step); Store (then a slot) overwrites. The morph slider blends two stored snapshots: heard while it moves, written once when it is released.
// Snapshots are kept in the project (ui-state.js), per song file and block. Only the live parameters of the block take part. @ample:live-snapshots
import { el, toast } from './ui.js';
import { S } from './store.js';
import { loadUiState, getUiKey, setUiKey } from './ui-state.js';
import { parameterLive } from '../lib/live-parameters.mjs';
import { takeSnapshot, morphWrites, parseValue } from '../lib/snapshots.mjs';

const SLOTS = ['A', 'B', 'C', 'D'];
// snapshots live in the project (ui-state.json) and travel with it; old browser-only ones are moved over once (ui-state.js)
const read = () => getUiKey('snapshots') || {};
const write = (all) => setUiKey('snapshots', all);
const keyOf = (panel) => `${S.path}#${panel.block}`;

/** The row for a panel ({ type, block, definitions, getParams, preview, commitMany, readOnly }), or null when the block has no live parameter to store. */
export function snapshotBar(panel) {
  const names = panel.definitions.filter((d) => d.name !== 'enabled' && (d.kind === 'number' || d.kind === 'choice') && parameterLive(panel.type, d.name) === 'param').map((d) => d.name);
  if (!panel.block || !names.length) return null;
  let storing = false, from = 'A', to = 'B';
  const stored = () => read()[keyOf(panel)] || {};
  const slotButtons = new Map();
  const paint = () => {
    const have = stored();
    for (const [slot, button] of slotButtons) {
      button.textContent = slot; button.classList.toggle('filled', Boolean(have[slot])); button.setAttribute('aria-pressed', 'false');
      button.title = have[slot] ? `Recall snapshot ${slot}${storing ? ' (Store is on: this overwrites it)' : ''}` : `Store the current values as snapshot ${slot}`;
    }
    store.classList.toggle('on', storing); store.setAttribute('aria-pressed', String(storing));
    slider.disabled = !(have[from] && have[to]) || from === to;
    slider.title = slider.disabled ? 'Store two different snapshots to morph between them' : `Morph from ${from} to ${to}`;
  };
  const choose = (slot) => {
    const all = read(), mine = all[keyOf(panel)] || {};
    if (!mine[slot] || storing) {
      mine[slot] = takeSnapshot(panel.getParams(), panel.definitions, names); all[keyOf(panel)] = mine; write(all); storing = false; paint(); toast(`Stored snapshot ${slot}.`); return;
    }
    const writes = names.filter((name) => name in mine[slot]).map((name) => ({ name, value: mine[slot][name] }));
    if (writes.length) panel.commitMany(writes);
  };
  const store = el('button', { type: 'button', className: 'snap-store', textContent: 'Store', 'aria-pressed': 'false', title: 'The next slot you click is overwritten with the current values', onclick: () => { storing = !storing; paint(); } });
  for (const slot of SLOTS) slotButtons.set(slot, el('button', { type: 'button', className: 'snap-slot', 'data-slot': slot, onclick: () => choose(slot) }));
  const select = (label, value, set) => el('select', { 'aria-label': label, onchange: (event) => { set(event.target.value); paint(); } }, SLOTS.map((slot) => el('option', { value: slot, textContent: slot, selected: slot === value })));
  let morphing = null;
  const slider = el('input', { type: 'range', min: '0', max: '1', step: '0.01', value: '0', 'aria-label': 'Morph between the two snapshots', className: 'snap-morph' });
  const writesAt = (t) => { const mine = stored(); return morphWrites(panel.definitions.filter((d) => names.includes(d.name)), mine[from] || {}, mine[to] || {}, t); };
  slider.oninput = () => {
    morphing = writesAt(Number(slider.value));
    for (const { name, value } of morphing) { const parsed = parseValue(value); panel.preview?.(name, parsed ? parsed.n : value); }   // heard at once; the song is written on release
  };
  slider.onchange = () => { const writes = morphing || writesAt(Number(slider.value)); morphing = null; if (writes.length) panel.commitMany(writes); };
  paint();
  loadUiState().then(paint);
  return el('div', { className: 'snapshot-bar', role: 'group', 'aria-label': 'Snapshots' }, [
    el('strong', { textContent: 'Snapshots' }), ...slotButtons.values(), store,
    el('span', { className: 'snap-morph-label', textContent: 'Morph' }), select('Morph from', from, (v) => { from = v; }), el('span', { textContent: '→' }), select('Morph to', to, (v) => { to = v; }), slider,
  ]);
}
