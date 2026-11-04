// Library bundles (plan bundles BU04): save blocks of the song with their files, seeds and audio, and place a saved bundle in a song under new names.
// The bundle library is a dialog (a list with Place and Delete); saving is an entry of the block menu. The work is done by /api/bundles. @ample:bundles

import { api } from './api.js';
import { ask, el, toast } from './ui.js';
import { S, refreshBuildStatus, setDoc } from './store.js';
import { refreshProjectPanel } from './projectpanel.js';

const labelled = (text, input) => el('label', { className: 'row', style: 'display:flex;gap:6px;align-items:center' }, [el('span', { textContent: text, style: 'min-width:120px' }), input]);

/** Asks for a name and saves the blocks (the selected ones, with the blocks that feed them when asked) as a bundle in the library. */
export async function saveAsBundle(blockNames) {
  const names = [...new Set(blockNames)].filter(Boolean);
  if (!names.length) { toast('Select a block first.'); return; }
  if (!S.path) return;
  const name = el('input', { value: names.length === 1 ? names[0] : names[0] + '-group', 'aria-label': 'Name of the bundle' });
  const description = el('input', { value: '', placeholder: 'What it is (optional)', 'aria-label': 'Description of the bundle' });
  const inputs = el('select', { 'aria-label': 'Include the blocks that feed these' }, [el('option', { value: 'no', textContent: 'No, only the chosen blocks' }), el('option', { value: 'yes', textContent: 'Yes, with the blocks that feed them' })]);
  const ok = await ask('Save as bundle', `Saves ${names.join(', ')} in the library with the files they read, the seeds they have and the audio of their takes. A block without a take (build it first) is saved without audio.`,
    [{ label: 'Save', value: true, primary: true }, { label: 'Cancel', value: false }], [labelled('Name', name), labelled('Description', description), labelled('Include inputs', inputs)]);
  if (!ok) return;
  try {
    const made = await api.post('/api/bundles/save', { path: S.path, blocks: names, name: name.value.trim(), description: description.value.trim(), includeInputs: inputs.value === 'yes' });
    const missing = made.without ? ` ${made.without} block${made.without === 1 ? ' has' : 's have'} no audio yet.` : '';
    const open = made.bundle.openInputs?.length ? ` Connections from outside were left out: ${made.bundle.openInputs.join(', ')}.` : '';
    toast(`Saved the bundle "${made.bundle.name}" (${made.bundle.blocks.length} block${made.bundle.blocks.length === 1 ? '' : 's'}, ${made.bundle.files.length} file${made.bundle.files.length === 1 ? '' : 's'}).${missing}${open}`);
  } catch (error) { toast(error.message); }
}

/** The bundle library: a list of the bundles, with Place in the song and Delete. */
export async function openBundleLibrary() {
  for (;;) {
    let bundles;
    try { bundles = (await api.get('/api/bundles')).bundles; } catch (error) { toast(error.message); return; }
    if (!bundles.length) { await ask('Bundle library', 'There are no bundles yet. Right-click a block and choose Save as bundle... to put it here.', [{ label: 'Close', value: false, primary: true }]); return; }
    const list = el('select', { id: 'bundle-list', size: Math.min(8, bundles.length + 1), 'aria-label': 'Bundles', style: 'width:100%' }, bundles.map((b, i) => el('option', { value: b.id, selected: i === 0, textContent: `${b.name}${b.blocks.length === 1 && b.blocks[0] === b.name ? '' : '  ·  ' + b.blocks.join(', ')}${b.description ? '  ·  ' + b.description : ''}` })));
    const choice = await ask('Bundle library', 'A bundle is a block, or a chain of blocks, that sounds the same when it is placed in another song. Place adds it under new names.',
      [{ label: 'Close', value: false }, { label: 'Delete', value: 'delete' }, { label: 'Place in the song...', value: 'place', primary: true }], [list]);
    const bundle = bundles.find((b) => b.id === list.value);
    if (!choice || !bundle) return;
    if (choice === 'delete') {
      if (await ask('Delete bundle', `Remove "${bundle.name}" from the library? Songs that used it keep their blocks.`, [{ label: 'Delete', value: true, primary: true }, { label: 'Keep', value: false }])) {
        try { await api.deleteBundle(bundle.id); } catch (error) { toast(error.message); }
      }
      continue;
    }

    await placeBundle(bundle);
    return;
  }
}

/** Asks for a prefix (and a value for each slot) and places the bundle in the open song. */
export async function placeBundle(bundle) {
  if (!S.path) { toast('Open a song first.'); return; }
  const prefix = el('input', { value: '', placeholder: 'for example intro-', 'aria-label': 'Prefix for the block names' });
  const slotInputs = bundle.slots.map((slot) => ({ slot, input: el('input', { value: slot.value, 'aria-label': `${slot.block} ${slot.param}` }) }));
  const note = bundle.blocks.some((name) => (S.doc?.blocks ?? []).some((b) => b.name === name)) ? 'The song already has a block with one of these names: give a prefix.' : 'Leave the prefix empty to keep the names.';
  const ok = await ask(`Place "${bundle.name}"`, `${note} The slots are the files the blocks play: change one to another file or folder of the project to hear something else (that block is then built again).`,
    [{ label: 'Place', value: true, primary: true }, { label: 'Cancel', value: false }], [labelled('Prefix', prefix), ...slotInputs.map(({ slot, input }) => labelled(`${slot.block}.${slot.param}`, input))]);
  if (!ok) return;
  const slots = {};
  for (const { slot, input } of slotInputs) if (input.value.trim() !== slot.value) slots[`${slot.block}.${slot.param}`] = input.value.trim();
  const send = (useExisting) => api.post('/api/bundles/place', { path: S.path, id: bundle.id, prefix: prefix.value.trim(), slots, useExisting });
  try {
    let placed;
    try { placed = await send(false); } catch (error) {
      if (error.code !== 'AMP5393') throw error;
      if (!(await ask('Files differ', `${error.message}`, [{ label: 'Keep the project\'s files', value: true, primary: true }, { label: 'Cancel', value: false }]))) return;
      placed = await send(true);
    }

    setDoc(placed.doc);
    refreshBuildStatus();
    refreshProjectPanel().catch(() => {});
    const open = placed.openInputs?.length ? ` Not connected: ${placed.openInputs.join(', ')}.` : '';
    toast(`Placed ${Object.values(placed.names).join(', ')}. ${placed.adopted.length} block${placed.adopted.length === 1 ? ' has' : 's have'} its saved audio.${open}`);
  } catch (error) { toast(error.message); }
}
