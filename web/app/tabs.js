// Open files as tabs (plan/editor-tabs.md, parts 2 and 3): one row of tabs with a dot for unsaved changes and a mark for errors, an overflow list that holds every tab,
// a phone layout that shows the active tab and the list, Save all, and one shared tab each for audio and image files. The tabs are a view on the files that the studio
// already opens (a song, a text or component file); a tab click opens that file through the same events as the Project panel. @ample:tabs

import { api, getToken } from './api.js';
import { planSaveAll } from '../lib/source-guards.mjs';
import { S, editSerial, notify, select, subscribe, undoHooks } from './store.js';
import { $, ask, el, showMenu, toast } from './ui.js';
import { createRecentFilesToggle, recentFilePaths } from './recent-files.js';
import { isOpenable } from '../lib/file-types.mjs';
import { removeRecoveryDraft } from '../lib/recovery-drafts.mjs';

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
const T = { tabs: [], active: '', state: {}, hidden: 0, bar: null, popup: null, remote: new Map(), channel: null, windowId: Math.random().toString(36).slice(2) };
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
// extensions that openProjectFile (main.js) can show; anything else gets no tab (the studio only toasts for it). A song is not one of them: it opens through ample:open.
const viewable = (path) => kindOf(path) !== 'image' && isOpenable(path);

/** Adds a tab, or replaces the previous unmodified preview or shared media tab. */
function add(path, preview) {
  if (!path) return;
  const existing = T.tabs.find((t) => t.path === path);
  if (existing) { if (!preview) existing.preview = false; return; }
  const kind = kindOf(path);
  const replaced = T.tabs.find((t) => kindOf(t.path) === kind && (SHARED.has(kind) || (preview && t.preview && !factsOf(t.path).dirty)));
  if (replaced) { replaced.path = path; replaced.preview = preview; }
  else T.tabs.push({ path, preview });
}

let opening;
const tabOpenOptions = new WeakMap();
// the view of a window that was put back (attached): applied once its file is the shown one, however long the open takes (the timers of the attached message only cover a quick open)
let pendingView = null;
const PENDING_VIEW_MS = 20000;
function applyPendingView(path) {
  const pending = pendingView;
  if (!pending || pending.path !== path) return;
  pendingView = null;
  if (Date.now() - pending.at > PENDING_VIEW_MS) return;   // a request that was refused long ago must not decorate a later, unrelated open
  const stillWanted = () => !opening && T.active === path;
  setTimeout(() => { if (stillWanted()) applyViewState(pending.state, stillWanted); }, 300);
}

/** Register the request synchronously so a later open can supersede it before its first await. */
export function beginTabOpen(request) {
  opening = request;
  return () => opening === request;
}

function answered(e) {
  const { path, ok, request } = e.detail;
  if (opening !== request) return;
  opening = null;
  if (ok && (request.type === 'ample:open' || viewable(path))) {
    const options = tabOpenOptions.get(request);
    if (!options?.existing) add(path, options?.preview);
    T.active = path;
    refreshState();
    applyPendingView(path);
  } else tabOpenOptions.get(request)?.onRefused?.();   // the opener said no: whoever asked takes its own change back
  save();
  render();
}

function openFile(path, options) {
  const request = new CustomEvent(kindOf(path) === 'song' ? 'ample:open' : 'ample:open-file', { detail: path });
  tabOpenOptions.set(request, options);
  document.dispatchEvent(request);
}

/** Makes a tab the active one: the open request decides. onRefused runs when the editor did not take it (a refused or failed open, a file that is open in another window). */
function activate(path, onRefused) {
  if (T.remote.has(path)) onRefused?.();   // the question about the other window follows; this tab does not become active
  openFile(path, { existing: true, onRefused });
}

export function isTabReactivation(request) { return tabOpenOptions.get(request)?.existing === true; }

let stateGeneration = 0;
async function refreshState() {
  const mine = ++stateGeneration;
  if (document.visibilityState !== 'visible' || !T.tabs.length) return;
  const paths = T.tabs.map((t) => t.path).filter((p) => !(kindOf(p) === 'song' && p === S.path));
  if (!paths.length) { render(); return; }
  try {
    const answer = await api.get(`/api/tabs/state?${paths.map((p) => `paths=${encodeURIComponent(p)}`).join('&')}`);
    if (mine !== stateGeneration) return;
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
      if (!saved.dirty && saved.saved === map[name] && current === map[name]) { await api.delete(`/api/project/file?path=${encodeURIComponent(name)}`); delete map[name]; T.tabs = T.tabs.filter((t) => t.path !== name); save(); }
      else if (saved.saved !== map[name] && !saved.dirty) delete map[name];   // it was saved over by someone else: it is an ordinary song now
    } catch { delete map[name]; }
  }
  rememberUntitled(map); render();
}

/** The first save of a new song: where should it go? The song is written to the chosen place, the blank file goes away and the song opens from its new place.
 *  With close (Close, then Save) it only writes: no tab and no open for the new place, so the close can go on to the neighbouring tab. Returns the new place, or false. */
async function placeUntitled(path, { close = false } = {}) {
  const name = el('input', { id: 'untitled-name', type: 'text', value: 'my-song', 'aria-label': 'Song name' });
  const folder = el('input', { id: 'untitled-folder', type: 'text', placeholder: 'folder (empty: the project folder)', 'aria-label': 'Folder' });
  const choice = await ask('Save the new song', 'Where should this song be saved? A name is letters, digits, _ and -.', [{ label: 'Cancel', value: false }, { label: 'Save', value: true, primary: true }],
    [el('label', { className: 'untitled-row' }, ['name ', name]), el('label', { className: 'untitled-row' }, ['folder ', folder])]);
  if (!choice) return false;
  try {
    if (S.path === path && S.doc?.dirty) await api.put('/api/doc', { path, text: S.doc.text, baseVersion: S.doc.version });   // a failed sync (a version conflict) stops the save: the draft stays
    const made = await api.post('/api/doc/save-as', { path, name: name.value.trim(), folder: folder.value.trim() });
    await api.post('/api/doc/revert', { path }).catch(() => {});
    await api.delete(`/api/project/file?path=${encodeURIComponent(path)}`).catch(() => {});
    const map = untitledMap(); delete map[path]; rememberUntitled(map);
    toast(`Saved as ${made.path}.`);
    if (close) return made.path;   // the closing tab stays until closeTab takes it away; an open of the new place could land after the close and bring its tab back
    T.tabs = T.tabs.filter((t) => t.path !== path);
    document.dispatchEvent(new CustomEvent('ample:open', { detail: made.path }));
    return made.path;
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

const closing = new Set();   // one close operation per tab at a time
async function closeTab(path) {
  const tab = T.tabs.find((t) => t.path === path);
  if (!tab || closing.has(path)) return;
  closing.add(path);
  try { await closeTabNow(path, tab); } finally { closing.delete(path); }
}
async function closeTabNow(path, tab) {
  const facts = factsOf(path);
  let closed = path;   // what Undo reopens: a new song saved on close comes back under its new name
  if (facts.dirty) {
    const choice = await ask('Close with unsaved changes', `${baseName(path)} has unsaved changes.`, [{ label: 'Cancel', value: 'cancel' }, { label: 'Discard changes', value: 'discard' }, { label: 'Save', value: 'save', primary: true }]);
    if (choice === 'cancel' || !choice) return;
    if (choice === 'save' && isUntitled(path)) {   // the first save asks for a name; cancelled or failed keeps Untitled open
      const placed = await placeUntitled(path, { close: true });   // written without opening it: the close goes on below and the neighbouring tab becomes active
      if (!placed) return;
      closed = placed;
    } else {
      try {
        if (kindOf(path) === 'song') await api.post(choice === 'save' ? '/api/doc/save' : '/api/doc/revert', { path });
        else await api.post(choice === 'save' ? '/api/wc/save' : '/api/wc/revert', { path });
      } catch (error) { toast(error.message || String(error)); return; }
    }
  }
  const index = T.tabs.findIndex((t) => t.path === path);
  if (index < 0) { render(); return; }   // the tab went away while the question was open
  const savedAs = closed !== path, wasShown = S.path === path;   // saved under a new name: the blank file it came from is gone
  lastClose = { path: closed, index, wasActive: T.active === path, serial: editSerial(), open: false };   // Ctrl+Z reopens it while nothing was edited since
  T.tabs.splice(index, 1);
  if (T.active === path) {
    const neighbours = T.tabs.filter((candidate) => !T.remote.has(candidate.path));
    let next = neighbours[Math.min(index, neighbours.length - 1)];
    const goneBlank = savedAs && wasShown;
    if (goneBlank) {
      // the editor still holds the blank file that is gone: it has to move to a song. Without another song tab the song that was just saved stays open under its new name.
      const songs = neighbours.filter((candidate) => kindOf(candidate.path) === 'song');
      next = songs.find((candidate) => T.tabs.indexOf(candidate) >= index) || songs[songs.length - 1];
      if (!next && neighbours.length) { lastClose = null; T.active = ''; save(); render(); openFile(closed, {}); return; }
    }
    // an activation the editor refuses (the question cancelled, the file failed to open): the close did not go through, the tab comes back and is the active one
    const takeBack = goneBlank
      ? () => { lastClose = null; openFile(closed, {}); }
      : () => { if (T.tabs.some((t) => t.path === closed)) return; T.tabs.splice(Math.min(index, T.tabs.length), 0, { path: closed }); T.active = closed; lastClose = null; save(); render(); refreshState(); };
    T.active = '';
    if (next) activate(next.path, takeBack); else { await openUntitled(); return; }   // there is always one editor
  }
  save(); refreshState(); render();
}

function tabMenu(x, y, path) {
  const mobile = window.matchMedia('(max-width: 700px)').matches;
  const items = [{ head: baseName(path) }];
  if (T.remote.has(path)) items.push({ label: 'Bring the window to the front', fn: () => post({ type: 'focus', path }) }, { label: 'Take it back into this window', fn: () => post({ type: 'putback', path }) });
  else {
    if (!mobile && !detachedPath()) items.push({ label: 'Open in a window', fn: () => detach(path) });
    items.push({ label: 'Save', fn: async () => { try { if (isUntitled(path)) { await placeUntitled(path); refreshState(); return; } await api.post(kindOf(path) === 'song' ? '/api/doc/save' : '/api/wc/save', { path }); refreshState(); } catch (e) { toast(e.message || String(e)); } } },
      { label: 'Revert', fn: async () => { try { await api.post(kindOf(path) === 'song' ? '/api/doc/revert' : '/api/wc/revert', { path }); try { removeRecoveryDraft(localStorage, S.project?.id || '', path); } catch { /* storage can be unavailable */ } refreshState(); if (path === S.path) document.dispatchEvent(new CustomEvent('ample:open', { detail: path })); } catch (e) { toast(e.message || String(e)); } } });
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
    else if (data.type === 'alive') { if (T.remote.has(data.path)) T.remote.get(data.path).seen = Date.now(); else { T.remote.set(data.path, { windowId: data.from, seen: Date.now() }); add(data.path); render(); } }   // a popup that resumed (or a reloaded main window) takes its ownership again
    else if (data.type === 'attached') { T.remote.delete(data.path); add(data.path); if (data.state) pendingView = { path: data.path, state: data.state, at: Date.now() }; activate(data.path); if (data.state) { const p = data.path; const stillWanted = () => !opening && T.active === p; const again = () => { if (stillWanted()) applyViewState(data.state, stillWanted); }; setTimeout(again, 700); setTimeout(again, 1800); } }
  };
}

/** A file that is open in another window: bring that window to the front, or take the file back here (that window closes and this one shows what it showed). */
const asking = new Set();   // one question per file, however many open paths ask at once
/** True when another window holds this file; then the focus / take-back question is shown and the caller must not open an editor. */
export function openedElsewhere(path) {
  if (!T.remote.has(path)) return false;
  remoteChoice(path);
  return true;
}
async function remoteChoice(path) {
  if (asking.has(path)) return;
  asking.add(path);
  try { await remoteChoiceNow(path); } finally { asking.delete(path); }
}
async function remoteChoiceNow(path) {
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
async function applyViewState(state, stillWanted = () => true) {
  if (!state || !stillWanted()) return;
  try {
    const { setView } = await import('./views.js');
    if (!stillWanted()) return;
    const work = $('#work');
    if (work) { work.classList.toggle('noR', state.rightOpen === false); work.classList.toggle('noL', state.leftOpen === false); }
    if (Array.isArray(state.stack) && state.stack.length && state.stack[0] === 'root') S.stack = state.stack;
    if (state.view) S.view = { ...state.view };
    setView(state.mode || 'blocks', true);
    notify();
    if (state.sel) { try { select(state.sel); } catch { /* the block may be gone */ } }
    if (state.mode === 'src' && state.line) {
      setTimeout(() => {
        if (!stillWanted()) return;
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
  div.onkeydown = (e) => { if (e.target !== div) return; const sibs = [...div.parentElement.children].filter((n) => !n.hidden); const at = sibs.indexOf(div); const go = (n) => { if (n) { e.preventDefault(); n.focus(); } }; if (e.key === 'ArrowRight') go(sibs[(at + 1) % sibs.length]); else if (e.key === 'ArrowLeft') go(sibs[(at - 1 + sibs.length) % sibs.length]); else if (e.key === 'Home') go(sibs[0]); else if (e.key === 'End') go(sibs[sibs.length - 1]); else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); activate(path); } else if (e.key === 'Delete') closeTab(path); };
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
  const had = document.activeElement && T.bar.contains(document.activeElement) ? document.activeElement : null;
  const hadPath = had?.closest?.('.tab')?.dataset.path, hadX = !!had?.classList?.contains('tab-x');
  T.bar.replaceChildren(list, more, T.recent, saveAllButton);
  if (hadPath) { const node = [...list.children].find((n) => n.dataset.path === hadPath); (hadX ? node?.querySelector('.tab-x') : node)?.focus?.(); }
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
      if (tab.path !== T.active) row.append(el('button', { type: 'button', className: 'tabs-row-x', textContent: '×', title: 'Close', 'aria-label': `Close ${baseName(tab.path)}`, onclick: async (e) => { e.stopPropagation(); await closeTab(tab.path); paint(); } }));
      row.onclick = () => { closePopup(); activate(tab.path); };
      row.onkeydown = (e) => { if (e.target === row && e.key === 'Enter') row.click(); };
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
  // what is typed in the Source editor is applied first: the server cannot know about it yet, so it would say "nothing to save"
  let typed = null;
  try { typed = await (await import('./source.js')).flushTypedSource(); } catch { typed = false; }
  await refreshState();
  const dirty = T.tabs.filter((t) => factsOf(t.path).dirty).map((t) => t.path);
  const plan = planSaveAll({ typed, dirty });
  if (plan.action !== 'ask') { toast(plan.message); return; }
  const remembered = localStorage.getItem(CHECKPOINT_KEY);
  const checkbox = el('input', { type: 'checkbox', id: 'saveall-checkpoint', checked: remembered === null ? true : remembered === '1' });
  const choice = await ask('Save all', `${dirty.length} file${dirty.length === 1 ? ' has' : 's have'} changed: ${dirty.map(baseName).join(', ')}. Save ${dirty.length === 1 ? 'it' : 'them all'}?`,
    [{ label: 'Cancel', value: 'cancel' }, { label: 'Save active', value: 'active' }, { label: 'Save all', value: 'all', primary: true }], [el('label', { className: 'saveall-cp' }, [checkbox, ' Make a checkpoint first, so the history keeps the state before this save'])]);
  if (!choice || choice === 'cancel') return;
  try { localStorage.setItem(CHECKPOINT_KEY, checkbox.checked ? '1' : '0'); } catch { /* storage may be blocked */ }
  const targets = choice === 'active' ? dirty.filter((p) => p === T.active) : dirty;
  if (!targets.length) { toast('The active file has no unsaved changes.'); return; }
  let detail = { saved: 0, skipped: 0, failed: 0 };
  try {
    if (checkbox.checked) { try { await api.post('/api/history/checkpoint', { label: 'Before Save all' }); } catch (error) { toast(`No checkpoint was made: ${error.message || error}`); } }
    const outcome = { saved: 0, skipped: 0, failed: 0 };
    for (const path of targets) {
      if (isUntitled(path)) { if (await placeUntitled(path)) outcome.saved++; else outcome.skipped++; continue; }
      try { await api.post(kindOf(path) === 'song' ? '/api/doc/save' : '/api/wc/save', { path }); outcome.saved++; } catch (error) { outcome.failed++; toast(error.message || String(error)); }
    }
    detail = outcome;
    if (!outcome.skipped && !outcome.failed) toast(`Saved ${outcome.saved} file${outcome.saved === 1 ? '' : 's'}.`);
    else toast(`Saved ${outcome.saved}, not saved ${outcome.skipped + outcome.failed} (${outcome.skipped} skipped, ${outcome.failed} failed).`);
  } catch (error) { toast(error.message || String(error)); }
  await refreshState();
  document.dispatchEvent(new CustomEvent('ample:saved-all', { detail }));
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
  // leaving the sequencer for the Blocks view: the sequence file's tab no longer is the one you are on, the song's tab is
  document.addEventListener('ample:view', (e) => { if (e.detail === 'blocks' && S.path && /\.(ampleseq|seq)$/i.test(T.active || '')) { T.active = S.path; save(); render(); } });
  // a tab for a file without opening it: what you look at stays (the files that Koda changed arrive like this, with their unsaved mark)
  document.addEventListener('ample:add-tab', (e) => { if (e.detail) { add(e.detail); save(); render(); refreshState(); } });
  // the answers to the open requests (main.js): one that went through is committed, a refused one is taken back; a song opened by any route is what the editor shows
  document.addEventListener('ample:open-result', answered);
  // the option "preview tabs" (Project settings): a single click in the Project panel opens a tab in italics that the next single click replaces; a double click or an edit keeps it
  document.addEventListener('ample:preview-file', (e) => {
    if (!previewOn()) return;
    const path = e.detail;
    if (kindOf(path) !== 'song' && !viewable(path)) return;   // a song opens through its own route (ample:open); any other file only when the opener can show it
    if (T.tabs.some((t) => t.path === path)) { if (T.active !== path) activate(path); return; }
    openFile(path, { preview: true });
  });
  document.addEventListener('ample:song-opened', () => {
    if (!opening) {
      if (!T.tabs.some((t) => t.path === S.path)) add(S.path);
      T.active = S.path; save(); render(); refreshState();
    }
    const opened = S.path;
    setTimeout(() => { if (S.path === opened) dropBlankUntitled(opened); }, 600);
  });
  // leaving the page with a blank, untouched new song: the file goes away too, so it does not stay in the project.
  // The page cannot tell a reload from leaving (pagehide is the same, the Navigation API does not report a reload from the browser), so a reload removes the blank song as well: the page that comes back shows the song that was open before.
  window.addEventListener('pagehide', (event) => {
    if (event.persisted || detachedPath()) return;
    try {
      const map = untitledMap();
      for (const name of Object.keys(map)) {
        const untouched = S.path === name ? S.doc && !S.doc.dirty && S.doc.text === map[name] : false;
        if (!untouched) continue;
        fetch(`/api/project/file?path=${encodeURIComponent(name)}`, { method: 'DELETE', headers: { 'X-Ample-Token': getToken() }, keepalive: true }).catch(() => {});
        delete map[name]; T.tabs = T.tabs.filter((t) => t.path !== name); if (T.active === name) T.active = T.tabs[0]?.path || ''; save();
      }
      rememberUntitled(map);
    } catch { /* leaving anyway */ }
  });
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
  // Reopen a stored non-song tab through the same lifecycle as a click.
  if (!mine && T.active && kindOf(T.active) !== 'song') {
    const restored = T.active;
    T.active = S.path || '';
    render();
    if (viewable(restored) && T.tabs.some((t) => t.path === restored)) setTimeout(() => { if (!opening && T.active === S.path) activate(restored); }, 0);
    else { save(); render(); }
  }
}
