// Open files as tabs (plan/editor-tabs.md, parts 2 and 3): one row of tabs with a dot for unsaved changes and a mark for errors, an overflow list that holds every tab,
// a phone layout that shows the active tab and the list, Save all, and one shared tab each for audio and image files. The tabs are a view on the files that the studio
// already opens (a song, a text or component file); a tab click opens that file through the same events as the Project panel. @ample:tabs

import { api, getToken } from './api.js';
import { S, editSerial, notify, select, subscribe, undoHooks } from './store.js';
import { $, ask, el, showMenu, toast } from './ui.js';
import { createRecentFilesToggle, recentFilePaths } from './recent-files.js';

const KIND = [
  ['song', /\.amplet$/i], ['module', /\.module$/i], ['component', /\.amplec$/i],
  ['audio', /\.(wav|mp3|flac|ogg|aif|aiff)$/i], ['image', /\.(png|jpe?g|gif|webp|svg)$/i],
];
export const kindOf = (path) => KIND.find(([, pattern]) => pattern.test(path))?.[0] ?? 'text';
const SHARED = new Set(['audio', 'image']);   // one tab each: the next audio file takes the place of the one in the audio tab

// There is always one editor: closing the last tab opens a new, blank, unsaved song ("Untitled"). It is a real file of the project while it is open, marked in this browser; when another song is opened while
// it is still blank it is deleted again, and the first save asks where it should go.
const untitledKey = () => `ample.untitled:${S.project?.id || ''}`;
// the map keeps the text each new song started with: a song is still blank while it has that text
const untitledMap = () => { try { return JSON.parse(localStorage.getItem(untitledKey()) || '{}') || {}; } catch { return {}; } };
const rememberUntitled = (map) => { try { localStorage.setItem(untitledKey(), JSON.stringify(map)); } catch { /* storage may be blocked */ } };
const isUntitled = (path) => Object.prototype.hasOwnProperty.call(untitledMap(), path);
const T = { tabs: [], active: '', state: {}, hidden: 0, bar: null, popup: null, internal: false, remote: new Map(), channel: null, windowId: Math.random().toString(36).slice(2) };
const post = (message) => { try { T.channel?.postMessage({ ...message, from: T.windowId }); } catch { /* a closed channel */ } };
const detachedPath = () => new URLSearchParams(location.search).get('tab');
const key = () => `ample.tabs:${S.project?.id || ''}`;
const baseName = (path) => path.split('/').pop();
const folderOf = (path) => path.split('/').slice(0, -1).join('/');
let raf = 0;
const renderSoon = () => { if (!raf) raf = requestAnimationFrame(() => { raf = 0; render(); }); };
const save = () => { try { localStorage.setItem(key(), JSON.stringify({ paths: T.tabs.map((t) => t.path), active: T.active })); } catch { /* storage may be blocked */ } };

/** The facts of a tab: the live ones for the song that is open (no waiting for the server), the polled ones for the rest. */
function factsOf(path) {
  if (kindOf(path) === 'song' && path === S.path && S.doc) {
    const diagnostics = S.doc.diagnostics || [];
    return { dirty: !!S.doc.dirty, errors: diagnostics.filter((d) => d.severity === 'error').length, warnings: diagnostics.filter((d) => d.severity === 'warning').length, graphBroken: !!S.doc.graphBroken };
  }
  return T.state[path] || { dirty: false, errors: 0, warnings: 0, graphBroken: false };
}

const previewOn = () => { try { return localStorage.getItem('ample.tabs.preview') === '1'; } catch { return false; } };

function add(path) {
  const existing = T.tabs.find((t) => t.path === path);
  if (existing) { existing.preview = false; return; }   // opened for real (a double click, an edit): the tab stays
  if (!path) return;
  const kind = kindOf(path);
  const shared = SHARED.has(kind) ? T.tabs.find((t) => kindOf(t.path) === kind) : null;
  if (shared) shared.path = path; else T.tabs.push({ path });
}

function openFile(path) {
  T.internal = true;
  try { document.dispatchEvent(new CustomEvent(kindOf(path) === 'song' ? 'ample:open' : 'ample:open-file', { detail: path })); } finally { T.internal = false; }
}

function activate(path) {
  if (T.remote.has(path)) { remoteChoice(path); return; }   // one editor per file, in whatever window
  T.active = path;
  save(); render();
  if (!(kindOf(path) === 'song' && path === S.path)) openFile(path);
}

async function refreshState() {
  if (document.visibilityState !== 'visible' || !T.tabs.length) return;
  const paths = T.tabs.map((t) => t.path).filter((p) => !(kindOf(p) === 'song' && p === S.path));
  if (!paths.length) { render(); return; }
  try {
    const answer = await api.get(`/api/tabs/state?paths=${encodeURIComponent(paths.join(','))}`);
    T.state = Object.fromEntries((answer.items || []).map((i) => [i.path, i]));
    // a file that is gone from the project closes its tab
    T.tabs = T.tabs.filter((t) => T.state[t.path]?.exists !== false || t.path === S.path);
    render();
  } catch { /* the marks are a help: the next poll tries again */ }
}

async function openUntitled() {
  try {
    const files = await api.get('/api/docs');
    let n = 1, base, path;
    do { base = n === 1 ? 'Untitled' : `Untitled-${n}`; path = `${base}.amplet`; n++; } while (files.includes(path));
    const made = await api.post('/api/songs', { name: base });
    path = made.path || path;
    const doc = await api.get(`/api/doc?path=${encodeURIComponent(path)}`);
    const map = untitledMap(); map[path] = doc.text; rememberUntitled(map);
    add(path); T.active = path; save();
    document.dispatchEvent(new CustomEvent('ample:open', { detail: path }));
  } catch (error) { toast(error.message || String(error)); }
}

/** A blank new song that nothing was done to goes away when another song is opened (the file, the tab and the mark). */
async function dropBlankUntitled(except) {
  const map = untitledMap();
  for (const name of Object.keys(map)) {
    if (name === except) continue;
    try {
      const saved = await api.get(`/api/doc/saved?path=${encodeURIComponent(name)}`);
      const current = S.path === name && S.doc ? S.doc.text : saved.saved;
      if (!saved.dirty && saved.saved === map[name] && current === map[name]) { await api.delete(`/api/project/file?path=${encodeURIComponent(name)}`); delete map[name]; T.tabs = T.tabs.filter((t) => t.path !== name); }
      else if (saved.saved !== map[name] && !saved.dirty) delete map[name];   // it was saved over by someone else: it is an ordinary song now
    } catch { delete map[name]; }
  }
  rememberUntitled(map); render();
}

/** The first save of a new song: where should it go? The song is written to the chosen place, the blank file goes away and the song opens from its new place. */
async function placeUntitled(path) {
  const name = el('input', { id: 'untitled-name', type: 'text', value: 'my-song', 'aria-label': 'Song name' });
  const folder = el('input', { id: 'untitled-folder', type: 'text', placeholder: 'folder (empty: the project folder)', 'aria-label': 'Folder' });
  const choice = await ask('Save the new song', 'Where should this song be saved? A name is letters, digits, _ and -.', [{ label: 'Cancel', value: false }, { label: 'Save', value: true, primary: true }],
    [el('label', { className: 'untitled-row' }, ['name ', name]), el('label', { className: 'untitled-row' }, ['folder ', folder])]);
  if (!choice) return false;
  try {
    if (S.path === path && S.doc?.dirty) await api.put('/api/doc', { path, text: S.doc.text, baseVersion: S.doc.version }).catch(() => {});
    const made = await api.post('/api/doc/save-as', { path, name: name.value.trim(), folder: folder.value.trim() });
    await api.post('/api/doc/revert', { path }).catch(() => {});
    await api.delete(`/api/project/file?path=${encodeURIComponent(path)}`).catch(() => {});
    const map = untitledMap(); delete map[path]; rememberUntitled(map);
    T.tabs = T.tabs.filter((t) => t.path !== path);
    add(made.path); T.active = made.path; save();
    document.dispatchEvent(new CustomEvent('ample:open', { detail: made.path }));
    toast(`Saved as ${made.path}.`);
    return true;
  } catch (error) { toast(error.message || String(error)); return false; }
}

// the last closed tab, for Undo (reopen) and Redo (close again); it counts only while nothing was edited after it
let lastClose = null;

async function tabUndo(kind) {
  const c = lastClose;
  if (!c || c.serial !== editSerial()) { lastClose = null; return false; }
  if (kind === 'undo' && !c.open) {
    if (T.tabs.some((t) => t.path === c.path)) { lastClose = null; return false; }
    add(c.path);
    const at = T.tabs.findIndex((t) => t.path === c.path);
    if (at >= 0) { const [tab] = T.tabs.splice(at, 1); T.tabs.splice(Math.min(c.index, T.tabs.length), 0, tab); }
    c.open = true;
    if (c.wasActive) activate(c.path); else { save(); refreshState(); render(); }
    toast(`Reopened ${baseName(c.path)}. Redo closes it again.`);
    return true;
  }
  if (kind === 'redo' && c.open) {
    if (!T.tabs.some((t) => t.path === c.path)) { lastClose = null; return false; }
    await closeTab(c.path);
    return true;
  }
  return false;
}

async function closeTab(path) {
  const tab = T.tabs.find((t) => t.path === path);
  if (!tab) return;
  const facts = factsOf(path);
  if (facts.dirty) {
    const choice = await ask('Close with unsaved changes', `${baseName(path)} has unsaved changes.`, [{ label: 'Cancel', value: 'cancel' }, { label: 'Discard changes', value: 'discard' }, { label: 'Save', value: 'save', primary: true }]);
    if (choice === 'cancel' || !choice) return;
    try {
      if (kindOf(path) === 'song') await api.post(choice === 'save' ? '/api/doc/save' : '/api/doc/revert', { path });
      else await api.post(choice === 'save' ? '/api/wc/save' : '/api/wc/revert', { path });
    } catch (error) { toast(error.message || String(error)); return; }
  }
  const index = T.tabs.findIndex((t) => t.path === path);
  lastClose = { path, index, wasActive: T.active === path, serial: editSerial(), open: false };   // Ctrl+Z reopens it while nothing was edited since
  T.tabs.splice(index, 1);
  if (T.active === path) {
    const next = T.tabs[Math.min(index, T.tabs.length - 1)];
    if (next) activate(next.path); else { await openUntitled(); return; }   // there is always one editor
  }
  save(); refreshState(); render();
}

function tabMenu(x, y, path) {
  const mobile = window.matchMedia('(max-width: 700px)').matches;
  const items = [{ head: baseName(path) }];
  if (T.remote.has(path)) items.push({ label: 'Bring the window to the front', fn: () => post({ type: 'focus', path }) }, { label: 'Take it back into this window', fn: () => post({ type: 'putback', path }) });
  else {
    if (!mobile && !detachedPath()) items.push({ label: 'Open in a window', fn: () => detach(path) });
    items.push({ label: 'Save', fn: async () => { try { await api.post(kindOf(path) === 'song' ? '/api/doc/save' : '/api/wc/save', { path }); refreshState(); } catch (e) { toast(e.message || String(e)); } } },
      { label: 'Revert', fn: async () => { try { await api.post(kindOf(path) === 'song' ? '/api/doc/revert' : '/api/wc/revert', { path }); refreshState(); if (path === S.path) document.dispatchEvent(new CustomEvent('ample:open', { detail: path })); } catch (e) { toast(e.message || String(e)); } } });
  }
  items.push({ label: 'Close', fn: () => closeTab(path) }, { label: 'Close others', fn: async () => { for (const t of [...T.tabs]) if (t.path !== path && !T.remote.has(t.path)) await closeTab(t.path); } },
    { label: 'Close saved', fn: async () => { for (const t of [...T.tabs]) if (!factsOf(t.path).dirty && t.path !== path && !T.remote.has(t.path)) await closeTab(t.path); } },
    { label: 'Copy path', fn: () => navigator.clipboard?.writeText(path).catch(() => {}) });
  showMenu(x, y, items);
}

/** A tab in a window of its own: the same studio address with ?tab=<path>, the same session and the same server working copy. The tab stays in the row as a placeholder. */
function detach(path) {
  const url = `${location.origin}${location.pathname}?tab=${encodeURIComponent(path)}#token=${getToken()}`;
  const w = window.open(url, `ample-tab-${path}`, 'popup,width=1100,height=800');
  if (!w) { toast('The browser blocked the new window: allow pop-ups for this address.'); return; }
  T.remote.set(path, { windowId: '?', seen: Date.now() });
  const next = T.tabs.find((t) => t.path !== path && !T.remote.has(t.path));
  if (T.active === path && next) activate(next.path);
  render();
}

/** The channel between the windows of one project: detached, attached, focus, putback and a heartbeat. */
function openChannel() {
  if (typeof BroadcastChannel === 'undefined' || !S.project?.id) return;
  T.channel = new BroadcastChannel(`ample:${S.project.id}`);
  T.channel.onmessage = ({ data }) => {
    if (!data || data.from === T.windowId) return;
    const mine = detachedPath();
    if (mine) {   // in a detached window
      if (data.path === mine && data.type === 'focus') { window.focus(); document.title = `● ${mine}`; setTimeout(() => { document.title = mine; }, 2500); toast('Another window asked for this file: here it is.'); }
      if (data.path === mine && data.type === 'putback') putBack();
      return;
    }
    if (data.type === 'detached') { T.remote.set(data.path, { windowId: data.from, seen: Date.now() }); add(data.path); if (T.active === data.path) { const next = T.tabs.find((t) => !T.remote.has(t.path)); if (next) activate(next.path); } render(); }
    else if (data.type === 'alive' && T.remote.has(data.path)) T.remote.get(data.path).seen = Date.now();
    else if (data.type === 'attached') { T.remote.delete(data.path); T.active = data.path; save(); render(); activate(data.path); if (data.state) { setTimeout(() => applyViewState(data.state), 700); setTimeout(() => applyViewState(data.state), 1800); } }
  };
}

/** A file that is open in another window: bring that window to the front, or take the file back here (that window closes and this one shows what it showed). */
async function remoteChoice(path) {
  const choice = await ask('Open in another window', `${baseName(path)} is open in another window. There is only one editor per file.`, [{ label: 'Cancel', value: 'cancel' }, { label: 'Bring that window to the front', value: 'focus' }, { label: 'Take it back here', value: 'take', primary: true }]);
  if (choice === 'focus') post({ type: 'focus', path });
  else if (choice === 'take') post({ type: 'putback', path });
}

/** What the person sees in this window: the view (blocks, code, assets), how deep in a group, the selected block, the side panels, the line in the code, the scroll and the zoom. */
async function captureViewState() {
  const { V } = await import('./views.js');
  const ta = document.querySelector('#se-ta');
  const work = $('#work');
  return {
    mode: V.mode, stack: [...(S.stack || ['root'])], sel: S.sel || null, view: S.view ? { ...S.view } : null,
    rightOpen: work ? !work.classList.contains('noR') : true, leftOpen: work ? !work.classList.contains('noL') : true,
    line: ta ? ta.value.slice(0, ta.selectionStart).split('\n').length : null, column: ta ? ta.selectionStart - (ta.value.lastIndexOf('\n', ta.selectionStart - 1) + 1) : null, scrollTop: ta?.scrollTop || 0,
  };
}

/** Shows again what the other window showed, as far as this file allows it. */
async function applyViewState(state) {
  if (!state) return;
  try {
    const { setView } = await import('./views.js');
    const work = $('#work');
    if (work) { work.classList.toggle('noR', state.rightOpen === false); work.classList.toggle('noL', state.leftOpen === false); }
    if (Array.isArray(state.stack) && state.stack.length && state.stack[0] === 'root') S.stack = state.stack;
    if (state.view) S.view = { ...state.view };
    setView(state.mode || 'blocks', true);
    notify();
    if (state.sel) { try { select(state.sel); } catch { /* the block may be gone */ } }
    if (state.mode === 'src' && state.line) {
      setTimeout(() => {
        const ta = document.querySelector('#se-ta');
        if (!ta) return;
        const lines = ta.value.split('\n');
        const line = Math.min(state.line, lines.length);
        const offset = lines.slice(0, line - 1).reduce((n, l) => n + l.length + 1, 0) + Math.min(state.column || 0, (lines[line - 1] || '').length);
        ta.focus(); ta.setSelectionRange(offset, offset); ta.scrollTop = state.scrollTop || 0;
      }, 400);
    }
  } catch { /* a view that cannot be restored is not worth an error */ }
}

/** The detached window: only this file, a slim header with Put back; closing the window puts the tab back. */
function startDetached(path) {
  document.body.classList.add('detached');
  document.title = path;
  post({ type: 'detached', path });
  setInterval(() => post({ type: 'alive', path }), 3000);
  window.addEventListener('pagehide', () => post({ type: 'attached', path }));
  const back = el('button', { type: 'button', id: 'tab-putback', className: 'primary', textContent: 'Put back in the main window', onclick: putBack });
  $('header')?.append(back);
  add(path); T.active = path;
  openFile(path);
}
async function putBack() { let state = null; try { state = await captureViewState(); } catch { /* the tab comes back without the view */ } post({ type: 'attached', path: detachedPath(), state }); window.close(); }

function marks(path) {
  const f = factsOf(path), out = [];
  if (f.dirty) out.push(el('i', { className: 'tab-dot', title: 'Unsaved changes', textContent: '●' }));
  if (f.graphBroken) out.push(el('i', { className: 'tab-bad', title: 'The text cannot be read into blocks', textContent: '⛔' }));
  else if (f.errors) out.push(el('i', { className: 'tab-err', title: `${f.errors} error(s)`, textContent: String(f.errors) }));
  else if (f.warnings) out.push(el('i', { className: 'tab-warn', title: `${f.warnings} warning(s)`, textContent: '!' }));
  return out;
}

function label(path) {
  const clash = T.tabs.filter((t) => baseName(t.path) === baseName(path)).length > 1;
  return clash && folderOf(path) ? `${baseName(path)} · ${folderOf(path)}` : baseName(path);
}

function tabElement(tab) {
  const path = tab.path, on = path === T.active, f = factsOf(path);
  const div = el('div', { className: `tab${on ? ' on' : ''}${tab.preview ? ' preview' : ''}${f.dirty ? ' dirty' : ''}${f.errors || f.graphBroken ? ' bad' : ''}`, role: 'tab', tabIndex: 0, title: path, 'aria-selected': String(on) });
  div.dataset.path = path;
  div.append(el('span', { className: 'tab-kind', textContent: { song: '♪', component: '◇', module: '▣', audio: '🔊', image: '🖼', text: '≡' }[kindOf(path)] || '≡' }), el('span', { className: 'tab-name', textContent: label(path) }), ...marks(path),
    el('button', { type: 'button', className: 'tab-x', textContent: '×', title: 'Close (middle click)', 'aria-label': `Close ${baseName(path)}`, onclick: (e) => { e.stopPropagation(); closeTab(path); } }));
  div.onclick = () => activate(path);
  div.oncontextmenu = (e) => { e.preventDefault(); tabMenu(e.clientX, e.clientY, path); };
  if (T.remote.has(path)) { div.classList.add('remote'); div.title = `${path} (open in another window: click to bring it to the front)`; }
  div.onauxclick = (e) => { if (e.button === 1) { e.preventDefault(); closeTab(path); } };
  div.onkeydown = (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); activate(path); } else if (e.key === 'Delete') closeTab(path); };
  return div;
}

function fit(list) {
  // the row never wraps: when the tabs do not fit, the last ones (never the active one) go to the list
  const nodes = [...list.children];
  nodes.forEach((n) => { n.hidden = false; });
  T.hidden = 0;
  for (let i = nodes.length - 1; i >= 0 && list.scrollWidth > list.clientWidth + 1; i--) {
    if (nodes[i].dataset.path === T.active) continue;
    nodes[i].hidden = true; T.hidden++;
  }
}

function render() {
  if (!T.bar) return;
  const dirtyCount = T.tabs.filter((t) => factsOf(t.path).dirty).length;
  const list = el('div', { className: 'tabs-list', role: 'tablist', 'aria-label': 'Open files' }, T.tabs.map(tabElement));
  const more = el('button', { type: 'button', id: 'tabs-more', className: 'tabs-more', title: 'All open files', 'aria-haspopup': 'listbox', textContent: `▾ ${T.tabs.length}`, onclick: (e) => { e.stopPropagation(); togglePopup(more); } });
  const saveAllButton = dirtyCount ? el('button', { type: 'button', id: 'tabs-saveall', className: 'tabs-saveall', textContent: `Save all (${dirtyCount})`, title: 'Save every file with unsaved changes (Ctrl+Shift+S)', onclick: saveAll }) : '';
  T.recent ??= createRecentFilesToggle();   // Recent lives with the tabs now: one place for the files that are open and the ones that were
  T.bar.replaceChildren(list, more, T.recent, saveAllButton);
  T.bar.hidden = T.tabs.length < 1;
  requestAnimationFrame(() => { fit(list); more.classList.toggle('some', T.hidden > 0); });
}

function togglePopup(anchor) {
  if (T.popup) { closePopup(); return; }
  const box = el('div', { id: 'tabs-popup', className: 'tabs-popup', role: 'listbox' });
  const phone = window.matchMedia('(max-width: 700px)').matches;
  const dirtyCount = T.tabs.filter((t) => factsOf(t.path).dirty).length;
  if (phone && dirtyCount) box.append(el('button', { type: 'button', className: 'tabs-sheet-save', textContent: `Save all (${dirtyCount})`, onclick: () => { closePopup(); saveAll(); } }));
  const filter = el('input', { type: 'search', placeholder: 'Filter open files', 'aria-label': 'Filter open files' });
  const rows = el('div', { className: 'tabs-rows' });
  const paint = () => {
    rows.replaceChildren();
    const q = filter.value.trim().toLowerCase();
    for (const tab of T.tabs.filter((t) => !q || t.path.toLowerCase().includes(q))) {
      const f = factsOf(tab.path);
      const row = el('div', { className: `tabs-row${tab.path === T.active ? ' on' : ''}`, role: 'option', tabIndex: 0, 'data-path': tab.path }, [
        el('span', { className: 'tab-name', textContent: baseName(tab.path) }), el('span', { className: 'tabs-folder', textContent: folderOf(tab.path) }),
        el('span', { className: 'tabs-facts', textContent: [f.dirty ? 'unsaved' : '', f.graphBroken ? 'cannot be read into blocks' : f.errors ? `${f.errors} error(s)` : '', f.warnings ? `${f.warnings} warning(s)` : ''].filter(Boolean).join(' · ') })]);
      row.onclick = () => { closePopup(); activate(tab.path); };
      row.onkeydown = (e) => { if (e.key === 'Enter') row.click(); };
      rows.append(row);
    }
    if (phone) {
      const open = new Set(T.tabs.map((t) => t.path));
      const recent = (recentFilePaths() || []).map((x) => (typeof x === 'string' ? x : x?.path)).filter((p) => p && !open.has(p) && (!q || p.toLowerCase().includes(q))).slice(0, 8);
      if (recent.length) rows.append(el('div', { className: 'tabs-head', textContent: 'Recent files' }));
      for (const path of recent) {
        const row = el('div', { className: 'tabs-row recent', role: 'option', tabIndex: 0 }, [el('span', { className: 'tab-name', textContent: baseName(path) }), el('span', { className: 'tabs-folder', textContent: folderOf(path) })]);
        row.onclick = () => { closePopup(); document.dispatchEvent(new CustomEvent(/\.amplet$/i.test(path) ? 'ample:open' : /\.module$/i.test(path) ? 'ample:open-source-file' : 'ample:open-file', { detail: path })); };
        rows.append(row);
      }
    }
  };
  filter.oninput = paint;
  filter.onkeydown = (e) => { if (e.key === 'Enter') rows.firstElementChild?.click(); else if (e.key === 'Escape') closePopup(); };
  box.append(filter, rows); paint();
  document.body.append(box);
  const r = anchor.getBoundingClientRect();
  box.style.top = `${r.bottom + 4}px`; box.style.right = `${Math.max(8, window.innerWidth - r.right)}px`;
  T.popup = box; filter.focus();
  setTimeout(() => document.addEventListener('pointerdown', outside, true), 0);
}
function outside(e) { if (T.popup && !T.popup.contains(e.target)) closePopup(); }
function closePopup() { T.popup?.remove(); T.popup = null; document.removeEventListener('pointerdown', outside, true); }

const CHECKPOINT_KEY = 'ample.saveall.checkpoint';
/** Save all: a dialog with the number of files, Save all / Save active / Cancel, and a remembered checkbox "make a checkpoint first" (on at the start). */
export async function saveAll() {
  const dirty = T.tabs.filter((t) => factsOf(t.path).dirty).map((t) => t.path);
  if (!dirty.length) { toast('Nothing to save.'); return; }
  const remembered = localStorage.getItem(CHECKPOINT_KEY);
  const checkbox = el('input', { type: 'checkbox', id: 'saveall-checkpoint', checked: remembered === null ? true : remembered === '1' });
  const choice = await ask('Save all', `${dirty.length} file${dirty.length === 1 ? ' has' : 's have'} changed: ${dirty.map(baseName).join(', ')}. Save ${dirty.length === 1 ? 'it' : 'them all'}?`,
    [{ label: 'Cancel', value: 'cancel' }, { label: 'Save active', value: 'active' }, { label: 'Save all', value: 'all', primary: true }], [el('label', { className: 'saveall-cp' }, [checkbox, ' Make a checkpoint first, so the history keeps the state before this save'])]);
  if (!choice || choice === 'cancel') return;
  try { localStorage.setItem(CHECKPOINT_KEY, checkbox.checked ? '1' : '0'); } catch { /* storage may be blocked */ }
  const targets = choice === 'active' ? dirty.filter((p) => p === T.active) : dirty;
  if (!targets.length) { toast('The active file has no unsaved changes.'); return; }
  try {
    if (checkbox.checked) { try { await api.post('/api/history/checkpoint', { label: 'Before Save all' }); } catch (error) { toast(`No checkpoint was made: ${error.message || error}`); } }
    for (const path of targets) { if (isUntitled(path)) { await placeUntitled(path); continue; } await api.post(kindOf(path) === 'song' ? '/api/doc/save' : '/api/wc/save', { path }); }
    toast(`Saved ${targets.length} file${targets.length === 1 ? '' : 's'}.`);
  } catch (error) { toast(error.message || String(error)); }
  await refreshState();
  document.dispatchEvent(new CustomEvent('ample:saved-all'));
}

/** What Koda is told about the open files: the list of tabs with their state, and for the ones with unsaved changes what changed (a short diff of the saved text against the working text). */
export async function tabsContextForKoda(subject = null) {
  if (!T.tabs.length) return '';
  const looked = [...(subject?.kind === 'tab' ? [subject.path] : []), ...((subject?.items || []).filter((i) => i.kind === 'tab').map((i) => i.path))];
  const { lineDiff } = await import('../lib/tabs-context.mjs');
  const lines = ['Open files in the studio (tabs):'];
  for (const tab of T.tabs.slice(0, 40)) {
    const f = factsOf(tab.path);
    lines.push(`- ${tab.path}${tab.path === T.active ? ' (the one on the screen)' : ''}${f.dirty ? ', UNSAVED changes' : ''}${f.graphBroken ? ', its text cannot be read into blocks' : f.errors ? `, ${f.errors} error(s)` : ''}${f.warnings ? `, ${f.warnings} warning(s)` : ''}`);
  }
  const changed = T.tabs.filter((t) => factsOf(t.path).dirty).slice(0, 4);
  for (const tab of changed) {
    try {
      let saved, working;
      if (kindOf(tab.path) === 'song') { saved = (await api.get(`/api/doc/saved?path=${encodeURIComponent(tab.path)}`)).saved; working = tab.path === S.path && S.doc ? S.doc.text : (await api.get(`/api/doc?path=${encodeURIComponent(tab.path)}`)).text; }
      else { const wc = await api.get(`/api/wc?path=${encodeURIComponent(tab.path)}`); saved = wc.saved; working = wc.text; }
      lines.push(`What changed in ${tab.path} (saved text against the unsaved text; the unsaved text is what the user sees and what builds use):\n${lineDiff(saved, working, 60)}`);
    } catch { /* a diff is a help */ }
  }
  // a tab that Koda is looking at: its text as it is on the screen (cut after a number of characters)
  for (const path of [...new Set(looked)].slice(0, 3)) {
    try {
      const text = kindOf(path) === 'song' ? (path === S.path && S.doc ? S.doc.text : (await api.get(`/api/doc?path=${encodeURIComponent(path)}`)).text) : (await api.get(`/api/wc?path=${encodeURIComponent(path)}`)).text;
      lines.push(`The text of ${path} as it is on the screen:\n${String(text).slice(0, 8000)}${String(text).length > 8000 ? '\n... (the rest is left out)' : ''}`);
    } catch { /* a text is a help */ }
  }
  return `\n\n${lines.join('\n')}`;
}

/** Starts the tabs: restores the open files of the project, follows the files the studio opens, and polls the marks of the other tabs. */
export function initTabs() {
  T.bar = $('#tabbar');
  if (!T.bar) return;
  try { const stored = JSON.parse(localStorage.getItem(key()) || 'null'); for (const p of stored?.paths || []) add(p); T.active = stored?.active || ''; } catch { /* a damaged list starts empty */ }
  if (S.path) { add(S.path); if (!T.active || kindOf(T.active) === 'song') T.active = S.path; }
  undoHooks.push(tabUndo);
  document.addEventListener('ample:open', (e) => { if (!T.internal) { add(e.detail); T.active = e.detail; save(); render(); } });
  document.addEventListener('ample:open-file', (e) => { if (!T.internal) { add(e.detail); T.active = e.detail; save(); render(); } });
  // the option "preview tabs" (Project settings): a single click in the Project panel opens a tab in italics that the next single click replaces; a double click or an edit keeps it
  document.addEventListener('ample:preview-file', (e) => {
    if (!previewOn()) return;
    const path = e.detail;
    if (T.tabs.some((t) => t.path === path)) { if (T.active !== path) activate(path); return; }
    const old = T.tabs.find((t) => t.preview && kindOf(t.path) === kindOf(path) && !factsOf(t.path).dirty);
    if (old) old.path = path; else T.tabs.push({ path, preview: true });
    T.active = path; save(); render(); openFile(path);
  });
  document.addEventListener('ample:song-opened', () => { add(S.path); T.active = S.path; save(); render(); refreshState(); setTimeout(() => dropBlankUntitled(S.path), 600); });
  // the first save of a new song asks where it goes
  const intercept = (event) => { if (T.active && isUntitled(T.active) && factsOf(T.active).dirty) { event.preventDefault(); event.stopImmediatePropagation(); placeUntitled(T.active); } };
  $('#btn-save')?.addEventListener('click', intercept, true);
  document.addEventListener('keydown', (e) => { if ((e.ctrlKey || e.metaKey) && !e.shiftKey && (e.key === 's' || e.key === 'S')) intercept(e); }, true);
  document.addEventListener('keydown', (e) => {
    if (e.ctrlKey && e.shiftKey && (e.key === 'S' || e.key === 's')) { e.preventDefault(); saveAll(); }
    else if (e.ctrlKey && e.key === 'Tab' && T.tabs.length > 1) { e.preventDefault(); const i = T.tabs.findIndex((t) => t.path === T.active); activate(T.tabs[(i + (e.shiftKey ? -1 : 1) + T.tabs.length) % T.tabs.length].path); }
  });
  subscribe(renderSoon);
  window.addEventListener('resize', renderSoon);
  openChannel();
  const mine = detachedPath();
  if (mine) startDetached(mine);
  else setInterval(() => { for (const [path, w] of T.remote) if (Date.now() - w.seen > 9000) { T.remote.delete(path); toast(`${baseName(path)}: its window was lost, the tab is back here.`); render(); } }, 3000);
  setInterval(refreshState, 3000);
  render(); refreshState();
}
