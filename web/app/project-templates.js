// The templates of a project: JSON files in its templates folder (so they travel with the project and its history). Saving some blocks of the song as a template, choosing which of their values become settings,
// and putting a template into a song at a bar, under free names. @ample:templates
import { api } from './api.js';
import { S, applyText, flat, notify, notifySelection, select } from './store.js';
import { ask, el, toast } from './ui.js';
import { openWindow } from './window.js';
import { play } from './player.js';
import { adjustableValues, appendSource, blockRanges, extractBlocks, fill, hasMaster, makeTemplate, mergeMaster, parametrize, renameBlocks, shiftBars, switchableBlocks, templateProblems } from '../lib/template-model.mjs';
import { HEADER_TAIL } from '../lib/block-header.mjs';

const DIR = 'templates';

/** The templates of the project, by name. A file that is not a valid template is left out. */
export async function listProjectTemplates() {
  let tree;
  try { tree = await api.get(`/api/project/tree?path=${DIR}`); } catch { return []; }
  const out = [];
  for (const file of (tree.children || []).filter((c) => !c.isDir && c.name.endsWith('.json'))) {
    try {
      const template = JSON.parse((await api.get(`/api/file?path=${encodeURIComponent(file.path)}`)).text);
      if (!templateProblems(template).length) out.push({ ...template, path: file.path });
    } catch { /* not a template */ }
  }
  return out.sort((a, b) => a.name.localeCompare(b.name));
}

export const saveProjectTemplate = (template) => api.put('/api/file', { path: `${DIR}/${template.id}.json`, text: `${JSON.stringify(template, null, 2)}\n` });
export const deleteProjectTemplate = (path) => api.delete(`/api/project/file?path=${encodeURIComponent(path)}`);

/** The blocks one can listen to: the master when the template has one, else every block of it (three sample blocks: three to pick from). */
export function templateEndpoints(template) {
  const blocks = blockRanges(template.source);
  const master = blocks.filter((b) => b.type === 'master');
  return (master.length ? master : blocks).map((b) => b.name);
}

/** Builds the template as a draft (its settings at their defaults, nothing added to the song) and plays one block of it (or its master). */
export async function listenTemplate(template, only) {
  const values = Object.fromEntries((template.settings ?? []).map((s) => [s.name, s.kind === 'switch' ? (String(s.default ?? '').trim() !== 'disabled' ? '' : (s.off ?? ' disabled')) : (s.default ?? '')]));
  const text = fill(template.source, template.settings ?? [], values);
  const report = await api.post('/api/build/text', { text, ...(only ? { only } : {}) });
  const take = only ? (report.blocks || []).find((b) => (b.block ?? b.Block) === only)?.takeId : report.masterTakeId;
  if (!take) throw new Error(only ? `${only} did not make any sound.` : 'This template has no master to listen to.');
  await play(take);
}

const changed = () => document.dispatchEvent(new CustomEvent('ample:templates-changed'));

/** The blocks that are selected on the board (real blocks, not the ports of a group). */
export function selectedBlockNames() {
  const real = new Set(flat().nodes.filter((n) => !n.proxy && !n.isGroup).map((n) => n.id));
  return [...(S.selSet ?? [])].filter((id) => real.has(id));
}

/** The window that saves the selected blocks as a template: a name, a note, tags, and which values become settings. */
export function openSaveTemplate() {
  const names = selectedBlockNames();
  if (!names.length) { toast('Select the blocks first: a template is made from the blocks that are selected.'); return null; }
  const { source, names: found, external } = extractBlocks(S.doc.text, names);
  const values = adjustableValues(source);
  const name = el('input', { value: found.length === 1 ? found[0] : `${found[0]} and more`, 'aria-label': 'Template name' });
  const say = el('input', { placeholder: 'What it does, in a line', 'aria-label': 'Description' });
  const tags = el('input', { placeholder: 'tags, separated by commas', 'aria-label': 'Tags' });
  const rows = values.map((v) => {
    const box = el('input', { type: 'checkbox', 'aria-label': `Make ${v.param} of ${v.block} a setting` });
    const label = el('input', { value: v.param, 'aria-label': `Label of ${v.param} of ${v.block}`, disabled: true });
    box.onchange = () => { label.disabled = !box.checked; };
    return { v, box, label, node: el('div', { className: 'pt-row' }, [box, el('span', { textContent: `${v.block} · ${v.param} = ${v.value}` }), label]) };
  });
  // blocks that people may switch off when they use the template (a background tone, say): each gets an on/off setting that starts as it is now
  const switches = switchableBlocks(source).map((b) => {
    const box = el('input', { type: 'checkbox', 'aria-label': 'Let people switch ' + b.block + ' on or off' });
    const label = el('input', { value: b.block, 'aria-label': 'Label of the switch of ' + b.block, disabled: true });
    box.onchange = () => { label.disabled = !box.checked; };
    return { b, box, label, node: el('div', { className: 'pt-row' }, [box, el('span', { textContent: b.block + ' · now ' + (b.on ? 'on' : 'off') }), label]) };
  });
  let changedAnything = false;
  for (const input of [name, say, tags]) input.oninput = () => { changedAnything = true; };
  const content = el('div', { className: 'pt' }, [
    el('p', { className: 'pt-blocks', textContent: `Blocks: ${found.join(', ')}.` }),
    ...(external.length ? [el('p', { className: 'bw-warn', textContent: `${external.length} link${external.length === 1 ? '' : 's'} to blocks that are not selected are left out: ${[...new Set(external.map((e) => `${e.block} ← ${e.from}`))].join(', ')}.` })] : []),
    el('label', { className: 'bw-field' }, [el('span', { textContent: 'Name' }), name]),
    el('label', { className: 'bw-field' }, [el('span', { textContent: 'Description' }), say]),
    el('label', { className: 'bw-field' }, [el('span', { textContent: 'Tags' }), tags]),
    ...(switches.length ? [el('h4', { textContent: 'Blocks people may switch on or off', className: 'pt-h' }), el('div', { className: 'pt-list' }, switches.map((r) => r.node))] : []),
    ...(rows.length ? [el('h4', { textContent: 'Values that can be changed when the template is used', className: 'pt-h' }), el('div', { className: 'pt-list' }, rows.map((r) => r.node))] : []),
  ]);
  const save = async () => {
    if (!name.value.trim()) { toast('Give the template a name.'); return false; }
    const picks = rows.filter((r) => r.box.checked).map((r, i) => ({ block: r.v.block, param: r.v.param, key: `${r.v.block}-${r.v.param}`.replace(/[^\w-]+/g, '-'), label: r.label.value.trim() || r.v.param, order: i }));
    const picked = switches.filter((r) => r.box.checked).map((r) => ({ block: r.b.block, key: ('use-' + r.b.block).replace(/[^\w-]+/g, '-'), label: r.label.value.trim() || r.b.block }));
    const made = parametrize(source, picks, picked);
    const template = makeTemplate({ name: name.value, say: say.value.trim(), tags: tags.value.split(',').map((t) => t.trim()).filter(Boolean), source: made.source, settings: made.settings });
    const existing = (await listProjectTemplates()).find((t) => t.id === template.id);
    if (existing && (await ask('Replace the template?', `A template named ${existing.name} exists in this project.`, [{ label: 'Cancel', value: false }, { label: 'Replace', value: true, primary: true }])) !== true) return false;
    await saveProjectTemplate(template);
    toast(`Saved the template ${template.name} in the ${DIR} folder.`);
    changed();
    return true;
  };
  return openWindow({
    id: 'save-template', title: 'Save as template', modal: true, resizable: true, width: 640, height: 600, content,
    dirty: () => changedAnything,
    question: { title: 'Save this template?', text: 'You filled in the template and did not save it yet.', discard: 'Do not save' },
    saveLabel: 'Save',
    save,
    buttons: [{ label: 'Cancel', value: 'cancel' }, { label: 'Save template', value: 'saved', primary: true, onclick: async () => (await save()) }],
  });
}

/** The window that puts a template into the song: where it starts, its settings, and the names the blocks will get. */
export function openInsertTemplate(template) {
  const minAt = Math.min(...[...template.source.matchAll(new RegExp(String.raw`^[A-Za-z][\w-]*\s+[A-Za-z][\w-]*` + HEADER_TAIL + String.raw`[^\n{]*?\bat\s+(\d+)`, 'gm'))].map((m) => Number(m[1])), Infinity);
  const first = Number.isFinite(minAt) ? minAt : 1;
  const start = el('input', { type: 'number', min: 1, value: first, 'aria-label': 'Starts at bar' });
  const inputs = (template.settings ?? []).map((s) => ({ s, input: s.kind === 'switch' ? el('input', { type: 'checkbox', checked: String(s.default ?? '').trim() !== 'disabled', role: 'switch', 'aria-label': s.label || s.name }) : el('input', { value: s.default ?? '', 'aria-label': s.label || s.name }) }));
  let touched = false;
  for (const x of [start, ...inputs.map((i) => i.input)]) x.oninput = () => { touched = true; };
  const content = el('div', { className: 'pt' }, [
    ...(template.say ? [el('p', { textContent: template.say })] : []),
    ...(template.tags?.length ? [el('div', { className: 'bw-chips' }, template.tags.map((t) => el('span', { className: 'tpl-chip', textContent: t })))] : []),
    el('label', { className: 'bw-field' }, [el('span', { textContent: 'Starts at bar' }), start]),
    ...inputs.map(({ s, input }) => el('label', { className: s.kind === 'switch' ? 'bw-switch' : 'bw-field' }, s.kind === 'switch' ? [input, el('span', { textContent: s.label || s.name })] : [el('span', { textContent: s.label || s.name }), input])),
    el('pre', { className: 'pt-source', textContent: template.source }),
  ]);
  const add = async () => {
    const values = Object.fromEntries(inputs.map(({ s, input }) => [s.name, s.kind === 'switch' ? (input.checked ? '' : (s.off ?? ' disabled')) : input.value]));
    const filled = fill(template.source, template.settings ?? [], values);
    const shifted = shiftBars(filled, (Math.max(1, Number(start.value) || 1)) - first);
    const taken = flat().nodes.map((n) => n.id);
    const { source, map } = renameBlocks(shifted, taken);
    let songText = S.doc.text, body = source, linked = [];
    if (hasMaster(body) && hasMaster(songText)) {
      const choice = await ask('This template has a master', 'The song already has a master. Link the template to it (the sound of the template goes into the master of the song), or add its master as well?', [{ label: 'Cancel', value: false }, { label: 'Add its own master', value: 'both' }, { label: 'Link to the master of the song', value: 'link', primary: true }]);
      if (!choice) return false;
      if (choice === 'link') {
        linked = blockRanges(body).filter((r) => r.type === 'master').map((r) => r.name);
        const t = S.tpl?.mixer;
        const merged = mergeMaster(songText, body);
        songText = merged.songText; body = merged.source;
      }
    }
    const result = await applyText(body ? appendSource(songText, body) : songText);
    if (result === null) return false;
    // the blocks that came in are selected, so they can be moved, grouped or deleted at once (a master that was merged into the song's own is not one of them)
    const added = Object.values(map).filter((name) => !linked.includes(name));
    if (added.length) { select(added[0]); for (const name of added.slice(1)) select(name, true); notifySelection(); notify(); }
    toast(`Added ${added.join(', ')}.`);
    return true;
  };
  return openWindow({
    id: 'insert-template', title: `Add: ${template.name}`, modal: true, resizable: true, width: 620, height: 560, content,
    dirty: () => touched,
    question: { title: 'Add this template to the song?', text: 'You changed its settings and did not add it yet.', discard: 'Do not add' },
    saveLabel: 'Add to song', save: add,
    buttons: [{ label: 'Cancel', value: 'cancel' }, { label: 'Add to song', value: 'added', primary: true, onclick: async () => (await add()) }],
  });
}

/**
 * Drag a template card into the song: while the button is down a preview of the blocks it would add (under the names they would get) hangs at the pointer and the board lights up when it is over it.
 * Let go over the board and the window with the settings opens, where adding can still be cancelled or confirmed. Let go elsewhere, or press Esc, and nothing happens.
 */
export function dragTemplate(template, start, card) {
  const board = () => document.querySelector('#cv');
  const over = (x, y) => { const r = board()?.getBoundingClientRect(); return !!r && x >= r.left && x <= r.right && y >= r.top && y <= r.bottom; };
  let ghost = null;
  const makeGhost = () => {
    const { map } = renameBlocks(template.source, flat().nodes.map((n) => n.id));
    const blocks = blockRanges(template.source).map((r) => ({ type: r.type, name: map[r.name] ?? r.name }));
    ghost = el('div', { className: 'tpl-drag', 'aria-hidden': 'true' }, [
      el('b', { textContent: `${template.name}: adds ${blocks.length} block${blocks.length === 1 ? '' : 's'}` }),
      ...blocks.slice(0, 8).map((b) => el('div', { className: 'tpl-drag-block' }, [el('span', { textContent: b.name }), el('small', { textContent: b.type })])),
      ...(blocks.length > 8 ? [el('small', { textContent: `and ${blocks.length - 8} more` })] : []),
      el('small', { className: 'tpl-drag-hint' }),
    ]);
    document.body.append(ghost);
  };
  const place = (e) => {
    ghost.style.transform = `translate(${e.clientX + 14}px, ${e.clientY + 14}px)`;
    const on = over(e.clientX, e.clientY);
    ghost.classList.toggle('on', on);
    board()?.classList.toggle('tpl-drop', on);
    ghost.querySelector('.tpl-drag-hint').textContent = on ? 'Let go to add (you can still cancel)' : 'Drag it onto the board';
  };
  const stop = () => {
    document.removeEventListener('pointermove', move, true);
    document.removeEventListener('pointerup', up, true);
    document.removeEventListener('pointercancel', stop, true);
    document.removeEventListener('keydown', key, true);
    ghost?.remove();
    board()?.classList.remove('tpl-drop');
    card?.classList.remove('dragging');
  };
  const move = (e) => {
    if (!ghost) {
      if (Math.hypot(e.clientX - start.x, e.clientY - start.y) < 6) return;
      makeGhost();
      card?.classList.add('dragging');
    }
    e.preventDefault();
    place(e);
  };
  const up = (e) => {
    const wasDrag = !!ghost;
    const dropped = wasDrag && over(e.clientX, e.clientY);
    stop();
    if (wasDrag) card?.addEventListener('click', (ev) => ev.stopPropagation(), { capture: true, once: true });
    if (dropped) openInsertTemplate(template);
  };
  const key = (e) => { if (e.key === 'Escape') { e.stopPropagation(); stop(); } };
  document.addEventListener('pointermove', move, true);
  document.addEventListener('pointerup', up, true);
  document.addEventListener('pointercancel', stop, true);
  document.addEventListener('keydown', key, true);
}
