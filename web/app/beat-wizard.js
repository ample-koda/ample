// The wizard of a beat (plan templates-library T04): what the catalogue says about it, the settings of the block that will be added (name, place, kit, voices, swing, humanize),
// a preview that plays in the browser with a tempo you can change, and Add: one drums block, one undo step. @ample:beats
import { api } from './api.js';
import { S, edit, flat } from './store.js';
import { el, toast } from './ui.js';
import { openWindow } from './window.js';
import { createBeatPreview } from './beat-preview.js';
import { blockNameOf, fillGridOf, gridOnKit, mapVoices, parseGrid, tempoFit } from '../lib/beat-model.mjs';
import { KEYS, beatOps, modeOf, progressionChords } from '../lib/beat-chords.mjs';

const dots = (n) => el('span', { className: 'tpl-dots', 'aria-label': `${n} of 5` }, Array.from({ length: 5 }, (_, i) => el('i', { className: i < n ? 'on' : '' })));
const list = (items) => el('ul', { className: 'bw-list' }, items.map((t) => el('li', { textContent: t })));
const section = (title, ...nodes) => el('section', { className: 'bw-sec' }, [el('h4', { textContent: title }), ...nodes]);
const labeled = (text, input, hint) => el('label', { className: 'bw-field' }, [el('span', { textContent: text }), input, ...(hint ? [el('small', { textContent: hint })] : [])]);
const freeName = (base) => { const names = new Set(flat().nodes.map((n) => n.id)); if (!names.has(base)) return base; let i = 2; while (names.has(`${base}-${i}`)) i++; return `${base}-${i}`; };

/** Free audio inputs of the mixers, buses and the master, in order: where the new sounds can be connected. */
function freeTargets(count) {
  const { nodes, links } = flat();
  const out = [];
  for (const node of nodes.filter((x) => ['mixer', 'bus', 'master'].includes(x.tpl))) {
    node.ins.forEach((input, index) => {
      if (out.length < count && input[1] === 'audio' && !links.some((l) => l.to === node.id && l.inp === index)) out.push({ to: node.id, input: input[0] });
    });
  }
  return out;
}

/** Kits first: the sample sets of the project that have the usual drum voices. */
async function loadKits() {
  let sets = [];
  try { sets = (await api.get('/api/samples')).sets || []; } catch { sets = []; }
  const score = (s) => ['kick', 'snare', 'hat'].filter((v) => s.files.some((f) => f.toLowerCase().includes(v))).length;
  return sets.map((s) => ({ folder: s.folder, files: s.files, score: score(s) })).sort((a, b) => b.score - a.score || a.folder.localeCompare(b.folder));
}

export async function openBeatWizard(id) {
  let beat;
  try { beat = await api.get(`/api/beats?id=${encodeURIComponent(id)}`); } catch (e) { toast(e.message || String(e)); return; }
  if (beat.error) { toast(beat.error); return; }
  const kits = await loadKits();
  const entries = parseGrid(beat.drums?.grid);
  const fillEntries = fillGridOf(beat.drums?.fill);
  const projectTempo = Number(S.doc?.project?.tempo) || 100;
  const typical = beat.tempo?.typical || projectTempo;
  const previewTempo = projectTempo >= (beat.tempo?.min ?? 0) && projectTempo <= (beat.tempo?.max ?? 999) ? projectTempo : typical;
  const defaults = { name: freeName(blockNameOf(beat.id)), bar: 1, bars: beat.form === 'loop' || !beat.bars ? 8 : beat.bars, kit: kits[0]?.score >= 2 ? kits[0].folder : '', swing: Math.round((beat.feel?.swing || 0) * 100), humanize: 10, bpm: previewTempo };

  // ---- what the catalogue says
  const fit = tempoFit(projectTempo, beat.tempo);
  const info = el('div', { className: 'bw-info' }, [
    el('p', { className: 'bw-say', textContent: beat.say }),
    el('div', { className: 'bw-chips' }, [...(beat.genres || []), ...(beat.mood || [])].map((t) => el('span', { className: 'tpl-chip', textContent: t }))),
    el('div', { className: 'bw-bars' }, [el('span', { textContent: 'Energy' }), dots(beat.feel?.energy || 0), el('span', { textContent: 'Density' }), dots(beat.feel?.density || 0), el('span', { textContent: 'Novelty' }), dots(beat.novelty || 0)]),
    el('p', { className: `bw-fit ${fit.kind}`, textContent: fit.text }),
    ...(beat.tips?.length ? [section('Tips', list(beat.tips))] : []),
    ...(beat.howToBringIn?.length ? [section('How to bring it in', list(beat.howToBringIn))] : []),
    ...(beat.fast || beat.slow ? [section('Faster and slower', list([beat.fast, beat.slow].filter(Boolean)))] : []),
    ...(beat.sections?.length ? [section('A song with it', el('p', { textContent: beat.sections.join(' · ') }))] : []),
    ...(beat.avoid?.length ? [section('Avoid', list(beat.avoid))] : []),
  ]);

  // ---- the settings
  const name = el('input', { value: defaults.name, 'aria-label': 'Block name' });
  const bar = el('input', { type: 'number', min: 1, value: defaults.bar, 'aria-label': 'Starts at bar' });
  const bars = el('input', { type: 'number', min: 1, max: 256, value: defaults.bars, 'aria-label': 'Length in bars' });
  const kit = el('select', { 'aria-label': 'Drum kit' }, [el('option', { value: '', textContent: 'The built-in sound' }), ...kits.map((k) => el('option', { value: k.folder, textContent: `${k.folder} (${k.files.length} sounds)` }))]);
  kit.value = defaults.kit;
  const swing = el('input', { type: 'range', min: 0, max: 60, value: defaults.swing, 'aria-label': 'Swing' });
  const swingOut = el('output', { textContent: `${defaults.swing}%` });
  const humanize = el('input', { type: 'range', min: 0, max: 50, value: defaults.humanize, 'aria-label': 'Humanize' });
  const humanizeOut = el('output', { textContent: `${defaults.humanize}%` });
  const bpm = el('input', { type: 'range', min: 50, max: 220, value: defaults.bpm, 'aria-label': 'Preview tempo' });
  const bpmOut = el('output', { textContent: `${defaults.bpm} bpm` });
  const mapBox = el('div', { className: 'bw-map' });
  const warn = el('p', { className: 'bw-warn', role: 'status' });
  const mapping = {};   // beat voice -> kit voice or ''

  const kitVoices = () => kits.find((k) => k.folder === kit.value)?.files ?? [];
  function drawMap() {
    mapBox.replaceChildren();
    if (!kit.value) { warn.textContent = 'The built-in sound has its own voices; no sample kit to put the voices on.'; return; }
    const proposal = mapVoices([...new Set(entries.map((e) => e.voice))], kitVoices());
    for (const voice of Object.keys(proposal)) {
      mapping[voice] = mapping[voice] !== undefined && kitVoices().includes(mapping[voice]) ? mapping[voice] : proposal[voice] || '';
      const select = el('select', { 'aria-label': `Sound for ${voice}` }, [el('option', { value: '', textContent: '(skip)' }), ...kitVoices().map((v) => el('option', { value: v, textContent: v }))]);
      select.value = mapping[voice];
      select.onchange = () => { mapping[voice] = select.value; touched(); };
      mapBox.append(el('div', { className: 'bw-maprow' }, [el('span', { textContent: voice }), el('span', { textContent: '→' }), select]));
    }
    const missing = Object.keys(proposal).filter((v) => !mapping[v]);
    warn.textContent = missing.length ? `No sound for ${missing.join(', ')}: those voices are left out.` : '';
  }
  const current = () => {
    const mapped = kit.value ? gridOnKit(entries, mapping) : { grid: entries.map((e) => [`${e.voice}:${e.steps}`, ...e.mods].join(' ')).join(' '), skipped: [] };
    const fill = kit.value ? gridOnKit(fillEntries, mapping).grid : fillEntries.map((e) => [`${e.voice}:${e.steps}`, ...e.mods].join(' ')).join(' ');
    return { mapped, fill };
  };

  // ---- the preview
  let preview = null, playing = false;
  const play = el('button', { type: 'button', className: 'primary', textContent: '▶ Play' });
  const previewNote = el('small', { className: 'bw-note', textContent: 'The tempo here only changes the preview; the tempo of the song stays the same.' });
  async function startPreview() {
    const { mapped } = current();
    const used = parseGrid(mapped.grid);
    if (!kit.value) { previewNote.textContent = 'Choose a sample kit to hear the beat (the built-in sound is not played here).'; return; }
    preview = createBeatPreview({ entries: used, fileOf: (voice) => (kitVoices().includes(voice) ? `${kit.value}/${voice}.wav` : ''), bpm: Number(bpm.value), swing: Number(swing.value) });
    try { await preview.start(); playing = true; play.textContent = '■ Stop'; previewNote.textContent = 'The tempo here only changes the preview; the tempo of the song stays the same.'; }
    catch (e) { preview = null; previewNote.textContent = e.message || String(e); }
  }
  const stopPreview = () => { preview?.stop(); preview = null; playing = false; play.textContent = '▶ Play'; };
  play.onclick = () => (playing ? stopPreview() : startPreview());
  const restart = () => { if (playing) { stopPreview(); startPreview(); } };

  let changed = false;
  const touched = () => { changed = true; restart(); };
  kit.onchange = () => { drawMap(); touched(); };
  swing.oninput = () => { swingOut.textContent = `${swing.value}%`; preview?.set({ swing: Number(swing.value) }); changed = true; };
  humanize.oninput = () => { humanizeOut.textContent = `${humanize.value}%`; changed = true; };
  bpm.oninput = () => { bpmOut.textContent = `${bpm.value} bpm`; preview?.set({ bpm: Number(bpm.value) }); };
  for (const input of [name, bar, bars]) input.oninput = () => { changed = true; };

  // the chords and the bass of a groove-set: tick boxes, off by default (the beat is added as drums only); the progression is put in a key
  const mode = modeOf(beat.chords?.mode);
  const key = el('select', { 'aria-label': 'Key' }, KEYS.map((k) => el('option', { value: k, textContent: `${k} ${mode}` })));
  key.value = mode === 'minor' ? 'A' : 'C';
  const chordsOn = el('input', { type: 'checkbox', id: 'bw-chords', disabled: !beat.chords });
  const bassOn = el('input', { type: 'checkbox', id: 'bw-bass', disabled: !beat.bass?.pattern });
  const connectOn = el('input', { type: 'checkbox', id: 'bw-connect', checked: true });
  const chordNote = el('small', { className: 'bw-note' });
  const progression = () => progressionChords(beat.chords?.progression, mode, key.value);
  const paintChords = () => { chordNote.textContent = beat.chords ? `${beat.chords.progression}: ${progression().join(' ')}. ${beat.chords.rhythm ? beat.chords.rhythm : ''}` : ''; keyRow.hidden = !(chordsOn.checked || bassOn.checked); };
  const keyRow = el('div', { className: 'bw-field' }, [el('span', { textContent: 'Key of the chords and the bass' }), key, chordNote]);
  key.onchange = () => { paintChords(); changed = true; };
  chordsOn.onchange = () => { paintChords(); changed = true; };
  bassOn.onchange = () => { paintChords(); changed = true; };
  connectOn.onchange = () => { changed = true; };
  const more = el('div', { className: 'bw-more' }, [
    el('label', { title: beat.chords ? 'A piano plays the chords of the beat' : 'This beat has no chords' }, [chordsOn, ' Chords (a piano)']),
    el('label', { title: beat.bass?.pattern ? 'A bass plays the pattern of the beat on the roots of the chords' : 'This beat has no bass' }, [bassOn, ' Bass']),
    el('label', { title: 'Put the new sounds on free inputs of the mixer or the master' }, [connectOn, ' Connect to the mixer']),
  ]);
  paintChords();

  const settings = el('div', { className: 'bw-settings' }, [
    labeled('Name', name),
    el('div', { className: 'bw-two' }, [labeled('Starts at bar', bar), labeled('Length (bars)', bars)]),
    labeled('Drum kit', kit, kits.length ? '' : 'The project has no sample kit yet: the built-in sound is used.'),
    mapBox, warn,
    el('div', { className: 'bw-slider' }, [el('span', { textContent: 'Swing' }), swing, swingOut]),
    el('div', { className: 'bw-slider' }, [el('span', { textContent: 'Humanize' }), humanize, humanizeOut]),
    more, keyRow,
    section('Preview', el('div', { className: 'bw-slider' }, [el('span', { textContent: 'Tempo' }), bpm, bpmOut]), el('div', { className: 'bw-play' }, [play]), previewNote),
  ]);
  drawMap();

  // beats that look like this one (no model: shared genres, mood, tempo and form), to open instead
  const similar = el('div', { className: 'bw-chips bw-similar' });
  api.get(`/api/beats?similar=${encodeURIComponent(beat.id)}&limit=6`).then((r) => {
    for (const b of r.beats || []) similar.append(el('button', { type: 'button', className: 'tpl-chip', textContent: b.name, title: `${b.form} · ${b.bpm} bpm`, onclick: () => { win?.close('similar'); openBeatWizard(b.id); } }));
    if (similar.children.length) info.append(section('Similar beats', similar));
  }).catch(() => {});
  const content = el('div', { className: 'bw' }, [info, settings]);
  let win = null;
  const add = async () => {
    const { mapped, fill } = current();
    if (!mapped.grid.trim()) { toast('There is nothing to play: choose sounds for at least one voice.'); return false; }
    const finalName = freeName(blockNameOf(name.value));
    const n = flat().nodes.length;
    const chords = chordsOn.checked ? progression() : null;
    const wantBass = bassOn.checked && beat.bass?.pattern;
    const sounds = 1 + (chords ? 1 : 0) + (wantBass ? 1 : 0);
    const targets = connectOn.checked ? freeTargets(sounds) : [];
    const ops = beatOps({
      name: finalName, kit: kit.value, grid: mapped.grid, fill, bar: Math.max(1, Number(bar.value) || 1), bars: Math.max(1, Number(bars.value) || defaults.bars), swing: Number(swing.value), humanize: Number(humanize.value),
      chords, bassPattern: wantBass ? beat.bass.pattern : '', rootChords: progression(), spot: { x: 40 + (n % 6) * 40, y: 40 + (n % 6) * 30 }, targets,
    });
    const ok = await edit(ops);
    if (ok === false) return false;
    const blocks = ops.filter((o) => o.type === 'addBlock').length;
    toast(`Added ${finalName}${blocks > 1 ? ` and ${blocks - 1} more block${blocks > 2 ? 's' : ''}` : ''}${targets.length ? `, connected to ${targets[0].to}` : '. Connect its sound to a mixer or the master to hear it'}.`);
    return true;
  };
  win = openWindow({
    id: 'beat-wizard', title: `Add a beat: ${beat.name}`, modal: true, width: 880, height: 600, content,
    dirty: () => changed,
    question: { title: 'Add this beat to the song?', text: 'You changed its settings and did not add it yet.', discard: 'Do not add' },
    saveLabel: 'Add to song',
    save: async () => add(),
    buttons: [{ label: 'Cancel', value: 'cancel' }, { label: 'Add to song', value: 'added', primary: true, onclick: async () => (await add()) }],
    onClose: stopPreview,
  });
  return win;
}
