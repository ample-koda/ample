// The Library tab of the left panel: every component of every .amplec file of the project, found by reading the files (not only what the open song imports),
// with a quick search (name, #tag, description, file), tags as filters and one-click adding: the import line, optionally an instance, shown first. @ample:component-library
import { api } from './api.js';
import { S, applyText } from './store.js';
import { ask, el, toast } from './ui.js';

const $ = (selector) => document.querySelector(selector);
let library = null;   // the last answer of /api/components/library
let rows = [];        // { entry, row } of what is drawn, in order (the keyboard walks it)
let current = -1;
let loading = null;
let loadedFor = '';

/** Reads the library (the server reads only the files that changed since the last time) and draws it. */
export async function renderComponentLibrary({ refresh = false } = {}) {
  const pane = $('#pane-clib');
  if (!pane) return;
  if (!pane.dataset.built) build(pane);
  const key = `${S.path}|${S.tplRev || 0}`;
  if (!refresh && library && loadedFor === key) { paint(); return; }
  loading = (async () => {
    try { library = await api.get(`/api/components/library?path=${encodeURIComponent(S.path || '')}${refresh ? '&refresh=true' : ''}`); loadedFor = key; }
    catch (error) { toast(error.message || String(error)); }
  })();
  await loading;
  paint();
}

const KIND_ICON = { Notes: '♪', Audio: '∿', Mixed: '◐' };

function build(pane) {
  pane.dataset.built = '1';
  const search = el('input', { id: 'clib-q', type: 'search', placeholder: 'Search (/)  name, #tag, description', title: 'Searches name, full name, description, #tags and file. / puts the cursor here, the arrows choose, Enter adds, Esc clears.', 'aria-label': 'Search components' });
  const kind = el('select', { id: 'clib-kind', title: 'Kind', 'aria-label': 'Kind' }, [el('option', { value: '', textContent: 'all' }), el('option', { value: 'Notes', textContent: '♪ notes' }), el('option', { value: 'Audio', textContent: '∿ audio' }), el('option', { value: 'Mixed', textContent: '◐ notes to sound' })]);
  const scan = el('button', { id: 'clib-scan', type: 'button', textContent: '⟳', title: 'Read the files again', 'aria-label': 'Scan again' });
  scan.onclick = () => renderComponentLibrary({ refresh: true });
  search.oninput = paint;
  kind.onchange = paint;
  search.onkeydown = (event) => {
    if (event.key === 'ArrowDown') { event.preventDefault(); choose(current + 1); }
    else if (event.key === 'ArrowUp') { event.preventDefault(); choose(current - 1); }
    else if (event.key === 'Enter' && rows[current]) { event.preventDefault(); add(rows[current].entry, true); }
    else if (event.key === 'Escape') { search.value = ''; paint(); }
  };
  pane.append(el('div', { className: 'pad clib-bar' }, [search, kind, scan]), el('div', { id: 'clib-info', className: 'clib-info' }), el('div', { id: 'clib-list' }));
  // / puts the cursor in the search, like in many tools
  document.addEventListener('keydown', (event) => {
    if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey || pane.style.display === 'none') return;
    const tag = document.activeElement?.tagName;
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || document.activeElement?.isContentEditable) return;
    event.preventDefault();
    search.focus();
  });
}

const words = () => ($('#clib-q')?.value || '').toLowerCase().split(/\s+/).filter(Boolean);

function matches(entry, file, query, kind) {
  if (kind && entry.kind !== kind) return false;
  const hay = [entry.title, entry.name, entry.description, file.file, entry.kind, ...(entry.tags || []).map((t) => `#${t}`)].join(' ').toLowerCase();
  return query.every((word) => hay.includes(word));
}

function paint() {
  const list = $('#clib-list');
  if (!list) return;
  list.replaceChildren();
  rows = [];
  const query = words(), kind = $('#clib-kind')?.value || '';
  let shown = 0, total = 0;
  for (const file of library?.files || []) {
    total += file.components.length;
    const hits = file.components.filter((entry) => matches(entry, file, query, kind));
    if (!hits.length) continue;
    const group = el('details', { open: true }, [el('summary', { textContent: `${file.name} (${hits.length})`, title: file.file })]);
    for (const entry of hits) { const row = entryRow(entry, file, query); rows.push({ entry: { ...entry, file: file.file }, row }); group.append(row); shown++; }
    list.append(group);
  }
  $('#clib-q').title = `${shown} of ${total} components. / puts the cursor here, the arrows choose, Enter adds, Esc clears.`;
  $('#clib-scan').title = library ? `Read the files again (last read ${new Date(library.at).toLocaleTimeString()}: ${library.scanned} read, ${library.cached} from the cache)` : 'Read the files again';
  $('#clib-info').textContent = shown ? '' : (total ? 'Nothing found.' : 'No component files in this project yet. A component is a .amplec file.');
  choose(rows.length ? Math.min(Math.max(current, 0), rows.length - 1) : -1);
}

function highlight(text, query) {
  const needles = query.filter((word) => !word.startsWith('#'));
  const span = el('span', { className: 'tpl-title' });
  if (!needles.length) { span.textContent = text; return span; }
  const pattern = new RegExp(`(${needles.map((word) => word.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')})`, 'gi');
  text.split(pattern).forEach((part, i) => span.append(i % 2 ? el('mark', { textContent: part }) : document.createTextNode(part)));
  return span;
}

function entryRow(entry, file, query) {
  const full = [`${entry.title}`, `${entry.name} · v${entry.version} · ${entry.seal}${entry.versions.length > 1 ? ` · ${entry.versions.length} versions` : ''}`, entry.processors?.length ? `Processors: ${entry.processors.join(', ')}` : '', entry.description, entry.tags?.length ? entry.tags.map((t) => `#${t}`).join(' ') : ''].filter(Boolean).join('\n');
  const builtIn = !!file.builtIn;   // ships with AMPLE: always known by name, nothing to import and no file in the project to show
  const actions = el('span', { className: 'clib-acts' }, [
    el('button', { type: 'button', className: 'primary', textContent: '＋', title: builtIn ? 'Add a block (built in: no import line is needed)' : 'Import and add a block (the lines that will be written are shown first)', 'aria-label': `Import and add ${entry.title}`, onclick: (e) => { e.stopPropagation(); add({ ...entry, file: file.file }, true); } }),
    ...(builtIn ? [] : [
      el('button', { type: 'button', textContent: '⤓', title: 'Only import (writes just the import line)', 'aria-label': `Import ${entry.title}`, onclick: (e) => { e.stopPropagation(); add({ ...entry, file: file.file }, false); } }),
      el('button', { type: 'button', textContent: '</>', title: 'Show the file', 'aria-label': `Show ${file.file}`, onclick: (e) => { e.stopPropagation(); document.dispatchEvent(new CustomEvent('ample:open-file', { detail: file.file })); } }),
    ]),
  ]);
  const head = el('div', { className: 'tplitem', title: full, tabindex: '0', draggable: true }, [
    el('span', { className: `clib-ic ${entry.kind.toLowerCase()}`, textContent: KIND_ICON[entry.kind] || '◐', title: entry.kind }),
    highlight(entry.title, query),
    ...(entry.versions.length > 1 ? [el('span', { className: 'clib-vers', textContent: String(entry.versions.length), title: `${entry.versions.length} versions` })] : []),
    entry.imported ? el('span', { className: 'clib-st', textContent: '✓', title: 'The song imports it' }) : entry.nameTaken ? el('span', { className: 'clib-taken', textContent: '⚠', title: `The song already has another component with the name ${entry.name}; importing it needs an alias (${entry.suggestedAlias})` }) : document.createComment(''),
    ...(entry.imported ? [] : [actions]),
  ]);
  if (entry.imported) head.append(actions);
  const body = el('div', { className: 'clib-entry' }, [head]);
  if (entry.description) body.append(el('div', { className: 'clib-desc', textContent: entry.description, title: entry.description }));
  if (entry.tags?.length) {
    const chips = el('div', { className: 'clib-tags' });
    for (const tag of entry.tags) chips.append(el('span', { className: `clib-chip${query.includes(`#${tag}`) ? ' on' : ''}`, textContent: `#${tag}`, title: `Filter on #${tag}`, onclick: (e) => { e.stopPropagation(); toggleTag(tag); } }));
    body.append(chips);
  }
  // dragged onto the board it becomes a block there (a component the song does not import yet is imported first)
  head.ondragstart = (event) => {
    event.dataTransfer.setData('application/x-ample-component', JSON.stringify({ file: file.file, name: entry.name, title: entry.title }));
    event.dataTransfer.setData('text/plain', entry.name);
    event.dataTransfer.effectAllowed = 'copy';
  };
  head.onclick = () => choose(rows.findIndex((r) => r.row === body));
  head.ondblclick = () => add({ ...entry, file: file.file }, true);
  return body;
}

function toggleTag(tag) {
  const search = $('#clib-q');
  const parts = search.value.split(/\s+/).filter(Boolean);
  const at = parts.map((p) => p.toLowerCase()).indexOf(`#${tag}`);
  if (at < 0) parts.push(`#${tag}`); else parts.splice(at, 1);
  search.value = parts.join(' ') + (parts.length ? ' ' : '');
  paint();
}

function choose(index) {
  if (!rows.length) { current = -1; return; }
  current = (index + rows.length) % rows.length;
  rows.forEach((r, i) => r.row.classList.toggle('cur', i === current));
  rows[current].row.scrollIntoView({ block: 'nearest' });
}

/** Shows what adding does (the lines that are written, a conflict when the name is taken by other code) and writes it only after a yes. */
async function add(entry, withBlock, alias = '', place = null) {
  if (!S.path) { toast('Open a song first.'); return; }
  try {
    const result = await api.post('/api/components/import', { path: S.path, file: entry.file, name: entry.name, add: withBlock, ...(alias ? { alias } : {}) });
    if (!result.ok) {
      // the name is taken by other code: an alias that was tried and works is offered (the first segment of the name becomes the alias)
      if (result.suggestedAlias) {
        const yes = await ask(`Cannot add ${entry.title} as it is`, `${result.message}

Import it as "${result.suggestedAlias}" instead? The instance is then written as ${result.suggestedAlias}${entry.name.includes('.') ? entry.name.slice(entry.name.indexOf('.')) : ''}.`, [{ label: 'Close', value: false }, { label: `Import as ${result.suggestedAlias}`, value: true, primary: true }]);
        if (yes) await add(entry, withBlock, result.suggestedAlias, place);
        return;
      }
      await ask(`Cannot add ${entry.title}`, result.message, [{ label: 'Close', value: true, primary: true }]);
      return;
    }
    if (!result.added.length) { toast(result.message); if (place) await placeBlock(entry, alias, place); return; }
    const lines = el('pre', { className: 'clib-diff', textContent: result.added.map((line) => `+ ${line}`).join('\n') });
    const go = await ask(`Add ${entry.title} to ${S.path}`, 'The song changes only with these lines.', [{ label: 'Cancel', value: false }, { label: withBlock ? 'Write import and block' : 'Write import', value: true, primary: true }], [lines]);
    if (!go) return;
    await applyText(result.text);
    toast(result.message);
    renderComponentLibrary({ refresh: false });
    if (place) await placeBlock(entry, alias, place);
  } catch (error) {
    toast(error.message || String(error));
  }
}

/** The type a component is written with in this song: its name, or with an alias the alias in place of the first segment. */
const typeOf = (entry, alias) => (alias ? alias + (entry.name.includes('.') ? entry.name.slice(entry.name.indexOf('.')) : `.${entry.name}`) : entry.name);

/** After the import is written: the block at the place where the entry was dropped (the components are read again first, so the type is known). */
async function placeBlock(entry, alias, place) {
  await (await import('./components.js')).loadComponents(S.path);
  const { addBlock } = await import('./canvas.js');
  await addBlock(typeOf(entry, alias), place.x, place.y);
}

/** An entry of the library was dropped on the board: a component the song imports becomes a block at once; one it does not import is imported first (the lines are shown, a taken name offers an alias), then the block stands where it was dropped. */
export async function dropComponent(entry, x, y) {
  if (!S.path) return;
  if (S.tpl[entry.name]) { const { addBlock } = await import('./canvas.js'); await addBlock(entry.name, x, y); return; }
  await add(entry, false, '', { x, y });
}

/** The tab is open and something changed (a component file was written, an import was added or removed): the list is read again (the server reads only what changed). */
export function refreshComponentLibraryIfOpen() {
  const pane = $('#pane-clib');
  if (pane && pane.style.display !== 'none') renderComponentLibrary().catch(() => {});
}
