// The file of the open song changed on disk and the working copy changed the same place: show every difference and let the person choose
// per difference (A: the file, B: the working copy, both, or their own text). The server already merged what did not overlap.

import { api } from './api.js';
import { ask, el, toast } from './ui.js';
import { S, setDoc } from './store.js';
import { CHOICES, compose, conflictCount } from '../lib/merge-choice.mjs';

const LABELS = { theirs: 'A: the file on disk', mine: 'B: my working copy', both: 'Both (A, then B)', custom: 'My own text' };

const block = (lines, label) => el('div', { className: 'dc-side' }, [
  el('div', { className: 'dc-label', textContent: label }),
  el('pre', { className: 'dc-lines', textContent: lines.length ? lines.join('\n') : '(nothing)' }),
]);

/** Fetches the differences and asks. Returns true when the working copy took the answer (save again to write it). */
export async function resolveDiskConflict() {
  if (!S.path) return false;
  const disk = await api.get(`/api/doc/disk?path=${encodeURIComponent(S.path)}`);
  if (disk.state !== 'conflict') {
    if (disk.state === 'merged' || disk.state === 'reloaded') { await (await import('./store.js')).reloadDoc(); toast('The file changed on disk; the working copy was brought in line with it.'); return true; }
    return false;
  }
  const choices = {};
  const nodes = [];
  let n = 0;
  disk.regions.forEach((region, i) => {
    if (!region.conflict) return;
    n++;
    choices[i] = { mode: 'mine', custom: region.mine.join('\n') };
    const custom = el('textarea', { className: 'dc-custom', rows: 3, value: choices[i].custom, hidden: true, 'aria-label': `Your own text for difference ${n}` });
    custom.oninput = () => { choices[i].custom = custom.value; };
    const radios = CHOICES.map((mode) => {
      const input = el('input', { type: 'radio', name: `dc-${i}`, value: mode, checked: mode === 'mine' });
      input.onchange = () => { choices[i].mode = mode; custom.hidden = mode !== 'custom'; };
      return el('label', { className: 'dc-choice' }, [input, ` ${LABELS[mode]}`]);
    });
    nodes.push(el('div', { className: 'dc-region' }, [
      el('div', { className: 'dc-title', textContent: `Difference ${n} of ${conflictCount(disk.regions)}` }),
      el('div', { className: 'dc-sides' }, [block(region.theirs, 'A: the file on disk'), block(region.mine, 'B: my working copy')]),
      el('div', { className: 'dc-choices' }, radios),
      custom,
    ]));
  });
  const answer = await ask('The file changed on disk',
    'Someone or something else changed this song file while you were working, in the same place as you. Nothing was saved. Choose what each difference becomes; the other changes were already merged.',
    [{ label: 'Use my choices', value: true, primary: true }, { label: 'Cancel', value: false }], [el('div', { className: 'dc-list' }, nodes)]);
  if (!answer) return false;
  setDoc(await api.post('/api/doc/resolve', { path: S.path, baseVersion: S.doc.version, text: compose(disk.regions, choices), diskSha: disk.diskSha }));
  toast('Your choices are in the working copy. Save to write them.');
  return true;
}
