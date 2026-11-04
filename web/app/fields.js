// The fields of the properties panel, by the kind a template gives each parameter: text, number, choice (a fixed list), combo (a list or your own
// text), seed, sampleset, file, pattern and channels. A fixed list is a select, never a text field; free text that is sometimes right is a combo box;
// nothing starts with a made-up text (an empty field shows a placeholder instead).
// @ample:fields

import { engineField } from './engine-field.js';
import { ask, el, toast } from './ui.js';
import { S } from './store.js';
import { api } from './api.js';
import { numberField } from './numfield.js';
import { seedDice } from './seed-dice.js';
import { openProjectFilePicker } from './filepicker.js';
import { draggedFile, fieldAccepts } from './dragfile.js';
import { playProjectFile } from './player.js';
import { parentFolder } from '../lib/filepicker-model.mjs';
import { parseVoiceMap, voiceSamplePath } from '../lib/stepseq-model.js';

const MAX_SEED = 18446744073709551615n;
import { isNumeric, joinChannels, rangeOf, readNumber, splitChannels, stripUnit, withUnit } from '../lib/numfield-model.js';

let listId = 0;

/** The kind of a parameter: what the template says, else number for number parameters, else text. */
export function kindOf(name, meta) {
  if (meta?.kind && meta.kind !== 'text') return meta.kind;
  return isNumeric(name, meta) ? 'number' : 'text';
}

function select(options, value, disabled, title, onPick) {
  const known = options.includes(value) || value === '';
  const s = el('select', { disabled, title });
  if (value === '') s.append(el('option', { value: '', textContent: 'choose...', selected: true }));
  options.forEach((o) => s.append(el('option', { value: o, textContent: o, selected: o === value })));
  if (!known) s.append(el('option', { value, textContent: `${value} (not in the list)`, selected: true }));
  s.onchange = () => onPick(s.value);
  return s;
}

function combo(options, value, disabled, title, placeholder, onCommit) {
  const id = `dl${listId++}`;
  const input = el('input', { value, disabled, title, placeholder: placeholder || '' });
  input.setAttribute('list', id);
  const list = el('datalist', { id }, options.map((o) => el('option', { value: o })));
  input.onchange = () => onCommit(input.value.trim());
  return el('div', { className: 'combof' }, [input, list]);
}

function fileField(value, meta, disabled, title, onCommit) {
  const input = el('input', { type: 'text', value, disabled, title, placeholder: meta?.placeholder || 'a file of the project', 'aria-label': 'Project file path' });
  const clear = el('button', { type: 'button', className: 'mini', textContent: '×', 'aria-label': 'Clear: leave this field empty', title: 'Clear: leave this field empty', 'aria-label': 'Clear file' });
  const pick = el('button', { type: 'button', className: 'mini', textContent: '…', 'aria-label': 'Choose a project file', title: 'Choose a project file', 'aria-label': 'Choose file' });
  const open = el('button', { type: 'button', className: 'mini', textContent: '→', 'aria-label': 'Open or reveal this file', title: 'Open or reveal this file', 'aria-label': 'Open file' });
  const packs = (meta?.accept || []).includes('.wav') && !disabled ? el('button', { type: 'button', className: 'mini', textContent: 'Pack...', title: 'Choose a WAV of an installed audio pack', 'aria-label': 'Choose pack file' }) : null;
  pick.disabled = disabled; clear.disabled = disabled; open.disabled = disabled;
  const validate = async () => {
    // the song writes a path with spaces or other signs in quotes (file "samples/my song.wav"): the file is the text inside them
    const path = input.value.trim().replace(/^"(.*)"$/, '$1');
    if (!path || path.startsWith('pack:')) { input.removeAttribute('aria-invalid'); return; }
    try {
      const exists = meta?.mode === 'folder'
        ? (await api.get(`/api/project/tree?path=${encodeURIComponent(parentFolder(path))}&depth=0`)).children.some((item) => item.isDir && item.path === path)
        : (await api.get('/api/project/files')).some((file) => file.path === path);
      input.setAttribute('aria-invalid', String(!exists));
      input.title = exists ? title : `This project ${meta?.mode === 'folder' ? 'folder' : 'file'} does not exist.`;
    } catch { input.removeAttribute('aria-invalid'); }
  };
  input.onchange = () => { onCommit(input.value.trim()); validate().catch(() => {}); };
  validate().catch(() => {});
  input.dataset.accept = (meta?.accept || []).join(',');
  input.dataset.mode = meta?.mode || 'file';
  // the drag state is kept by dragfile.js: the browser hides the data of a drag until the drop
  input.ondragover = (event) => { const drag = draggedFile(); if (!drag) return; event.preventDefault(); input.classList.toggle('drop-invalid', !fieldAccepts(drag, meta?.accept, meta?.mode)); };
  input.ondragleave = () => input.classList.remove('drop-invalid');
  input.ondrop = (event) => {
    const drag = draggedFile();
    const path = drag?.path || event.dataTransfer?.getData('application/x-ample-path') || '';
    if (!path) return;
    event.preventDefault(); input.classList.remove('drop-invalid');
    if (!fieldAccepts(drag || { path, isDir: false }, meta?.accept, meta?.mode)) { toast('This file type is not accepted here.'); return; }
    input.value = path; onCommit(path); validate().catch(() => {});
  };
  pick.onclick = async () => { const chosen = await openProjectFilePicker(input.value, meta?.accept || [], meta?.mode || 'file'); if (chosen !== undefined) { input.value = chosen; onCommit(chosen); validate().catch(() => {}); } };
  clear.onclick = () => { input.value = ''; input.removeAttribute('aria-invalid'); onCommit(''); };
  open.onclick = () => { if (!input.value) return; document.dispatchEvent(new CustomEvent('ample:reveal-file', { detail: input.value })); document.dispatchEvent(new CustomEvent(/\.(amplet|module)$/i.test(input.value) ? 'ample:open' : 'ample:open-file', { detail: input.value })); };
  if (packs) {
    packs.onclick = async () => {
      try {
        const { files } = await api.get('/api/packs/files');
        if (!files.length) { toast('No audio packs with WAV files are installed (see: ample packs list).'); return; }
        const chosen = await ask('Pack file', 'Choose a WAV of an installed audio pack.', files.slice(0, 40).map((value) => ({ label: value.slice('pack:'.length), value })));
        if (chosen) { input.value = chosen; onCommit(chosen); validate().catch(() => {}); }
      } catch (error) { toast(error.message); }
    };
  }

  return el('div', { className: 'file-field' }, [input, pick, ...(packs ? [packs] : []), open, clear]);
}

/**
 * Makes the editing control of one parameter.
 * @returns {Promise<HTMLElement>|HTMLElement|null} null when the caller should use its own text field (kind text)
 */
const NAMED_DRUM_VOICES = { 'beat-A': ['kick', 'snare', 'hat'], 'beat-B': ['kick', 'snare', 'hat'] };

/** The voices a drum pattern plays: the names before the colons, or the voices of a named pattern. */
export function patternVoices(pattern) {
  if (NAMED_DRUM_VOICES[pattern.trim()]) return NAMED_DRUM_VOICES[pattern.trim()];
  return [...new Set(pattern.split(/[\s|;]+/).map((part) => part.split(':')[0].trim().toLowerCase()).filter((v, i, all) => v && part_has_colon(pattern, v)))];
}
const part_has_colon = (pattern, voice) => new RegExp(`(^|[\\s|;])${voice}:`, 'i').test(pattern);

/** A table of the voices of a drum pattern; each can be replaced by a sample of another kit (writes the map text). */
function voiceMapField(value, o, commit) {
  const entries = parseVoiceMap(value);
  const voices = [...new Set([...patternVoices(o.params?.pattern || ''), ...entries.keys()])];
  const write = () => commit([...entries].map(([voice, target]) => `${voice}:${target}`).join(';'));
  const table = el('div', { className: 'voicemap' });
  if (!voices.length) table.append(el('span', { className: 'note', textContent: 'The pattern has no voices yet.' }));
  voices.forEach((voice) => {
    const mapped = entries.get(voice);
    const pick = el('button', { type: 'button', className: 'mini', textContent: mapped ? 'change...' : 'replace...', title: `Replace ${voice} by a sample of another kit`, disabled: o.disabled });
    const kit = String(o.params?.kit || '').replace(/^"|"$/g, '');
    const samplePath = voiceSamplePath(voice, entries, kit);
    const listen = el('button', { type: 'button', className: 'mini', textContent: '▶', 'aria-label': `Listen to ${voice}`, title: `Listen to ${voice}`, disabled: !samplePath });
    listen.onclick = () => playProjectFile(samplePath).catch((error) => { if (!error.toasted) toast(error.message); });
    const reset = el('button', { type: 'button', className: 'mini', textContent: '×', 'aria-label': 'Back to the sample of the kit', title: 'Back to the sample of the kit', disabled: o.disabled || !mapped });
    pick.onclick = async () => {
      const chosen = await openProjectFilePicker('', ['.wav'], 'file');
      if (chosen) { entries.set(voice, chosen.replace(/\.wav$/i, '')); write(); }
    };
    reset.onclick = () => { entries.delete(voice); write(); };
    table.append(el('div', { className: `voicemap-row${mapped ? ' mapped' : ''}` }, [
      el('span', { className: 'vm-voice', textContent: voice }),
      el('span', { className: 'vm-source', textContent: mapped || 'from the kit', title: mapped || '' }),
      listen, pick, reset,
    ]));
  });
  return table;
}

export function fieldFor(k, meta, value, o) {
  const kind = kindOf(k, meta);
  const title = o.title || meta?.documentation || '';
  const commit = (text) => o.save(text);
  switch (kind) {
    case 'number': {
      if (readNumber(value, null, meta?.unit).error && value !== '') return null;   // a value like "derived" stays a text field
      // a component writes its numbers tight (-3dB, 35%), the built-in blocks with a space (-3 dB)
      return numberField({ value: stripUnit(value, meta?.unit), range: rangeOf(k, meta), unit: meta?.unit, disabled: o.disabled, title, onCommit: (t) => commit(meta?.tight && meta.unit ? `${t}${meta.unit}` : withUnit(t, meta?.unit)) });
    }
    case 'choice': return meta.quoted ? select(meta.options ?? [], String(value).replace(/^"|"$/g, ''), o.disabled, title, (v) => commit(`"${v}"`)) : select(meta.options ?? [], value, o.disabled, title, commit);
    case 'combo': return combo(meta.options ?? [], value, o.disabled, title, meta.placeholder, commit);
    case 'engine': return engineField({ blockType: o.tpl, value, disabled: o.disabled, title, commit: (text) => commit(text === '' ? '' : `"${text}"`) });
    case 'seed': {
      const input = combo([], value === 'derived' ? '' : value, o.disabled, title || 'derived follows the master seed; or an unsigned 64-bit whole number', 'derived', (text) => {
        if (text === '' || text === 'derived' || /^\d{1,20}$/.test(text) && BigInt(text) <= MAX_SEED) commit(text === '' ? 'derived' : text);
        else { toast('A seed is the word derived or an unsigned 64-bit whole number.'); input.querySelector('input').value = value === 'derived' ? '' : value; }
      });
      return el('span', { className: 'seed-row' }, [input, seedDice(() => input.querySelector('input'), { disabled: o.disabled })]);
    }
    case 'file': {
      return fileField(value, meta, o.disabled, title, commit);
    }
    case 'sampleset': {
      // a word that is not a folder path (the template default, a built-in name) means the built-in sound: show an empty field that says so
      const builtin = value !== '' && !value.includes('/');
      return fileField(builtin ? '' : value, { ...meta, mode: 'folder', placeholder: builtin ? 'built-in synth (choose a folder for samples)' : undefined }, o.disabled, title || 'A folder of the project with WAV files', commit);
    }
    case 'voicemap': return voiceMapField(value, o, commit);
    case 'channels': {
      const words = splitChannels(value, meta.count || 4, meta.options ? meta.options[Math.floor(meta.options.length / 2)] : '0');
      const row = el('div', { className: 'channels' });
      words.forEach((w, i) => {
        const write = (word) => commit(joinChannels(words.map((x, j) => (j === i ? word : x))));
        const label = el('span', { className: 'chn', textContent: String(i + 1), title: `input ${i + 1}` });
        const control = meta.options
          ? select(meta.options, w, o.disabled, `input ${i + 1}`, write)
          : numberField({ value: stripUnit(w, meta.unit), range: rangeOf(k, meta), disabled: o.disabled, title: `input ${i + 1}, ${meta.unit || ''}`, onCommit: (t) => write(t) });
        row.append(el('div', { className: 'chan' }, [label, control]));
      });
      return row;
    }
    case 'pattern': return null;       // the step editor above the parameters edits it; the text field stays as the plain fallback
    default: return null;
  }
}
