// Render a block to a file of the project: the audio of its take, as WAV or MP3, written into a folder of the project (plan project-panel PP08).

import { api } from './api.js';
import { ask, el, toast } from './ui.js';
import { S, build } from './store.js';
import { refreshProjectPanel, revealInProject } from './projectpanel.js';

/** Asks for a name, folder and format and writes the take of the block (it is built first when it has no take). */
export async function renderBlockToFile(blockName) {
  const block = blockName || S.sel;
  if (!block) { toast('Select a block first.'); return; }
  let take = S.build.takes?.[block];
  if (!take) {
    if (!(await ask('Render to file', `${block} has no take yet. Build it first?`, [{ label: 'Build and render', value: true, primary: true }, { label: 'Cancel', value: false }]))) return;
    try { await build(block); } catch (e) { toast(`${block} could not be built: ${e.message}`); return; }
    take = S.build.takes?.[block];
    if (!take) { toast(`${block} could not be built.`); return; }
  }
  const name = el('input', { value: block, 'aria-label': 'File name without extension' });
  const folder = el('input', { value: 'renders', 'aria-label': 'Project folder' });
  const format = el('select', { 'aria-label': 'Format' }, [el('option', { value: 'wav', textContent: 'WAV (the take as it is)' }), el('option', { value: 'mp3', textContent: 'MP3 (encoded with ffmpeg)' })]);
  const choice = await ask('Render to file', `Writes the audio of ${block} to a file of the project, or gives it to you as a download (the folder is then not used).`, [{ label: 'Render', value: 'save', primary: true }, { label: 'Download', value: 'download' }, { label: 'Cancel', value: false }], [name, folder, format]);
  if (!choice) return;
  if (choice === 'download') {
    try { const size = await api.downloadTake(take, name.value.trim() || block, format.value); toast(`Downloading ${name.value.trim() || block}.${format.value} (${Math.round(size / 1024)} KB)`); } catch (e) { toast(`Could not make the download: ${e.message}`); }
    return;
  }
  let made;
  try { made = await api.post(`/api/takes/${encodeURIComponent(take)}/export`, { name: name.value.trim(), folder: folder.value.trim(), format: format.value }); } catch (e) { toast(`Could not render the file: ${e.message}`); return; }
  toast(`Rendered ${made.path} (${Math.round(made.sizeBytes / 1024)} KB)`);
  await refreshProjectPanel().catch(() => {});
  revealInProject(made.path);
}
