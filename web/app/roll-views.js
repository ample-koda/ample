// How a midi-clip, a MIDI file and a sequencer channel open in AMPLE Jam, the only piano roll. The Jam view memory (window place, zoom, scroll) lives in the browser's storage per
// project and song (the same per-project, per-song scope as the block groups in store.js), keyed by block id. @ample:pianoroll-lite

import { S, applyText, edit, flat, readOnly, songClock } from './store.js';
import { api } from './api.js';
import { ask, toast } from './ui.js';
import { readViews, writeViews } from '../lib/lite-flow.mjs';
import { editables, evaluate, isExpression } from '../lib/note-pipeline.mjs';
import { helpersOfText } from './song-helpers.js';
import { barBeats } from '../lib/meter.mjs';

const storeKey = () => `ample.rollview:${S.project ? S.project.id : ''}:${S.path}`;

/** The remembered record of a block ({ lite }) or undefined; storage that is blocked reads as nothing remembered. */
export function rememberedView(blockId) {
  try { return readViews(localStorage.getItem(storeKey()) || '')[blockId]; } catch { return undefined; }
}

/** Remembers part of a block's record ({ lite }). */
export function rememberView(blockId, patch) {
  try { localStorage.setItem(storeKey(), writeViews(localStorage.getItem(storeKey()) || '', blockId, patch)); } catch { /* storage may be blocked or full */ }
}

/** Whether a block can be opened in the roll: a midi-clip whose notes are written in the compact form (or has none yet). */
/** Whether the notes of a clip are a note pipeline (take.notes |> ...) and not a literal: the roll shows what it makes, read-only, and never writes into it. */
export const isPipeline = (node) => node?.tpl === 'midi-clip' && isExpression(node.params.notes ?? '');

/**
 * Opens one hand-written editable { } piece of a pipeline in the roll: an ordinary, editable clip of just those notes. Every change is written back into that piece of the song
 * (the server rewrites only that piece); the rest of the pipeline is not touched.
 */
export async function openEditablePiece(node, index) {
  const piece = editables(node.params.notes ?? '')[index];
  if (!piece) { toast('There is no hand-written piece number ' + (index + 1) + ' in this pipeline.'); return null; }
  const jam = await import('./pianoroll-lite.js');
  const write = async (text) => {
    try {
      const result = await api.post('/api/notes/editable', { path: S.path, block: node.id, index, notes: String(text).replace(/^"|"$/g, '') });
      return Boolean(await applyText(result.text));
    } catch (error) { toast(error.message); return false; }
  };
  return jam.openPianoRollLite(`${node.id}\u00b7piece${index + 1}`, { pseudo: { id: `${node.id}\u00b7piece${index + 1}`, title: `${node.title} (hand-written piece ${index + 1})`, params: {}, flags: [], tpl: 'midi-clip', parts: [] }, text: `"${piece.entries}"`, ppq: 1_000_000, write });
}

export const rollable = (node) => node?.tpl === 'midi-clip' && !isPipeline(node) && !node.params.chords && !node.params.melody && !/^\s*notes\s*\{/.test(node.params.notes ?? '');

/** The notes a pipeline makes, as compact text: worked out here (exact, the same corpus as the server), or by the server when the pipeline needs it (a bake kept in a file). */
/** The random-streams a clip declares (random-stream NAME SEED lines): { name: seed text }. */
export const streamsOf = (node) => Object.fromEntries(Object.entries(node?.params || {}).filter(([k]) => k.startsWith('random-stream:')).map(([k, v]) => [k.slice('random-stream:'.length), String(v)]));

/** What the evaluator needs to know about the song: the notes of every clip (with its streams when it is a pipeline), the tempo and the master-seed. */
export function pipelineSources() {
  return Object.fromEntries(flat().nodes.filter((n) => n.tpl === 'midi-clip').map((n) => [n.id, isExpression(n.params.notes ?? '') ? { notes: n.params.notes ?? '', streams: streamsOf(n) } : (n.params.notes ?? '')]));
}
export const masterSeedOfSong = () => { try { return BigInt(String(S.doc?.project?.masterSeed ?? '0').replace(/\D/g, '') || '0'); } catch { return 0n; } };

/** The helpers of the song as a table for the evaluator (built again only when the song text changed). */
export const songHelpers = () => helpersOfText(S.doc?.text ?? '');

export async function pipelineNotes(node) {
  const sources = pipelineSources();
  const result = evaluate(node.params.notes, { sources, tempo: Number(S.doc?.project?.tempo) || 120, beatsPerBar: Number(S.doc?.project?.beatsPerBar) || 4, master: masterSeedOfSong(), streams: streamsOf(node), helpers: songHelpers(), clock: songClock() ? () => songClock() : null });
  if (!result.errors.length) return { text: result.notes, problems: result.warnings };
  if (result.errors.every((e) => e.id === 'needs-server')) {
    const data = await api.post('/api/midi/clip-notes', { path: S.path, block: node.id });
    return { text: data.text, problems: [] };
  }
  return { text: null, problems: result.errors };
}

/**
 * The normal way to open a midi-clip. A clip written with chords or a melody is first written out as notes (one undoable edit that removes the chords or the
 * melody and adds the notes the clip plays), after the person has seen how many notes that makes; the shorthand is back with Undo or the history.
 */
export async function openRoll(blockId, options = {}) {
  const node = flat().nodes.find((n) => n.id === blockId);
  if (!node) return null;
  if ((node.params.melody !== undefined || node.params.chords !== undefined) && !options.writeOut && !isPipeline(node)) { const me = await import('./melody-editor.js'); return me.openMelodyEditor(blockId); }   // the line stays: no write-out to notes
  const jam = await import('./pianoroll-lite.js');
  if (rollable(node)) return jam.openPianoRollLite(blockId, options);
  if (isPipeline(node)) {
    let made;
    try { made = await pipelineNotes(node); } catch (error) { toast(error.message); return null; }
    if (made.text === null) { toast(`The notes of ${node.title} are a pipeline with a problem: ${made.problems[0]?.message || 'see the source'}.`); const { gotoSource } = await import('./panels.js'); gotoSource(node); return null; }
    // the roll shows what the pipeline makes, read-only (pseudo clip); its menu can bake the pipeline or turn it into plain notes
    return jam.openPianoRollLite(blockId, { pseudo: { id: blockId, title: node.title, params: {}, flags: ['lock'], tpl: 'midi-clip', parts: node.parts || [] }, text: `"${made.text}"`, ppq: 1_000_000, readOnly: true, derived: { block: blockId, pieces: editables(node.params.notes ?? '').length } });
  }
  if (/^\s*notes\s*\{/.test(node.params.notes ?? '')) { toast('This clip uses the expanded notes form; it is edited as text.'); const { gotoSource } = await import('./panels.js'); gotoSource(node); return null; }
  if (readOnly()) { toast('This song is read-only.'); return null; }
  let data;
  try { data = await api.post('/api/midi/clip-notes', { path: S.path, block: blockId }); } catch (error) { toast(error.message); return null; }
  const what = node.params.chords ? 'chords' : 'melody';
  const choice = await ask('Write the clip as notes?', `${node.title} is written with ${what}. The roll edits notes, so the clip is written out as ${data.count} notes (${data.bars} bars) and the ${what} line is removed. This is one edit: Undo brings the ${what} back.`, [
    { label: 'Cancel', value: false }, { label: `Write ${data.count} notes and open the roll`, value: true, primary: true },
  ]);
  if (!choice) return null;
  const ops = [{ type: 'setParameter', block: blockId, name: 'notes', value: `"${data.text}"` }];
  for (const name of ['chords', 'melody']) if (node.params[name] !== undefined) ops.push({ type: 'removeParameter', block: blockId, name });
  if (await edit(ops)) setTimeout(() => jam.openPianoRollLite(blockId, options), 50);
  return null;
}

/** The notes of a MIDI file as compact notes text in ticks of the file (a note over a bar line is cut at the line; channel, bank and program are kept). */
export function compactFromFile(data) {
  const bar = barBeats() * data.ppq, out = [];
  let tie = 0;
  for (const n of data.notes) {
    const first = Math.floor(n.start / bar), last = Math.floor((n.end - 1) / bar);
    const id = last > first ? tie++ : -1;     // the pieces of a note over a bar line share a tie id, so they play as one note
    for (let b = first; b <= last; b++) {
      const from = Math.max(n.start, b * bar) - b * bar, to = Math.min(n.end, (b + 1) * bar) - b * bar;
      if (to > from) out.push([b + 1, from, to - from, n.pitch, Math.max(1, n.velocity), n.channel, n.bank, n.program, ...(id >= 0 ? [id] : [])].join(':'));
    }
  }
  return out.join(' ');
}

/** Opens the read-only roll of a MIDI file block, with an action in the Jam menu that makes an editable copy as a midi-clip. */
export async function openMidiFileRoll(blockId) {
  const node = flat().nodes.find((n) => n.id === blockId);
  if (!node?.params.file) { toast('Choose a MIDI file first.'); return null; }
  let data;
  try { data = await api.get(`/api/midi/notes?file=${encodeURIComponent(node.params.file)}&track=${encodeURIComponent(node.params.track || 'all')}`); } catch (error) { toast(error.message); return null; }
  const jam = await import('./pianoroll-lite.js');
  return jam.openPianoRollLite(blockId, { pseudo: { id: blockId, title: node.title, params: {}, flags: ['lock'], tpl: 'midi-clip', parts: [] }, text: `"${compactFromFile(data)}"`, ppq: data.ppq, readOnly: true, file: data });
}

/** The first free block name starting with base (base, base-2, base-3 ...). */
export const freeName = (base) => {
  const names = new Set(flat().nodes.map((n) => n.id));
  if (!names.has(base)) return base;
  let i = 2;
  while (names.has(`${base}-${i}`)) i++;
  return `${base}-${i}`;
};
