// The sound check: what is wrong with the sound of the built song (silent, very quiet, clipping, not heard, master loudness, cut values),
// each with a button to select the block and, when there is one, a button that puts the fix (normalize, a lower volume) into the song.

import { el, toast } from './ui.js';
import { api } from './api.js';
import { S, reloadDoc, select } from './store.js';
import { focusNode } from './canvas.js';

const FIX_LABEL = (fix) => (fix === 'normalize' ? 'Add normalize' : `Add volume ${fix.slice('volume:'.length)} dB`);

export async function openSoundCheck() {
  const path = S.path;
  if (!path) return;
  let dialog = document.querySelector('dialog.soundcheck');
  dialog?.remove();
  dialog = el('dialog', { className: 'soundcheck', 'aria-label': 'Sound check' });
  const body = el('div', { className: 'soundcheck-body' });
  const close = el('button', { type: 'button', textContent: 'Close', onclick: () => dialog.close() });
  dialog.append(el('h2', { textContent: 'Sound check' }), body, el('div', { className: 'soundcheck-actions' }, [close]));
  dialog.addEventListener('close', () => dialog.remove());
  document.body.append(dialog);
  dialog.showModal();

  const render = async () => {
    body.replaceChildren(el('p', { textContent: 'Looking at the built takes...' }));
    let result;
    try {
      result = await api.get(`/api/sound-check?path=${encodeURIComponent(path)}`);
    } catch (error) {
      body.replaceChildren(el('p', { textContent: error.message }));
      return;
    }

    if (!result.ok) { body.replaceChildren(el('p', { textContent: result.message })); return; }
    const rows = result.findings.map((f) => {
      const row = el('div', { className: `soundcheck-row ${f.severity}` }, [
        el('span', { className: 'soundcheck-severity', textContent: f.severity }),
        el('button', { type: 'button', className: 'soundcheck-block', textContent: f.block, title: 'Show this block', onclick: () => { if (!f.block.startsWith('line ')) { select(f.block); focusNode?.(f.block); dialog.close(); } } }),
        el('span', { className: 'soundcheck-message', textContent: f.message }),
      ]);
      if (f.fix) {
        row.append(el('button', { type: 'button', className: 'soundcheck-fix', textContent: FIX_LABEL(f.fix), title: 'Put an effect after this block (Undo takes it away again)', onclick: async () => {
          try {
            await api.post('/api/sound-check/fix', { path, block: f.block, fix: f.fix });
            await reloadDoc();
            toast(`${FIX_LABEL(f.fix)} after ${f.block}. Build again to hear it.`);
            await render();
          } catch (error) { toast(error.message); }
        } }));
      }

      return row;
    });
    const notBuilt = result.notBuilt > 0 ? el('p', { className: 'soundcheck-note', textContent: `${result.notBuilt} block(s) have no take yet: build the song for a full check.` }) : null;
    body.replaceChildren(...(rows.length ? rows : [el('p', { textContent: 'Nothing to report in the built takes.' })]), ...(notBuilt ? [notBuilt] : []));
  };

  await render();
}
