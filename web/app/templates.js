// The Templates tab of the left panel: the beats of the built-in catalogue (the server, no AI) and the templates of the project, to search, look at and add; suggestions from what the song lacks;
// and "Save selection as template" for your own. @ample:templates
import { api } from './api.js';
import { S, flat, subscribe } from './store.js';
import { $, ask, el, toast } from './ui.js';
import { openBeatWizard } from './beat-wizard.js';
import { deleteProjectTemplate, dragTemplate, listenTemplate, listProjectTemplates, templateEndpoints, openInsertTemplate, openSaveTemplate, selectedBlockNames } from './project-templates.js';
import { queryFor, suggestions } from '../lib/template-suggest.mjs';
import { HEADER_TAIL } from '../lib/block-header.mjs';

const PAGE = 40;
const state = { source: 'built-in', text: '', genre: '', kind: '', matchTempo: false, offset: 0, items: [], count: 0, projectItems: [], seq: 0, suggestion: '' };

const dots = (n, max = 5) => el('span', { className: 'tpl-dots', title: `${n} of ${max}`, 'aria-label': `${n} of ${max}` }, Array.from({ length: max }, (_, i) => el('i', { className: i < n ? 'on' : '' })));

function beatCard(item) {
  const c = el('div', { className: 'tpl-card', tabIndex: 0, role: 'button', title: 'Double-click to add it to the song', 'data-id': item.id }, [
    el('div', { className: 'tpl-head' }, [el('b', { textContent: item.name }), el('small', { textContent: item.form })]),
    el('div', { className: 'tpl-say', textContent: item.say }),
    el('div', { className: 'tpl-meta' }, [el('span', { textContent: `${item.bpm} bpm` }), dots(item.energy), ...item.genres.map((g) => el('span', { className: 'tpl-chip', textContent: g }))]),
    ...(item.tempoMatch ? [el('div', { className: 'tpl-match', textContent: item.tempoMatch })] : []),
  ]);
  c.ondblclick = () => openBeatWizard(item.id);
  c.onkeydown = (e) => { if (e.key === 'Enter') openBeatWizard(item.id); };
  return c;
}

/** ▶ Listen (builds the template as a draft and plays it); with several blocks to hear, ‹ › picks which one. */
function listenRow(t) {
  const ends = templateEndpoints(t);
  if (!ends.length) return el('span', { hidden: true });
  let at = 0, busy = false;
  const label = el('span', { className: 'tpl-listen-block', textContent: ends[0], hidden: ends.length < 2 });
  const play = el('button', { type: 'button', className: 'tpl-listen', textContent: '▶ Listen', title: 'Hear this template (the settings at their defaults); nothing is added to the song' });
  const step = (d) => el('button', { type: 'button', className: 'tpl-listen-step', textContent: d < 0 ? '‹' : '›', hidden: ends.length < 2, title: d < 0 ? 'The previous block' : 'The next block', 'aria-label': d < 0 ? 'Previous block to hear' : 'Next block to hear', onclick: (e) => { e.stopPropagation(); at = (at + d + ends.length) % ends.length; label.textContent = ends[at]; } });
  play.onclick = async (e) => {
    e.stopPropagation();
    if (busy) return;
    busy = true; play.textContent = 'Building…'; play.disabled = true;
    try { await listenTemplate(t, ends.length > 1 || !t.source.match(/^master\s/m) ? ends[at] : undefined); } catch (error) { toast(error.message || String(error)); }
    busy = false; play.textContent = '▶ Listen'; play.disabled = false;
  };
  const row = el('div', { className: 'tpl-listen-row' }, [play, step(-1), label, step(1)]);
  row.onpointerdown = (e) => e.stopPropagation();
  row.ondblclick = (e) => e.stopPropagation();
  return row;
}

function projectCard(t) {
  const remove = el('button', { type: 'button', className: 'tpl-x', textContent: '×', title: `Delete the template ${t.name}`, 'aria-label': `Delete the template ${t.name}`, onclick: async (e) => {
    e.stopPropagation();
    if ((await ask('Delete the template?', `${t.name} is removed from the templates folder of this project (AMPLE Redux keeps the file).`, [{ label: 'Cancel', value: false }, { label: 'Delete', value: true, primary: true }])) !== true) return;
    try { await deleteProjectTemplate(t.path); toast(`Deleted ${t.name}.`); loadProject(); } catch (error) { toast(error.message || String(error)); }
  } });
  const c = el('div', { className: 'tpl-card', tabIndex: 0, role: 'button', title: 'Drag it into the song, or double-click to add it', 'data-id': t.id }, [
    el('div', { className: 'tpl-head' }, [el('b', { textContent: t.name }), remove]),
    ...(t.say ? [el('div', { className: 'tpl-say', textContent: t.say })] : []),
    el('div', { className: 'tpl-meta' }, [el('span', { textContent: `${(t.source.match(new RegExp(String.raw`^[A-Za-z][\w-]*\s+[A-Za-z][\w-]*` + HEADER_TAIL, 'gm')) || []).length} block(s)` }), ...(t.tags || []).map((g) => el('span', { className: 'tpl-chip', textContent: g }))]),
    listenRow(t),
  ]);
  c.onpointerdown = (e) => { if (e.button === 0 && e.pointerType !== 'touch' && !e.target.closest('button')) dragTemplate(t, { x: e.clientX, y: e.clientY }, c); };
  c.ondblclick = () => openInsertTemplate(t);
  c.onkeydown = (e) => { if (e.key === 'Enter') openInsertTemplate(t); };
  return c;
}

async function load(more) {
  const seq = ++state.seq;
  if (!more) { state.offset = 0; state.items = []; }
  const q = new URLSearchParams({ limit: String(PAGE), offset: String(state.offset) });
  if (state.text) q.set('text', state.text);
  if (state.genre) q.set('genre', state.genre);
  if (state.kind) q.set('kind', state.kind);
  if (state.matchTempo && S.doc?.project?.tempo) q.set('bpm', String(S.doc.project.tempo));
  let res;
  try { res = await api.get(`/api/beats?${q}`); } catch { res = { count: 0, beats: [], error: true }; }
  if (seq !== state.seq) return;
  state.items = [...state.items, ...(res.beats || [])];
  state.count = res.count || 0;
  state.offset = state.items.length;
  paint();
}

async function loadProject() {
  const seq = ++state.seq;
  const all = await listProjectTemplates();
  if (seq !== state.seq) return;
  const words = state.text.toLowerCase().split(/\s+/).filter(Boolean);
  state.projectItems = all.filter((t) => words.every((w) => `${t.name} ${t.say} ${(t.tags || []).join(' ')}`.toLowerCase().includes(w)));
  state.count = state.projectItems.length;
  paint(all.length);
}

function paint(projectTotal = 0) {
  const list = $('#tpl-list');
  if (!list) return;
  const project = state.source === 'project';
  list.replaceChildren(...(project ? state.projectItems.map(projectCard) : state.items.map(beatCard)));
  if (!(project ? state.projectItems : state.items).length) {
    list.append(el('div', { className: 'note', textContent: project
      ? (projectTotal ? 'No template matches the search.' : 'No templates in this project yet. Select some blocks and press "Save selection as template".')
      : 'No template matches. Clear the search or the filters.' }));
  }
  const more = $('#tpl-more');
  more.hidden = project || state.items.length >= state.count;
  more.textContent = `Show more (${state.count - state.items.length})`;
  $('#tpl-count').textContent = project ? `${state.count} template${state.count === 1 ? '' : 's'}` : `${state.count} beat${state.count === 1 ? '' : 's'}`;
  $('#tpl-save').disabled = !selectedBlockNames().length;
  paintSuggestions();
}

function paintSuggestions() {
  const box = $('#tpl-suggest');
  if (!box) return;
  box.replaceChildren();
  if (state.source !== 'built-in') { box.hidden = true; return; }
  let list = [];
  try { list = suggestions(flat().nodes.filter((n) => !n.proxy).map((n) => ({ id: n.id, tpl: n.tpl, params: n.params }))); } catch { list = []; }
  box.hidden = !list.length;
  for (const s of list) {
    const on = state.suggestion === s.id;
    box.append(el('button', { type: 'button', className: `tpl-suggestion${on ? ' on' : ''}`, 'aria-pressed': String(on), title: s.why, onclick: () => {
      state.suggestion = on ? '' : s.id;
      state.kind = on ? '' : queryFor(s, 0).kind;
      state.matchTempo = !on;
      $('#tpl-tempo').checked = state.matchTempo;
      load(false);
    } }, [el('b', { textContent: s.title }), el('small', { textContent: s.why })]));
  }
}

function setSource(source) {
  state.source = source;
  state.text = '';
  $('#tpl-q').value = '';
  for (const b of document.querySelectorAll('#pane-tpl .tpl-source button')) { const on = b.dataset.source === source; b.classList.toggle('on', on); b.setAttribute('aria-pressed', String(on)); }
  $('#tpl-filters').hidden = source === 'project';
  if (source === 'project') loadProject(); else load(false);
}

/** Draws the tab (once) and loads the first page. */
export async function renderTemplates() {
  const pane = $('#pane-tpl');
  if (!pane) return;
  if (!pane.dataset.ready) {
    pane.dataset.ready = '1';
    const search = el('input', { id: 'tpl-q', type: 'search', placeholder: 'Search..', 'aria-label': 'Search templates' });
    let timer = 0;
    search.oninput = () => { clearTimeout(timer); timer = setTimeout(() => { state.text = search.value.trim(); if (state.source === 'project') loadProject(); else load(false); }, 200); };
    const genre = el('select', { id: 'tpl-genre', 'aria-label': 'Genre' }, [el('option', { value: '', textContent: 'All genres' })]);
    genre.onchange = () => { state.genre = genre.value; load(false); };
    const tempo = el('input', { type: 'checkbox', id: 'tpl-tempo' });
    tempo.onchange = () => { state.matchTempo = tempo.checked; load(false); };
    const source = el('div', { className: 'tpl-source', role: 'group', 'aria-label': 'Where the templates come from' }, [
      el('button', { type: 'button', className: 'on', textContent: 'Built-in', 'aria-pressed': 'true', 'data-source': 'built-in', onclick: () => setSource('built-in') }),
      el('button', { type: 'button', textContent: 'Project', 'aria-pressed': 'false', 'data-source': 'project', title: 'The templates saved in this project', onclick: () => setSource('project') }),
    ]);
    const save = el('button', { id: 'tpl-save', type: 'button', textContent: 'Save selection as template…', title: 'Select some blocks on the board first', disabled: true, onclick: () => openSaveTemplate() });
    pane.append(el('div', { className: 'pad tpl-bar' }, [source, search, el('div', { id: 'tpl-filters', className: 'tpl-filters' }, [genre, el('label', { title: 'Only beats that fit the tempo of the song (also half and double time)' }, [tempo, ' fits the tempo'])]), el('div', { id: 'tpl-suggest', className: 'tpl-suggest', hidden: true }), save, el('small', { id: 'tpl-count', className: 'tpl-count' })]),
      el('div', { id: 'tpl-list', className: 'tpl-list' }),
      el('div', { className: 'pad' }, [el('button', { id: 'tpl-more', type: 'button', hidden: true, onclick: () => load(true) })]));
    api.get('/api/beats?overview=1').then((o) => { for (const g of Object.keys(o.genres || {}).slice(0, 24)) genre.append(el('option', { value: g, textContent: g })); }).catch(() => {});
    document.addEventListener('ample:templates-changed', () => { if (state.source === 'project') loadProject(); });
    subscribe?.(() => { if ($('#pane-tpl')?.style.display !== 'none') { $('#tpl-save').disabled = !selectedBlockNames().length; paintSuggestions(); } });
  }
  if (state.source === 'project') await loadProject();
  else if (!state.items.length) await load(false);
  else paintSuggestions();
}
