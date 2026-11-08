import { api } from './api.js';
import { el } from './ui.js';
import { S } from './store.js';
import { listRecoveryDrafts } from '../lib/recovery-drafts.mjs';
import { listWorkRecords } from '../lib/work-state.mjs';

const LEGACY_RECENT_KEY = 'ample.se.recent';
const recentKey = () => `ample.se.recent:${S.project?.id || ''}`;
const EDITABLE = /\.amplet$|\.module$|\.(?:lyr|amplelrc|lrc|chords|txt|md|csv|json|seq|ampleseq)$/i;
let nextMenuId = 0;
let listenersBound = false;

export function rememberRecentFile(path) {
  if (!path || !EDITABLE.test(path)) return;
  try {
    const key = recentKey();
    const stored = JSON.parse(localStorage.getItem(key) || localStorage.getItem(LEGACY_RECENT_KEY) || '[]');
    const recent = Array.isArray(stored) ? stored.filter((item) => typeof item === 'string') : [];
    localStorage.setItem(key, JSON.stringify([path, ...recent.filter((item) => item !== path)].slice(0, 12)));
  } catch { /* storage may be blocked */ }
}

export function recentFilePaths() {
  try { return JSON.parse(localStorage.getItem(recentKey()) || localStorage.getItem(LEGACY_RECENT_KEY) || '[]').filter((path) => typeof path === 'string'); } catch { return []; }
}

function sourcePath() {
  const close = document.querySelector('#se-close-file');
  const title = document.querySelector('#se-title')?.textContent;
  return close && !close.hidden && title ? title : null;
}

function bindDismissal() {
  if (listenersBound) return;
  listenersBound = true;
  document.addEventListener('pointerdown', (event) => {
    for (const menu of document.querySelectorAll('.recent-files-menu:not([hidden])')) {
      if (!menu.parentElement.contains(event.target)) {
        menu.hidden = true;
        menu.previousElementSibling?.setAttribute('aria-expanded', 'false');
      }
    }
  });
  document.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape') return;
    for (const menu of document.querySelectorAll('.recent-files-menu:not([hidden])')) {
      menu.hidden = true;
      menu.previousElementSibling?.setAttribute('aria-expanded', 'false');
    }
  });
}

export function createRecentFilesToggle() {
  bindDismissal();
  const id = `recent-files-menu-${++nextMenuId}`;
  const button = el('button', { type: 'button', className: 'recent-files-toggle', textContent: 'File ▾', title: 'Add a new file, Save all, the files that are open and the recent ones', 'aria-label': 'File menu', 'aria-expanded': 'false', 'aria-controls': id });
  const menu = el('div', { id, className: 'recent-files-menu', role: 'menu', hidden: true });
  const wrap = el('span', { className: 'recent-files-control' }, [button, menu]);
  const close = () => { menu.hidden = true; button.setAttribute('aria-expanded', 'false'); };
  const openPath = (path) => {
    close();
    const event = /\.amplet$/i.test(path) ? 'ample:open' : /\.module$/i.test(path) ? 'ample:open-source-file' : 'ample:open-file';
    document.dispatchEvent(new CustomEvent(event, { detail: path }));
  };
  const addRow = (path, label, draft, active) => {
    const row = el('button', { type: 'button', role: 'menuitem', className: `recent-file-row${active ? ' active' : ''}`, title: path, onclick: () => openPath(path) });
    row.dataset.file = path;
    row.append(el('span', { className: 'recent-file-path', textContent: path }));
    if (label) row.append(el('span', { className: 'recent-file-label', textContent: label }));
    const sourceState = path === sourcePath() ? document.querySelector('#se .savectl')?.dataset.state : null;
    if (draft) row.append(el('span', { className: 'recent-file-dirty', textContent: '● Unsaved', title: `Recoverable changes from ${new Date(draft.updated).toLocaleString()}` }));
    else if (sourceState && sourceState !== 'clean') row.append(el('span', { className: 'recent-file-dirty', textContent: '● Unsaved', title: 'The source editor has changes not yet saved' }));
    else if (path === S.path && S.doc?.dirty) row.append(el('span', { className: 'recent-file-dirty', textContent: '● Unsaved', title: 'The song has unsaved changes' }));
    return row;
  };
  const paint = async () => {
    menu.replaceChildren();
    // the actions of the File menu: a new file of a kind (it is made and opened in its editor), and Save all
    const act = (label, hint, fn, id) => { const r = el('button', { type: 'button', role: 'menuitem', className: 'recent-file-row file-action', id, onclick: fn }); r.append(el('span', { className: 'recent-file-path', textContent: label })); if (hint) r.append(el('span', { className: 'recent-file-label', textContent: hint })); return r; };
    const kindsBox = el('div', { className: 'file-kinds', role: 'group', 'aria-label': 'Add new', hidden: true });
    const addNewRow = act('Add new  \u25B8', 'a file of a kind, opened at once', async () => {
      kindsBox.hidden = !kindsBox.hidden;
      if (kindsBox.hidden || kindsBox.childElementCount) return;
      let kinds = [];
      try { kinds = await api.get('/api/project/kinds'); } catch { /* the list stays empty */ }
      const first = ['song', 'folder'];
      const sorted = [...kinds.filter((k) => first.includes(k.id)).sort((a, b) => first.indexOf(a.id) - first.indexOf(b.id)), ...kinds.filter((k) => !first.includes(k.id)).sort((a, b) => a.label.localeCompare(b.label, undefined, { sensitivity: 'base' }))];
      for (const kind of sorted) kindsBox.append(act(kind.label, '', () => { close(); import('./projectpanel.js').then((m) => m.addNew(kind, '')); }));
      if (!sorted.length) kindsBox.append(el('div', { className: 'recent-files-empty', textContent: 'Could not load the kinds of file.' }));
    }, 'file-add-new');
    menu.append(addNewRow, kindsBox, act('Save all', 'Ctrl+Shift+S', () => { close(); import('./tabs.js').then((m) => m.saveAll()); }, 'file-save-all'), el('div', { className: 'recent-files-rule' }));
    menu.append(el('div', { className: 'recent-files-heading', textContent: 'Open now' }));
    try {
      const files = await api.get('/api/project/files');
      const paths = new Set(files.map((file) => file.path).filter((path) => EDITABLE.test(path)));
      const drafts = new Map(listRecoveryDrafts(localStorage, S.project?.id || '').map((draft) => [draft.path, draft]));
      for (const record of listWorkRecords(localStorage, S.project?.id || '')) if (!drafts.has(record.path) || drafts.get(record.path).updated < record.updated) drafts.set(record.path, record);
      const active = [...new Set([S.path, sourcePath()].filter((path) => path && paths.has(path)))];
      if (active.length) active.forEach((path) => menu.append(addRow(path, path === S.path ? 'Current song' : 'Source editor', drafts.get(path), true)));
      else menu.append(el('div', { className: 'recent-files-empty', textContent: 'No file is open.' }));
      const recent = recentFilePaths().filter((path) => paths.has(path) && !active.includes(path));
      menu.append(el('div', { className: 'recent-files-heading', textContent: 'Recent files' }));
      if (recent.length) recent.forEach((path) => menu.append(addRow(path, '', drafts.get(path), false)));
      else menu.append(el('div', { className: 'recent-files-empty', textContent: 'No other recent files.' }));
    } catch {
      menu.append(el('div', { className: 'recent-files-empty', textContent: 'Could not load project files.' }));
    }
  };
  button.onclick = async () => {
    const opening = menu.hidden;
    close();
    if (!opening) return;
    button.setAttribute('aria-expanded', 'true');
    menu.hidden = false;
    await paint();
  };
  return wrap;
}
