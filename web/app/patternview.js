// The Patterns view (plan patterns P04): one full-width view for a drums block, in the spirit of a step sequencer. The lanes (voices) with their samples, the grid of the
// selected pattern and the strip of sections. It is built from the parts that exist (the drum grid with its pattern chooser and section strip, the voice map field), so
// the text it writes is exactly what the properties panel writes. @ample:patterns

import { $, el, toast } from './ui.js';
import { S, edit, flat, readOnly, subscribe } from './store.js';
import { V, registerView, setView } from './views.js';
import { renderDrumGrid } from './stepseq.js';
import { fieldFor } from './fields.js';
import { createSaveControl } from './savecontrol.js';
import { draggedFile } from './dragfile.js';
import { kindOf } from '../lib/file-kinds.mjs';

const PV = { id: null, token: 0 };

/** Opens the view for a drums block. */
export function openPatternView(blockId) {
  PV.id = blockId;
  setView('pat');
}

export function initPatternView() {
  registerView('pat', render);
  subscribe(() => { if (V.mode === 'pat') render().catch(() => {}); });
}

/** The map text with an entry for a voice: voice:folder/sample, entries separated by ; (an entry of the same voice is replaced). */
export function withMapEntry(mapText, voice, folderAndSample) {
  const entries = String(mapText ?? '').replace(/^"|"$/g, '').split(';').map((e) => e.trim()).filter(Boolean).filter((e) => !e.toLowerCase().startsWith(`${voice.toLowerCase()}:`));
  entries.push(`${voice}:${folderAndSample}`);
  return entries.join(';');
}

/** The folder and the sample name of a project audio file (samples/kit-b/clap.wav gives samples/kit-b/clap), or null. */
export function folderAndSample(path) {
  const m = /^(.*)\/([^/]+)\.(wav|flac|mp3|ogg|aif|aiff)$/i.exec(path);
  return m ? `${m[1]}/${m[2]}` : null;
}

async function render() {
  const root = $('#pt');
  if (!root) return;
  const token = ++PV.token;
  const n = flat().nodes.find((x) => x.id === PV.id);
  root.innerHTML = '';
  const bar = el('div', { className: 'bmbar' });
  bar.append(el('button', { id: 'pt-back', textContent: '← Back to the blocks', title: 'The patterns are written in the song as you make them', onclick: () => setView('blocks') }), el('b', { textContent: n ? `Patterns: ${n.title}` : 'Patterns' }), el('span', { className: 'sp' }), createSaveControl());
  root.append(bar);
  if (!n || n.tpl !== 'drums') {
    root.append(el('p', { className: 'note', style: 'padding:12px', textContent: 'Choose a drums block and open its Patterns view from the properties.' }));
    return;
  }
  const locked = readOnly() || n.flags.includes('lock');
  const body = el('div', { className: 'ptbody' });
  const grid = el('div', { id: 'pt-grid' });
  body.append(el('p', { className: 'note', textContent: 'Pick a pattern, click the steps, and put patterns in order as sections below the grid. A sample from the project panel dropped on a lane (a row of the grid) gives that voice its sample.' }), grid);
  root.append(body);
  await renderDrumGrid(grid, n, locked);
  if (token !== PV.token) return;
  // a sample dropped on a lane: the voice plays that sample (written to the map parameter)
  grid.querySelectorAll('.seqrow[data-voice]').forEach((row) => {
    const voice = row.dataset.voice;
    row.ondragover = (event) => { const drag = draggedFile(); if (drag && !drag.isDir && kindOf(drag.path)?.id === 'audio') { event.preventDefault(); row.classList.add('drop-ok'); } };
    row.ondragleave = () => row.classList.remove('drop-ok');
    row.ondrop = (event) => {
      event.preventDefault();
      row.classList.remove('drop-ok');
      const drag = draggedFile();
      const target = drag && folderAndSample(drag.path);
      if (!target) { toast('Drop a WAV or other audio file of the project on a lane.'); return; }
      edit([{ type: 'setParameter', block: n.id, name: 'map', value: withMapEntry(n.params.map, voice, target) }]);
      toast(`${voice} now plays ${target}`);
    };
  });
  // the samples of the lanes: the voice map of the block, as in the properties
  const meta = (S.tpl.drums?.parameters || []).find((p) => p.name === 'map');
  if (meta) {
    const field = fieldFor('map', meta, (n.params.map ?? '').replace(/^"|"$/g, ''), { disabled: locked, title: meta.documentation || '', params: n.params, save: (text) => edit([{ type: 'setParameter', block: n.id, name: 'map', value: text }]) });
    if (field) body.append(el('h4', { textContent: 'Samples per lane', style: 'padding-left:0' }), field);
  }
}
