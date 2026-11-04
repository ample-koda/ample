// The plugin settings editor: a form generated from the settings schema of the manifest, stored in the project (secrets only
// by reference), with undo, redo, reset to defaults, and two ways back. @ample:plugins-ui

import { api } from './api.js';
import { $, ask, el, toast } from './ui.js';
import { S, edit } from './store.js';
import { registerView, setView } from './views.js';
import { checkValue, defaultsOf } from './plugin-model.js';

const P = { id: null, schema: {}, values: {}, snap: '', undo: [], redo: [], list: [], scope: 'song', projectValues: {}, models: null };

/** The settings that name a model file: the editor offers the files the servers have and checks the one chosen. */
const MODEL_FIELDS = { synthModelFile: 'dit', vocalExtractionModel: 'dit', lmModelFile: 'lm', cliLmModel: 'lm' };
/** cliLmModel belongs to the CLI backend, so the server lists never say anything about it. */
const CHECKED_FIELDS = Object.keys(MODEL_FIELDS).filter((k) => k !== 'cliLmModel');

/** What the song says (text) turned into the typed value the form shows, by the schema. */
const typed = (rule, text) => (rule.type === 'boolean' ? text === 'true' || text === 'on' : rule.type === 'integer' || rule.type === 'number' ? Number(text) : rule.type === 'array' ? JSON.parse(text) : text);
const textOf = (value) => (Array.isArray(value) ? JSON.stringify(value) : String(value));

export async function openPluginSettings(id) {
  P.list = (await api.get('/api/plugins')).plugins || [];
  await select(id || (P.list[0] && P.list[0].id));
  setView('plug');
  loadModels();
}

async function select(id) {
  if (!id) return;
  const r = await api.get(`/api/plugins/settings?id=${encodeURIComponent(id)}`);
  P.id = id; P.schema = r.schema; P.projectValues = { ...r.values };
  P.values = { ...defaultsOf(r.schema) };
  if (P.scope === 'song') {
    // What this song says on its settings line; everything else is the plugin's own default.
    for (const [k, text] of Object.entries((S.doc && S.doc.settings) || {})) {
      const [plugin, name] = k.split('.');
      const rule = (r.schema.properties || {})[name];
      if (plugin === id && rule) { try { P.values[name] = typed(rule, text); } catch { /* an unreadable value stays the default */ } }
    }
  } else P.values = { ...r.values };
  P.snap = JSON.stringify(P.values); P.undo = []; P.redo = [];
  P.models = null;
}

/** Loads the model lists after the form is shown (the servers may be slow), then redraws what depends on them. */
async function loadModels() {
  if (P.id !== 'acestep') return;
  const id = P.id;
  let models = null;
  try { models = await api.get('/api/ace/models'); } catch { /* the form still works without the file lists */ }
  if (P.id !== id || !models) return;
  P.models = models;
  if (!$('#pg-errors')) return;
  if (dirty()) { addLists(); live(); } else render();
}

/** Puts the file name lists on the model fields once the lists have arrived (without losing what is being typed). */
function addLists() {
  Object.keys(MODEL_FIELDS).forEach((k) => {
    const input = document.querySelector(`#pg input[data-field="${k}"]`);
    if (!input || $(`#pg-list-${k}`)) return;
    const list = modelList(k);
    const row = input.closest('.prop');
    input.setAttribute('list', list.id);
    input.setAttribute('aria-describedby', `${input.getAttribute('aria-describedby') || ''} pg-verdict-${k}`.trim());
    row.append(list);
    const after = $(`#pg-desc-${k}`) || row;
    after.after(el('div', { className: 'note', id: `pg-verdict-${k}`, 'aria-live': 'polite', style: 'margin:-2px 0 8px 176px;font-size:11px' }));
  });
}

function modelList(k) {
  const list = el('datalist', { id: `pg-list-${k}` });
  P.models.models.filter((x) => x.role === MODEL_FIELDS[k]).forEach((x) => list.append(el('option', { value: x.file, label: `${[x.local ? 'local' : '', x.faer ? 'Faer (remote GPU)' : ''].filter(Boolean).join(' + ') || 'unknown'} ${bytesText(x.localBytes ?? x.faerBytes)}`.trim() })));
  return list;
}

const push = () => { const s = JSON.stringify(P.values); if (P.undo.length && P.undo[P.undo.length - 1] === s) return; P.undo.push(s); P.redo = []; };
const dirty = () => JSON.stringify(P.values) !== P.snap;
const errorsOf = () => [...Object.entries(P.schema.properties || {}).map(([k, r]) => (k in P.values ? checkValue(k, P.values[k], r) : null)), ...modelErrors()].filter(Boolean);

const bytesText = (n) => (n == null ? '' : `${(n / 1073741824).toFixed(2)} GB`);

/** The quantisation of a model file name (Q5_K_M, F16, ...), or '' when it has none. */
const quantOf = (file) => (String(file).match(/(?:^|[-_.])((?:I?Q\d[A-Z0-9_]*)|BF16|F16|F32)(?=\.gguf$)/i) || [, ''])[1].toUpperCase();

/** What the two servers say about the model file named in the form (null when nothing is chosen, nothing is known or the setting is not about these servers). */
function modelVerdict(key) {
  const file = String(P.values[key] || '').trim();
  const m = P.models;
  if (!file || !m || P.id !== 'acestep' || !CHECKED_FIELDS.includes(key)) return null;
  // The CLI backend loads its own files; the lists of the servers say nothing about them.
  if (String(P.values.backend ?? '') === 'cli') return null;
  const entry = m.models.find((x) => x.file === file);
  const route = key === 'vocalExtractionModel' ? m.routes.voice : m.routes.song;
  // The lists are of the project's stored endpoint; a different endpoint in the form makes the local list a hint only.
  const endpointDiffers = P.values.endpoint != null && String(P.values.endpoint) !== m.local.endpoint;
  const localKnown = m.local.reachable && m.local.known !== false && !endpointDiffers;
  const faerKnown = m.faer.connected && m.faer.known !== false;
  const where = [entry?.local ? 'on the local ACE server' : localKnown ? 'not on the local ACE server' : m.local.reachable ? 'local ACE file list unknown' : 'local ACE server not reachable', entry?.faer ? 'on Faer (remote GPU)' : faerKnown ? 'not on Faer (remote GPU)' : m.faer.connected ? 'Faer file list unknown' : 'Faer (remote GPU) not connected'];
  const same = entry?.same === true ? ' Same size on both.' : entry?.same === false ? ' The size differs between the two: it is not the same file.' : '';
  const localHas = localKnown && !!entry?.local, faerHas = faerKnown && !!entry?.faer;
  // "Faer, else local" can use either place, so the file is only missing when neither place is known to have it and no connected place has an unknown list.
  const unknownList = (m.faer.connected && !faerKnown) || (m.local.reachable && !localKnown);
  const missing = route === 'faer' ? (faerKnown && !entry?.faer)
    : route === 'faer-else-local' ? ((faerKnown || localKnown) && !localHas && !faerHas && !unknownList)
      : (localKnown && !entry?.local);
  const relevantUnknown = route === 'faer' ? m.faer.connected && !faerKnown : route === 'faer-else-local' ? unknownList : m.local.reachable && !localKnown;
  const unknown = !missing && !entry && relevantUnknown;
  const xl = /xl-/i.test(file), quant = quantOf(file);
  const family = (file.match(/(turbo|sft|base)/i) || [''])[0].toLowerCase();
  const alternatives = m.models
    .filter((x) => x.role === (MODEL_FIELDS[key] || 'dit') && (route === 'faer' ? x.faer : route === 'faer-else-local' ? (x.faer || x.local) : x.local) && x.file !== file && family && x.file.toLowerCase().includes(family) && /xl-/i.test(x.file) === xl)
    .sort((x, y) => Number(quantOf(y.file) === quant) - Number(quantOf(x.file) === quant));
  return { file, route, missing, unknown, text: `${where.join(', ')}.${same}${unknown ? ' Not checked: that file list is not known.' : ''}`, alternatives };
}

const modelErrors = () => CHECKED_FIELDS.map((k) => {
  const v = modelVerdict(k);
  return v?.missing ? `${k}: ${v.file} is not on ${v.route === 'faer' ? 'Faer (remote GPU)' : v.route === 'faer-else-local' ? 'Faer (remote GPU) or the local ACE' : 'the local ACE'} server that this project uses for it${v.alternatives.length ? ` (the same kind there: ${v.alternatives.map((x) => x.file).join(', ')})` : ''}` : null;
});

function render() {
  const root = $('#pg');
  root.innerHTML = '';
  const nav = el('div', { className: 'pgnav' });
  nav.append(el('h4', { textContent: 'Plugins', style: 'padding:0' }));
  P.list.forEach((p) => {
    const b = el('button', { textContent: p.title || p.id, className: p.id === P.id ? 'on' : '', onclick: async () => { if (dirty() && !(await ask('Unsaved settings', `The settings of ${P.id} were changed and not applied.`, [{ label: 'Keep editing', value: false }, { label: 'Discard and switch', value: true, primary: true }]))) return; await select(p.id); render(); } });
    b.dataset.plugin = p.id;
    nav.append(b);
  });
  const main = el('div', { style: 'flex:1;display:flex;flex-direction:column;min-width:0' });
  const bar = el('div', { className: 'bmbar' });
  bar.append(
    el('b', { textContent: `Settings: ${P.id}` }),
    el('span', { className: 'row', style: 'margin:0' }, [['song', 'This song'], ['project', 'New songs (project defaults)']].map(([k, l]) => el('button', { textContent: l, className: P.scope === k ? 'on' : '', title: k === 'song' ? 'The settings of this song, written on its settings line: they are part of the song and of its builds' : 'The starting values for new songs. A song that exists keeps its own settings.', onclick: async () => { if (dirty() && !(await ask('Unsaved settings', 'Switching drops the changes you have not applied.', [{ label: 'Keep editing', value: false }, { label: 'Discard and switch', value: true, primary: true }]))) return; P.scope = k; await select(P.id); render(); } }))),
    P.scope === 'song' && missingRequired().length ? el('button', { id: 'pg-fix-required', className: 'primary', textContent: `Fix: add ${missingRequired().join(', ')}`, title: 'This song lacks settings its plugin requires in every song: add them from the project defaults', onclick: fixRequired }) : el('span'),
    P.scope === 'song' ? el('button', { id: 'pg-defaults', textContent: 'Fill from project defaults', title: 'Copy the project defaults into the form (Save to keep them in this song)', onclick: () => { push(); P.values = { ...P.projectValues }; render(); } }) : el('span'),
    el('button', { id: 'pg-undo', textContent: `↶ Undo (${P.undo.length})`, disabled: !P.undo.length, onclick: () => { P.redo.push(JSON.stringify(P.values)); P.values = JSON.parse(P.undo.pop()); render(); } }),
    el('button', { id: 'pg-redo', textContent: `↷ Redo (${P.redo.length})`, disabled: !P.redo.length, onclick: () => { P.undo.push(JSON.stringify(P.values)); P.values = JSON.parse(P.redo.pop()); render(); } }),
    el('button', { id: 'pg-reset', textContent: 'Reset', title: 'Put every setting back to the plugin defaults', onclick: () => { push(); P.values = defaultsOf(P.schema); render(); } }),
    el('span', { className: 'sp' }),
    el('span', { id: 'pg-status', className: 'note', style: 'margin:0', textContent: dirty() ? 'not saved' : '' }),
    el('button', { id: 'pg-undoback', textContent: 'Cancel', title: 'Discard the changes made here and go back', onclick: undoBack }),
    el('button', { id: 'pg-apply', textContent: 'Save', title: 'Save the settings and go back', className: 'primary', onclick: applyBack }),
  );
  const form = el('div', { style: 'padding:12px 16px;overflow:auto' });
  const props = Object.entries(P.schema.properties || {});
  if (!props.length) form.append(el('p', { className: 'note', textContent: 'This plugin has no settings.' }));
  props.forEach(([k, rule]) => {
    const row = el('div', { className: 'prop', style: 'grid-template-columns:170px 1fr;max-width:640px' });
    row.dataset.setting = k;
    const v = k in P.values ? P.values[k] : rule.default;
    let input;
    if (rule.type === 'boolean') {
      input = el('input', { type: 'checkbox', checked: !!v, style: 'width:auto' });
      input.onchange = () => { push(); P.values[k] = input.checked; live(); };
    } else if (rule.enum) {
      input = el('select');
      rule.enum.forEach((o) => input.append(el('option', { value: o, textContent: o, selected: o === v })));
      input.onchange = () => { push(); P.values[k] = input.value; live(); };
    } else if (rule.type === 'array') {
      input = listEditor(k, Array.isArray(v) ? v : []);
    } else if (rule.type === 'integer' || rule.type === 'number') {
      input = el('input', { type: 'number', value: v ?? '', min: rule.minimum, max: rule.maximum, step: rule.type === 'integer' ? 1 : 'any' });
      input.onfocus = push;
      input.oninput = () => { if (input.value === '') delete P.values[k]; else P.values[k] = Number(input.value); live(); };
    } else {
      input = el('input', { value: v ?? '', type: /secret|token|password|key/i.test(k) ? 'password' : 'text' });
      if (MODEL_FIELDS[k] && P.models) {
        const list = modelList(k);
        input.setAttribute('list', list.id);
        row.append(list);
      }
      input.onfocus = push;
      input.oninput = () => { P.values[k] = input.value; live(); };
    }
    input.dataset.field = k;
    input.id = `pg-in-${k}`;
    const described = [rule.description ? `pg-desc-${k}` : '', MODEL_FIELDS[k] && P.models ? `pg-verdict-${k}` : ''].filter(Boolean).join(' ');
    if (described) input.setAttribute('aria-describedby', described);
    row.append(el('label', { textContent: k, title: [rule.description || '', MODEL_FIELDS[k] && P.models ? 'Faer is the remote GPU service; the local ACE server runs on this machine.' : ''].filter(Boolean).join(' '), htmlFor: input.id }), input);
    form.append(row);
    if (rule.description) form.append(el('div', { className: 'note', id: `pg-desc-${k}`, style: 'margin:-2px 0 6px 176px;font-size:11px', textContent: rule.description }));
    if (MODEL_FIELDS[k] && P.models) form.append(el('div', { className: 'note', id: `pg-verdict-${k}`, 'aria-live': 'polite', style: 'margin:-2px 0 8px 176px;font-size:11px' }));
  });
  form.append(gpuPanel());
  form.append(el('div', { id: 'pg-errors', role: 'alert' }), el('p', { className: 'note', textContent: 'Secrets are never stored in the settings: use a reference to the vault (the vault arrives with a later chunk). The settings are saved in the project, so a restore brings them back.' }));
  main.append(bar, form);
  root.style.flexDirection = 'row';
  root.append(nav, main);
  live();
}

/** The GPU memory of the machine (totals only) and the plugin processes kept in memory, with a button that gives that memory back now. */
function gpuPanel() {
  const box = el('div', { className: 'note', id: 'pg-gpu', style: 'margin-top:14px;max-width:640px' });
  const text = el('span', { textContent: 'GPU memory: reading...' });
  const free = el('button', { type: 'button', id: 'pg-gpu-free', textContent: 'Free GPU memory now', title: 'Stops the plugin processes that are kept in memory between builds', style: 'margin-left:10px', disabled: true });
  const show = async () => {
    try {
      const status = await api.get('/api/gpu');
      const device = status.device ? `${status.device.freeMb} MB free of ${status.device.totalMb} MB` : 'no NVIDIA GPU found';
      const kept = status.kept.length ? `kept in memory: ${status.kept.map((k) => `${k.plugin} (idle ${k.idleSeconds} s)`).join(', ')}` : 'nothing kept in memory';
      text.textContent = `GPU memory: ${device}; ${kept}.`;
      free.disabled = !status.kept.length;
    } catch (error) { text.textContent = `GPU memory: not available (${error.message}).`; }
  };
  free.onclick = async () => {
    try { const r = await api.post('/api/gpu/release', {}); toast(r.released ? `Gave back the memory of ${r.released} plugin process(es).` : 'Nothing was kept in memory.'); } catch (error) { toast(error.message); }
    await show();
  };
  box.append(text, free);
  show();
  return box;
}

function listEditor(key, items) {
  const box = el('div', { className: 'dlist' });
  const draw = () => {
    box.innerHTML = '';
    items.forEach((it, i) => {
      const row = el('div', { className: 'drow', draggable: true });
      row.ondragstart = (e) => e.dataTransfer.setData('text/plain', String(i));
      row.ondragover = (e) => { e.preventDefault(); row.classList.add('over'); };
      row.ondragleave = () => row.classList.remove('over');
      row.ondrop = (e) => { e.preventDefault(); const from = Number(e.dataTransfer.getData('text/plain')); push(); const [m] = items.splice(from, 1); items.splice(i, 0, m); P.values[key] = [...items]; draw(); live(); };
      row.append(el('span', { textContent: '⋮⋮' }), el('span', { textContent: String(it), style: 'flex:1' }), el('button', { textContent: '✕', title: 'Remove this item', 'aria-label': 'Remove this item', onclick: () => { push(); items.splice(i, 1); P.values[key] = [...items]; draw(); live(); } }));
      box.append(row);
    });
    const add = el('input', { placeholder: 'add an entry and press Enter' });
    add.onkeydown = (e) => { if (e.key === 'Enter' && add.value.trim()) { push(); items.push(add.value.trim()); P.values[key] = [...items]; draw(); live(); } };
    box.append(add);
  };
  draw();
  return box;
}

function live() {
  const st = $('#pg-status');
  if (st) st.textContent = dirty() ? 'not saved' : '';
  const box = $('#pg-errors');
  if (box) { box.innerHTML = ''; errorsOf().forEach((m) => box.append(el('div', { className: 'seerr error', textContent: `● ${m}` }))); }
  Object.keys(MODEL_FIELDS).forEach((k) => {
    const note = $(`#pg-verdict-${k}`);
    if (!note) return;
    const v = modelVerdict(k);
    note.replaceChildren();
    if (!v) return;
    note.append(document.createTextNode(`${v.file}: ${v.text} `));
    if (v.missing && v.alternatives[0]) note.append(el('button', { type: 'button', textContent: `Use ${v.alternatives[0].file}`, 'aria-label': `Use ${v.alternatives[0].file} instead of ${v.file} for ${k}`, title: `Replace ${v.file} with ${v.alternatives[0].file} in ${k}`, onclick: () => { push(); P.values[k] = v.alternatives[0].file; render(); } }));
  });
}

/** The settings this plugin wants every song to state itself (the schema's x-songRequired) that this song does not have yet. */
const missingRequired = () => ((P.schema && P.schema['x-songRequired']) || []).filter((name) => !(S.doc && S.doc.settings && `${P.id}.${name}` in S.doc.settings));

/** The auto fix: write the missing required settings into the song from the project defaults. */
async function fixRequired() {
  const ops = missingRequired().map((name) => ({ type: 'setSetting', key: `${P.id}.${name}`, value: textOf(P.projectValues[name] ?? defaultsOf(P.schema)[name] ?? '') }));
  if (ops.length && await edit(ops)) { toast(`Added ${ops.length} required setting(s) to this song.`); await select(P.id); render(); }
}

async function applyBack() {
  const errs = errorsOf();
  if (errs.length) { toast(`Fix first: ${errs[0]}.`); return; }
  if (P.scope === 'song') {
    // The song gets a settings line with what differs from the plugin's defaults; a value back at its default is removed again.
    const defaults = defaultsOf(P.schema);
    const ops = [];
    Object.entries(P.schema.properties || {}).forEach(([name, rule]) => {
      const key = `${P.id}.${name}`;
      const value = name in P.values ? P.values[name] : defaults[name];
      const same = textOf(value) === textOf(defaults[name]);
      const had = S.doc && S.doc.settings && key in S.doc.settings;
      if (!same) { if (!had || S.doc.settings[key] !== textOf(value)) ops.push({ type: 'setSetting', key, value: textOf(value) }); } else if (had) ops.push({ type: 'removeSetting', key });
    });
    if (ops.length && !(await edit(ops))) return;
    P.snap = JSON.stringify(P.values);
    toast(`Settings of ${P.id} saved in this song.`);
    setView('blocks', true);
    return;
  }
  try { await api.put('/api/plugins/settings', { id: P.id, values: P.values }); } catch (e) { toast(`${e.code ? `${e.code}: ` : ''}${e.message}`); return; }
  P.snap = JSON.stringify(P.values);
  toast(`Defaults of ${P.id} for new songs saved in the project.`);
  setView('blocks', true);
}

async function undoBack() {
  if (dirty() && !(await ask('Cancel changes', 'Throw away the changes made here?', [{ label: 'Keep editing', value: false }, { label: 'Discard changes', value: true, primary: true }]))) return;
  setView('blocks', true);
}

export function initPluginUi() { registerView('plug', render); }
