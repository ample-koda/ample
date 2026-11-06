// Open files as tabs (plan/editor-tabs.md, parts 2 and 3): one row of tabs with a dot for unsaved changes and a mark for errors, an overflow list that holds every tab,
// a phone layout that shows the active tab and the list, Save all, and one shared tab each for audio and image files. The tabs are a view on the files that the studio
// already opens (a song, a text or component file); a tab click opens that file through the same events as the Project panel. @ample:tabs

import { api, getToken } from './api.js';
import { S, subscribe } from './store.js';
import { $, ask, el, showMenu, toast } from './ui.js';

const KIND = [
  ['song', /\.amplet$/i], ['module', /\.module$/i], ['component', /\.amplec$/i],
  ['audio', /\.(wav|mp3|flac|ogg|aif|aiff)$/i], ['image', /\.(png|jpe?g|gif|webp|svg)$/i],
];
export const kindOf = (path) => KIND.find(([, pattern]) => pattern.test(path))?.[0] ?? 'text';
const SHARED = new Set(['audio', 'image']);   // one tab each: the next audio file takes the place of the one in the audio tab

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
  if (T.remote.has(path)) { post({ type: 'focus', path }); toast(`${baseName(path)} is open in another window: it is brought to the front.`); return; }   // one editor per file, in whatever window
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
  T.tabs.splice(index, 1);
  if (T.active === path) {
    const next = T.tabs[Math.min(index, T.tabs.length - 1)];
    if (next) activate(next.path); else if (S.path) { add(S.path); T.active = S.path; }
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
    else if (data.type === 'attached') { T.remote.delete(data.path); T.active = data.path; save(); render(); activate(data.path); }
  };
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
function putBack() { post({ type: 'attached', path: detachedPath() }); window.close(); }

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
  T.bar.replaceChildren(list, more, saveAllButton);
  T.bar.hidden = T.tabs.length < 1;
  requestAnimationFrame(() => { fit(list); more.classList.toggle('some', T.hidden > 0); });
}

function togglePopup(anchor) {
  if (T.popup) { closePopup(); return; }
  const box = el('div', { id: 'tabs-popup', className: 'tabs-popup', role: 'listbox' });
  const filter = el('input', { type: 'search', placeholder: 'Filter open files', 'aria-label': 'Filter open files' });
  const rows = el('div', { className: 'tabs-rows' });
  const paint = () => {
    rows.replaceChildren();
    const q = filter.value.trim().toLowerCase();
    for (const tab of T.tabs.filter((t) => !q || t.path.toLowerCase().includes(q))) {
      const f = factsOf(tab.path);
      const row = el('div', { className: `tabs-row${tab.path === T.active ? ' on' : ''}`, role: 'option', tabIndex: 0 }, [
        el('span', { className: 'tab-name', textContent: baseName(tab.path) }), el('span', { className: 'tabs-folder', textContent: folderOf(tab.path) }),
        el('span', { className: 'tabs-facts', textContent: [f.dirty ? 'unsaved' : '', f.graphBroken ? 'cannot be read into blocks' : f.errors ? `${f.errors} error(s)` : '', f.warnings ? `${f.warnings} warning(s)` : ''].filter(Boolean).join(' · ') }),
        el('button', { type: 'button', className: 'tab-x', textContent: '×', title: 'Close', onclick: (e) => { e.stopPropagation(); closeTab(tab.path).then(paint); } })]);
      row.onclick = () => { closePopup(); activate(tab.path); };
      row.onkeydown = (e) => { if (e.key === 'Enter') row.click(); };
      rows.append(row);
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
    for (const path of targets) await api.post(kindOf(path) === 'song' ? '/api/doc/save' : '/api/wc/save', { path });
    toast(`Saved ${targets.length} file${targets.length === 1 ? '' : 's'}.`);
  } catch (error) { toast(error.message || String(error)); }
  await refreshState();
  document.dispatchEvent(new CustomEvent('ample:saved-all'));
}

/** What Koda is told about the open files: the list of tabs with their state, and for the ones with unsaved changes what changed (a short diff of the saved text against the working text). */
export async function tabsContextForKoda() {
  if (!T.tabs.length) return '';
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
  return `\n\n${lines.join('\n')}`;
}

/** Starts the tabs: restores the open files of the project, follows the files the studio opens, and polls the marks of the other tabs. */
export function initTabs() {
  T.bar = $('#tabbar');
  if (!T.bar) return;
  try { const stored = JSON.parse(localStorage.getItem(key()) || 'null'); for (const p of stored?.paths || []) add(p); T.active = stored?.active || ''; } catch { /* a damaged list starts empty */ }
  if (S.path) { add(S.path); if (!T.active || kindOf(T.active) === 'song') T.active = S.path; }
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
  document.addEventListener('ample:song-opened', () => { add(S.path); T.active = S.path; save(); render(); refreshState(); });
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
