// The save control: a dot that says there are changes, a save icon and a menu with the less common actions (Save as, Revert, Export, Render block to file...).
// It sits in every editor, so the state and the actions are the same in the code view, the lyrics editor, the roll and the top bar (plan project-panel PP07).

import { api } from './api.js';
import { $, ask, el, showMenu, toast } from './ui.js';
import { S, dirty, reloadDoc, save, subscribe } from './store.js';
import { fileState, saveMenuState } from '../lib/file-state.mjs';
import { openProjects } from './projects.js';
import { openExportDialog } from './package.js';
import { revealInProject } from './projectpanel.js';
import { renderBlockToFile } from './renderfile.js';
import { download } from './player.js';
import { masterTakeOf } from '../lib/transport-model.mjs';
import { flat } from './store.js';

const hooks = new Set();
const controls = new Set();
/** Editors call this when what they hold changes without the store knowing (text typed, text applied). */
export function refreshSaveControls() { for (const control of controls) control.refresh?.(); }
/** An editor registers what must happen before a save: apply the text that was typed, write the notes of the roll. Returns the function that removes it. */
export function beforeSave(fn) { hooks.add(fn); return () => hooks.delete(fn); }
/** Editors register how to tell that they hold text that is not applied. */
const typedProbes = new Set();
export function typedProbe(fn) { typedProbes.add(fn); return () => typedProbes.delete(fn); }

const isTyped = () => [...typedProbes].some((probe) => { try { return !!probe(); } catch { return false; } });
export const currentFileState = () => fileState({ typed: isTyped(), changed: !!dirty() && !S.peek });

export async function saveEverything() {
  for (const fn of hooks) await fn();
  await save();
}

async function saveAs() {
  if (!S.path) return;
  const input = el('input', { value: S.path.replace(/^.*\//, '').replace(/\.amplet$/, '') + '-copy', 'aria-label': 'Name of the new song' });
  if (!(await ask('Save as', 'Writes the working copy to a new song of the project. The song that is open stays as it is.', [{ label: 'Save a copy', value: true, primary: true }, { label: 'Cancel', value: false }], [input]))) return;
  for (const fn of hooks) await fn();
  const made = await api.post('/api/doc/save-as', { path: S.path, name: input.value.trim() });
  toast(`Saved a copy as ${made.path}`);
  document.dispatchEvent(new CustomEvent('ample:open', { detail: made.path }));
}

const revertHandlers = new Set();
/** An editor with its own staged state registers { can(), run() }: Revert then restores that instead of the song. */
export function onRevert(handler) { revertHandlers.add(handler); return () => revertHandlers.delete(handler); }
const ownRevert = () => [...revertHandlers].find((h) => h.can());

async function revert() {
  const own = ownRevert();
  if (own) {
    if (!(await ask('Revert changes', 'Throws away every change since the file was saved.', [{ label: 'Revert', value: true, primary: true }, { label: 'Keep', value: false }]))) return;
    await own.run();
    return;
  }
  if (!(await ask('Revert changes', 'Throws away every change since the file was saved.', [{ label: 'Revert', value: true, primary: true }, { label: 'Keep', value: false }]))) return;
  await api.post('/api/doc/revert', { path: S.path });
  await reloadDoc();
  toast('Reverted to the saved file.');
}

/** Builds a save control. Put the element in the bar of an editor; it keeps itself up to date. */
export function createSaveControl({ compact = false } = {}) {
  const dot = el('span', { className: 'savedot', 'aria-live': 'polite' });
  const button = el('button', { type: 'button', className: 'savebtn', textContent: 'Save', title: 'Save (Ctrl+S)', 'aria-label': 'Save' });
  const more = el('button', { type: 'button', className: 'savemore', textContent: '▾', title: 'More: Save as, Revert, Export, Render block to file', 'aria-label': 'File menu', 'aria-haspopup': 'menu' });
  const root = el('span', { className: compact ? 'savectl compact' : 'savectl' }, compact ? [more] : [dot, button, more]);
  const songTake = () => masterTakeOf(S.build.takes || {}, flat().nodes.map((n) => n.id), flat().nodes);
  const hasTake = () => !!(S.sel && S.build.takes && S.build.takes[S.sel]);
  const refresh = () => {
    const state = currentFileState();
    const can = saveMenuState(state, { hasSong: !!S.path, hasTake: hasTake() });
    dot.textContent = state.mark ? `${state.mark} ${state.label}` : '';
    dot.title = state.title;
    dot.dataset.state = state.id;
    button.disabled = !can.save;
    root.dataset.state = state.id;
  };
  button.onclick = () => saveEverything().catch((error) => toast(error.message || String(error)));
  more.onclick = (event) => {
    event.stopPropagation();
    const state = currentFileState();
    const can = saveMenuState(state, { hasSong: !!S.path, hasTake: hasTake() });
    const rect = more.getBoundingClientRect();
    const item = (label, enabled, fn) => (enabled ? { label, fn: () => Promise.resolve(fn()).catch((error) => toast(error.message || String(error))) } : { head: `${label} (not now)` });
    showMenu(rect.left, rect.bottom + 2, [
      item('Save', can.save, saveEverything),
      item('Save as...', can.saveAs, saveAs),
      item('Revert changes', can.revert || !!ownRevert(), revert),
      '-',
      item('Export...', can.export, () => openProjects('backup')),
      item('Export package (.ampletx)...', !!S.path, openExportDialog),
      item('Render block to file...', can.renderBlock, () => renderBlockToFile(S.sel)),
      item('Download the song audio (WAV)...', !!songTake(), () => download(songTake(), 'wav')),
      item('Download the song audio (MP3)...', !!songTake(), () => download(songTake(), 'mp3')),
      '-',
      item('Show in the project panel', can.show, () => revealInProject(S.path)),
      item('Copy path', can.copy, () => navigator.clipboard.writeText(S.path).then(() => toast('Path copied.'))),
    ]);
  };
  subscribe(refresh);
  refresh();
  root.refresh = refresh;
  controls.add(root);
  return root;
}

// Ctrl+S saves from anywhere in the app (the code editor and the editors register what must be applied first)
addEventListener('keydown', (event) => {
  if ((event.ctrlKey || event.metaKey) && !event.shiftKey && !event.altKey && event.key.toLowerCase() === 's' && S.path && !S.peek) {
    event.preventDefault();
    saveEverything().catch((error) => toast(error.message || String(error)));
  }
});
