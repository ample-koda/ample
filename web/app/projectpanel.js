import { filterProjectFiles, parentFolder } from '../lib/filepicker-model.mjs';
import { projectRowsWindow, projectViewFromZoom } from '../lib/project-panel-model.mjs';
import { api } from './api.js';
import { renderProjectTree } from './projecttree.js';
import { addProjectPreview, filePlayCell } from './filelist.js';
import { uploadProjectFiles } from './filepicker.js';
import { $, ask, el, showMenu, toast } from './ui.js';
import { copyMenuItem } from './copy-path.js';
import { S, reloadDoc } from './store.js';
import { blocksFor, kindOf } from '../lib/file-kinds.mjs';
import { pointBlockAtFile, useFileInSong } from './canvas.js';
import { beginFileDrag, dragHasFiles, draggedFile } from './dragfile.js';
import { projectItemIcon } from './fileicons.js';
import { addDetailPreview, addTileControls, clearTilePreviews } from './project-tile-preview.js';
import { clickSelection } from '../lib/project-selection-model.mjs';
import { attachMarquee, moveSelection } from './projectselect.js';

let kindsCache = null;
const projectKinds = async () => kindsCache || (kindsCache = await api.get('/api/project/kinds'));

/** Creates a file or folder of a kind in a folder (the Add menu), then opens it in the editor that fits. */
export async function addNew(kind, inFolder) {
  try {
    // a file browser: the folders and what is in them, a name with the extension after it, and what is made (the folder the kind prefers, or the one this was started from)
    const { openProjectFilePicker } = await import('./filepicker.js');
    const picked = await openProjectFilePicker('', undefined, 'new', { title: `New ${kind.label.toLowerCase()}`, extension: kind.extension, isFolder: kind.isFolder, folder: inFolder || kind.folder || '' });
    if (!picked) return;
    const body = { kind: kind.id, name: picked.name };
    if (picked.folder) body.folder = picked.folder;
    const made = await api.post('/api/project/new', body);
    await refreshProjectPanel();
    if (made.open === 'song') document.dispatchEvent(new CustomEvent('ample:open', { detail: made.path }));
    else if (made.open === 'text' || made.open === 'midi') document.dispatchEvent(new CustomEvent('ample:open-file', { detail: made.path }));
    else if (made.isFolder || made.open === 'none') document.dispatchEvent(new CustomEvent('ample:reveal-file', { detail: made.path }));   // a folder is shown (and opened) in the project files
    toast(`Created ${made.path}`);
  } catch (error) { toast(error.message); }
}

/** The Add › entries for a folder: one per kind of file AMPLE can create. */
async function addMenuItems(inFolder) {
  const kinds = await projectKinds().catch(() => []);
  const folder = kinds.find((kind) => kind.id === 'folder'), song = kinds.find((kind) => kind.label === 'Song');
  const rest = kinds.filter((kind) => kind !== folder && kind !== song).sort((left, right) => left.label.localeCompare(right.label, undefined, { sensitivity: 'base' }));
  const item = (kind) => ({ label: kind.label, icon: projectItemIcon({ kind: kind.id, isDir: kind.isFolder, path: kind.id }), fn: () => addNew(kind, inFolder) });
  // recording starts here: what is recorded lands in this folder
  const where = inFolder || 'the project';
  const record = { label: 'Record audio here...', icon: projectItemIcon({ kind: 'audio', isDir: false, path: 'recording.wav' }), fn: () => import('./recorder.js').then((m) => m.openRecorder({ title: `Record into ${where}`, folder: inFolder || '', useLabel: 'Save in this folder', onSaved: () => refreshProjectPanel() })).catch((error) => toast(error.message || String(error))) };
  const hum = { label: 'Hum to MIDI here...', icon: projectItemIcon({ kind: 'midi', isDir: false, path: 'hum.mid' }), fn: () => import('./hum.js').then((m) => m.startHum({ title: `Hum a melody into ${where}`, acceptLabel: 'Save in this folder', onAccept: async ({ result, bytes, options }) => {
    const stamp = new Date().toISOString().replace(/[^0-9]/g, '').slice(0, 14), stem = `hum-${stamp}`;
    await api.uploadProject(new File([bytes], `${stem}.wav`, { type: 'audio/wav' }), inFolder || '', 'rename', () => {});
    const saved = await api.post('/api/hum/save-midi', { folder: inFolder || '', name: stem, tempo: options.tempo, notes: result.notes });
    toast(`Saved ${stem}.wav and ${saved.path}`);
    await refreshProjectPanel();
  } })).catch((error) => toast(error.message || String(error))) };
  return { label: 'Add', children: [...[folder, song].filter(Boolean).map(item), ...(rest.length ? ['-'] : []), ...rest.map(item), '-', record, hum] };
}

/** The entries that put a project file to use in the open song. */
function useMenuItems(item) {
  const kind = kindOf(item.path, item.isDir);
  const entries = [];
  const blocks = blocksFor(kind, S.templates);
  if (blocks.length && S.doc) entries.push({ label: 'Use in song', children: blocks.map((choice) => ({ label: `New ${choice.title || choice.block} block${choice.param !== 'file' ? ` (${choice.param})` : ''}`, fn: () => useFileInSong(item.path, { block: choice.block, param: choice.param, isDir: item.isDir }) })) });
  if (blocks.length && S.doc) {
    const existing = (S.doc.blocks || []).flatMap((b) => (S.tpl[b.type]?.parameters || []).filter((p) => blocks.some((c) => c.block === b.type && c.param === p.name)).map((p) => ({ label: `${b.name} › ${p.name}`, fn: () => pointBlockAtFile(b.name, p.name, item.path) })));
    if (existing.length) entries.push({ label: 'Point a block at it', children: existing });
  }
  return entries;
}

/**
 * Renames or moves a file or folder. With refactor (the default) the songs and modules that point at it follow: a dry run lists every line that changes
 * and nothing happens until it is confirmed. Without refactor only the file moves and the paths that point at it may break.
 */
async function moveWithPreview(item, destination, refactor) {
  try {
    if (!destination || destination === item.path) return;
    const label = item.isDir ? 'folder' : 'file';
    let edits = [];
    if (refactor) edits = (await api.post('/api/project/rename', { source: item.path, destination, dryRun: true })).edits || [];
    const extra = [];
    if (edits.length) extra.push(el('pre', { className: 'refactor-list', textContent: edits.slice(0, 60).map((e) => `${e.file}:${e.line}  ${e.old.trim()}  →  ${e.new.trim()}`).join('\n') + (edits.length > 60 ? `\n... and ${edits.length - 60} more` : '') }));
    const text = refactor
      ? (edits.length ? `${item.path} becomes ${destination}. ${edits.length} line(s) that point at it will be updated:` : `${item.path} becomes ${destination}. Nothing in the songs or modules points at it.`)
      : `${item.path} becomes ${destination}. Only the ${label} moves: paths that point at it are not changed and may stop working.`;
    const ok = await ask(refactor ? `Move ${label}` : `Move ${label} without refactor`, text, [{ label: refactor ? 'Move' : 'Move anyway', value: true, primary: true }, { label: 'Cancel', value: false }], extra);
    if (!ok) return;
    const done = await api.post('/api/project/rename', { source: item.path, destination, refactor });
    await refreshProjectPanel();
    if ((done.edits || []).length) await reloadDoc().catch(() => {});   // the open song may have changed
    toast(refactor ? `Moved to ${destination}; ${(done.edits || []).length} reference(s) updated.` : `Moved to ${destination}. References were not changed.`);
  } catch (error) { toast(error.message); }
}

/** A name for the copy of an item that is free in its folder: "name copy.ext", then "name copy 2.ext" and so on. */
function copyName(item) {
  const taken = new Set(items.map((entry) => entry.name));
  const dot = item.isDir ? -1 : item.name.lastIndexOf('.');
  const stem = dot > 0 ? item.name.slice(0, dot) : item.name, ext = dot > 0 ? item.name.slice(dot) : '';
  let name = `${stem} copy${ext}`;
  for (let n = 2; taken.has(name); n++) name = `${stem} copy ${n}${ext}`;
  return name;
}

/** Duplicates a file or a folder (with what is in it) next to the original; the name can be changed first. */
async function duplicateItem(item) {
  const input = el('input', { value: copyName(item), 'aria-label': 'Name of the copy' });
  if (!(await ask('Duplicate', `A copy of ${item.path} is made in the same folder.`, [{ label: 'Duplicate', value: true, primary: true }, { label: 'Cancel', value: false }], [input]))) return;
  const name = input.value.trim();
  if (!name || name.includes('/')) { toast('A name has no slash.'); return; }
  try {
    const parent = parentFolder(item.path);
    const done = await api.post('/api/project/copy', { source: item.path, destination: parent ? `${parent}/${name}` : name });
    await refreshProjectPanel();
    toast(`Copied to ${done.path}`);
  } catch (error) { toast(error.message); }
}

async function renameItem(item, refactor) {
  const input = el('input', { value: item.name, 'aria-label': 'New name' });
  if (!(await ask(refactor ? 'Rename' : 'Rename without refactor', 'Choose a name that is unused in this folder.', [{ label: 'Next', value: true, primary: true }, { label: 'Cancel', value: false }], [input]))) return;
  const name = input.value.trim();
  if (!name || name.includes('/')) { toast('A name has no slash. Use Move to change the folder.'); return; }
  const parent = parentFolder(item.path);
  await moveWithPreview(item, parent ? `${parent}/${name}` : name, refactor);
}

async function moveItem(item, refactor) {
  const input = el('input', { value: item.path, 'aria-label': 'Project relative destination path' });
  if (!(await ask(refactor ? 'Move' : 'Move without refactor', 'Enter the project-relative destination path.', [{ label: 'Next', value: true, primary: true }, { label: 'Cancel', value: false }], [input]))) return;
  await moveWithPreview(item, input.value.trim().replace(/^\/+|\/+$/g, ''), refactor);
}

async function showUsage(item) {
  try {
    const usage = await api.get(`/api/project/usage?path=${encodeURIComponent(item.path)}`);
    const list = usage.users || [];
    toast(list.length ? `Used by ${list.map((u) => `${u.block} (${u.path})`).join(', ')}` : 'Nothing in the songs refers to this.');
  } catch (error) { toast(error.message); }
}


const ProjectColumnsDefault = [
  { id: 'name', width: 260 }, { id: 'kind', width: 90 }, { id: 'size', width: 100 }, { id: 'length', width: 90 }, { id: 'modified', width: 180 },
];
const ProjectColumnsExtra = [{ id: 'length', width: 90 }, { id: 'usedBy', width: 180 }, { id: 'path', width: 250 }, { id: 'sha256', width: 220 }];
/** The height of a row of the details list in pixels (the same number is in the style sheet: extra.css, .project-details). */
const DETAIL_ROW = 40;
let folder = '';
let treeWidth = 260;
let selected = '';
let selectedPaths = new Set();
let anchor = '';          // the item a Shift+click counts the range from
let lastVisible = [];   // the items of the list as they are on the screen (in order), also those of a long list that are not drawn
let dragGroup = null;     // the items that are being dragged together (a dragged item that is part of a selection of several)

/** A click on an item: plain, Ctrl/Cmd (toggle) or Shift (range from the anchor) like a file manager. `order` is the items on the screen in order. */
function selectClick(event, item, order) {
  const result = clickSelection(order.map((entry) => entry.path), selectedPaths, anchor, item.path, event);
  selectedPaths = result.selected;
  anchor = result.anchor;
  selected = item.path;
}

/** Shows the selection on the tiles and rows that are on the screen without drawing the list again (used while a rectangle is drawn). */
function applySelectionClasses(host) {
  host.querySelectorAll('.project-tile[data-path], tr[data-path]').forEach((node) => {
    const on = selectedPaths.has(node.dataset.path);
    node.classList.toggle('sel', on);
    if (node.tagName === 'TR') node.setAttribute('aria-selected', String(on));
  });
}

const selectedItems = () => items.filter((entry) => selectedPaths.has(entry.path));
let view = 'tiles';
let size = 2;
let filter = 'all';
let items = [];
let baseItems = [];   // the open folder itself; while a search is typed, items holds what matches in that folder and every folder below it
let query = '';
/** The line under the results of a search: look in the folders below the open one as well (shown as one part per folder). */
async function searchBelow(host) {
  const query0 = query;
  let found = baseItems;
  try { found = await deepItems(); } catch { toast('The folders below could not be searched.'); return; }
  if (query !== query0) return;   // the search text changed while the folders were read
  items = found;
  renderContents(host);
}
const deepLine = (host) => {
  const line = el('button', { type: 'button', className: 'project-deep-line', textContent: 'Search the folders below as well' });
  line.onclick = () => searchBelow(host);
  line.ondblclick = () => searchBelow(host);
  return line;
};

/** Every file and folder below the open folder (the search looks through all of them), capped so a huge project stays responsive. */
async function deepItems() {
  const response = await api.get(`/api/project/tree?path=${encodeURIComponent(folder)}&depth=12${hiddenQuery()}`);
  const out = [];
  const walk = (nodes) => { for (const node of nodes || []) { if (out.length >= 3000) return; out.push(node); if (node.children) walk(node.children); } };
  walk(response.children);
  return out;
}

/** A small line icon for the filter chips that have no file kind of their own (All, Hidden). */
function glyph(markup) {
  const icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  icon.setAttribute('class', 'project-item-icon chip-icon');
  icon.setAttribute('viewBox', '0 0 24 24');
  icon.setAttribute('fill', 'none');
  icon.setAttribute('stroke', 'currentColor');
  icon.setAttribute('aria-hidden', 'true');
  icon.innerHTML = markup;
  return icon;
}
const ALL_ICON = '<rect x="4" y="4" width="7" height="7" rx="1"/><rect x="13" y="4" width="7" height="7" rx="1"/><rect x="4" y="13" width="7" height="7" rx="1"/><rect x="13" y="13" width="7" height="7" rx="1"/>';
const HIDDEN_ICON = '<path d="M3 12s3.5-6 9-6 9 6 9 6-3.5 6-9 6-9-6-9-6z"/><circle cx="12" cy="12" r="2.5"/><path d="M4 20 20 4"/>';
let sortCol = 'name';
let sortDir = 1;
let columns = ProjectColumnsDefault.map((column) => ({ ...column }));
const usageCache = new Map();
const hashCache = new Map();
const optionalCells = new WeakMap();
const detailWindows = new WeakMap();
let optionalCellObserver = null;
let settingsTimer = 0;
let sharedLayoutEdited = false;
let ready = false;
let zoomPointerX = null;
let treeHost = null;
const treeOpen = new Set();   // the folders of the tree that are open: a toggle that happens while a refresh is on its way must survive it
let treeRoot = null;
const sizes = [120, 150, 180, 220, 270];
const stateKey = () => `ample.project-panel.${S.project?.id || 'current'}`;

let showHidden = false;   // names that start with a dot (such as .git) stay out of sight unless asked for
const hiddenQuery = () => (showHidden ? '&hidden=1' : '');

/** Starts a drag of one item, or of the whole selection when the item is part of a selection of several. */
function beginItemDrag(event, item) {
  beginFileDrag(event, item);
  const group = selectedPaths.has(item.path) && selectedPaths.size > 1 ? selectedItems() : null;
  dragGroup = group && group.length > 1 ? group : null;
  if (dragGroup && event.dataTransfer) event.dataTransfer.setData('application/x-ample-paths', JSON.stringify(dragGroup.map((entry) => entry.path)));
}
addEventListener('dragend', () => setTimeout(() => { dragGroup = null; }, 0));

const refreshAfterMove = { refresh: () => refreshProjectPanel(), reloadDoc: () => reloadDoc() };

/** Several items are dropped on a folder: one confirmation that lists every move and the references that follow. */
async function dropGroupOnFolder(group, targetFolder) {
  await moveSelection(group, targetFolder, refreshAfterMove);
  selectedPaths = new Set(); anchor = '';
}

/** Moves a dragged project item into a folder ('' is the project root): the same preview and refactor as the Move command. */
async function dropItemOnFolder(source, targetFolder) {
  const name = source.split('/').pop();
  const destination = targetFolder ? `${targetFolder}/${name}` : name;
  if (destination === source || destination.startsWith(`${source}/`)) return;
  await moveWithPreview({ path: source, name, isDir: !!draggedFile()?.isDir }, destination, true);
}

function saveState() {
  try { localStorage.setItem(stateKey(), JSON.stringify({ folder, treeWidth, view, size, sortCol, sortDir, showHidden })); } catch { /* storage may be blocked */ }
}

function saveSharedLayout() {
  sharedLayoutEdited = true;
  clearTimeout(settingsTimer);
  settingsTimer = setTimeout(() => api.post('/api/project/settings', { projectPanel: { view, tileSize: size, sortColumn: sortCol, sortDescending: sortDir < 0, columns, lengthColumn: true } }).catch((error) => toast(`Project layout was not saved: ${error.message}`)), 250);
}

function projectColumnValue(item, id) {
  if (id === 'name') return item.name;
  if (id === 'kind') return item.isDir ? 'folder' : item.kind;
  if (id === 'size') return item.isDir ? '—' : sizeText(item.sizeBytes);
  if (id === 'modified') return new Date(item.modified).toLocaleString();
  if (id === 'path') return item.path;
  if (id === 'usedBy' || id === 'sha256' || id === 'length') return '';
  return '';
}

const lengthCache = new Map();
const AUDIO_NAME = /\.(wav|mp3|flac|ogg|aif|aiff)$/i;
const lengthText = (seconds) => { const total = Math.round(seconds * 10) / 10, minutes = Math.floor(total / 60); return `${minutes}:${(total - minutes * 60).toFixed(1).padStart(4, '0')}`; };

/** The columns that need a read of the file are filled in when their row is on the screen. */
function loadLengthColumn(item, cell) {
  if (item.isDir || !AUDIO_NAME.test(item.name)) { cell.textContent = '—'; return; }
  if (lengthCache.has(item.path)) { cell.textContent = lengthText(lengthCache.get(item.path)); return; }
  cell.textContent = '…';
  const probe = new Audio();
  probe.preload = 'metadata';
  probe.onloadedmetadata = () => { lengthCache.set(item.path, probe.duration); if (cell.isConnected) cell.textContent = lengthText(probe.duration); probe.removeAttribute('src'); };
  probe.onerror = () => { if (cell.isConnected) cell.textContent = '?'; };
  probe.src = `/api/project/audio?path=${encodeURIComponent(item.path)}`;
}

function sizeText(bytes) { return bytes < 1024 ? `${bytes} B` : `${Math.round(bytes / 1024)} KB`; }

function loadProjectColumn(item, id, cell) {
  if (id === 'length') { loadLengthColumn(item, cell); return; }
  if (item.isDir) { cell.textContent = '—'; return; }
  const cache = id === 'usedBy' ? usageCache : hashCache;
  const render = (value) => {
    if (id === 'usedBy') {
      const users = value.users || [];
      const names = [...new Set(users.map((user) => user.block || user.path))];
      cell.textContent = users.length ? `${users.length} · ${names.slice(0, 2).join(', ')}${names.length > 2 ? '…' : ''}` : 'Unused';
      cell.title = users.map((user) => `${user.path} · ${user.block || user.type}`).join('\n') || 'No song or module references this file.';
    } else {
      cell.textContent = value.sha256.slice(0, 12);
      cell.title = value.sha256;
    }
    cell.removeAttribute('aria-busy');
  };
  if (cache.has(item.path)) {
    const saved = cache.get(item.path);
    if (saved) render(saved);
    else { cell.textContent = 'Loading...'; cell.setAttribute('aria-busy', 'true'); }
    return;
  }
  cell.textContent = 'Loading...'; cell.setAttribute('aria-busy', 'true'); cache.set(item.path, null);
  const request = id === 'usedBy' ? api.get(`/api/project/usage?path=${encodeURIComponent(item.path)}`) : api.projectFileHash(item.path);
  request.then((value) => { cache.set(item.path, value); if (cell.isConnected) render(value); }).catch((error) => { cache.delete(item.path); if (cell.isConnected) { cell.textContent = 'Unavailable'; cell.title = error.message; cell.removeAttribute('aria-busy'); } });
}

function observeProjectColumns(host) {
  const cells = host.querySelectorAll('td[data-column]');
  if (!('IntersectionObserver' in window)) {
    cells.forEach((cell) => { const item = items.find((entry) => entry.path === (cell.dataset.path || cell.dataset.itemPath)); if (item) loadProjectColumn(item, cell.dataset.column, cell); });
    return;
  }
  optionalCellObserver ||= new IntersectionObserver((entries) => entries.forEach((entry) => {
    if (!entry.isIntersecting) return;
    optionalCellObserver.unobserve(entry.target);
    const target = optionalCells.get(entry.target);
    if (target) loadProjectColumn(target.item, target.id, entry.target);
  }), { root: host, rootMargin: '80px' });
  cells.forEach((cell) => {
    const item = items.find((entry) => entry.path === (cell.dataset.path || cell.dataset.itemPath));
    if (item) { optionalCells.set(cell, { item, id: cell.dataset.column }); optionalCellObserver.observe(cell); }
  });
}

function matches(item) {
  return (!query || item.name.toLocaleLowerCase('en-US').includes(query.toLocaleLowerCase('en-US')))
    && (filter === 'all' || filter === 'packs' ? filter === 'all' || item.isDir : item.kind === filter);
}

function renderDetailRows(host, body, visible, columns, makeRow, scrolled = null) {
  let state = detailWindows.get(host);
  if (!state) {
    state = { render: null, focusPath: '', pending: null };
    state.render = () => {
      const { start, end } = projectRowsWindow(state.items.length, state.pending ?? host.scrollTop, host.clientHeight || 320, DETAIL_ROW, 8, 2000);
      state.body.replaceChildren();
      const spacer = (height) => { if (!height) return; const cell = el('td', { colSpan: state.columns.length, 'aria-hidden': 'true' }); cell.style.height = `${height}px`; state.body.append(el('tr', { className: 'project-virtual-spacer' }, [cell])); };
      if (state.items.length > 2000) spacer(start * DETAIL_ROW);
      for (let index = start; index < end; index++) state.body.append(state.makeRow(state.items[index], index));
      if (state.items.length > 2000) spacer((state.items.length - end) * DETAIL_ROW);
      if (state.pending !== null) { host.scrollTop = state.pending; state.pending = null; }   // the host was emptied, which reset its scroll position
      if (state.focusPath) [...state.body.querySelectorAll('tr[data-path]')].find((row) => row.dataset.path === state.focusPath)?.focus({ preventScroll: true });   // focusing a row must not scroll the list back to it
    };
    host.addEventListener('scroll', () => { if (state.items.length <= 2000) return; if (host.windowFrame) cancelAnimationFrame(host.windowFrame); host.windowFrame = requestAnimationFrame(state.render); }, { passive: true });
    detailWindows.set(host, state);
  }
  state.body = body;
  state.items = visible;
  state.columns = columns;
  state.makeRow = makeRow;
  state.pending = scrolled;
  state.render();
}

function renderContents(host) {
  clearTilePreviews();
  optionalCellObserver?.disconnect();
  const scrolledBefore = host.scrollTop;
  host.innerHTML = '';
  const filtered = items.filter(matches);
  const value = (item) => sortCol === 'size' ? item.sizeBytes : sortCol === 'length' ? lengthCache.get(item.path) ?? -1 : sortCol === 'modified' ? item.modified : sortCol === 'usedBy' ? usageCache.get(item.path)?.users?.length ?? -1 : sortCol === 'sha256' ? hashCache.get(item.path)?.sha256 || item.name : item[sortCol] || item.name;
  const compare = (left, right) => { const a = value(left), b = value(right); return (a < b ? -1 : a > b ? 1 : 0) * sortDir; };
  const visible = [...filtered.filter((item) => !item.isDir).sort(compare), ...filtered.filter((item) => item.isDir).sort(compare)];
  lastVisible = visible;
  // a search that looks in the folders below: first what is in the open folder, then one part per folder with a heading (what is drawn; visible stays the list of the items themselves)
  let display = visible;
  if (query.trim() && items !== baseItems && visible.length) {
    const parentOf = (path) => (path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '');
    const groups = new Map();
    for (const item of visible) { const parent = parentOf(item.path); if (!groups.has(parent)) groups.set(parent, []); groups.get(parent).push(item); }
    const keys = [...groups.keys()].sort((a, b) => (a === folder ? -1 : b === folder ? 1 : a.localeCompare(b, undefined, { sensitivity: 'base', numeric: true })));
    if (keys.length > 1 || keys[0] !== folder) {
      display = keys.flatMap((parent) => [{ head: true, label: parent === folder ? 'In this folder' : parent.slice(folder ? folder.length + 1 : 0) + '/', count: groups.get(parent).length }, ...groups.get(parent)]);
    }
  }
  if (visible.length === 0) {
    host.append(el('p', { className: 'project-panel-empty', textContent: query || filter !== 'all' ? 'No files match the current search and filters.' : 'This folder is empty. Use + to create a folder or upload files.' }));
    if (query.trim() && items === baseItems) host.append(deepLine(host));
    return;
  }
  if (view === 'details') {
    const table = el('table', { className: 'project-details' });
    const labels = { name: 'Name', kind: 'Type', size: 'Size', length: 'Length', modified: 'Modified', usedBy: 'Used by', path: 'Path', sha256: 'SHA-256' };
    const headingCells = columns.map((column, index) => {
      const cell = el('th', { textContent: `${labels[column.id]}${sortCol === column.id ? (sortDir > 0 ? ' ▲' : ' ▼') : ''}`, draggable: true, tabIndex: 0, 'aria-sort': sortCol === column.id ? (sortDir > 0 ? 'ascending' : 'descending') : 'none' });
      cell.style.width = `${column.width}px`;
      cell.onclick = (event) => { if (event.target.closest('.project-column-grip')) return; sortDir = sortCol === column.id ? -sortDir : 1; sortCol = column.id; saveState(); saveSharedLayout(); renderContents(host); };
      cell.onkeydown = (event) => { if (!['Enter', ' '].includes(event.key)) return; event.preventDefault(); cell.click(); };
      cell.ondragstart = (event) => event.dataTransfer?.setData('application/x-ample-column', column.id);
      cell.ondragover = (event) => event.preventDefault();
      cell.ondrop = (event) => { event.preventDefault(); const id = event.dataTransfer?.getData('application/x-ample-column'), from = columns.findIndex((item) => item.id === id); if (from < 0 || from === index) return; const [moved] = columns.splice(from, 1); columns.splice(index, 0, moved); saveSharedLayout(); renderContents(host); };
      cell.oncontextmenu = (event) => { event.preventDefault(); showMenu(event.clientX, event.clientY, [...ProjectColumnsExtra.filter((extra) => !columns.some((item) => item.id === extra.id)).map((extra) => ({ label: `Add ${labels[extra.id]} column`, fn: () => { columns.push(extra); saveSharedLayout(); renderContents(host); } })), ...(columns.length > 1 ? [{ label: `Remove ${labels[column.id]} column`, fn: () => { columns = columns.filter((item) => item.id !== column.id); if (sortCol === column.id) sortCol = 'name'; saveSharedLayout(); renderContents(host); } }] : []), { label: 'Reset columns', fn: () => { columns = [...ProjectColumnsDefault]; sortCol = 'name'; sortDir = 1; saveSharedLayout(); renderContents(host); } }]); };
      const grip = el('span', { className: 'project-column-grip', title: 'Drag to resize this column', role: 'separator', tabIndex: 0, 'aria-orientation': 'vertical', 'aria-label': `Resize ${labels[column.id]} column`, 'aria-valuemin': '72', 'aria-valuemax': '640', 'aria-valuenow': String(column.width) });
      grip.onpointerdown = (event) => { event.preventDefault(); event.stopPropagation(); const x = event.clientX, width = column.width; grip.setPointerCapture(event.pointerId); grip.onpointermove = (move) => { column.width = Math.max(72, Math.min(640, width + move.clientX - x)); cell.style.width = `${column.width}px`; grip.setAttribute('aria-valuenow', String(column.width)); }; grip.onpointerup = () => { grip.onpointermove = null; grip.onpointerup = null; saveSharedLayout(); }; };
      grip.onkeydown = (event) => { if (!['ArrowLeft', 'ArrowRight'].includes(event.key)) return; event.preventDefault(); event.stopPropagation(); column.width = Math.max(72, Math.min(640, column.width + (event.key === 'ArrowRight' ? 12 : -12))); cell.style.width = `${column.width}px`; grip.setAttribute('aria-valuenow', String(column.width)); saveSharedLayout(); };
      cell.append(grip); return cell;
    });
    table.append(el('thead', {}, [el('tr', {}, headingCells)]));
    const body = el('tbody');
    const makeRow = (item, index) => {
      if (item.head) return body.appendChild(el('tr', { className: 'project-group-head' }, [el('td', { colSpan: columns.length, textContent: `${item.label} (${item.count})` })]));
      const row = el('tr', { className: selectedPaths.has(item.path) ? 'sel' : '' });
      row.dataset.path = item.path;
      row.tabIndex = selectedPaths.has(item.path) ? 0 : -1;
      row.setAttribute('aria-selected', String(selectedPaths.has(item.path)));
      if (visible.length > 2000) { row.setAttribute('aria-posinset', String(index + 1)); row.setAttribute('aria-setsize', String(visible.length)); }
      columns.forEach((column) => { const cell = el('td', { textContent: projectColumnValue(item, column.id) }); if (column.id === 'name') { const label = cell.textContent; cell.textContent = ''; cell.className = 'file-name-cell'; cell.append(el('span', { className: 'file-name-content' }, [projectItemIcon(item), filePlayCell(item), el('span', { className: 'file-name-text', textContent: label })])); } cell.style.width = `${column.width}px`; if (column.id === 'usedBy' || column.id === 'sha256') { cell.dataset.path = item.path; cell.dataset.column = column.id; } else if (column.id === 'length') { cell.dataset.itemPath = item.path; cell.dataset.column = 'length'; } row.append(cell); });
      const nameCell = row.querySelector('.file-name-cell');
      if (nameCell) addDetailPreview(row, nameCell, item);
      row.onclick = (event) => { if (event.detail >= 2 && item.isDir) { openItem(item); return; } selectClick(event, item, visible); if (event.detail === 1 && !item.isDir && !event.ctrlKey && !event.shiftKey && !event.metaKey) document.dispatchEvent(new CustomEvent('ample:preview-file', { detail: item.path })); detailWindows.get(host).focusPath = item.path; renderContents(host); };
      row.ondblclick = () => openItem(item);
      row.onkeydown = (event) => {
        if (event.key === 'Enter') { openItem(item); return; }
        if (event.key === ' ') { event.preventDefault(); row.click(); return; }
        const current = visible.indexOf(item), next = event.key === 'ArrowDown' ? Math.min(visible.length - 1, current + 1) : event.key === 'ArrowUp' ? Math.max(0, current - 1) : event.key === 'Home' ? 0 : event.key === 'End' ? visible.length - 1 : -1;
        if (next < 0 || !visible.length) return;
        event.preventDefault(); selected = visible[next].path; selectedPaths = new Set([selected]); detailWindows.get(host).focusPath = selected;
        if (visible.length > 2000) host.scrollTop = next * DETAIL_ROW;
        renderContents(host);
      };
      row.oncontextmenu = (event) => contextMenu(event, item);
      row.draggable = true;
      row.dataset.path = item.path; if (item.isDir) row.dataset.dir = '1';
      row.ondragstart = (event) => beginItemDrag(event, item);
      body.append(row);
      return row;
    };
    table.append(body); host.append(table); renderDetailRows(host, body, display, columns, makeRow, scrolledBefore); observeProjectColumns(host); if (query.trim() && items === baseItems) host.append(deepLine(host)); return;
  }
  const grid = el('div', { className: 'project-tiles' }); grid.style.setProperty('--project-tile', `${sizes[size]}px`);
  display.forEach((item) => {
    if (item.head) { grid.append(el('div', { className: 'project-group-head', textContent: `${item.label} (${item.count})` })); return; }
    const tile = el('div', { role: 'group', tabIndex: 0, draggable: true, className: `project-tile${selectedPaths.has(item.path) ? ' sel' : ''}`, 'aria-label': item.name }, [projectItemIcon(item), el('span', { className: 'project-item-name', textContent: item.name, title: item.name })]);
    tile.dataset.path = item.path; if (item.isDir) tile.dataset.dir = '1';
    addProjectPreview(tile, item);
    addTileControls(tile, item);
    tile.onkeydown = (event) => { if (event.target !== tile) return; if (event.key === 'Enter') openItem(item); else if (event.key === ' ') { event.preventDefault(); tile.click(); } };
    tile.ondragstart = (event) => beginItemDrag(event, item);
    tile.onclick = (event) => { if (event.detail >= 2 && item.isDir) { openItem(item); return; } selectClick(event, item, visible); renderContents(host); };
    tile.ondblclick = () => openItem(item);
    tile.oncontextmenu = (event) => contextMenu(event, item);
    grid.append(tile);
  });
  host.append(grid);
  if (query.trim() && items === baseItems) host.append(deepLine(host));
}

const PACKAGE_NAME = /\.ampletx(\.zip)?$/i;

let lastOpened = { path: '', at: 0 };
function openItem(item) {
  // the second click and the dblclick event of one double click both arrive when the rows were not replaced in between: open once
  const now = Date.now();
  if (lastOpened.path === item.path && now - lastOpened.at < 500) return;
  lastOpened = { path: item.path, at: now };
  if (!item.isDir && PACKAGE_NAME.test(item.path)) { import('./package.js').then((module) => module.openPackageViewer(item.path)).catch((error) => toast(error.message || String(error))); return; }
  if (item.isDir) { folder = item.path; saveState(); refreshProjectPanel().catch(() => {}); return; }
  // a sound file opens in a window of its own that floats over the page: the panel stays as it is, and nothing needs to be connected to the file
  if (AUDIO_NAME.test(item.name || item.path)) { import('./filepreview.js').then((module) => module.openFilePreview(item)).catch((error) => toast(error.message || String(error))); return; }
  document.dispatchEvent(new CustomEvent('ample:reveal-file', { detail: item.path }));
  if (/\.(amplet|module)$/i.test(item.path)) document.dispatchEvent(new CustomEvent("ample:open", { detail: item.path }));
  else if (/\.amplec$/i.test(item.path)) document.dispatchEvent(new CustomEvent("ample:open-file", { detail: item.path }));
  else document.dispatchEvent(new CustomEvent('ample:open-file', { detail: item.path }));
}

/** The menu of a selection of several: only what makes sense for all of them. */
function selectionMenu(event) {
  event.preventDefault();
  const group = selectedItems();
  const folders = group.filter((entry) => entry.isDir).length;
  showMenu(event.clientX, event.clientY, [
    { head: `${group.length} items selected${folders ? ` (${folders} folder${folders === 1 ? '' : 's'})` : ''}` },
    { label: 'Move to...', fn: () => moveGroup(group) },
    { label: 'Duplicate', fn: () => duplicateGroup(group) },
    '-',
    { label: 'Copy paths', fn: () => navigator.clipboard.writeText(group.map((entry) => entry.path).join('\n')).then(() => toast(`${group.length} paths copied.`)) },
    { label: 'Clear selection', fn: () => { selectedPaths = new Set(); anchor = ''; renderContents($('#project-panel-content')); } },
    '-',
    { label: 'Delete...', fn: () => deleteGroup(group) },
  ]);
}

async function moveGroup(group) {
  const input = el('input', { value: folder, 'aria-label': 'Destination folder (empty is the project root)' });
  if (!(await ask(`Move ${group.length} items`, 'Enter the project-relative destination folder. The next step shows exactly what will move.', [{ label: 'Next', value: true, primary: true }, { label: 'Cancel', value: false }], [input]))) return;
  await dropGroupOnFolder(group, input.value.trim().replace(/^\/+|\/+$/g, ''));
}

async function duplicateGroup(group) {
  const taken = new Set(items.map((entry) => entry.name));
  const plan = group.map((entry) => {
    const dot = entry.isDir ? -1 : entry.name.lastIndexOf('.');
    const stem = dot > 0 ? entry.name.slice(0, dot) : entry.name, ext = dot > 0 ? entry.name.slice(dot) : '';
    let name = `${stem} copy${ext}`;
    for (let n = 2; taken.has(name); n++) name = `${stem} copy ${n}${ext}`;
    taken.add(name);
    return { source: entry.path, destination: parentFolder(entry.path) ? `${parentFolder(entry.path)}/${name}` : name };
  });
  const list = el('pre', { className: 'refactor-list', textContent: plan.map((entry) => `${entry.source}  →  ${entry.destination}`).join('\n') });
  if (!(await ask(`Duplicate ${group.length} items`, 'A copy of each is made next to the original. Nothing else changes.', [{ label: 'Duplicate', value: true, primary: true }, { label: 'Cancel', value: false }], [list]))) return;
  let done = 0;
  const failed = [];
  for (const entry of plan) {
    try { await api.post('/api/project/copy', entry); done += 1; } catch (error) { failed.push(`${entry.source}: ${error.message || error}`); }
  }
  await refreshProjectPanel();
  toast(failed.length ? `Copied ${done} of ${plan.length}. ${failed[0]}` : `Copied ${done} items.`);
}

async function deleteGroup(group) {
  // a folder can only be removed when it is empty: say so before, not after
  const removable = [], kept = [];
  for (const entry of group) {
    if (!entry.isDir) { removable.push(entry); continue; }
    let count = 0;
    try { count = (await api.get(`/api/project/tree?path=${encodeURIComponent(entry.path)}&depth=1&showHidden=true`)).children.length; } catch { count = 0; }
    if (count) kept.push({ path: entry.path, reason: `is not empty (${count} item${count === 1 ? '' : 's'}); empty it first` }); else removable.push(entry);
  }
  if (!removable.length) { toast(`Nothing can be deleted: ${kept[0]?.path} ${kept[0]?.reason}.`); return; }
  const lines = [...removable.map((entry) => entry.path), ...(kept.length ? ['', 'Left alone:', ...kept.map((entry) => `${entry.path}: ${entry.reason}`)] : [])];
  const list = el('pre', { className: 'refactor-list', id: 'delete-plan', textContent: lines.join('\n') });
  if (!(await ask(`Delete ${removable.length} item${removable.length === 1 ? '' : 's'}`, 'They are removed with a recoverable journal pre-image, so they can be brought back from the history.', [{ label: 'Delete', value: true, primary: true }, { label: 'Cancel', value: false }], [list]))) return;
  let done = 0;
  const failed = [];
  for (const entry of removable) {
    try { if (entry.isDir) await api.deleteProjectFolder(entry.path); else await api.deleteProjectFile(entry.path); done += 1; } catch (error) { failed.push(`${entry.path}: ${error.message || error}`); }
  }
  selectedPaths = new Set(); anchor = ''; selected = '';
  await refreshProjectPanel();
  toast(failed.length ? `Deleted ${done} of ${removable.length}. ${failed[0]}` : `Deleted ${done} item${done === 1 ? '' : 's'}.`);
}

async function contextMenu(event, item) {
  if (selectedPaths.size > 1 && selectedPaths.has(item.path)) { selectionMenu(event); return; }
  event.preventDefault();
  const x = event.clientX, y = event.clientY;
  const add = item.isDir ? [await addMenuItems(item.path)] : [];
  const use = useMenuItems(item);
  const text = !item.isDir && /.(lyr|lrc|amplelrc|chordpro|chords|txt|md|csv|json|module|amplec|seq|ampleseq)$/i.test(item.path) ? [{ label: 'Open as text', fn: () => document.dispatchEvent(new CustomEvent('ample:open-file', { detail: item.path })) }] : [];
  const pack = !item.isDir && PACKAGE_NAME.test(item.path) ? [{ label: 'Import...', fn: () => import('./package.js').then((module) => module.openImportDialog(item.path)) }] : [];
  showMenu(x, y, [
    { label: pack.length ? 'Open read-only' : 'Open', fn: () => openItem(item) },
    ...pack,
    ...add,
    ...use,
    ...text,
    { label: 'Show where used', fn: () => showUsage(item) },
    '-',
    { label: 'Reveal', fn: () => revealInProject(item.path) },
    copyMenuItem(item.path),
    ...(!item.isDir ? [{ label: 'Download', title: 'Download the file as it is', fn: () => import('./project-export.js').then((module) => module.exportOriginal(item)) }] : []),
    ...(item.isDir || AUDIO_NAME.test(item.name || item.path) ? [{ label: 'Download as', children: (item.isDir ? ['zip'] : ['wav', 'mp3', 'flac']).map((format) => ({ label: format.toUpperCase(), fn: () => import('./project-export.js').then((module) => module.exportAs(item, format)) })) }] : []),
    { label: 'Duplicate...', fn: () => duplicateItem(item) },
    { label: 'Rename...', fn: () => renameItem(item, true) },
    { label: 'Move...', fn: () => moveItem(item, true) },
    { label: 'Advanced', children: [{ label: 'Rename without refactor...', fn: () => renameItem(item, false) }, { label: 'Move without refactor...', fn: () => moveItem(item, false) }] },
    { label: 'Delete...', fn: async () => { try { if (await ask('Delete project item', `${item.path} will be removed with a recoverable journal pre-image.`, [{ label: 'Delete', value: true, primary: true }, { label: 'Cancel', value: false }])) { if (item.isDir) await api.deleteProjectFolder(item.path); else await api.deleteProjectFile(item.path); selected = ''; selectedPaths.clear(); await refreshProjectPanel(); } } catch (error) { toast(error.message); } } },
  ]);
}

export async function refreshProjectPanel() {
  const host = $('#project-panel-content');
  if (!host) return;
  usageCache.clear(); hashCache.clear();
  const response = await api.get(`/api/project/tree?path=${encodeURIComponent(folder)}&depth=1${hiddenQuery()}`);
  baseItems = response.children;
  items = baseItems;
  await refreshProjectTree();
  const crumb = document.querySelector('.project-breadcrumb');
  if (crumb) fillBreadcrumb(crumb);
  renderContents(host);
}

/** The open folder as clickable parts with an Up button: the way back up that does not need the tree. */
function fillBreadcrumb(crumb) {
  const parts = folder.split('/').filter(Boolean);
  const go = (path) => { if (path === folder) return; folder = path; saveState(); refreshProjectPanel().catch((error) => toast(error.message)); };
  const up = el('button', { type: 'button', className: 'crumb-up', textContent: '↑', title: 'Up one folder (Alt+Up)', 'aria-label': 'Up one folder' });
  up.disabled = !folder;
  up.onclick = () => go(parentFolder(folder));
  const part = (label, path, current) => {
    const button = el('button', { type: 'button', className: `crumb-part${current ? ' current' : ''}`, textContent: label, title: path ? `Open ${path}` : 'Open the project folder' });
    if (current) button.setAttribute('aria-current', 'page');
    button.onclick = () => go(path);
    return button;
  };
  const entries = [{ label: S.project?.name || 'Project', path: '' }, ...parts.map((name, index) => ({ label: name, path: parts.slice(0, index + 1).join('/') }))];
  const separator = () => el('span', { className: 'crumb-sep', textContent: '/' });
  // hidden = how many of the first parts are folded into one "…" button; upOnly = the last resort, only the Up button
  const render = (hidden, upOnly) => {
    const nodes = [up];
    if (!upOnly) {
      if (hidden > 0) {
        const folded = entries.slice(0, hidden);
        const more = el('button', { type: 'button', className: 'crumb-part crumb-more', textContent: '…', title: folded.map((entry) => entry.label).join(' / '), 'aria-label': 'Show the folders above' });
        more.onclick = () => { const box = more.getBoundingClientRect(); showMenu(box.left, box.bottom, folded.map((entry) => ({ label: entry.label, fn: () => go(entry.path) }))); };
        nodes.push(more, separator());
      }
      entries.slice(hidden).forEach((entry, index) => { if (index) nodes.push(separator()); nodes.push(part(entry.label, entry.path, hidden + index === entries.length - 1)); });
    }
    crumb.replaceChildren(...nodes);
  };
  // it scales: the whole path first, then the early parts fold into "…", and as a last resort only the Up button stays
  const fit = () => {
    for (let hidden = 0; hidden < entries.length; hidden++) { render(hidden, false); if (crumb.scrollWidth <= crumb.clientWidth + 1) return; }
    if (entries.length > 1) render(0, true);
  };
  crumb._fit = fit;
  if (!crumb._observer && typeof ResizeObserver === 'function') {
    let frame = 0;
    crumb._observer = new ResizeObserver(() => { cancelAnimationFrame(frame); frame = requestAnimationFrame(() => crumb._fit?.()); });
    crumb._observer.observe(crumb);
  }
  fit();
}

async function refreshProjectTree() {
  if (!treeHost) return;
  if (!treeHost.dataset.toggles) { treeHost.dataset.toggles = '1'; treeHost.addEventListener('ample:tree-toggle', (event) => { if (event.detail.expanded) treeOpen.add(event.detail.path); else treeOpen.delete(event.detail.path); }); }
  const remember = (nodes) => nodes?.forEach((item) => { if (item.expanded) treeOpen.add(item.path); remember(item.children); });
  remember(treeRoot?.children);
  const root = await api.get(`/api/project/tree?depth=1${hiddenQuery()}`);
  const fresh = { name: S.project?.name || 'Project', path: '', isDir: true, expanded: true, children: root.children }, opened = new Set();
  // the open folders are read again after every answer: one that was opened or folded while this was loading counts too
  for (let round = 0; round < 4; round++) {
    const todo = [...treeOpen].filter((path) => path && !opened.has(path)).sort((left, right) => left.split('/').length - right.split('/').length);
    if (!todo.length) break;
    for (const path of todo) {
      opened.add(path);
      const parts = path.split('/'); let nodes = fresh.children, item = null;
      for (const part of parts) { item = nodes?.find((candidate) => candidate.name === part); if (!item) break; nodes = item.children; }
      if (item) { item.children = (await api.get(`/api/project/tree?path=${encodeURIComponent(item.path)}&depth=1${hiddenQuery()}`)).children; item.expanded = true; }
    }
  }
  treeRoot = fresh;
  const fold = (nodes) => nodes?.forEach((item) => { if (item.expanded && !treeOpen.has(item.path)) item.expanded = false; fold(item.children); });
  fold(treeRoot.children);
  renderProjectTree(treeHost, [treeRoot], selectTreeFolder, loadTreeChildren, folder, contextMenu);
}

async function loadTreeChildren(item) {
  return (await api.get(`/api/project/tree?path=${encodeURIComponent(item.path)}&depth=1${hiddenQuery()}`)).children;
}

async function selectTreeFolder(item) {
  folder = item.path; saveState(); await refreshProjectPanel();
}

export async function revealInProject(path) {
  folder = parentFolder(path); selected = path; selectedPaths = new Set([path]); saveState();
  const button = document.querySelector('#btabs button[data-t="project"]');
  if (button) button.click();
  refreshProjectPanel().then(async () => {
    const parts = folder.split('/').filter(Boolean); let nodes = treeRoot?.children, current = null;
    for (const part of parts) { current = nodes?.find((item) => item.name === part); if (!current) break; current.children = await loadTreeChildren(current); current.expanded = true; nodes = current.children; }
    await refreshProjectTree();
    const row = [...document.querySelectorAll('#project-panel-content .project-tile,#project-panel-content tr')].find((node) => node.dataset.path === path);
    row?.scrollIntoView({ block: 'nearest' });
  }).catch((error) => toast(error.message));
}

export function initProjectPanel() {
  if (ready) return;
  ready = true;
  try { const saved = JSON.parse(localStorage.getItem(stateKey()) || '{}'); folder = typeof saved.folder === 'string' ? saved.folder : ''; showHidden = saved.showHidden === true; treeWidth = Number.isInteger(saved.treeWidth) ? Math.max(150, Math.min(520, saved.treeWidth)) : 260; view = saved.view || 'tiles'; size = Number.isInteger(saved.size) ? Math.max(0, Math.min(4, saved.size)) : 2; sortCol = saved.sortCol || 'name'; sortDir = saved.sortDir || 1; } catch { /* first visit or blocked storage */ }
  const host = $('#b-project');
  if (!host) return;
  const search = el('input', { type: 'search', placeholder: 'Search this folder', 'aria-label': 'Search project files' });
  const uploadProgress = el('div', { className: 'project-upload-progress', 'aria-live': 'polite' });
  const tree = el('div', { className: 'project-panel-tree' }); treeHost = tree;
  const content = el('div', { id: 'project-panel-content' });
  // right-click on an empty spot: Add in the folder that is open
  content.oncontextmenu = async (event) => {
    if (event.target.closest?.('tr,.project-tile')) return;
    event.preventDefault();
    const add = await addMenuItems(folder);
    showMenu(event.clientX, event.clientY, [add, '-', { label: 'Upload files...', fn: () => uploadFiles.click() }, { label: 'Bundle library...', fn: () => import('./bundles.js').then((m) => m.openBundleLibrary()) }]);
  };
  const filterRow = el('div', { className: 'project-panel-filters' });
  for (const [key, label] of [['all', 'All'], ['song', 'Songs'], ['audio', 'Audio'], ['midi', 'MIDI'], ['lyrics', 'Lyrics'], ['module', 'Modules'], ['packs', 'Packs']]) {
    const chip = el('button', { type: 'button', className: `chip${filter === key ? ' on' : ''}`, title: label, 'aria-label': label });
    chip.append(key === 'all' ? glyph(ALL_ICON) : projectItemIcon({ kind: key === 'packs' ? 'pack' : key, path: key }), el('span', { className: 'lbl', textContent: label }));
    chip.onclick = () => { filter = key; filterRow.querySelectorAll('.chip:not(.hidden-toggle)').forEach((node) => node.classList.toggle('on', node === chip)); renderContents(content); };
    filterRow.append(chip);
  }
  const hiddenChip = el('button', { type: 'button', className: `chip hidden-toggle${showHidden ? ' on' : ''}`, 'aria-label': 'Hidden', title: 'Show names that start with a dot, such as .git', 'aria-pressed': String(showHidden) });
  hiddenChip.onclick = async () => { showHidden = !showHidden; hiddenChip.classList.toggle('on', showHidden); hiddenChip.setAttribute('aria-pressed', String(showHidden)); saveState(); await refreshProjectPanel(); };
  hiddenChip.append(glyph(HIDDEN_ICON), el('span', { className: 'lbl', textContent: 'Hidden' }));
  filterRow.append(hiddenChip);
  const details = el('button', { type: 'button', textContent: view === 'details' ? 'Tile view' : 'Details', 'aria-label': 'Toggle detailed file list' });
  details.onclick = () => { view = view === 'tiles' ? 'details' : 'tiles'; zoom.value = view === 'details' ? '-1' : String(size); details.textContent = view === 'details' ? 'Tile view' : 'Details'; saveState(); saveSharedLayout(); renderContents(content); };
  const zoom = el('input', { type: 'range', min: '-1', max: '4', value: view === 'details' ? '-1' : String(size), 'aria-label': 'Project view size; far left is the detailed list' });
  zoom.onpointerdown = (event) => { zoomPointerX = event.clientX; };
  zoom.onpointermove = (event) => { if (event.buttons) zoomPointerX = event.clientX; };
  zoom.onpointerup = () => { zoomPointerX = null; };
  zoom.onpointercancel = () => { zoomPointerX = null; };
  zoom.onkeydown = () => { zoomPointerX = null; };
  zoom.oninput = () => { const next = projectViewFromZoom(Number(zoom.value), view, zoomPointerX, zoom.getBoundingClientRect().left, 26); view = next.view; size = next.size; zoom.value = view === 'details' ? '-1' : String(size); saveState(); saveSharedLayout(); details.textContent = view === 'details' ? 'Tile view' : 'Details'; renderContents(content); };
  search.oninput = () => { query = search.value; items = baseItems; renderContents(content); };
  const uploadFiles = el('input', { type: 'file', multiple: true, hidden: true, 'aria-label': 'Upload project files' });
  const uploadSelectedFiles = async (fileList, into = folder) => {
    uploadProgress.replaceChildren();
    const rows = new Map();
    const showProgress = (file, loaded, total, state) => {
      let row = rows.get(file);
      if (!row) { const bar = el('progress', { max: file.size || 1, value: 0, 'aria-label': `Upload ${file.name}` }); const label = el('span', { textContent: file.name }); const value = el('span'); row = el('div', { className: 'project-upload-row' }, [label, bar, value]); rows.set(file, row); uploadProgress.append(row); }
      const bar = row.querySelector('progress'); bar.max = Math.max(1, total); bar.value = loaded;
      row.lastElementChild.textContent = state === 'complete' ? 'Complete' : state === 'skipped' ? 'Skipped' : `${total ? Math.floor(loaded / total * 100) : 0}%`;
    };
    try { await uploadProjectFiles(fileList, into, showProgress); await refreshProjectPanel(); }
    catch (error) { toast(error.message); }
  };
  uploadFiles.onchange = async () => { await uploadSelectedFiles(uploadFiles.files || []); uploadFiles.value = ''; };
  const actions = el('button', { type: 'button', textContent: '+', title: 'Upload files or create a folder', 'aria-label': 'Project actions', onclick: (event) => addMenuItems(folder).then((add) => showMenu(event.clientX, event.clientY, [add, '-', { label: 'Upload files...', fn: () => uploadFiles.click() }, { label: 'New folder...', fn: async () => { const input = el('input', { value: '', placeholder: 'folder-name', 'aria-label': 'New folder name' }); if (await ask('New project folder', `Create a folder in ${folder || 'the project root'}.`, [{ label: 'Create', value: true, primary: true }, { label: 'Cancel', value: false }], [input])) { await api.createProjectFolder(folder ? `${folder}/${input.value.trim()}` : input.value.trim()); await refreshProjectPanel(); } } }])) });
  const crumb = el('span', { className: 'project-breadcrumb', title: 'Drop an item here to move it out of this folder' });
  fillBreadcrumb(crumb);
  const toolbar = el('div', { className: 'project-panel-toolbar' }, [crumb, search, filterRow, details, zoom, actions, uploadFiles, uploadProgress]);
  const body = el('div', { className: 'project-panel-body' }, [tree]);
  body.style.setProperty('--project-tree-width', `${treeWidth}px`);
  const divider = el('div', { className: 'project-tree-grip', role: 'separator', tabIndex: 0, 'aria-label': 'Resize folder tree', 'aria-orientation': 'vertical', 'aria-valuemin': '150', 'aria-valuemax': '520', 'aria-valuenow': String(treeWidth) });
  divider.onpointerdown = (event) => { event.preventDefault(); const start = event.clientX, initial = tree.getBoundingClientRect().width; divider.setPointerCapture(event.pointerId); divider.onpointermove = (move) => { treeWidth = Math.max(150, Math.min(520, initial + move.clientX - start)); body.style.setProperty('--project-tree-width', `${treeWidth}px`); divider.setAttribute('aria-valuenow', String(treeWidth)); }; divider.onpointerup = () => { divider.onpointermove = null; divider.onpointerup = null; saveState(); }; };
  divider.onkeydown = (event) => { if (!['ArrowLeft', 'ArrowRight'].includes(event.key)) return; event.preventDefault(); treeWidth = Math.max(150, Math.min(520, treeWidth + (event.key === 'ArrowRight' ? 16 : -16))); body.style.setProperty('--project-tree-width', `${treeWidth}px`); divider.setAttribute('aria-valuenow', String(treeWidth)); saveState(); };
  body.append(divider, content);
  host.innerHTML = ''; host.append(toolbar, body);
  api.get('/api/project/settings').then((settings) => { const saved = settings.projectPanel; if (!saved || sharedLayoutEdited) return; view = saved.view; size = saved.tileSize; sortCol = saved.sortColumn; sortDir = saved.sortDescending ? -1 : 1; columns = saved.lengthColumn || saved.columns.some((column) => column.id === 'length') ? saved.columns : saved.columns.flatMap((column) => (column.id === 'size' ? [column, { id: 'length', width: 90 }] : [column])); details.textContent = view === 'details' ? 'Tile view' : 'Details'; zoom.value = view === 'details' ? '-1' : String(size); saveState(); renderContents(content); }).catch(() => {});
  const folderUnder = (event) => { const node = event.target.closest?.('[data-dir][data-path], .project-breadcrumb'); if (!node) return undefined; return node.classList.contains('project-breadcrumb') ? parentFolder(folder) : node.dataset.path; };
  const scroller = $('#project-panel-content');
  if (scroller) {
    attachMarquee(scroller, {
      targets: () => [...scroller.querySelectorAll('.project-tile[data-path], tr[data-path]')],
      current: () => selectedPaths,
      set: (paths, last) => { selectedPaths = paths; if (last) { selected = last; anchor = last; } applySelectionClasses(scroller); },
      // the details list draws only a window of a long list: the rows the rectangle crosses come from their place
      rows: () => {
        const body = view === 'details' ? scroller.querySelector('table.project-details tbody') : null;
        if (!body || !lastVisible.length) return null;
        const area = scroller.getBoundingClientRect();
        const drawn = body.querySelector('tr[data-path]');
        return { paths: lastVisible.map((entry) => entry.path), rowHeight: drawn ? drawn.getBoundingClientRect().height || DETAIL_ROW : DETAIL_ROW, top: body.getBoundingClientRect().top - area.top + scroller.scrollTop };
      },
    });
  }
  host.addEventListener('keydown', (event) => {
    if (event.target.closest?.('input, textarea, select')) return;
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'a') { event.preventDefault(); selectedPaths = new Set(items.filter(matches).map((entry) => entry.path)); renderContents(host); return; }
    if (event.altKey && event.key === 'ArrowUp' && folder) { event.preventDefault(); folder = parentFolder(folder); saveState(); refreshProjectPanel().catch((error) => toast(error.message)); return; }
    if (event.key === 'Escape' && selectedPaths.size) { selectedPaths = new Set(); anchor = ''; renderContents(host); return; }
    if (event.key === 'Delete' && selectedPaths.size > 1) { event.preventDefault(); deleteGroup(selectedItems()); }
  });
  if (treeHost) {
    const treeTarget = (event) => event.target.closest?.('[data-dir][data-path]');
    treeHost.ondragover = (event) => {
      const row = treeTarget(event), drag = draggedFile();
      if (row && drag && row.dataset.path !== drag.path && parentFolder(drag.path) !== row.dataset.path) { event.preventDefault(); event.dataTransfer.dropEffect = 'move'; }
    };
    treeHost.ondrop = async (event) => {
      const row = treeTarget(event), drag = draggedFile();
      if (!row || !drag) return;
      event.preventDefault();
      if (dragGroup) { const group = dragGroup; dragGroup = null; await dropGroupOnFolder(group, row.dataset.path); return; }
      await dropItemOnFolder(drag.path, row.dataset.path);
    };
  }
  host.ondragover = (event) => {
    if (dragHasFiles(event)) { event.preventDefault(); event.dataTransfer.dropEffect = 'copy'; return; }
    const drag = draggedFile(), target = folderUnder(event);
    if (drag && target !== undefined && target !== drag.path && parentFolder(drag.path) !== target) { event.preventDefault(); event.dataTransfer.dropEffect = 'move'; }
  };
  host.ondrop = async (event) => {
    const drag = draggedFile(), target = folderUnder(event);
    if (!event.dataTransfer?.files.length && dragGroup && target !== undefined) { event.preventDefault(); const group = dragGroup; dragGroup = null; await dropGroupOnFolder(group, target); return; }
    if (!event.dataTransfer?.files.length && drag && target !== undefined) { event.preventDefault(); await dropItemOnFolder(drag.path, target); return; }
    if (!event.dataTransfer?.files.length) return;
    event.preventDefault();
    // onto a folder: into that folder; anywhere else: into the folder that is open
    const onFolder = event.target.closest?.('[data-dir][data-path]');
    await uploadSelectedFiles(event.dataTransfer.files, onFolder ? onFolder.dataset.path : folder);
  };
  refreshProjectTree().then(refreshProjectPanel).catch((error) => toast(error.message));
}
