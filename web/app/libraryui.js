// The library manager: linked folders (catalogs) as a tree, the assets in a table with their status, details with where an
// asset is used, link / scan / relocate / read-only / unlink, import into the project, duplicates, and the state of ample.lock.
// @ample:library-manager @ample:mounts

import { api } from './api.js';
import { $, ask, el, showMenu, toast } from './ui.js';
import { S, notify, select } from './store.js';
import { registerView, setView } from './views.js';
import { dangerousAct } from './dangerous.js';
import { focusNode } from './canvas.js';
import { registerPlaybackAudio } from './playback-volume.js';
import { stop as stopPlayer } from './player.js';

const LB = { data: null, lock: null, uses: [], sel: { kind: 'all' }, asset: null, q: '', open: new Set() };
// one sound at a time can be tried out in the table: the play button of a row, or the same button again to stop it
const AUDIO_FILE = /.(wav|mp3|ogg|flac|aiff?)$/i;
let tryOut = null;   // { uuid, audio }
function stopTryOut() { if (tryOut) { tryOut.audio.pause(); tryOut = null; } }
document.addEventListener('ample:view', (event) => { if (event.detail !== 'lib') stopTryOut(); });   // leaving the Library stops the sound
function toggleTryOut(a) {
  const same = tryOut?.uuid === a.uuid;
  stopTryOut();
  if (same) { render(); return; }
  stopPlayer();   // the song player and a sound try-out do not play over each other
  const audio = registerPlaybackAudio(new Audio(`/api/library/audio?asset=${encodeURIComponent(a.uuid)}`));
  const entry = { uuid: a.uuid, audio };
  tryOut = entry;
  audio.onended = () => { if (tryOut === entry) { tryOut = null; render(); } };
  audio.onerror = () => { if (tryOut === entry) { tryOut = null; render(); } toast('This sound could not be played.'); };
  audio.play().catch(() => { if (tryOut === entry) { tryOut = null; render(); } });
  render();
}
const kb = (n) => (n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1048576).toFixed(1)} MB`);

async function load() {
  LB.data = await api.get('/api/library');
  try { LB.lock = await api.get('/api/lock'); } catch { LB.lock = { exists: false, checks: [] }; }
  try { LB.uses = S.path ? await api.get(`/api/library/uses?path=${encodeURIComponent(S.path)}`) : []; } catch { LB.uses = []; }
}

export async function refreshLibrary() { await load(); if ($('#lb').style.display !== 'none') render(); }

const lockOf = (a) => (LB.lock ? LB.lock.checks.find((c) => c.entry.id === a.uuid || (c.entry.section === 'pack' && c.entry.name === a.name)) : null);
const usesOf = (a) => LB.uses.filter((u) => u.assetUuid === a.uuid);
const mountOf = (id) => LB.data.mounts.find((m) => m.id === id);

function statusOf(a) {
  const m = mountOf(a.mountId);
  if (!m || m.state !== 'online') return 'offline';
  if (a.missing) return 'missing';
  const c = lockOf(a);
  return c && c.status !== 'ok' ? c.status : 'ok';
}

function visible() {
  const q = LB.q.toLowerCase();
  const dupes = new Set((LB.data.duplicates || []).flat());
  return LB.data.assets.filter((a) => {
    if (LB.sel.kind === 'mount' && a.mountId !== LB.sel.id) return false;
    if (LB.sel.kind === 'folder' && !(a.mountId === LB.sel.mount && (a.relPath.startsWith(`${LB.sel.path}/`)))) return false;
    if (LB.sel.kind === 'dupes' && !dupes.has(a.uuid)) return false;
    if (LB.sel.kind === 'problems' && statusOf(a) === 'ok') return false;
    return !q || a.name.toLowerCase().includes(q) || a.kind.includes(q) || (a.tags || '').toLowerCase().includes(q);
  });
}

function render() {
  const root = $('#lb');
  if (!LB.data) return;
  root.innerHTML = '';
  const tree = el('div', { className: 'lbtree tree', role: 'tree', 'aria-label': 'Assets and linked folders' });
  tree.onkeydown = (e) => {
    const items = [...tree.querySelectorAll('[role=treeitem]')];
    const at = items.indexOf(document.activeElement);
    if (at < 0) return;
    const go = (i) => { e.preventDefault(); const n = items[Math.max(0, Math.min(items.length - 1, i))]; items.forEach((x) => { x.tabIndex = -1; }); n.tabIndex = 0; n.focus(); };
    if (e.key === 'ArrowDown') go(at + 1);
    else if (e.key === 'ArrowUp') go(at - 1);
    else if (e.key === 'Home') go(0);
    else if (e.key === 'End') go(items.length - 1);
    else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); items[at].click(); }
  };
  const mid = el('div', { className: 'lbmid lbcenter' });
  const det = el('div', { className: 'lbdet' });
  const dupes = (LB.data.duplicates || []).flat().length;
  const problems = LB.data.assets.filter((a) => statusOf(a) !== 'ok').length;
  const row = (label, sel, depth, extra) => {
    const same = JSON.stringify(sel) === JSON.stringify(LB.sel);
    const r = el('div', { className: `rev${same ? ' sel' : ''}`, style: `padding-left:${8 + depth * 14}px`, role: 'treeitem', 'aria-level': String(depth + 1), 'aria-selected': String(same), tabIndex: same || (!LB.sel && label.startsWith('📚')) ? 0 : -1 });
    r.append(el('span', { textContent: label }), extra || '');
    r.onclick = () => { const keys = document.activeElement?.getAttribute?.('role') === 'treeitem'; LB.sel = sel; LB.asset = null; render(); if (keys) document.querySelector('#lb .rev.sel')?.focus(); };
    return r;
  };
  tree.append(el('h4', { textContent: 'Assets', style: 'padding:8px 10px 2px;margin:0' }), row(`📚 All assets (${LB.data.assets.length})`, { kind: 'all' }, 0));
  tree.append(el('h5', { textContent: 'Catalogs and folders (linked)', style: 'margin:8px 8px 2px' }));
  LB.data.mounts.forEach((m) => {
    const as = LB.data.assets.filter((a) => a.mountId === m.id);
    const badge = m.state !== 'online' ? el('span', { className: 'tag missing', textContent: m.state, style: 'margin-left:6px' }) : '';
    const r = row(`${m.readOnly ? '🔒 ' : '✎ '}${m.scope === 'global' ? '🔗 ' : ''}${m.name} (${as.length})`, { kind: 'mount', id: m.id }, 0, badge);
    r.dataset.mount = m.id;
    r.title = `${m.name}\ncatalog ${m.uuid}\n${m.path}\n${m.scope}, ${m.readOnly ? 'read-only' : 'read-write'}`;
    r.oncontextmenu = (e) => { e.preventDefault(); mountMenu(e, m); };
    tree.append(r);
    const folders = new Set();
    as.forEach((a) => { const parts = a.relPath.split('/').slice(0, -1); parts.forEach((_, i) => folders.add(parts.slice(0, i + 1).join('/'))); });
    [...folders].sort().forEach((f) => tree.append(row(`📁 ${f.split('/').pop()} (${as.filter((a) => a.relPath.startsWith(`${f}/`)).length})`, { kind: 'folder', mount: m.id, path: f }, f.split('/').length)));
  });
  tree.append(el('h5', { textContent: 'Views', style: 'margin:8px 8px 2px' }), row(`⧉ Duplicates (${dupes})`, { kind: 'dupes' }, 0), row(`⚠ Problems (${problems})`, { kind: 'problems' }, 0));
  tree.append(el('button', { textContent: '+ Link a folder...', id: 'lb-link', style: 'margin:10px 8px;width:calc(100% - 16px)', onclick: linkDialog }));

  const bar = el('div', { className: 'bmbar' });
  const q = el('input', { placeholder: 'Search the library...', value: LB.q, style: 'width:220px' });
  q.oninput = () => { LB.q = q.value; renderTable(tbl); };
  const lock = LB.lock;
  const counts = lock && lock.exists ? lock.checks.reduce((m, c) => ({ ...m, [c.status]: (m[c.status] || 0) + 1 }), {}) : null;
  bar.append(q, el('span', { id: 'lb-lock', className: 'note', style: 'margin:0', textContent: !lock || !lock.exists ? 'ample.lock: none yet' : `ample.lock: ${counts.ok || 0} ok${counts.changed ? `, ${counts.changed} changed` : ''}${counts.missing ? `, ${counts.missing} missing` : ''}${counts.offline ? `, ${counts.offline} offline` : ''}` }),
    el('button', { id: 'lb-lockupdate', textContent: 'Update ample.lock', title: 'Pin what the songs use now (accepts changed assets)', onclick: async () => { try { await api.post('/api/lock/update'); toast('ample.lock updated.'); await refreshLibrary(); } catch (e) { toast(e.message); } } }));
  const tbl = el('div', { className: 'lbtbl' });
  mid.append(bar, tbl);
  renderTable(tbl);
  renderDetails(det);
  root.append(tree, mid, det);
}

function renderTable(tbl) {
  tbl.innerHTML = '';
  tbl.append(el('div', { className: 'lbrow lbhead' }, ['name', 'kind', 'status', 'hash', 'size', 'rev', 'used'].map((t) => el('span', { textContent: t }))));
  const list = visible();
  if (!list.length) tbl.append(el('p', { className: 'note', textContent: LB.data.mounts.length ? 'Nothing here.' : 'Nothing is linked yet. Link a folder of samples, packs, voices or lyrics: nothing is found automatically, and a folder is read-only unless you say otherwise.' }));
  list.slice(0, 1000).forEach((a) => {
    const st = statusOf(a);
    const r = el('div', { className: `lbrow${LB.asset && LB.asset.uuid === a.uuid ? ' sel' : ''}` });
    r.dataset.asset = a.uuid;
    const playable = AUDIO_FILE.test(a.relPath || a.name || '');
    const playing = tryOut?.uuid === a.uuid;
    const nameCell = el('span', { className: 'lbname' }, playable ? [el('button', { type: 'button', className: `lbplay${playing ? ' on' : ''}`, textContent: playing ? '■' : '▶', title: playing ? 'Stop' : 'Play this sound', 'aria-label': `${playing ? 'Stop' : 'Play'} ${a.name}`, onclick: (event) => { event.stopPropagation(); toggleTryOut(a); } }), el('span', { textContent: a.name })] : [el('span', { className: 'lbplay-space', 'aria-hidden': 'true' }), el('span', { textContent: a.name })]);
    r.append(nameCell, el('span', { textContent: a.kind }), el('span', { className: `tag ${st === 'ok' ? 'ok' : st === 'changed' ? 'peek' : 'missing'}`, textContent: st }), el('span', { textContent: a.hash ? a.hash.slice(7, 15) : '-', title: a.hash || '' }), el('span', { textContent: kb(a.size) }), el('span', { textContent: `r${a.revision}` }), el('span', { textContent: usesOf(a).length || '' }));
    r.onclick = () => { LB.asset = a; render(); };
    tbl.append(r);
  });
}

function renderDetails(det) {
  det.innerHTML = '';
  const a = LB.asset;
  if (!a) {
    const m = LB.sel.kind === 'mount' ? mountOf(LB.sel.id) : null;
    det.append(el('h4', { textContent: m ? 'Linked folder' : 'Details', style: 'padding:8px 10px 2px' }));
    if (m) {
      const kv = el('div', { className: 'pad' });
      [['name', m.name], ['path', m.path], ['catalog id', m.uuid], ['scope', m.scope], ['access', m.readOnly ? 'read-only (never written to)' : 'read-write'], ['state', m.state]].forEach(([k, v]) => kv.append(el('div', { className: 'prop' }, [el('label', { textContent: k }), el('span', { textContent: v, style: 'word-break:break-all' })])));
      kv.append(el('div', { className: 'row' }, [el('button', { textContent: 'Scan', onclick: () => post('/api/library/scan', { mount: m.id }, 'Scanned.') }), el('button', { textContent: 'Relocate...', onclick: () => relocateDialog(m) }), el('button', { textContent: m.readOnly ? 'Make read-write' : 'Make read-only', onclick: () => post('/api/library/access', { mount: m.id, readOnly: !m.readOnly }, 'Changed.') }), el('button', { textContent: 'Unlink...', onclick: () => unlink(m) })]));
      det.append(kv);
    } else det.append(el('p', { className: 'note', style: 'padding:6px 10px', textContent: 'Select an asset to see where it lives, where the song uses it and what you can do with it.' }));
    return;
  }
  const m = mountOf(a.mountId);
  const pad = el('div', { className: 'pad' });
  det.append(el('h4', { textContent: a.name, style: 'padding:8px 10px 2px' }), pad);
  [['id', a.uuid], ['kind', a.kind], ['file', a.relPath], ['folder', m ? m.name : '?'], ['hash', a.hash || '(never read)'], ['size', kb(a.size)], ['revision', String(a.revision)], ['status', statusOf(a)]].forEach(([k, v]) => pad.append(el('div', { className: 'prop' }, [el('label', { textContent: k }), el('span', { textContent: v, style: 'word-break:break-all' })])));
  const tags = el('input', { value: a.tags || '', placeholder: 'tags' });
  const lic = el('input', { value: a.license || '', placeholder: 'license' });
  const credit = el('input', { value: a.credit || '', placeholder: 'credit' });
  pad.append(el('div', { className: 'prop' }, [el('label', { textContent: 'tags' }), tags]), el('div', { className: 'prop' }, [el('label', { textContent: 'license' }), lic]), el('div', { className: 'prop' }, [el('label', { textContent: 'credit' }), credit]));
  pad.append(el('div', { className: 'row' }, [el('button', { textContent: 'Save notes', onclick: () => post('/api/library/describe', { asset: a.uuid, tags: tags.value, license: lic.value, credit: credit.value }, 'Saved.') }), el('button', { id: 'lb-import', textContent: 'Copy into project', title: 'Copies the file into assets/ with its provenance', onclick: () => post('/api/library/import', { asset: a.uuid }, 'Copied into the project (assets/).') })]));
  pad.append(el('h4', { textContent: 'Where is it used?', style: 'padding:0;margin:10px 0 4px' }));
  const uses = usesOf(a);
  if (!uses.length) pad.append(el('p', { className: 'note', textContent: 'Not used by the song now.' }));
  uses.forEach((u) => {
    const r = el('div', { className: 'row' }, [el('span', { textContent: `${u.block}.${u.parameter}` }), el('button', { textContent: 'Block', onclick: () => { setView('blocks'); select(u.block); notify(); const n = document.querySelector(`.node[data-id="${CSS.escape(u.block)}"]`); if (n) focusNode({ x: parseFloat(n.style.left), y: parseFloat(n.style.top) }); } })]);
    pad.append(r);
  });
  const c = lockOf(a);
  if (c && c.status !== 'ok') pad.append(el('div', { className: 'seerr warning', textContent: `▲ ${c.message}` }));
}

async function post(path, body, done) {
  try { await api.post(path, body); toast(done); await refreshLibrary(); notify(); } catch (e) { toast(`${e.code ? `${e.code}: ` : ''}${e.message}`); }
}

function mountMenu(e, m) {
  showMenu(e.clientX, e.clientY, [{ head: m.name }, { label: 'Scan', fn: () => post('/api/library/scan', { mount: m.id }, 'Scanned.') }, { label: 'Relocate...', fn: () => relocateDialog(m) }, { label: m.readOnly ? 'Make read-write' : 'Make read-only', fn: () => post('/api/library/access', { mount: m.id, readOnly: !m.readOnly }, 'Changed.') }, { label: 'Unlink...', danger: true, fn: () => unlink(m) }]);
}

async function linkDialog() {
  const path = el('input', { placeholder: 'folder on this computer, for example D:\\Music\\Samples' });
  const name = el('input', { placeholder: 'name (default: the folder name)' });
  const rw = el('input', { type: 'checkbox', style: 'width:auto' });
  const proj = el('input', { type: 'checkbox', style: 'width:auto' });
  const ok = await ask('Link a folder', 'A linked folder is indexed and fingerprinted, never uploaded. It is read-only unless you allow AMPLE to write into it (then a marker file with the catalog id is written there).', [{ label: 'Cancel', value: false }, { label: 'Link and scan', value: true, primary: true }], [path, name, el('label', {}, [rw, ' read-write']), el('label', {}, [proj, ' this project only'])]);
  if (!ok || !path.value.trim()) return;
  try {
    await api.post('/api/library/mounts', { name: name.value.trim() || undefined, path: path.value.trim(), readOnly: !rw.checked, scope: proj.checked ? 'project' : 'global' });
    toast('Linked and scanned.');
    await refreshLibrary();
  } catch (e) { toast(`${e.code ? `${e.code}: ` : ''}${e.message}`); }
}

async function relocateDialog(m) {
  const path = el('input', { placeholder: 'where the folder is now', value: '' });
  const force = el('input', { type: 'checkbox', style: 'width:auto' });
  const ok = await ask(`Relocate "${m.name}"`, 'Points the link at the new place after an identity check (marker, fingerprint or its files). Songs and ample.lock do not change.', [{ label: 'Cancel', value: false }, { label: 'Relocate', value: true, primary: true }], [path, el('label', {}, [force, ' skip the identity check'])]);
  if (!ok || !path.value.trim()) return;
  await post('/api/library/relocate', { mount: m.id, path: path.value.trim(), force: force.checked }, 'Relocated.');
}

async function unlink(m) {
  if (await dangerousAct('/api/acts/unlink-mount', { mount: m.id })) { LB.sel = { kind: 'all' }; LB.asset = null; toast('Unlinked (no file was touched).'); await refreshLibrary(); notify(); }
}

export async function openLibrary(selection) {
  await load();
  if (selection) LB.sel = selection;
  setView('lib');
}

export function initLibraryUi() {
  registerView('lib', async () => { await load(); render(); });
}
