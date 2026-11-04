// The Storage page of the projects view: what the takebank uses, what is protected, a dry run of a clean-up with its impact,
// a progress bar with cancel while it runs, and the options (soft limit, how many older takes stay, archive folder).
import { api } from './api.js';
import { el, toast } from './ui.js';
import { openDefrag } from './defrag.js';

const GB = 1024 ** 3;
const size = (b) => (b >= GB ? `${(b / GB).toFixed(1)} GB` : b >= 1024 ** 2 ? `${(b / 1024 ** 2).toFixed(0)} MB` : `${Math.max(0, Math.round(b / 1024))} KB`);
const REASONS = {
  superseded: 'Older takes of a block that has a newer one',
  missing: 'Takes whose data is already gone',
  current: 'The current take of each block',
  input: 'Takes the current takes were made from',
  chosen: 'Chosen takes and variants',
  baked: 'Baked or pinned takes (named in a song or a history revision)',
  recent: 'Made in the last hour',
  kept: 'Kept by your "older takes to keep" option',
  final: 'Finals',
};

export async function storagePage(body) {
  let overview;
  try { overview = await api.get('/api/storage'); } catch (e) { body.append(el('p', { className: 'seerr error', textContent: `${e.code ? `${e.code}: ` : ''}${e.message}` })); return; }
  const o = overview.overview;
  body.append(el('h2', { textContent: 'Storage' }));
  body.append(el('p', { className: 'note', textContent: 'Everything AMPLE renders is kept as takes in the project, so a song never has to render twice. Over time that adds up. Nothing here is removed without your click, and nothing that is baked, chosen, tagged or the current take of a block is ever touched: removed takes simply render again when they are needed.' }));

  const bar = el('div', { id: 'st-bar', style: 'display:flex;height:18px;border-radius:4px;overflow:hidden;background:var(--panel2,#333);margin:8px 0' });
  const total = Math.max(1, o.dataBytes);
  bar.append(el('div', { title: `Protected ${size(o.protectedBytes)}`, style: `width:${(o.protectedBytes / total) * 100}%;background:var(--ok,#3a8)` }));
  bar.append(el('div', { title: `Can be given back ${size(o.reclaimableBytes)}`, style: `width:${(o.reclaimableBytes / total) * 100}%;background:var(--warn,#c93)` }));
  body.append(bar);
  body.append(el('p', { id: 'st-summary', textContent: `${size(o.dataBytes)} in ${o.takes} take(s) at ${o.dataFolder}. Protected ${size(o.protectedBytes)}, can be given back ${size(o.reclaimableBytes)}. AI working files ${size(o.aiBytes)}, temporary files ${size(o.tmpBytes)}. Free on the drive: ${size(o.freeBytes)} of ${size(o.totalBytes)}.` }));
  if (o.overLimit) body.append(el('p', { className: 'seerr warning', id: 'st-over', textContent: `This project is over its soft limit of ${o.softLimitGb} GB. A clean-up can give back about ${size(o.reclaimableBytes)}; nothing protected is touched.` }));
  if (o.lowDisk) body.append(el('p', { className: 'seerr warning', id: 'st-low', textContent: 'The drive is low on free space (under 5 GB). Free some space before you render a full song.' }));
  if (o.duplicateTakeRows) body.append(el('p', { className: 'note', textContent: `${o.duplicateTakeRows} take(s) hold exactly the same data as another take (the data is stored once, so this costs no space).` }));

  const why = el('table', { className: 'tbl', id: 'st-reasons' });
  why.append(el('tr', {}, ['What', 'Takes', 'Size'].map((t) => el('th', { textContent: t }))));
  o.byReason.forEach((r) => why.append(el('tr', {}, [el('td', { textContent: REASONS[r.name] || r.name }), el('td', { textContent: String(r.takes) }), el('td', { textContent: size(r.bytes) })])));
  body.append(el('h4', { textContent: 'Where the space goes', style: 'padding:0;margin:14px 0 4px' }), why);

  body.append(el('h4', { textContent: 'Clean up', style: 'padding:0;margin:14px 0 4px' }));
  const area = el('div', { id: 'st-clean' });
  body.append(area);
  const showRunning = (state) => {
    area.replaceChildren();
    const pct = state.totalBytes ? Math.min(100, Math.round((state.bytes / state.totalBytes) * 100)) : state.total ? Math.round((state.done / state.total) * 100) : 0;
    const progress = el('progress', { id: 'st-progress', max: 100, value: pct, style: 'width:100%' });
    area.append(el('p', { id: 'st-running', textContent: `Cleaning up: ${size(state.bytes)} of ${size(state.totalBytes)} (${state.done} of ${state.total} steps)` }), progress,
      el('button', { id: 'st-cancel', textContent: 'Cancel', onclick: async (e) => {
        const b = e.currentTarget;
        b.disabled = true; b.textContent = 'Cancelling...';   // stays so until the status changes and the page is rebuilt
        try { await api.post('/api/storage/cancel', {}); } catch (err) { b.disabled = false; b.textContent = 'Cancel'; toast(`Could not cancel the clean-up: ${err.message}`); }
      } }));
  };
  const poll = async () => {
    let s;
    try { s = await api.get('/api/storage/status'); } catch (e) { toast(`Could not read the clean-up status: ${e.message}`); return; }
    if (s.state === 'running') { showRunning(s); setTimeout(poll, 600); return; }
    if (s.state === 'done') toast(`Clean-up done: ${size(s.bytes)} given back.`);
    else if (s.state === 'cancelled') toast(`Clean-up stopped after ${size(s.bytes)}; nothing is damaged.`);
    else if (s.state === 'failed') toast(`Clean-up failed: ${s.error}`);
    if (s.state !== 'idle') { body.replaceChildren(); await storagePage(body); }
  };
  const afterDefrag = async (state) => {
    if (state.state === 'closed') { const now = await api.get('/api/storage/status'); if (now.state === 'running') poll(); return; }
    body.replaceChildren(); await storagePage(body);
  };
  // the clean-up view opens at once and looks at the takes block by block while the dry run is worked out
  const dryRun = async () => {
    openDefrag({ planPromise: api.post('/api/storage/plan', {}).then((r) => r.plan), overview: o, reasons: REASONS, size, onFinished: afterDefrag });
  };
  if (overview.clean.state === 'running') {
    showRunning(overview.clean);
    const standIn = { bytes: overview.clean.totalBytes, tempBytes: 0, protectedBytes: o.protectedBytes, byReason: [{ name: 'superseded', takes: overview.clean.total, bytes: overview.clean.totalBytes }], fingerprint: 'running' };
    openDefrag({ plan: standIn, overview: o, reasons: REASONS, size, running: true, onFinished: afterDefrag });
  } else area.append(el('button', { id: 'st-dry', textContent: 'See what a clean-up would do...', onclick: () => dryRun().catch((e) => toast(e.message)) }));

  if (overview.plain && overview.plain.files > 0) {
    body.append(el('h4', { textContent: 'Smaller takes', style: 'padding:0;margin:18px 0 4px' }),
      el('p', { className: 'note', id: 'st-plain', textContent: `${overview.plain.files} take file(s) (${size(overview.plain.bytes)}) were stored before takes were compressed. Compressing them is lossless: the sound, the takes and their ids stay exactly the same, and silence and quiet passages shrink to almost nothing (often to a third or less).` }),
      el('button', { id: 'st-compress', textContent: 'Compress the existing takes', onclick: async () => { try { await api.post('/api/storage/compress', {}); poll(); } catch (e) { toast(`${e.code ? e.code + ': ' : ''}${e.message}`); } } }));
  }
  body.append(el('h4', { textContent: 'Options of this project', style: 'padding:0;margin:18px 0 4px' }));
  const s = overview.settings;
  const field = (id, label, input, note) => body.append(el('div', { className: 'row' }, [el('label', { htmlFor: id, textContent: label, style: 'min-width:220px' }), input]), el('p', { className: 'note', textContent: note }));
  const limit = el('input', { id: 'st-limit', type: 'number', min: 0, step: 1, value: String(s.softLimitGb) });
  const keep = el('input', { id: 'st-keep', type: 'number', min: 0, max: 50, value: String(s.keepSuperseded) });
  const archive = el('input', { id: 'st-archive', value: s.archivePath || '', placeholder: 'empty: reclaimed data is deleted', style: 'width:320px' });
  field('st-limit', 'Soft limit (GB, 0 is off)', limit, 'Above this size AMPLE only suggests a clean-up. Nothing is ever removed because of the limit.');
  field('st-keep', 'Older takes to keep per block', keep, 'A clean-up keeps the current take and this many older takes of each block.');
  field('st-archive', 'Move to an archive folder', archive, 'A clean-up moves the data here (for example on another drive) instead of deleting it.');
  body.append(el('h4', { textContent: 'Where the takes are kept', style: 'padding:0;margin:18px 0 4px' }), el('p', { className: 'note', textContent: 'The take data can live on another drive than the project (for example a roomy D: drive). Moving shows progress and can be stopped; what is not moved yet is still found where it is.' }));
  const where = el('input', { id: 'st-where', value: s.dataPath || '', placeholder: 'for example D:\\ample-takes\\this-project (empty: inside the project)', style: 'width:420px' });
  body.append(el('div', { className: 'row' }, [where, el('button', { id: 'st-move', textContent: 'Move the takes there', onclick: async () => { try { await api.post('/api/storage/move', { path: where.value.trim() }); poll(); } catch (e) { toast(`${e.code ? e.code + ': ' : ''}${e.message}`); } } })]));
  body.append(el('button', { id: 'st-save', textContent: 'Save the options', onclick: async () => {
    try { await api.post('/api/storage/settings', { softLimitGb: Number(limit.value), keepSuperseded: Number(keep.value), archivePath: archive.value.trim() }); } catch (e) { toast(`${e.code ? e.code + ': ' : ''}Could not save the options: ${e.message}`); return; }
    toast('Storage options saved.');
    body.replaceChildren(); await storagePage(body);
  } }));
}

// A calm hint when the project grew past its soft limit or the drive is low; asked once after the studio opened.
export async function storageHint() {
  try {
    const { overview } = await api.get('/api/storage');
    if (!overview.overLimit && !overview.lowDisk) return;
    const mb = `${(overview.reclaimableBytes / GB).toFixed(1)} GB`;
    toast(overview.overLimit
      ? `This project is over its storage limit. A clean-up can give back about ${mb}: Projects, then Storage.`
      : 'The drive is low on free space. Projects, then Storage, shows what can be given back.');
  } catch { /* the hint is optional */ }
}
