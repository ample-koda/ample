// Files attached to a message to Koda or to the assistant: a screenshot of notes, a score, a text. Pick them with the paperclip, drop them on the window, or paste them (Ctrl+V of a
// picture from the clipboard). Each file is sent to the server at once (POST /api/ai/attachments: at most 40 MB, no programs) and shows as a chip with a thumbnail for a picture;
// the message then carries the ids. The server shows pictures to the model and puts the other files in its working copy. @ample:attachments

import { api } from './api.js';
import { el, toast } from './ui.js';

const MAX_BYTES = 40 * 1024 * 1024, MAX_FILES = 8;
const size = (n) => (n >= 1048576 ? `${(n / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`);
export const IMAGE_ACCEPT = 'image/png,image/jpeg,image/gif,image/webp,.png,.jpg,.jpeg,.gif,.webp,.txt,.md,.csv,.json,.amplet,.amplec,.ampleseq,.lrc,.chordpro,.mid,.pdf';

/**
 * One tray of attachments. `chips` is the element to put in the layout (`view()` makes another one that shows the same files, for a second place); `add` is the paperclip button
 * (`button()` makes another); `ids()` are the ids of the files that are ready; `take()` the same and the tray is emptied (call it when the message is sent);
 * `pasteInto(element)` makes Ctrl+V of a file or picture in that element attach it. `dropTarget` takes dropped files, `pasteTarget` takes pasted ones.
 */
export function fileAttachment({ onChange, dropTarget, pasteTarget } = {}) {
  const items = [];   // { id, name, size, kind, url, busy }
  const views = new Set();
  let chips = null;   // the first view: it stays, the others go when they are no longer in the page
  const view = () => { const v = el('span', { className: 'att-chips' }); views.add(v); paint(); return v; };
  const chipOf = (it) => el('span', { className: `att-chip${it.busy ? ' busy' : ''}`, title: `${it.name} (${size(it.size)})${it.kind === 'image' ? ': the model can see this picture' : ': put in the working copy of the task'}` }, [
    it.url ? el('img', { src: it.url, alt: '', className: 'att-thumb' }) : el('i', { className: 'att-file', textContent: '▤', 'aria-hidden': 'true' }),
    el('span', { className: 'att-name', textContent: it.busy ? `${it.name}...` : it.name }),
    el('button', { type: 'button', className: 'att-x', textContent: '×', title: `Remove ${it.name}`, 'aria-label': `Remove ${it.name}`, onclick: (e) => { e.stopPropagation(); e.preventDefault(); remove(it); } }),
  ]);
  chips = view();
  function paint() {
    for (const v of [...views]) {
      if (v !== chips && !v.isConnected && views.size > 1) { views.delete(v); continue; }
      v.replaceChildren(...items.map(chipOf));
    }
    onChange?.();
  }
  const remove = (it) => { const at = items.indexOf(it); if (at >= 0) items.splice(at, 1); if (it.url) URL.revokeObjectURL(it.url); paint(); };
  const upload = async (file) => {
    if (items.length >= MAX_FILES) { toast(`At most ${MAX_FILES} files with one message.`); return; }
    if (file.size > MAX_BYTES) { toast(`${file.name || 'The file'} is larger than ${MAX_BYTES / 1048576} MB.`); return; }
    if (file.size === 0) { toast(`${file.name || 'The file'} is empty.`); return; }
    const picture = /^image\//.test(file.type);
    const name = file.name || `pasted-${new Date().toISOString().replace(/[^0-9]/g, '').slice(0, 14)}.${(file.type.split('/')[1] || 'png').replace('jpeg', 'jpg')}`;
    const it = { id: '', name, size: file.size, kind: picture ? 'image' : 'file', url: picture ? URL.createObjectURL(file) : '', busy: true };
    items.push(it); paint();
    try {
      const r = await api.postBinary(`/api/ai/attachments?name=${encodeURIComponent(name)}`, new Uint8Array(await file.arrayBuffer()));
      it.id = r.id; it.name = r.name; it.kind = r.kind; it.busy = false; paint();
    } catch (error) { remove(it); toast(error.message || String(error)); }
  };
  const addAll = async (files) => { for (const f of files) await upload(f); };
  const pick = el('input', { type: 'file', accept: IMAGE_ACCEPT, multiple: true, hidden: true, 'aria-label': 'Attach files' });
  pick.onchange = async () => { await addAll([...pick.files]); pick.value = ''; };
  const button = () => el('button', { type: 'button', className: 'att-add', textContent: '📎', title: 'Attach a picture or a file (a screenshot of notes, a score, a text): pick it, drop it here, or paste it with Ctrl+V', 'aria-label': 'Attach files', onclick: (e) => { e.stopPropagation(); pick.click(); } });
  const pasteInto = (target) => target?.addEventListener('paste', (e) => {
    const files = [...(e.clipboardData?.files || [])];
    if (!files.length) return;   // plain text pastes as always
    e.preventDefault(); e.stopPropagation();
    addAll(files);
  });
  pasteInto(pasteTarget);
  if (dropTarget) {
    const has = (e) => [...(e.dataTransfer?.types || [])].includes('Files');
    const over = (e) => { if (has(e)) { e.preventDefault(); dropTarget.classList.add('att-drop'); } };
    dropTarget.addEventListener('dragover', over);
    dropTarget.addEventListener('dragenter', over);
    dropTarget.addEventListener('dragleave', (e) => { if (e.target === dropTarget) dropTarget.classList.remove('att-drop'); });
    dropTarget.addEventListener('drop', (e) => {
      dropTarget.classList.remove('att-drop');
      if (!has(e) || !e.dataTransfer.files.length) return;
      e.preventDefault(); e.stopPropagation();
      addAll([...e.dataTransfer.files]);
    });
  }
  const ids = () => items.filter((i) => i.id && !i.busy).map((i) => i.id);
  return {
    add: button(), button, pick, chips, view, pasteInto, ids, count: () => items.length, busy: () => items.some((i) => i.busy),
    take: () => { const taken = ids(); for (const it of items) if (it.url) URL.revokeObjectURL(it.url); items.length = 0; paint(); return taken; },
  };
}

/** The tray that Koda shares: every text field of every bubble puts its files in it, and the next message to Koda takes them. */
let kodaTray = null;
export function kodaAttachments() { return (kodaTray ??= fileAttachment()); }

/** The paperclip and the chips for one text field of a Koda bubble (pasting into the field attaches too): [button, chips]. */
export function kodaAttachField(input) {
  const tray = kodaAttachments();
  tray.pasteInto(input);
  return [tray.button(), tray.view()];
}
