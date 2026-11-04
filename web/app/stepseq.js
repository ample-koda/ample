// The step editors in the properties panel of the sample instruments: a grid of sixteen steps per drum voice and a strum row for the guitar.
// They only write the text parameters (pattern, strum, swing, humanize, kit, set) through the normal edit path, so the source stays the truth.
// @ample:samples

import { el, toast } from './ui.js';
import { build, edit, notify, S } from './store.js';
import { play, playProjectFile, setLoop, stop } from './player.js';
import { api } from './api.js';
import { numberField } from './numfield.js';
import { rangeOf } from '../lib/numfield-model.js';
import { layerHint, missingLine, regionRows } from '../lib/setinfo.js';
import { stepsPerBar } from '../lib/meter.mjs';
import { stepsOfBlock, stepsPerBeat, DRUM_CYCLE, VELOCITY_CYCLE, NAMED, STRUM_CYCLE, beatOf, cycleCell, formatChoke, formatDrums, formatStrum, missingVoices, nameOf, next, parseChoke, parseDrums, parseStrum, parseVoiceMap, toggleSolo, velocityOf, voiceSamplePath, withLength, withVelocity } from '../lib/stepseq-model.js';
import { MAIN, addPattern, deletePattern, duplicatePattern, formatPatterns, formatSong, parsePatterns, parseSong, renamePattern, sectionNames, setGrid, uniqueName } from '../lib/pattern-model.mjs';
import { sectionTimeline } from './section-timeline.js';
import { ask } from './ui.js';

let sets = null;

/** The sample sets of the project (cached until the project changes: call forgetSets). */
export async function loadSets() {
  if (!sets) {
    try { sets = (await api.get('/api/samples')).sets; } catch { sets = []; }
  }
  return sets;
}
export const forgetSets = () => { sets = null; };

const selectedPattern = new Map();   // block id to the name of the pattern its grid shows

/** Draws the grid again for a block (after another pattern was chosen): the panel is asked to render the block again. */
function reselect() { notify(); }

/** The pattern chooser, its buttons and the section strip of a drums block (the text it writes is the patterns and song parameters). */
function patternBar(n, locked, { defined, song, current, writeSet, select, mainParam = 'pattern', mainFallback = 'beat-A', newGrid = 'kick:' + '.'.repeat(stepsPerBar()), builtIns = true }) {
  const names = defined.map((x) => x.name);
  const box = el('div', { className: 'seqpatterns' });
  const chooser = el('select', { id: 'seq-pattern', disabled: locked, 'aria-label': 'Pattern', title: 'The pattern the grid below edits' }, [MAIN, ...names].map((name) => el('option', { value: name, textContent: name === MAIN ? 'main pattern' : name, selected: name === current })));
  chooser.onchange = () => select(chooser.value);
  const askName = async (title, start) => { const input = el('input', { value: start, 'aria-label': 'Name' }); const ok = await ask(title, 'Letters, digits, - and _.', [{ label: 'OK', value: true, primary: true }, { label: 'Cancel', value: false }], [input]); return ok ? input.value.trim() : null; };
  const gridNow = () => (current === MAIN ? param(n, mainParam, mainFallback) : defined.find((x) => x.name === current)?.grid ?? '');
  const flat = (text) => (NAMED[text] ? NAMED[text] : text);
  const buttons = [
    el('button', { id: 'seq-pattern-new', textContent: 'New', disabled: locked, title: 'A new empty pattern', onclick: async () => { const name = await askName('New pattern', uniqueName('pattern', names)); if (name === null) return; const r = addPattern(defined, song, name, newGrid); await writeSet(r.patterns, r.entries); select(r.name); } }),
    el('button', { id: 'seq-pattern-copy', textContent: 'Duplicate', disabled: locked, title: 'A copy of the pattern that is shown', onclick: async () => { const r = duplicatePattern(defined, song, current === MAIN ? 'main' : current, flat(gridNow())); await writeSet(r.patterns, r.entries); select(r.name); } }),
    el('button', { id: 'seq-pattern-rename', textContent: 'Rename', disabled: locked || current === MAIN, title: current === MAIN ? 'The main pattern has no name' : 'Rename this pattern; the song follows', onclick: async () => { const name = await askName('Rename pattern', current); if (!name || name === current) return; const r = renamePattern(defined, song, current, name); if (!r) { import('./ui.js').then((m) => m.toast('That name is taken or not allowed.')); return; } await writeSet(r.patterns, r.entries); select(r.name); } }),
    el('button', { id: 'seq-pattern-delete', textContent: 'Delete', disabled: locked || current === MAIN, title: current === MAIN ? 'The main pattern stays' : 'Delete this pattern and its sections', onclick: async () => { const r = deletePattern(defined, song, current); await writeSet(r.patterns, r.entries); select(MAIN); } }),
  ];
  box.append(el('div', { className: 'row' }, [el('label', { textContent: 'pattern' }), chooser, ...buttons]));
  // the sections: the song plays these in order, shown as a small timeline (app/section-timeline.js); it hands back the new list and the text is written here, as before
  const addNames = sectionNames(defined).filter((name) => builtIns || !['beat-A', 'beat-B'].includes(name));
  box.append(sectionTimeline({ entries: song, current, names: addNames, locked, mainName: MAIN, blockId: n.id, onSelect: (name) => select(name === MAIN || names.includes(name) ? name : MAIN), onChange: (entries) => writeSet(defined, entries) }));
  return box;
}

const param = (n, name, fallback = '') => (n.params[name] ?? fallback).replace(/^"|"$/g, '');

function setChooser(n, key, label, sample, locked) {
  const current = param(n, key);
  const select = el('select', { disabled: locked, title: `The folder of WAV files this block plays (a folder of the project with a / in its name)` });
  const names = [...new Set([current, ...sample.map((s) => s.folder)].filter(Boolean))];
  if (!names.includes(current)) names.unshift(current);
  names.forEach((name) => select.append(el('option', { value: name, textContent: sample.some((s) => s.folder === name) ? name : `${name || '(none)'}  (built in sound)`, selected: name === current })));
  select.onchange = () => edit([{ type: 'setParameter', block: n.id, name: key, value: select.value }]);
  const prop = el('div', { className: 'prop' }, [el('label', { textContent: label }), select]);
  const info = sample.find((s) => s.folder === current);
  // S12: a plain project folder set (not an installed pack.: value, read-only, and not an .sfz set, whose own
  // opcodes already carry gain/tune) can have its own set.json gain/tune edited here, instead of by hand.
  const editable = Boolean(info && current && !current.startsWith('pack:') && !current.toLowerCase().endsWith('.sfz'));
  const hint = info ? [layerHint(info), missingLine(info)].filter(Boolean) : [];
  if (!info || (!hint.length && !(info.voices?.length))) {
    if (editable) appendSetGainTune(prop, current, locked);
    return prop;
  }
  const box = el('div', { className: 'setinfo' }, [prop]);
  hint.forEach((t) => box.append(el('p', { className: 'note', textContent: t })));
  const rows = regionRows(info);
  const table = el('table', { className: 'regions' }, [el('tr', {}, ['voice', 'layers', 'round robins', 'engine plays', 'loop'].map((h) => el('th', { textContent: h })))]);
  rows.forEach((r) => table.append(el('tr', {}, [r.voice, r.layers, r.roundRobins, r.plays, r.loop].map((v) => el('td', { textContent: String(v) })))));
  box.append(el('details', { className: 'regionview' }, [el('summary', { textContent: `Regions of this set (${rows.length})` }), table]));
  if (editable) appendSetGainTune(box, current, locked);
  return box;
}

/** S12: this set's own set.json gain (dB) and tune (semitones), read once and written back merged with whatever
 * else set.json already holds (loops, offered layer/round-robin counts) so this never clobbers them. */
function appendSetGainTune(host, folder, locked) {
  const settingsPath = `${folder}/set.json`;
  let settings = {};
  const gain = el('input', { type: 'number', className: 'seqn', value: '0', min: -24, max: 24, title: "This set's own gain in dB (set.json), on top of every note's own velocity", disabled: locked });
  const tune = el('input', { type: 'number', className: 'seqn', value: '0', min: -24, max: 24, title: "This set's own tuning in semitones (set.json)", disabled: locked });
  host.append(el('div', { className: 'prop' }, [el('label', { textContent: 'set gain/tune' }), gain, tune]));
  (async () => {
    try { settings = JSON.parse((await api.get(`/api/file?path=${encodeURIComponent(settingsPath)}`)).text || '{}'); }
    catch { settings = {}; }
    gain.value = String(settings.gain ?? 0);
    tune.value = String(settings.tune ?? 0);
  })();
  const write = async () => {
    settings = { ...settings, gain: Math.max(-24, Math.min(24, Math.round(Number(gain.value) || 0))), tune: Math.max(-24, Math.min(24, Math.round(Number(tune.value) || 0))) };
    gain.value = String(settings.gain); tune.value = String(settings.tune);
    try { await api.put('/api/file', { path: settingsPath, text: JSON.stringify(settings, null, 2) }); }
    catch (error) { toast(error.message || String(error)); }
  };
  gain.onchange = write;
  tune.onchange = write;
}

function cell(value, kind, index, onclick, disabled) {
  const b = el('button', { className: `seqc ${kind} v${value === '.' ? 'rest' : value}`, textContent: value === '.' ? '' : value, disabled, title: `step ${index + 1}` });
  b.dataset.step = String(index);
  b.dataset.value = value;
  b.onclick = onclick;
  return b;
}

/** The voices of the built-in drum sound (the same names as in Renderers.cs). */
const BUILTIN_KIT = { folder: '', builtin: true, files: ['kick', 'snare', 'hat', 'openhat', 'clap', 'rim', 'tom', 'lowtom', 'hightom', 'crash'] };

/** The drum grid of a block: with a sample kit its voices are the files of the folder, without one the voices of the built-in sound. */
export async function renderDrumGrid(pad, n, locked) {
  const all = await loadSets();
  const W = stepsOfBlock(n.params.steps);   // the steps of a bar of this block: its steps parameter, else four for every beat
  const box = el('div', { className: 'seqbox', id: 'seq-drums' });
  pad.append(el('h4', { textContent: 'Drum kit', style: 'padding-left:0' }), box);
  box.append(setChooser(n, 'kit', 'sample kit', all, locked));
  if (!document.querySelector('#pt')?.contains(pad)) box.append(el('button', { id: 'seq-open-view', type: 'button', textContent: 'Patterns view', title: 'A full-width view with the lanes and their samples, the grid of a pattern and the sections', onclick: () => import('./patternview.js').then((m) => m.openPatternView(n.id)) }));
  const kit = all.find((s) => s.folder === param(n, 'kit')) ?? BUILTIN_KIT;   // without a sample kit the built-in sound plays the grid
  // named patterns and the section song (plan patterns P03): the grid edits the selected pattern; the plain `pattern` parameter is the first one
  const defined = parsePatterns(param(n, 'patterns')).patterns;
  const song = parseSong(param(n, 'song')).entries;
  let current = selectedPattern.get(n.id) ?? MAIN;
  if (current !== MAIN && !defined.some((x) => x.name === current)) current = MAIN;
  selectedPattern.set(n.id, current);
  const gridText = current === MAIN ? param(n, 'pattern', 'beat-A') : defined.find((x) => x.name === current).grid;
  const parsed = parseDrums(gridText, W);
  if (parsed.error) {
    box.append(el('p', { className: 'seqerr', textContent: `The pattern cannot be shown as a grid: ${parsed.error}. Fix it in the field below or reset it.` }), el('button', { textContent: 'Reset to beat-A', disabled: locked, onclick: () => edit([{ type: 'setParameter', block: n.id, name: 'pattern', value: 'beat-A' }]) }));
    return;
  }
  const setText = (name, value) => (value === '' ? { type: 'removeParameter', block: n.id, name } : { type: 'setParameter', block: n.id, name, value });
  const writeGrid = (grid) => edit([current === MAIN ? { type: 'setParameter', block: n.id, name: 'pattern', value: grid } : setText('patterns', formatPatterns(setGrid(defined, current, grid)))]);
  const write = (voices) => writeGrid(current === MAIN ? (nameOf(voices, W) ?? formatDrums(voices, W)) : formatDrums(voices));
  const writeSet = (nextPatterns, nextSong) => edit([setText('patterns', formatPatterns(nextPatterns)), setText('song', formatSong(nextSong))]);
  // X02: a listen button per voice (the same path a replaced or kit-sourced voice would actually play, see X01's
  // voiceMapField) and a "closed by" field writing the choke parameter text (SFZ03/X01's choke mechanism), both
  // editable right where the step grid already is instead of only in the separate voice table.
  const voiceMap = parseVoiceMap(param(n, 'map'));
  const chokeEntries = parseChoke(param(n, 'choke'));
  const writeChoke = (voice, closers) => {
    const next = new Map(chokeEntries);
    if (closers.length) next.set(voice, closers); else next.delete(voice);
    edit([setText('choke', formatChoke(next))]);
  };
  const head = el('div', { className: 'seqrow seqhead' }, [el('span', { className: 'seqname' })]);
  for (let i = 0; i < W; i++) head.append(el('span', { className: `seqnum ${beatOf(i, W)}`, textContent: Number.isInteger(stepsPerBeat(W)) && i % stepsPerBeat(W) === 0 ? String(i / stepsPerBeat(W) + 1) : '' }));
  box.append(patternBar(n, locked, { defined, song, current, writeSet, newGrid: 'kick:' + '.'.repeat(W), select: (name) => { selectedPattern.set(n.id, name); reselect(); } }));
  box.append(head);
  const missing = kit.builtin ? [] : missingVoices(parsed.voices, kit.files);
  parsed.voices.forEach((v, vi) => {
    const row = el('div', { className: 'seqrow' });
    row.dataset.voice = v.name;
    row.append(el('span', { className: `seqname${missing.includes(v.name) ? ' missing' : ''}`, textContent: v.name, title: missing.includes(v.name) ? `${kit.folder} has no ${v.name}.wav` : `${v.name}.wav` }));
    [...v.steps].forEach((c, i) => {
      const b = cell(c, beatOf(i, W), i, (event) => { if (i >= (v.length ?? W)) return; const cycle = event.shiftKey ? VELOCITY_CYCLE : DRUM_CYCLE; const voices = parsed.voices.map((x, xi) => (xi === vi ? { ...x, steps: cycleCell(x.steps, i, cycle, W) } : x)); write(voices); }, locked);
      b.title = i >= (v.length ?? W) ? `step ${i + 1}: beyond the length of this voice (the first ${v.length} steps repeat)` : `step ${i + 1}: click cycles rest, hit, accent, ghost; shift+click sets a velocity 3, 5, 7 or 9`;
      if (i >= (v.length ?? W)) b.classList.add('beyond');
      row.append(b);
    });
    const change = (patch) => write(parsed.voices.map((x, xi) => (xi === vi ? { ...x, ...patch } : x)));
    const mute = el('button', { className: `seqm${v.mute ? ' on' : ''}`, textContent: 'M', title: `Mute ${v.name}`, disabled: locked, onclick: () => change({ mute: !v.mute }) });
    const solo = el('button', { className: 'seqs', textContent: 'S', title: `Solo ${v.name} (mutes the others in the text)`, disabled: locked, onclick: () => write(toggleSolo(parsed.voices, vi)) });
    const level = el('input', { type: 'number', className: 'seqn', value: v.level, min: -24, max: 12, title: `Level of ${v.name} in dB`, disabled: locked });
    level.onchange = () => change({ level: Math.max(-24, Math.min(12, Math.round(Number(level.value) || 0))) });
    const pan = el('input', { type: 'number', className: 'seqn', value: v.pan, min: -32, max: 32, title: `Pan of ${v.name} (-32 left, 32 right)${kit.builtin ? ', not used by the built-in sound' : ''}`, disabled: locked || kit.builtin });
    pan.onchange = () => change({ pan: Math.max(-32, Math.min(32, Math.round(Number(pan.value) || 0))) });
    const swing = el('input', { type: 'number', className: 'seqn', value: v.swing >= 0 ? v.swing : '', placeholder: 'sw', min: 0, max: 60, title: `Swing of ${v.name} (percent; empty follows the block's swing)`, disabled: locked });
    swing.onchange = () => change({ swing: swing.value === '' ? -1 : Math.max(0, Math.min(60, Math.round(Number(swing.value) || 0))) });
    const length = el('input', { type: 'number', className: 'seqn', value: v.length ?? W, min: 1, max: W, title: `Length of ${v.name} in steps (1 to ${W}); a shorter row repeats`, disabled: locked });
    length.onchange = () => write(parsed.voices.map((x, xi) => (xi === vi ? withLength(x, length.value, W) : x)));
    const tune = el('input', { type: 'number', className: 'seqn', value: v.tune || 0, min: -24, max: 24, title: `Pitch of ${v.name} in semitones (for tuned toms and percussion)${kit.builtin ? ', not used by the built-in sound' : ''}`, disabled: locked || kit.builtin });
    tune.onchange = () => change({ tune: Math.max(-24, Math.min(24, Math.round(Number(tune.value) || 0))) });
    const samplePath = voiceSamplePath(v.name, voiceMap, kit.folder);
    const listen = el('button', { type: 'button', className: 'seqlisten', textContent: '▶', 'aria-label': `Listen to ${v.name}`, title: samplePath ? `Listen to ${v.name}` : `No single sample to preview for ${v.name} (an .sfz or pack kit, or no kit)`, disabled: locked || !samplePath, onclick: () => playProjectFile(samplePath).catch((error) => { if (!error.toasted) toast(error.message || String(error)); }) });
    const choke = el('input', { type: 'text', className: 'seqn seqchoke', value: (chokeEntries.get(v.name) || []).join(','), placeholder: 'closed by', title: `Voices that, when hit, cut ${v.name} short (comma separated, for example hat-open)`, disabled: locked });
    choke.onchange = () => writeChoke(v.name, choke.value.split(',').map((c) => c.trim().toLowerCase()).filter(Boolean));
    row.append(mute, solo, level, pan, swing, length, tune, listen, choke);
    row.append(el('button', { className: 'seqx', textContent: '✕', 'aria-label': `Remove ${v.name} from the pattern`, title: `Remove ${v.name} from the pattern`, disabled: locked, onclick: () => write(parsed.voices.filter((_, xi) => xi !== vi)) }));
    box.append(row);
    // the velocity lane: one bar per step, click or drag up and down to set 1 (low) to 9 (high), the bottom edge makes a rest
    const lane = el('div', { className: 'seqlane', title: `Velocity of ${v.name}: click or drag the bars` });
    lane.dataset.voice = v.name;
    const bars = [];
    for (let i = 0; i < W; i++) {
      const fill = el('i');
      fill.style.height = `${(velocityOf(v.steps[i]) / 9) * 100}%`;
      bars.push(fill);
      lane.append(el('span', { className: `seqv${i >= (v.length ?? W) ? ' beyond' : ''}` }, [fill]));
    }
    if (!locked) {
      let working = null;
      const apply = (event) => {
        const box2 = lane.getBoundingClientRect();
        const i = Math.floor(((event.clientX - box2.left) / box2.width) * W);
        if (i < 0 || i >= (v.length ?? W)) return;
        const level = Math.max(0, Math.min(9, Math.round((1 - (event.clientY - box2.top) / box2.height) * 9)));
        working = withVelocity(working, i, level, W);
        bars[i].style.height = `${(level / 9) * 100}%`;
      };
      lane.onpointerdown = (event) => { lane.setPointerCapture(event.pointerId); working = v.steps; apply(event); lane.onpointermove = apply; };
      lane.onpointerup = () => { lane.onpointermove = null; if (working && working !== v.steps) write(parsed.voices.map((x, xi) => (xi === vi ? { ...x, steps: working } : x))); working = null; };
    }
    box.append(el('div', { className: 'seqrow seqlanerow' }, [el('span', { className: 'seqname' }), lane]));
  });
  if (missing.length) box.append(el('p', { className: 'seqerr', textContent: `No sample for: ${missing.join(', ')}. The build fails until the file is in ${kit.folder} or the voice is removed.` }));
  const free = kit.files.filter((f) => !parsed.voices.some((v) => v.name === f));
  const tools = el('div', { className: 'row seqtools' });
  const add = el('select', { disabled: locked || !free.length, title: 'Add a voice of the kit to the pattern' }, [el('option', { value: '', textContent: free.length ? '+ add a voice...' : 'every voice is in the pattern' }), ...free.map((f) => el('option', { value: f, textContent: f }))]);
  add.onchange = () => { if (add.value) write([...parsed.voices, { name: add.value, steps: '.'.repeat(W), length: W, mute: false, level: 0, pan: 0, swing: -1, tune: 0 }]); };
  const presets = el('select', { disabled: locked, title: 'Start from a named pattern' }, [el('option', { value: '', textContent: 'preset...' }), ...Object.keys(NAMED).map((k) => el('option', { value: k, textContent: k }))]);
  presets.onchange = () => { if (presets.value) writeGrid(NAMED[presets.value] && current !== MAIN ? NAMED[presets.value] : presets.value); };
  tools.append(add, presets, el('button', { textContent: 'Clear', disabled: locked, onclick: () => write(parsed.voices.map((v) => ({ ...v, steps: '.'.repeat(W) }))) }));
  const loopButton = el('button', { textContent: '▶ Loop', title: 'Build this block and play it in a loop while you edit', disabled: locked && false });
  let looping = false;
  loopButton.onclick = async () => {
    if (looping) { looping = false; setLoop(false); stop(); loopButton.textContent = '▶ Loop'; return; }
    await build(n.id);
    const take = S.build?.takes?.[n.id];
    if (!take) return;
    looping = true; loopButton.textContent = '■ Stop loop';
    setLoop(true);
    play(take).catch(() => { looping = false; loopButton.textContent = '▶ Loop'; });
  };
  tools.append(loopButton);
  box.append(tools, el('p', { className: 'note', textContent: 'Click a step: rest, hit (x), accent (X), ghost note (o). Shift+click gives an exact velocity (digits 3, 5, 7, 9). A bar has ' + W + ' steps (the steps field below changes that); a longer row goes on over the next bars.' }));
  [['steps', 'Steps in a bar. A step lasts a bar divided by this: with 8 the pattern plays at half speed, with 32 at double speed, whatever the tempo of the song is.'], ['swing', 'How far the odd steps are pushed later (percent of a step).'], ['humanize', 'A little variation in volume and timing; the same song always sounds the same.']].forEach(([key, tip]) => {
    box.append(el('div', { className: 'prop' }, [el('label', { textContent: key, title: tip }), numberField({ value: key === 'steps' ? String(W) : (param(n, key, '0') || '0'), range: rangeOf(key), disabled: locked, title: tip, onCommit: (text) => edit([{ type: 'setParameter', block: n.id, name: key, value: text }]) })]));
  });
}

/** The strum row(s) for the guitar block. */
export async function renderStrum(pad, n, locked) {
  const all = await loadSets();
  const box = el('div', { className: 'seqbox', id: 'seq-guitar' });
  pad.append(el('h4', { textContent: 'Guitar', style: 'padding-left:0' }), box);
  box.append(setChooser(n, 'set', 'sample set', all.filter((s) => s.notes > 0), locked));
  // named strums and the section song: the strip and the chooser are the ones of the drums, the text lives in patterns and song
  const defined = parsePatterns(param(n, 'patterns')).patterns;
  const song = parseSong(param(n, 'song')).entries;
  let current = selectedPattern.get(n.id) ?? MAIN;
  if (current !== MAIN && !defined.some((x) => x.name === current)) current = MAIN;
  selectedPattern.set(n.id, current);
  const W = stepsOfBlock(n.params.steps);   // the steps of a bar: the steps parameter, else four for every beat
  const parsed = parseStrum(current === MAIN ? param(n, 'strum', 'D...D...D.u.D.u.') : defined.find((x) => x.name === current).grid, W);
  if (parsed.error) {
    box.append(el('p', { className: 'seqerr', textContent: `The strum cannot be shown: ${parsed.error}.` }), el('button', { textContent: 'Reset', disabled: locked, onclick: () => edit([{ type: 'setParameter', block: n.id, name: 'strum', value: 'D...D...D.u.D.u.' }]) }));
    return;
  }
  const setText = (name, value) => (value === '' ? { type: 'removeParameter', block: n.id, name } : { type: 'setParameter', block: n.id, name, value });
  const write = (bars) => edit([current === MAIN ? { type: 'setParameter', block: n.id, name: 'strum', value: formatStrum(bars) } : setText('patterns', formatPatterns(setGrid(defined, current, formatStrum(bars))))]);
  const writeSet = (nextPatterns, nextSong) => edit([setText('patterns', formatPatterns(nextPatterns)), setText('song', formatSong(nextSong))]);
  box.append(patternBar(n, locked, { defined, song, current, writeSet, select: (name) => { selectedPattern.set(n.id, name); reselect(); }, mainParam: 'strum', mainFallback: 'D...D...D.u.D.u.', newGrid: '.'.repeat(W), builtIns: false }));
  const head = el('div', { className: 'seqrow seqhead' }, [el('span', { className: 'seqname' })]);
  for (let i = 0; i < W; i++) head.append(el('span', { className: `seqnum ${beatOf(i, W)}`, textContent: Number.isInteger(stepsPerBeat(W)) && i % stepsPerBeat(W) === 0 ? String(i / stepsPerBeat(W) + 1) : '' }));
  box.append(head);
  parsed.bars.forEach((bar, bi) => {
    const row = el('div', { className: 'seqrow' });
    row.dataset.bar = String(bi);
    row.append(el('span', { className: 'seqname', textContent: parsed.bars.length > 1 ? `bar ${bi + 1}` : 'strum' }));
    [...bar].forEach((c, i) => row.append(cell(c, beatOf(i, W), i, () => write(parsed.bars.map((b, k) => (k === bi ? cycleCell(b, i, STRUM_CYCLE, W) : b))), locked)));
    if (parsed.bars.length > 1) row.append(el('button', { className: 'seqx', textContent: '✕', 'aria-label': 'Remove this bar of the pattern', title: 'Remove this bar of the pattern', disabled: locked, onclick: () => write(parsed.bars.filter((_, k) => k !== bi)) }));
    box.append(row);
  });
  const tools = el('div', { className: 'row seqtools' }, [el('button', { textContent: '+ bar', disabled: locked || parsed.bars.length >= 8, title: 'Add a bar; the bars cycle in order', onclick: () => write([...parsed.bars, parsed.bars[parsed.bars.length - 1]]) })]);
  const voicing = el('select', { disabled: locked, title: 'How the chord is spread over the strings' }, ['open', 'triad', 'power'].map((v) => el('option', { value: v, textContent: v, selected: v === (param(n, 'voicing', 'open') || 'open') })));
  voicing.onchange = () => edit([{ type: 'setParameter', block: n.id, name: 'voicing', value: voicing.value }]);
  tools.append(el('label', { className: 'note', textContent: 'voicing', style: 'margin:0 4px' }), voicing);
  box.append(tools, el('p', { className: 'note', textContent: 'Click a step: let it ring, down D, soft down d, up U, soft up u, mute x. The strings sound one after the other (stagger). A bar has ' + W + ' steps.' }));
  [['steps', 'Steps in a bar. A step lasts a bar divided by this: with 8 the strum plays at half speed, with 32 at double speed, whatever the tempo of the song is.', String(W)], ['stagger', 'Milliseconds between the strings of one strum.', '22'], ['gain', 'Loudness in decibels.', '0']].forEach(([key, tip, fallback]) => {
    box.append(el('div', { className: 'prop' }, [el('label', { textContent: key, title: tip }), numberField({ value: param(n, key, fallback) || fallback, range: rangeOf(key), disabled: locked, title: tip, onCommit: (text) => edit([{ type: 'setParameter', block: n.id, name: key, value: text }]) })]));
  });
}

export { next };
