// The sequencer workspace (plan fl-block FL04 to FL07): a full-screen editor for the .seq file of a `sequencer` block, in the spirit of FL Studio.
// A channel rack, the pattern grid (steps for drums, a piano roll for notes, a switch for loops), a playlist with clips and sections, and the lanes strip that says
// which channels each lane of the block carries. Everything is text: the .seq file (written by Apply and back, or Save) and the `lanes` parameter of the block.
// It is an advanced view, opened from a sequencer block; the node editor and the source stay the main ways to work. @ample:sequencer

import { api } from './api.js';
import { $, ask, el, showMenu, toast } from './ui.js';
import { withJamIcon } from './window-chrome.js';
import { notesIn, thumbRects } from '../lib/seq-notes.mjs';
import { activeChannels } from '../lib/seq-activity.mjs';
import { gmProgramName, parseGmProgram } from '../lib/gm-programs.mjs';
import { S, applyText, build, edit, flat, readOnly, refreshInputs, subscribe } from './store.js';
import { embedFile, extractToFile, readEmbedded, withEmbedded } from '../lib/embedded-seq.mjs';
import { lostAnything, lossText, restoreLost, sequenceLoss } from '../lib/seq-loss.mjs';
import { V, registerView, setView } from './views.js';
import { createSaveControl, beforeSave, typedProbe, refreshSaveControls } from './savecontrol.js';
import { openProjectFilePicker } from './filepicker.js';
import { playProjectFile, playTransient, stop as stopPlayback } from './player.js';
import { attachGmPicker } from './gm-picker.js';
import { dragHasFiles, draggedFile } from './dragfile.js';
import { uploadProjectFiles } from './filepicker.js';
import { unlockAudio, startLoopPlayer, stopLoopPlayer, setLoopRange, seekLoopPlayer, swapTake, state as loopState } from './loopplayer.js';
import { barAt, barOfClick, barSeconds, rangeFromDrag, rangeSeconds } from '../lib/loop-model.mjs';
import {
  TYPES, addChannel, addClip, addMarker, addPattern, addSection, addVariation, barsOfRow, check, deletePattern, duplicatePattern, formatLanes, formatSeq, lanesRenamed, lanesWithout, parseLanes, parseSeq,
  movePattern, removeChannel, removeClip, removeMarker, removeSection, renameChannel, renamePattern, rowsOf, setChannel, setLoop, setPattern, setRow, songBars, toggleStep, trackCount, updateClip, updateMarker, updateSection,
} from '../lib/seq-model.mjs';

/** PAT plays the chosen pattern again and again, SONG plays the playlist (what Play did before); remembered in this browser. */
const loadPlayMode = () => { try { return localStorage.getItem('ample.sq.playMode') === 'pattern' ? 'pattern' : 'song'; } catch { return 'song'; } };
const Q = { playMode: loadPlayMode(), blockId: null, embedded: false, path: '', seq: null, snapshot: '', original: '', undo: [], redo: [], pattern: null, clip: -1, barPx: 30, error: '', busy: false, range: null, cursor: 1, repeat: true, live: true, stale: true, builtTake: null, status: '', wroteLive: false, liveBusy: false, liveAgain: false };
const COLORS = ['#e8505b', '#f2994a', '#f2c94c', '#6fcf97', '#56ccf2', '#7c83fd', '#bb6bd9'];
const dirty = () => !!Q.seq && formatSeq(Q.seq) !== Q.snapshot;
const colorOf = (index) => COLORS[index % COLORS.length];

/** Opens the workspace for a sequencer block; it asks for a file first when the block has none. */
/** The line of the header of a block in the song text (0-based), for the edits that keep a sequence in the song. */
const headerLineOf = (blockId) => S.doc?.blocks.find((b) => b.name === blockId)?.line;

export async function openSequencer(blockId) {
  const node = flat().nodes.find((n) => n.id === blockId);
  if (!node || node.tpl !== 'sequencer') { toast('Choose a sequencer block.'); return; }
  let path = (node.params.file ?? '').replace(/^"|"$/g, '');
  // a sequence kept in the song itself is opened from there
  const embeddedText = headerLineOf(blockId) === undefined || S.moduleStack.length ? null : readEmbedded(S.doc.text, headerLineOf(blockId));
  if (embeddedText !== null) path = '';
  if (!path && embeddedText === null) {
    const name = el('input', { value: node.id, 'aria-label': 'Name of the sequence file' });
    if (!(await ask('A sequence', 'This block has no sequence yet. Make a file in sequences/ (it starts with a lead and a kick channel); you can keep it in the song instead from the sequencer, with Keep in the song.', [{ label: 'Create', value: true, primary: true }, { label: 'Cancel', value: false }], [name]))) return;
    const made = await api.post('/api/project/new', { kind: 'sequence', name: name.value.trim() });
    await edit([{ type: 'setParameter', block: node.id, name: 'file', value: made.path }]);
    path = made.path;
  }
  let text;
  if (embeddedText !== null) text = embeddedText;
  else try { text = (await api.get(`/api/file?path=${encodeURIComponent(path)}`)).text; } catch (error) { toast(error.message); return; }
  const parsed = parseSeq(text);
  Q.embedded = embeddedText !== null;
  Q.blockId = blockId; Q.path = Q.embedded ? '(kept in the song)' : path; Q.snapshot = text; Q.original = text; Q.undo = []; Q.redo = []; Q.clip = -1; Q.busy = false;
  Q.range = null; Q.cursor = 1; Q.stale = true; Q.builtTake = null; Q.status = ''; Q.wroteLive = false;
  Q.error = parsed.error ?? '';
  Q.seq = parsed.seq ?? { channels: [], patterns: [], clips: [], sections: [], markers: [], loop: null };
  Q.pattern = Q.seq.patterns[0]?.name ?? null;
  setView('seq');
  foldBottom();   // the playlist of the sequencer is the main screen: the Arrangement panel under it folds away while it is open
  requestAnimationFrame(fitIfTooWide);   // and a long song is zoomed out until the whole song is in view (a short one keeps the usual zoom)
}

/** The sequencer editor, when it is open: its block, whether the sequence is kept in the song, and whether it has changes that were not applied. Otherwise null. */
export const sequencerInfo = () => (V.mode === 'seq' && Q.seq ? { blockId: Q.blockId, embedded: Q.embedded, dirty: dirty() } : null);

/** What the text of a sequence would take away from the sequence in the editor, in words ('' when nothing). */
export function lossOfStaging(text) {
  const parsed = parseSeq(text);
  if (!parsed.seq || !Q.seq) return '';
  const loss = sequenceLoss(Q.seq, parsed.seq);
  return lostAnything(loss) ? lossText(loss) : '';
}

/** Koda's change goes into the open editor as one step (Undo takes it back); nothing is written until the person applies. Returns false when the text is not a sequence. */
export function stageSequence(text, keepMine = false) {
  const parsed = parseSeq(text);
  if (!parsed.seq) return false;
  if (keepMine && Q.seq) parsed.seq = restoreLost(Q.seq, parsed.seq);
  Q.pattern = Q.seq.patterns.some((p) => p.name === Q.pattern) ? Q.pattern : (parsed.seq.patterns[0]?.name ?? null);
  // an AMPLE Jam window of a row keeps the notes it was opened with: it is closed, so it is opened again with the new ones
  document.querySelectorAll('dialog.mls-panel[data-sequencer] .mls-close').forEach((button) => button.click());
  commit(parsed.seq);
  return true;
}

/** Closes the editor without applying what was changed in it (Koda's bigger change goes to the song). */
export function leaveSequencerDiscarding() {
  Q.seq = null; Q.snapshot = '';
  setView('blocks');
}

export function initSequencer() {
  registerView('seq', render);
  addEventListener('keydown', (event) => {
    if (V.mode !== 'seq' || event.code !== 'Space' || event.ctrlKey || event.metaKey || event.altKey) return;
    const target = event.target;
    if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable || target.tagName === 'BUTTON')) return;
    event.preventDefault(); event.stopPropagation();
    if (loopState().playing) stopNow(); else playNow().catch((error) => { setStatus(''); toast(error.message || String(error)); });
  }, true);
  addEventListener('keydown', (event) => {
    if (V.mode !== 'seq' || !Q.seq || event.altKey) return;
    const target = event.target;
    if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable)) return;
    if (target?.closest?.('.mls-panel')) return;   // AMPLE Jam of a row keeps its own undo
    const key = event.key.toLowerCase();
    const locked = readOnly() || !!node()?.flags.includes('lock');
    if ((event.ctrlKey || event.metaKey) && (key === 'z' || key === 'y')) {
      event.preventDefault(); event.stopPropagation();
      if (key === 'y' || event.shiftKey) redoEdit(); else undoEdit();
    } else if (!event.ctrlKey && !event.metaKey && (key === 'delete' || key === 'backspace') && Q.clip >= 0 && Q.clip < Q.seq.clips.length && !locked) {
      event.preventDefault(); event.stopPropagation();
      deleteChosenClip();
    }
  }, true);
  typedProbe(() => V.mode === 'seq' && dirty());
  beforeSave(async () => { if (V.mode === 'seq' && dirty()) await writeFile(); });
  subscribe(() => { if (V.mode === 'seq' && !Q.busy) { const lanes = $('#sq-lanes'); if (lanes) paintLanes(lanes); } });
}

async function writeFile() {
  const text = formatSeq(Q.seq);
  if (Q.embedded) { const line = headerLineOf(Q.blockId); if (line === undefined || !(await applyText(withEmbedded(S.doc.text, line, text)))) throw new Error('The sequence could not be written into the song.'); }
  else await api.put('/api/file', { path: Q.path, text });
  Q.snapshot = text;
  await refreshInputs().catch(() => {});
  refreshSaveControls();
}

function undoEdit() {
  if (!Q.undo.length) return;
  Q.redo.push(Q.seq); Q.seq = Q.undo.pop(); Q.clip = -1;
  render(); refreshSaveControls();
}

function redoEdit() {
  if (!Q.redo.length) return;
  Q.undo.push(Q.seq); Q.seq = Q.redo.pop(); Q.clip = -1;
  render(); refreshSaveControls();
}

/** Removes the clip that is chosen in the playlist (the one with the outline). */
function deleteChosenClip() {
  if (Q.clip < 0 || !Q.seq || Q.clip >= Q.seq.clips.length) return;
  const index = Q.clip;
  Q.clip = -1;
  commit(removeClip(Q.seq, index));
}

function commit(next) {
  Q.undo.push(Q.seq); Q.redo = [];
  Q.seq = next;
  Q.stale = true;
  render();
  refreshSaveControls();
  scheduleLive();
}

const node = () => flat().nodes.find((n) => n.id === Q.blockId);

/** Moves the sequence of the block into the song. The file stays where it is: only the block stops using it. */
async function embedSequence() {
  stopNow();
  const text = formatSeq(Q.seq), line = headerLineOf(Q.blockId);
  if (line === undefined) { toast('The block was not found in the song.'); return; }
  const doc = await applyText(embedFile(S.doc.text, line, text));
  if (!doc) return;
  const was = Q.path;
  Q.embedded = true; Q.path = '(kept in the song)'; Q.snapshot = text; Q.original = text; Q.wroteLive = false; Q.stale = true;
  render();
  toast(`The sequence is kept in the song now. ${was} is still there, and the block no longer uses it.`);
}

/** Writes the sequence out to a file of the project and lets the block use that file; the file is made new, never over another one without asking. */
async function extractSequence() {
  stopNow();
  const line = headerLineOf(Q.blockId);
  if (line === undefined) { toast('The block was not found in the song.'); return; }
  const input = el('input', { value: `sequences/${Q.blockId}.ampleseq`, 'aria-label': 'Path of the sequence file' });
  if (!(await ask('Extract to a file', 'The sequence is written to this file, and the block uses the file from now on.', [{ label: 'Extract', value: true, primary: true }, { label: 'Cancel', value: false }], [input]))) return;
  const path = input.value.trim().replace(/^\/+/, '');
  if (!path || !/\.(ampleseq|seq)$/i.test(path)) { toast('Give a path that ends with .ampleseq.'); return; }
  const exists = await api.get(`/api/file?path=${encodeURIComponent(path)}`).then(() => true, () => false);
  if (exists && !(await ask('File exists', `${path} is already there. Replace it with this sequence?`, [{ label: 'Replace', value: true, primary: true }, { label: 'Cancel', value: false }]))) return;
  const text = formatSeq(Q.seq);
  await api.put('/api/file', { path, text });
  const doc = await applyText(extractToFile(S.doc.text, line, path));
  if (!doc) return;
  Q.embedded = false; Q.path = path; Q.snapshot = text; Q.original = text; Q.wroteLive = false; Q.stale = true;
  render();
  toast(`The sequence is in ${path} now, and the block uses it.`);
}
const lanesNow = () => parseLanes((node()?.params.lanes ?? '').replace(/^"|"$/g, ''), Q.seq.channels);

async function setLanes(lanes) {
  const text = formatLanes(lanes);
  await edit([text ? { type: 'setParameter', block: Q.blockId, name: 'lanes', value: text } : { type: 'removeParameter', block: Q.blockId, name: 'lanes' }]);
}

/** On opening: when the song is wider than the playlist, zoom out until all of it is in view; it never zooms in (the Fit button does that too). */
function fitIfTooWide() {
  const scroll = $('#sq-playlist .sqscroll');
  if (!scroll || !Q.seq) return;
  const total = Math.max(16, songBars(Q.seq) + 4), px = Math.floor((scroll.clientWidth - 4) / total);
  if (px >= 4 && px < Q.barPx) { Q.barPx = px; render(); }
}
let bottomWasOpen = null;
function foldBottom() { const bottom = document.querySelector('#bottom'); if (!bottom) return; if (bottomWasOpen === null) bottomWasOpen = !bottom.classList.contains('min'); bottom.classList.add('min'); const toggle = document.querySelector('#b-min'); if (toggle) toggle.textContent = '▴'; }
function unfoldBottom() { if (bottomWasOpen === null) return; const bottom = document.querySelector('#bottom'); if (bottom && bottomWasOpen) { bottom.classList.remove('min'); const toggle = document.querySelector('#b-min'); if (toggle) toggle.textContent = '▾'; } bottomWasOpen = null; }
async function leave(applyIt) {
  if (applyIt && Q.error) { toast('Fix the file first: ' + Q.error); return; }
  if (applyIt) { if (dirty()) await writeFile(); } else if (dirty() && !(await ask('Undo and back', 'Throw away the changes made in this editor?', [{ label: 'Keep editing', value: false }, { label: 'Undo and back', value: true, primary: true }]))) return;
  stopLoopPlayer();
  if (!applyIt && Q.wroteLive) {
    if (Q.embedded) { const line = headerLineOf(Q.blockId); if (line !== undefined) await applyText(withEmbedded(S.doc.text, line, Q.original)); } else await api.put('/api/file', { path: Q.path, text: Q.original });
    Q.wroteLive = false; refreshInputs().catch(() => {});
  }
  unfoldBottom();
  setView('blocks');
}

/** The scroll places of the workspace: they survive the redraw that follows every change. */
const SCROLLERS = ['.sqbody', '#sq-rack', '#sq-patterns', '#sq-playlist', '.sqscroll'];
/** On a desktop the rack and the rows of the pattern are one grid (the style sheet makes #sq-rack, .sqmain and #sq-patterns display: contents): the card of a channel and its row of the chosen pattern sit on one line, like the channel rack of FL Studio. The DOM stays what it was. */
function placeRack(rack, main) {
  const put = (node, row, column) => { if (node) { node.style.gridRow = String(row); node.style.gridColumn = String(column); } };
  put(rack.querySelector('h4'), 1, 1); put(rack.querySelector('#sq-add-channel'), 2, 1);
  const box = main.querySelector('#sq-patterns');
  if (box) { put(box.firstElementChild, 1, 2); put(box.querySelector('#sq-pattern-head'), 2, 2); put(box.querySelector(':scope > p.note'), 2, 2); }
  const cards = [...rack.querySelectorAll(':scope > .sqchannel')], rows = box ? [...box.querySelectorAll(':scope > .sqgridrow')] : [];
  cards.forEach((card, i) => put(card, 3 + i, 1));
  rows.forEach((row, i) => put(row, 3 + i, 2));
  put(rack.querySelector(':scope > p.note'), 3 + cards.length, 1);
  for (const wide of main.querySelectorAll(':scope > :not(#sq-patterns)')) wide.style.gridColumn = '1 / -1';   // the playlist, the lanes and the messages use the whole width
}
function scrollPlaces(root) {
  const places = SCROLLERS.map((selector) => [selector, root.querySelector(selector)?.scrollTop ?? 0, root.querySelector(selector)?.scrollLeft ?? 0]);
  return { places, self: [root.scrollTop, root.scrollLeft], parent: root.parentElement ? [root.parentElement.scrollTop, root.parentElement.scrollLeft] : null };
}
function restoreScroll(root, saved) {
  const apply = () => {
    root.scrollTop = saved.self[0]; root.scrollLeft = saved.self[1];
    if (saved.parent && root.parentElement) { root.parentElement.scrollTop = saved.parent[0]; root.parentElement.scrollLeft = saved.parent[1]; }
    for (const [selector, top, left] of saved.places) { const node = root.querySelector(selector); if (node) { node.scrollTop = top; node.scrollLeft = left; } }
  };
  apply();
  requestAnimationFrame(apply);
}

/** One line that says what the transport does now, and what to do next. */
function hintText() {
  const playing = loopState().playing;
  if (Q.playMode === 'pattern' && !Q.range) return `${playing ? '▶ ' : ''}PAT: the pattern ${Q.pattern ?? ''} repeats, from its first place in the playlist. SONG plays the whole song. ${Q.live ? 'A change you make while it plays comes in at the next loop.' : 'Changes are heard the next time you press Play.'} Space plays and stops.`;
  const range = Q.range ? `bars ${Q.range.at} to ${Q.range.at + Q.range.bars - 1}` : null;
  const where = range ? `The chosen part (${range}) ${Q.repeat ? 'repeats' : 'plays once'}` : `The whole song ${Q.repeat ? 'repeats' : 'plays once'}`;
  const change = Q.live ? 'A change you make while it plays comes in at the next loop.' : 'Changes are heard the next time you press Play.';
  const choose = range ? 'Drag over the bar numbers above the playlist to choose other bars (✕ clears the choice).' : 'Drag over the bar numbers above the playlist to choose bars to repeat; click a number to start from there.';
  return `${playing ? '▶ ' : ''}${where}. ${choose} ${change} Space plays and stops.`;
}

let hintOpen = false;   // the explanation of playing and looping is behind the (i) button until it is asked for
function render() {
  const root = $('#sq');
  if (!root || !Q.seq) return;
  const savedScroll = scrollPlaces(root);
  root.innerHTML = '';
  const locked = readOnly() || !!node()?.flags.includes('lock');
  const undoButton = el('button', { id: 'sq-undo', textContent: `↶ Undo (${Q.undo.length})`, title: 'Undo (Ctrl+Z)', disabled: !Q.undo.length, onclick: () => undoEdit() });
  const redoButton = el('button', { id: 'sq-redo', textContent: `↷ Redo (${Q.redo.length})`, title: 'Redo (Ctrl+Y)', disabled: !Q.redo.length, onclick: () => redoEdit() });
  const deleteClipButton = el('button', { id: 'sq-clip-delete', textContent: '✕ Delete clip', disabled: locked || Q.clip < 0 || Q.clip >= Q.seq.clips.length, title: 'Remove the chosen clip of the playlist (the one with the outline): Delete does the same, Ctrl+Z takes it back', onclick: () => deleteChosenClip() });
  const bar = el('div', { className: 'bmbar' }, [
    el('button', { id: 'sq-undoback', textContent: '← Undo and back', title: 'Throw away the changes made in this editor and go back', onclick: () => leave(false) }),
    el('button', { id: 'sq-apply', className: 'primary', textContent: 'Apply and back →', title: 'Write the sequence file and go back', onclick: () => leave(true) }),
    el('b', { textContent: `Sequencer: ${node()?.title ?? ''}  ${Q.path}` }),
    el('button', { id: 'sq-ai', textContent: 'AI ▾', title: 'Ask Codex about this sequence (nothing changes until you accept a proposal)', onclick: (event) => { const r = event.currentTarget.getBoundingClientRect(); import('./ai.js').then((m) => m.openAiMenu(r.left, r.bottom + 2, { context: 'sequence', path: Q.embedded ? S.path : Q.path, song: S.path, block: Q.blockId })).catch((error) => toast(error.message || String(error))); } }),
    undoButton, redoButton, deleteClipButton,
    el('button', { id: Q.embedded ? 'sq-extract' : 'sq-embed', textContent: Q.embedded ? '⤒ Extract to a file...' : '⤓ Keep in the song', title: Q.embedded ? 'Write this sequence to a file of the project and use that file instead' : 'Keep this sequence in the song itself, in the block (the file stays where it is)', disabled: locked, onclick: () => (Q.embedded ? extractSequence() : embedSequence()).catch((error) => toast(error.message || String(error))) }),
    el('span', { className: 'sqtransport' }, [
      el('span', { className: 'sqmode', role: 'group', 'aria-label': 'Play mode' }, [
        el('button', { id: 'sq-mode-pat', textContent: 'PAT', className: Q.playMode === 'pattern' ? 'on' : '', 'aria-pressed': String(Q.playMode === 'pattern'), title: 'Pattern: the chosen pattern repeats, from its first place in the playlist', onclick: () => setPlayMode('pattern') }),
        el('button', { id: 'sq-mode-song', textContent: 'SONG', className: Q.playMode === 'song' ? 'on' : '', 'aria-pressed': String(Q.playMode === 'song'), title: 'Song: the playlist plays, the whole song or the bars chosen on the ruler', onclick: () => setPlayMode('song') }),
      ]),
      el('button', { id: 'sq-play', className: 'primary', textContent: '▶ Play', title: Q.stale || !Q.builtTake ? 'Build what changed, then play (Space). It starts at the first of the chosen bars, or at the bar you clicked on the ruler, or at bar 1.' : 'Play (Space). It starts at the first of the chosen bars, or at the bar you clicked on the ruler, or at bar 1.', onclick: () => playNow().catch((e) => { setStatus(''); toast(e.message || String(e)); }) }),
      el('button', { id: 'sq-stop', textContent: '■ Stop', title: 'Stop playing', disabled: !loopState().playing, onclick: () => stopNow() }),
      el('button', { id: 'sq-repeat', className: Q.repeat ? 'on' : '', textContent: '⟳ Loop', 'aria-pressed': String(Q.repeat), title: 'On: when it reaches the end it starts again (the chosen bars, or the whole song when none are chosen). Off: it plays once and stops.', onclick: () => { Q.repeat = !Q.repeat; setLoopRange(loopSeconds(), Q.repeat).catch(() => {}); render(); } }),
      el('button', { id: 'sq-live', className: Q.live ? 'on' : '', textContent: '● Live', 'aria-pressed': String(Q.live), title: 'On: a change you make while it plays is built in the background and you hear it the next time the loop comes round. Off: changes are heard the next time you press Play.', onclick: () => { Q.live = !Q.live; render(); } }),
      el('span', { id: 'sq-pos', className: 'sqpos', textContent: `bar ${Q.cursor}` }),
      ...(Q.range ? [el('span', { id: 'sq-range', className: 'sqrangelabel', textContent: `repeat bars ${Q.range.at} to ${Q.range.at + Q.range.bars - 1}` }), el('button', { id: 'sq-range-clear', textContent: '✕', title: 'Do not repeat a part: repeat the whole song', onclick: () => chooseRange(null).catch(() => {}) })] : []),
      el('span', { id: 'sq-status', className: 'sqstatus', textContent: Q.status }),
      el('button', { id: 'sq-hint-toggle', className: 'sqinfo', textContent: 'ⓘ', title: 'How playing and looping work', 'aria-label': 'Explain playing and looping', 'aria-expanded': String(hintOpen), onclick: () => { hintOpen = !hintOpen; render(); } }),
    ]),
    el('span', { className: 'sp' }), createSaveControl(),
  ]);
  root.append(bar);
  root.append(el('p', { id: 'sq-hint', className: 'sqhint', hidden: !hintOpen, textContent: hintText() }));
  if (Q.error) root.append(el('p', { className: 'seqerr', id: 'sq-error', textContent: `The file cannot be read as a sequence: ${Q.error}. Fix it in the source (Edit as text in the project panel) and open the sequencer again.` }));
  const body = el('div', { className: 'sqbody' });
  const rack = el('div', { className: 'sqrack', id: 'sq-rack' });
  const main = el('div', { className: 'sqmain' });
  body.append(rack, main);
  root.append(body);
  paintRack(rack, locked);
  paintPatterns(main, locked);
  paintPlaylist(main, locked);
  const lanes = el('div', { className: 'sqlanes', id: 'sq-lanes' });
  main.append(lanes);
  paintLanes(lanes);
  placeRack(rack, main);
  startTick();
  restoreScroll(root, savedScroll);
  const problems = check(Q.seq);
  if (problems.length) main.append(el('p', { className: 'seqerr', textContent: problems.join('. ') + '.' }));
}

// ---------- playing: a repeat range, a playhead and live changes (plan sequencer-live) ----------
const tempo = () => S.doc?.project?.tempo ?? 120;
/** The bars that PAT repeats: the first place of the chosen pattern in the playlist (null when it is in no clip). */
const patternRange = () => { const clip = Q.seq?.clips.find((c) => c.pattern === Q.pattern); return clip ? { at: clip.at, bars: clip.bars } : null; };
/** The part that repeats: the bars chosen on the ruler, else (in PAT) the chosen pattern, else the whole song. */
const effectiveRange = () => Q.range || (Q.playMode === 'pattern' ? patternRange() : null);
const loopSeconds = () => { const range = effectiveRange(); return range ? rangeSeconds(range, tempo(), Infinity) : null; };
function setPlayMode(mode) {
  if (Q.playMode === mode) return;
  Q.playMode = mode;
  try { localStorage.setItem('ample.sq.playMode', mode); } catch { /* storage may be blocked */ }
  render();
  if (loopState().playing) playNow().catch((e) => toast(e.message || String(e)));   // the music follows the switch
}

/** Makes sure there is a take of the block that matches the file: writes the file and builds the block when something changed. */
async function ensureTake() {
  while (Q.liveBusy) await new Promise((r) => setTimeout(r, 100));   // a background build of the same block finishes first
  if (Q.builtTake && !Q.stale) return Q.builtTake;
  Q.busy = true;
  try {
    await writeFile();
    await build(Q.blockId);
    const take = S.build.takes?.[Q.blockId];
    if (!take) throw new Error('The block could not be built. See the build log.');
    Q.builtTake = take; Q.stale = false;
    return take;
  } finally { Q.busy = false; }
}

/** Plays from the start of the repeat range, else from the cursor (the bar last clicked on the ruler). */
async function playNow() {
  unlockAudio();
  if (Q.error) { toast('Fix the file first: ' + Q.error); return; }
  if (Q.playMode === 'pattern' && !effectiveRange()) { toast(`Put the pattern "${Q.pattern ?? ''}" in the playlist first: PAT plays it from its first place there (SONG plays the whole song).`); return; }
  setStatus(Q.stale || !Q.builtTake ? 'building...' : '');
  const take = await ensureTake();
  const range = effectiveRange();
  const from = range ? (range.at - 1) * barSeconds(tempo()) : (Q.cursor - 1) * barSeconds(tempo());
  await startLoopPlayer(take, { from, loop: loopSeconds(), looping: Q.repeat });
  setStatus('');
}

async function stopNow() { stopLoopPlayer(); setStatus(''); }

/** Chooses (or clears) the stretch that repeats; while it plays the change is heard at once. */
async function chooseRange(range, { play = false } = {}) {
  Q.range = range;
  if (range) Q.cursor = range.at;
  render();
  if (play) await playNow();
  else if (loopState().playing) await setLoopRange(loopSeconds(), Q.repeat);
}

const repeatRange = (range) => { Q.repeat = true; chooseRange(range, { play: true }).catch((e) => toast(e.message || String(e))); };

function setStatus(text) {
  Q.status = text;
  const node = $('#sq-status');
  if (node) node.textContent = text;
}

/** A change while the music plays: build it in the background and let the player bring it in when the loop comes round. */
let liveTimer = 0;
function scheduleLive() {
  clearTimeout(liveTimer);
  if (!Q.live || !loopState().playing) return;
  liveTimer = setTimeout(() => liveSwap().catch((e) => { setStatus('the new version could not be built'); toast(e.message || String(e)); }), 700);
}

async function liveSwap() {
  if (Q.liveBusy) { Q.liveAgain = true; return; }
  Q.liveBusy = true;
  try {
    do {
      Q.liveAgain = false;
      if (!loopState().playing || Q.error) return;
      setStatus('building the change...');
      await writeFile();
      Q.wroteLive = true;
      await build(Q.blockId);
      const take = S.build.takes?.[Q.blockId];
      if (!take) { setStatus('the change could not be built'); return; }
      Q.builtTake = take; Q.stale = false;
      if (Q.liveAgain) continue;
      await swapTake(take);
      setStatus('the change plays when the loop comes round');
    } while (Q.liveAgain);
  } finally { Q.liveBusy = false; }
}

let tickFrame = 0;
function startTick() {
  if (tickFrame) return;
  const frame = () => {
    if (V.mode !== 'seq') { tickFrame = 0; return; }
    paintPlayhead();
    tickFrame = requestAnimationFrame(frame);
  };
  tickFrame = requestAnimationFrame(frame);
}

/** The light next to a channel follows what plays at the playhead (a note, a drum step, a loop). */
function paintLights(st) {
  const lights = document.querySelectorAll('#sq-rack .sqlight');
  if (!lights.length) return;
  const on = st.playing ? activeChannels(Q.seq, barAt(st.position, tempo())) : new Set();
  lights.forEach((light) => light.classList.toggle('on', on.has(light.dataset.channel)));
}
function paintPlayhead() {
  const st = loopState();
  paintLights(st);
  const hint = $('#sq-hint');
  if (hint) { const text = hintText(); if (hint.textContent !== text) hint.textContent = text; }
  const head = $('#sq-playhead');
  if (head) { head.style.display = st.playing ? '' : 'none'; head.style.left = `${(barAt(st.position, tempo()) - 1) * Q.barPx}px`; }
  const pos = $('#sq-pos');
  if (pos) pos.textContent = st.playing ? `bar ${barAt(st.position, tempo()).toFixed(1)}` : `bar ${Q.cursor}`;
  const stop = $('#sq-stop');
  if (stop) stop.disabled = !st.playing;
  if (st.playing && st.pending) { const status = $('#sq-status'); if (status && !status.textContent) status.textContent = 'the change plays when the loop comes round'; }
  if (!st.pending && Q.status === 'the change plays when the loop comes round' && st.playing) setStatus('');
}

// ---------- the channel rack ----------
let hearing = 0;
/** Plays a short phrase of the instrument of a General MIDI channel (its soundfont, bank and program), so it can be chosen by ear. */
const stopHearing = () => { hearing++; stopPlayback(); };
async function hearGm(c, programOverride) {
  const mine = ++hearing;
  const program = programOverride ?? Number(c.settings.program ?? 0);
  const low = (program >= 32 && program <= 39) || /bass/i.test(c.name);   // a bass is tried an octave and a half lower
  const base = low ? 36 : 60, ppq = 480;
  const notes = [0, 4, 7, 12].map((interval, index) => ({ bar: 1, tick: index * 240, ticks: index === 3 ? 960 : 240, ppq, pitch: base + interval, velocity: 90 }));
  try {
    const result = await api.gmAudition({ file: c.settings.file || 'pack:musescore-general', bank: Number(c.settings.bank ?? 0), program, channel: 0, gain: 0, limiter: 'soft', notes });
    if (mine === hearing) await playTransient(result.blob);
  } catch (error) { if (mine === hearing) toast(error.message || String(error)); }
}

function baseName(path) { return String(path || '').split('/').pop() || '(none)'; }

/**
 * Sounds dropped on the channels: on a channel they replace its sound (the sample of a drum, the file of a loop, the sample set of a notes channel when a folder is dropped),
 * on the empty part of the rack, or below the channels, they make a new channel. A file from the project panel or from the computer (it is uploaded first) can be dropped;
 * an MP3 is turned into a WAV first.
 */
const AUDIO = /\.(wav|mp3|flac|ogg|aif|aiff)$/i;
async function droppedSounds(event) {
  const own = draggedFile();
  if (own) return [{ path: own.path, isDir: !!own.isDir }];
  const files = [...(event.dataTransfer?.files || [])];
  if (!files.length) return [];
  const done = await uploadProjectFiles(files, 'samples', () => {});
  return done.filter(Boolean).map((result) => ({ path: result.converted || result.path, isDir: false }));
}

function dropOnChannel(host, row, channel, locked) {
  row.ondragover = (event) => { if (locked || !(draggedFile() || dragHasFiles(event))) return; event.preventDefault(); event.stopPropagation(); event.dataTransfer.dropEffect = 'copy'; row.classList.add('drop-ok'); };
  row.ondragleave = () => row.classList.remove('drop-ok');
  row.ondrop = async (event) => {
    row.classList.remove('drop-ok');
    if (locked) return;
    event.preventDefault(); event.stopPropagation();
    try {
      const [sound] = await droppedSounds(event);
      if (!sound) return;
      const key = channel.type === 'drum' ? 'sample' : channel.type === 'loop' ? 'file' : channel.type === 'gm' ? 'file' : 'set';
      if (key === 'set' && !sound.isDir) { toast(`${channel.name} plays a set of samples (a folder): drop a folder on it. Drop a sound on the empty part of the rack to make a channel of it.`); return; }
      if (key !== 'set' && sound.isDir) { toast(`${channel.name} plays one file: drop a sound file on it.`); return; }
      if (key === 'file' && channel.type === 'gm' ? !/\.sf2$/i.test(sound.path) : key !== 'set' && !AUDIO.test(sound.path)) { toast(channel.type === 'gm' ? 'A General MIDI channel plays a SoundFont (.sf2).' : 'That is not a sound file.'); return; }
      commit(setChannel(Q.seq, channel.name, { [key]: sound.path }));
      toast(`${channel.name} plays ${sound.path} now.`);
    } catch (error) { toast(error.message || String(error)); }
  };
}

function dropOnRack(host, locked) {
  host.ondragover = (event) => { if (locked || !(draggedFile() || dragHasFiles(event))) return; event.preventDefault(); event.dataTransfer.dropEffect = 'copy'; host.classList.add('drop-ok'); };
  host.ondragleave = (event) => { if (!host.contains(event.relatedTarget)) host.classList.remove('drop-ok'); };
  host.ondrop = async (event) => {
    host.classList.remove('drop-ok');
    if (locked) return;
    event.preventDefault();
    try {
      let next = Q.seq;
      for (const sound of await droppedSounds(event)) {
        const base = sound.path.split('/').pop().replace(/\.[A-Za-z0-9]+$/, '');
        if (sound.isDir) { const made = addChannel(next, base, 'notes', { set: sound.path }); next = setChannel(made.seq, made.name, { color: colorOf(made.seq.channels.length - 1) }); }
        else if (AUDIO.test(sound.path)) { const made = addChannel(next, base, 'drum', { sample: sound.path }); next = setChannel(made.seq, made.name, { color: colorOf(made.seq.channels.length - 1) }); }
      }
      if (next !== Q.seq) { commit(next); toast('A channel was made of what was dropped.'); }
    } catch (error) { toast(error.message || String(error)); }
  };
}

function paintRack(host, locked) {
  dropOnRack(host, locked);
  const add = el('select', { id: 'sq-add-channel', disabled: locked, 'aria-label': 'Add a channel', title: 'Add a channel of a type' }, [el('option', { value: '', textContent: '+ channel...' }), ...TYPES.map((t) => el('option', { value: t, textContent: t }))]);
  add.onchange = () => { if (!add.value) return; const type = add.value; const r = addChannel(Q.seq, type, type, type === 'loop' ? { bars: '2' } : {}); commit(setChannel(r.seq, r.name, { color: colorOf(r.seq.channels.length - 1) })); };
  host.append(el('h4', { textContent: 'Channels', style: 'padding:0 0 4px' }), add);
  Q.seq.channels.forEach((c, index) => {
    const row = el('div', { className: 'sqchannel' });
    row.dataset.channel = c.name;
    dropOnChannel(host, row, c, locked);
    const color = el('input', { type: 'color', className: 'sqcolor', value: /^#[0-9a-f]{6}$/i.test(c.settings.color ?? '') ? c.settings.color : colorOf(index), disabled: locked, 'aria-label': `Colour of ${c.name}` });
    color.onchange = () => commit(setChannel(Q.seq, c.name, { color: color.value }));
    const name = el('button', { className: 'sqname', textContent: c.name, title: 'Rename this channel', disabled: locked, onclick: async () => { const input = el('input', { value: c.name }); if (!(await ask('Rename channel', 'Letters, digits, - and _. The lanes follow.', [{ label: 'OK', value: true, primary: true }, { label: 'Cancel', value: false }], [input]))) return; const next = renameChannel(Q.seq, c.name, input.value.trim()); if (!next) { toast('That name is taken or not allowed.'); return; } await setLanes(lanesRenamed(lanesNow(), c.name, input.value.trim())); commit(next); } });
    const type = el('select', { disabled: locked, 'aria-label': `Type of ${c.name}` }, TYPES.map((t) => el('option', { value: t, textContent: t, selected: t === c.type })));
    type.onchange = () => commit(setChannel(Q.seq, c.name, { type: type.value }));
    const toggle = (key, label, title) => el('button', { className: `sqtoggle${c.settings[key] === 'on' ? ' on' : ''}`, textContent: label, title, disabled: locked, 'aria-pressed': String(c.settings[key] === 'on'), onclick: () => commit(setChannel(Q.seq, c.name, { [key]: c.settings[key] === 'on' ? null : 'on' })) });
    const number = (key, label, min, max, fallback) => { const input = el('input', { type: 'number', className: 'seqn', value: c.settings[key] ?? fallback, min, max, title: label, disabled: locked, 'aria-label': `${label} of ${c.name}` }); input.onchange = () => commit(setChannel(Q.seq, c.name, { [key]: String(Math.max(min, Math.min(max, Math.round(Number(input.value) || 0)))) })); return input; };
    row.append(el('span', { className: 'sqlight', 'data-channel': c.name, title: 'Lights up while this channel sounds', 'aria-hidden': 'true' }), color, name, type, toggle('mute', 'M', `Mute ${c.name}`), toggle('solo', 'S', `Solo ${c.name}`), number('vol', 'Level in dB', -60, 12, 0), number('pan', 'Pan', -32, 32, 0),
      el('button', { className: 'seqx', textContent: '✕', title: `Remove ${c.name} (its rows go too)`, disabled: locked, onclick: async () => { await setLanes(lanesWithout(lanesNow(), c.name)); commit(removeChannel(Q.seq, c.name)); } }));
    const details = el('div', { className: 'sqdetails' });
    // the General MIDI program: type a number or a name (the browser suggests the names), the name of the chosen instrument is shown under the row
    const gmName = el('span', { className: 'sqgmname' });
    const showGmName = () => { const program = Number(c.settings.program ?? 0), bank = Number(c.settings.bank ?? 0); gmName.textContent = bank === 0 && gmProgramName(program) ? `♪ ${gmProgramName(program)} (GM ${program})` : ''; gmName.hidden = !gmName.textContent; };
    const programField = () => {
      const input = el('input', { type: 'text', className: 'seqn', autocomplete: 'off', inputMode: 'text', value: String(c.settings.program ?? 0), title: 'Program (0 to 127): type a number or an instrument name', disabled: locked, 'aria-label': `Program of ${c.name}` });
      if (!locked) attachGmPicker(input, { hear: (program) => hearGm(c, program), stopHear: stopHearing, pick: (program) => commit(setChannel(Q.seq, c.name, { program: String(program) })) });
      input.onchange = () => { const program = parseGmProgram(input.value); if (program === null) { input.value = String(c.settings.program ?? 0); toast('Not a General MIDI program: type 0 to 127 or an instrument name.'); return; } commit(setChannel(Q.seq, c.name, { program: String(program) })); };
      return input;
    };
    const pick = async (key, label, mode, accept) => { const chosen = await openProjectFilePicker(c.settings[key] ?? '', accept, mode); if (chosen !== undefined) commit(setChannel(Q.seq, c.name, { [key]: chosen })); };
    if (c.type === 'drum') details.append(el('button', { textContent: `sample: ${baseName(c.settings.sample)}`, title: c.settings.sample || 'Choose a WAV file of the project', disabled: locked, onclick: () => pick('sample', 'sample', 'file', ['.wav']) }), el('button', { className: 'sqhear', textContent: '▶', title: 'Hear this sample', 'aria-label': `Hear ${c.name}`, disabled: !c.settings.sample, onclick: () => playProjectFile(c.settings.sample).catch((error) => toast(error.message || String(error))) }));
    else if (c.type === 'loop') details.append(el('button', { textContent: `file: ${baseName(c.settings.file)}`, title: c.settings.file || 'Choose a loop', disabled: locked, onclick: () => pick('file', 'file', 'file', ['.wav']) }), number('bars', 'Bars of the loop', 1, 64, 1));
    else if (c.type === 'guitar') {
      const voicing = el('select', { disabled: locked, 'aria-label': `Voicing of ${c.name}` }, ['open', 'triad', 'power'].map((v) => el('option', { value: v, textContent: v, selected: v === (c.settings.voicing ?? 'open') })));
      voicing.onchange = () => commit(setChannel(Q.seq, c.name, { voicing: voicing.value }));
      details.append(el('button', { textContent: `set: ${c.settings.set ? baseName(c.settings.set) : '(choose)'}`, title: 'A folder of guitar notes (e2.wav, a2.wav, ...)', disabled: locked, onclick: async () => { const chosen = await openProjectFilePicker(c.settings.set ?? '', [], 'folder'); if (chosen !== undefined) commit(setChannel(Q.seq, c.name, { set: chosen })); } }), voicing, number('stagger', 'Stagger in ms', 0, 80, 22));
    } else if (c.type === 'gm') {
      details.append(el('button', { textContent: `soundfont: ${c.settings.file ? baseName(c.settings.file.replace(/^pack:/, '')) : 'General MIDI pack'}`, title: c.settings.file || 'Choose a SoundFont of the project (the General MIDI pack is the default)', disabled: locked, onclick: () => pick('file', 'file', 'file', ['.sf2']) }), programField(), number('bank', 'Bank', 0, 127, 0), el('button', { className: 'sqhear', textContent: '▶', title: 'Hear this instrument: a short phrase with the soundfont, bank and program that are chosen', 'aria-label': `Hear ${c.name}`, onclick: () => hearGm(c) }), gmName);
      showGmName();
    } else {
      const wave = el('select', { disabled: locked || !!c.settings.set, 'aria-label': `Tone of ${c.name}` }, ['square', 'triangle', 'saw'].map((w) => el('option', { value: w, textContent: w, selected: w === (c.settings.wave ?? (c.type === 'bass' ? 'saw' : c.type === 'piano' ? 'triangle' : 'square')) })));
      wave.onchange = () => commit(setChannel(Q.seq, c.name, { wave: wave.value }));
      details.append(wave, el('button', { textContent: `set: ${c.settings.set ? baseName(c.settings.set) : 'built-in tone'}`, title: 'Play a sample set (a folder of notes) instead of the built-in tone', disabled: locked, onclick: async () => { const chosen = await openProjectFilePicker(c.settings.set ?? '', [], 'folder'); if (chosen !== undefined) commit(setChannel(Q.seq, c.name, { set: chosen })); } }), number('shift', 'Shift in semitones', -48, 48, c.type === 'bass' ? -12 : 0));
    }
    row.append(details);
    host.append(row);
  });
  if (!Q.seq.channels.length) host.append(el('p', { className: 'note', textContent: 'No channels yet. Add a drum channel with a sample, a notes channel for a melody or a loop, or drop a sound here.' }));
}

// ---------- the patterns ----------
/** A small picture of the notes of a row: one bar per note, the high ones at the top. */
function thumbOf(text, bars, colour) {
  const NS = 'http://www.w3.org/2000/svg', svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('class', 'sqthumb'); svg.setAttribute('viewBox', '0 0 100 24'); svg.setAttribute('preserveAspectRatio', 'none'); svg.setAttribute('aria-hidden', 'true');
  for (const r of thumbRects(text, bars)) {
    const rect = document.createElementNS(NS, 'rect');
    rect.setAttribute('x', String(r.x * 100)); rect.setAttribute('y', String(1 + r.y * 19)); rect.setAttribute('width', String(Math.max(0.8, r.w * 100))); rect.setAttribute('height', '3');
    rect.setAttribute('fill', colour || 'currentColor'); rect.setAttribute('opacity', String(0.4 + Math.min(127, r.velocity) / 127 * 0.6));
    svg.append(rect);
  }
  return el('span', { className: 'sqthumbwrap' }, [svg]);
}
let draggingPattern = null;   // the name of the pattern tab that is being dragged (to another place in the row, or onto a track of the playlist)
/** The menu of a pattern tab: the same actions as the buttons beside the tabs, which it presses. */
function patternMenu(event, p, locked) {
  const press = (id) => () => { Q.pattern = p.name; render(); document.getElementById(id)?.click(); };
  showMenu(event.clientX, event.clientY, [
    { head: p.name },
    ...(locked ? [] : [{ label: 'Rename...', fn: press('sq-pattern-rename') }, { label: 'Duplicate', fn: press('sq-pattern-copy') }, { label: 'Variation', fn: press('sq-pattern-variation') }]),
    { label: 'Play pattern', fn: press('sq-pattern-play') },
    ...(locked ? [] : ['-', { label: 'Delete', danger: true, fn: press('sq-pattern-delete') }]),
  ]);
}
const isNoteChannel = (c) => c.type !== 'drum' && c.type !== 'loop' && c.type !== 'guitar';
/** How many notes a pattern has in all its rows of notes. */
const patternNotes = (p) => { const rows = rowsOf(Q.seq, p); return Q.seq.channels.filter(isNoteChannel).reduce((sum, c) => sum + notesIn(rows[c.name] ?? ''), 0); };
const patternColour = (p, i) => (/^#[0-9a-f]{6}$/i.test(p.color) ? p.color : colorOf(i));
const plural = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;
function paintPatterns(host, locked) {
  const box = el('div', { className: 'sqpatterns', id: 'sq-patterns' });
  const tabs = el('div', { className: 'sqtabs', role: 'tablist' });
  Q.seq.patterns.forEach((p, i) => {
    const tab = el('button', { className: `sqtab${p.name === Q.pattern ? ' on' : ''}`, textContent: p.name, role: 'tab', 'aria-selected': String(p.name === Q.pattern), onclick: () => { if (Q.pattern === p.name) return; Q.pattern = p.name; render(); if (Q.playMode === 'pattern' && loopState().playing) playNow().catch((e) => toast(e.message || String(e))); } });
    tab.dataset.pattern = p.name;
    tab.addEventListener('contextmenu', (event) => { event.preventDefault(); patternMenu(event, p, locked); });
    if (!locked) {
      tab.addEventListener('dblclick', () => { Q.pattern = p.name; document.getElementById('sq-pattern-rename')?.click(); });
      tab.draggable = true;
      tab.addEventListener('dragstart', (event) => { draggingPattern = p.name; event.dataTransfer.effectAllowed = 'copyMove'; event.dataTransfer.setData('text/plain', p.name); });
      tab.addEventListener('dragend', () => { draggingPattern = null; });
      tab.addEventListener('dragover', (event) => { if (!draggingPattern || draggingPattern === p.name) return; event.preventDefault(); event.dataTransfer.dropEffect = 'move'; });
      tab.addEventListener('drop', (event) => {
        if (!draggingPattern) return;
        event.preventDefault();
        const moved = movePattern(Q.seq, draggingPattern, i);
        draggingPattern = null;
        if (moved) commit(moved); else toast('A variation stays after the pattern it is made from.');
      });
    }
    const made = patternNotes(p);
    tab.dataset.count = String(made);   // shown behind the name by the style sheet, so the text of the tab stays its name
    tab.title = `${p.name} · ${plural(p.bars, 'bar')} · ${plural(made, 'note')}`;
    tab.setAttribute('aria-label', p.name);
    tab.style.setProperty('--pc', patternColour(p, i));
    tab.style.borderBottomColor = p.color || colorOf(i);
    tabs.append(tab);
  });
  const askName = async (title, start) => { const input = el('input', { value: start }); return (await ask(title, 'Letters, digits, - and _.', [{ label: 'OK', value: true, primary: true }, { label: 'Cancel', value: false }], [input])) ? input.value.trim() : null; };
  const current = Q.seq.patterns.find((p) => p.name === Q.pattern);
  const buttons = el('span', { className: 'row', style: 'margin:0' }, [
    el('button', { id: 'sq-pattern-new', textContent: 'New', disabled: locked, onclick: async () => { const name = await askName('New pattern', 'pattern'); if (name === null) return; const r = addPattern(Q.seq, name, 1); Q.pattern = r.name; commit(r.seq); } }),
    el('button', { id: 'sq-pattern-copy', textContent: 'Duplicate', disabled: locked || !current, onclick: () => { const r = duplicatePattern(Q.seq, current.name); Q.pattern = r.name; commit(r.seq); } }),
    el('button', { id: 'sq-pattern-variation', textContent: 'Variation', title: 'A variation starts with the rows of this pattern; change only what differs', disabled: locked || !current, onclick: () => { const r = addVariation(Q.seq, current.name); Q.pattern = r.name; commit(r.seq); } }),
    el('button', { id: 'sq-pattern-play', textContent: '⟳ Play pattern', title: 'Repeat the first place of this pattern in the playlist', disabled: !current, onclick: () => { const clip = Q.seq.clips.find((c) => c.pattern === current.name); if (!clip) { toast('Put this pattern in the playlist first.'); return; } repeatRange({ at: clip.at, bars: Math.min(clip.bars, current.bars) }); } }),
    el('button', { id: 'sq-pattern-rename', textContent: 'Rename', disabled: locked || !current, onclick: async () => { const name = await askName('Rename pattern', current.name); if (!name || name === current.name) return; const next = renamePattern(Q.seq, current.name, name); if (!next) { toast('That name is taken or not allowed.'); return; } Q.pattern = name; commit(next); } }),
    el('button', { id: 'sq-pattern-delete', textContent: 'Delete', disabled: locked || !current, onclick: () => { const next = deletePattern(Q.seq, current.name); Q.pattern = next.patterns[0]?.name ?? null; commit(next); } }),
  ]);
  box.append(el('div', { className: 'row' }, [tabs, buttons]));
  if (!current) { box.append(el('p', { className: 'note', textContent: 'No pattern yet: make one with New.' })); host.append(box); return; }
  const currentIndex = Q.seq.patterns.indexOf(current);
  const jamOpeners = new Map();   // channel name -> opens the Jam of that row (filled while the rows are made)
  const withNotes = () => Q.seq.channels.filter((c) => isNoteChannel(c) && jamOpeners.has(c.name) && notesIn(rowsOf(Q.seq, current)[c.name] ?? '') > 0);
  box.append(el('div', { className: 'sqpatternhead', id: 'sq-pattern-head', style: `--pc:${patternColour(current, currentIndex)}` }, [el('span', { className: 'sqdot', 'aria-hidden': 'true' }), el('strong', { textContent: current.name }), el('span', { className: 'note', id: 'sq-pattern-summary', textContent: `${plural(current.bars, 'bar')} · ${plural(patternNotes(current), 'note')}` }),
    el('button', { className: 'sqjamall', id: 'sq-jam-all', textContent: 'Jam all voices', disabled: locked || !Q.seq.channels.some((c) => isNoteChannel(c) && notesIn(rowsOf(Q.seq, current)[c.name] ?? '') > 0), title: 'Open the Jam on the first voice with notes; the other voices show behind it as outlines in their own colour, and a click on one opens that voice', onclick: () => withNotes()[0] && jamOpeners.get(withNotes()[0].name)() })]));
  const colour = el('input', { type: 'color', value: /^#[0-9a-f]{6}$/i.test(current.color) ? current.color : colorOf(Q.seq.patterns.indexOf(current)), disabled: locked, 'aria-label': 'Colour of the pattern' });
  colour.onchange = () => commit(setPattern(Q.seq, current.name, { color: colour.value }));
  const bars = el('input', { type: 'number', className: 'seqn', id: 'sq-pattern-bars', value: current.bars, min: 1, max: 64, disabled: locked, 'aria-label': 'Bars of the pattern' });
  bars.onchange = () => commit(setPattern(Q.seq, current.name, { bars: Number(bars.value) || 1 }));
  // the bars and the colour sit in the line of the pattern itself, so there is no row of their own
  const head = box.querySelector('#sq-pattern-head');
  head.insertBefore(el('span', { className: 'sqpatternfields' }, [...(current.from ? [el('span', { className: 'note', id: 'sq-from', textContent: `variation of ${current.from}` })] : []), el('label', { textContent: 'bars', htmlFor: 'sq-pattern-bars' }), bars, colour]), head.querySelector('#sq-jam-all'));
  const rows = rowsOf(Q.seq, current);
  // a row per channel: steps for a drum, a roll for notes, a switch for a loop
  Q.seq.channels.forEach((c, channelIndex) => {
    const row = el('div', { className: `seqrow sqgridrow${current.from && !(c.name in current.rows) ? ' inherited' : ''}` });
    row.dataset.voice = c.name;
    row.style.setProperty('--vc', /^#[0-9a-f]{6}$/i.test(c.color) ? c.color : colorOf(channelIndex));   // the colour of the channel, the same as in the list on the left
    row.append(el('span', { className: 'seqname', textContent: c.name, title: c.type === 'gm' && Number(c.settings.bank ?? 0) === 0 ? `gm · ${gmProgramName(Number(c.settings.program ?? 0))}` : c.type }));
    const text = rows[c.name] ?? '';
    const own = c.name in current.rows;
    if (current.from && current.rows[c.name]?.trim() === '-') {
      row.append(el('span', { className: 'note', textContent: 'silenced in this variation' }), el('button', { className: 'seqx', textContent: 'Inherit', disabled: locked, onclick: () => commit(setRow(Q.seq, current.name, c.name, '')) }));
      box.append(row);
      return;
    }
    if (c.type === 'drum') {
      barsOfRow(text, current.bars).forEach((steps, bar) => {
        const group = el('span', { className: 'sqbar' });
        [...steps].forEach((ch, s) => {
          const cell = el('button', { className: `seqc ${s % 4 === 0 ? 'beat' : s % 2 === 0 ? 'eighth' : 'sixteenth'} v${ch === '.' ? 'rest' : ch}`, textContent: ch === '.' ? '' : ch, disabled: locked, title: `bar ${bar + 1}, step ${s + 1}` });
          cell.dataset.bar = String(bar); cell.dataset.step = String(s); cell.dataset.value = ch;
          cell.onclick = () => commit(toggleStep(Q.seq, current.name, c.name, bar, s));
          group.append(cell);
        });
        row.append(group);
      });
      if (text || own) row.append(el('button', { className: 'seqx', textContent: current.from ? 'Inherit' : 'Clear', title: current.from ? 'Use the row of the base pattern again' : 'Empty this row', disabled: locked, onclick: () => commit(setRow(Q.seq, current.name, c.name, '')) }));
      if (current.from && text) row.append(el('button', { className: 'seqx', textContent: 'Silence', title: 'This channel is silent in the variation', disabled: locked, onclick: () => commit(setRow(Q.seq, current.name, c.name, '-')) }));
    } else if (c.type === 'loop') {
      const on = c.name in rows;
      row.append(el('button', { className: `sqtoggle${on ? ' on' : ''}`, textContent: on ? 'plays in this pattern' : 'silent here', 'aria-pressed': String(on), disabled: locked, onclick: () => commit(setRow(Q.seq, current.name, c.name, on ? (current.from ? '-' : '') : 'on')) }), el('span', { className: 'note', textContent: `starts every ${c.settings.bars ?? 1} bar(s)` }));
    } else if (c.type === 'guitar') {
      const at = text.indexOf(' chords ');
      const strum = el('input', { className: 'seqtext', value: at < 0 ? text : text.slice(0, at), placeholder: 'D...D.u.D.u.D.u.', disabled: locked, 'aria-label': `Strum of ${c.name}` });
      const chords = el('input', { className: 'seqtext', value: at < 0 ? '' : text.slice(at + 8), placeholder: 'Am F', disabled: locked, 'aria-label': `Chords of ${c.name}` });
      const save = () => commit(setRow(Q.seq, current.name, c.name, strum.value.trim() || chords.value.trim() ? `${strum.value.trim()} chords ${chords.value.trim()}`.trim() : ''));
      strum.onchange = save; chords.onchange = save;
      row.append(strum, el('span', { className: 'note', textContent: 'chords' }), chords);
    } else {
      const count = text ? (text.match(/\d+:[\d.]+:[\d.]+:\d+:\d+/g) || []).length : 0;
      // a row that starts with = is a formula (a note pipeline worked out for this pattern): it is shown and edited as text, the roll is for rows of notes
      if (text.trim().startsWith('=')) {
        const edit = el('button', { className: 'sqlite', textContent: 'Edit formula', disabled: locked, title: 'A note pipeline (a rhythm that repeats with values that cycle) that is worked out for this pattern', onclick: async () => {
          const field = el('textarea', { rows: 6, value: text.trim().replace(/^=\s*/, ''), 'aria-label': 'Formula', spellcheck: false });
          if (!(await ask('Formula of ' + c.name, 'Notes written as a pipeline, for example: 1:0:0.5:60:100 1:2:0.5:62:90 |> bars(1..=1) |> repeat(4) |> set(pitch="38 43 45 38"). Leave it empty to remove the row.', [{ label: 'Save', value: true, primary: true }, { label: 'Cancel', value: false }], [field]))) return;
          const next = field.value.trim();
          Q.undo.push(Q.seq); Q.redo = []; Q.seq = setRow(Q.seq, current.name, c.name, next ? '= ' + next : ''); refreshSaveControls(); render();
        } });
        row.append(el('span', { className: 'note', textContent: 'ƒ formula' }), edit);
        box.append(row);
        return;
      }
      const ghostRowsOf = (self) => withNotes().filter((o) => o !== self).map((o) => ({ id: 'sq-' + o.name, title: o.name, color: /^#[0-9a-f]{6}$/i.test(o.color) ? o.color : colorOf(Q.seq.channels.indexOf(o)), text: rowsOf(Q.seq, current)[o.name] ?? '', open: () => jamOpeners.get(o.name)?.() }));
      const openJam = () => {
        import('./pianoroll-lite.js').then((m) => m.openPianoRollLite('sq-' + c.name + '·' + current.name, { sequencerBlock: Q.blockId || '', ghostRows: ghostRowsOf(c), voiceColor: /^#[0-9a-f]{6}$/i.test(c.settings.color ?? '') ? c.settings.color : colorOf(Q.seq.channels.indexOf(c)), pseudo: { id: 'sq-' + c.name + '·' + current.name, title: `${c.name} · ${current.name}`, params: {}, flags: [], tpl: 'midi-clip', parts: [] }, text: text ? `"${text}"` : '', ppq: Number(c.settings.ppq) > 0 ? Number(c.settings.ppq) : 1_000_000, write: async (value) => { Q.undo.push(Q.seq); Q.redo = []; Q.seq = setRow(Q.seq, current.name, c.name, value.replace(/^"|"$/g, '').trim()); refreshSaveControls(); return true; }, onClose: render, instrument: c.type === 'gm' ? { file: c.settings.file || '', bank: c.settings.bank ?? 0, program: c.settings.program ?? 0 } : null }));
      };
      jamOpeners.set(c.name, openJam);
      const jam = withJamIcon(el('button', { className: 'sqlite', disabled: locked, 'aria-label': 'AMPLE Jam', title: 'Jam on the notes of this channel in this pattern: draw, paint, slice and hear them as you go', onclick: openJam }));
      jam.lastChild.remove();   // the icon alone: the count and the picture of the row open the Jam as well
      const summary = el('span', { className: 'note sqopen', textContent: `${count} note${count === 1 ? '' : 's'}`, title: locked ? '' : `Open ${c.name} in AMPLE Jam` });
      const picture = thumbOf(text, current.bars, row.style.getPropertyValue('--vc'));
      picture.title = locked ? '' : `Open ${c.name} in AMPLE Jam`;
      for (const part of [summary, picture]) part.addEventListener('click', () => { if (!locked) openJam(); });
      if (!count) row.classList.add('silent');
      row.append(summary, picture, jam);
    }
    box.append(row);
  });
  host.append(box);
}

// ---------- the playlist ----------
const fmtTime = (seconds) => `${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, '0')}`;
const clockOf = (at, bars) => { const per = barSeconds(tempo()); return `${fmtTime((at - 1) * per)} to ${fmtTime((at - 1 + bars) * per)} at ${tempo()} BPM`; };
/** A tooltip card for a block of the playlist: what kind of block it is, where it sits and what it means. */
let sqTip;
function wireSqTip(node, build) {
  const show = () => {
    sqTip ||= el('div', { className: 'sq-tip', role: 'tooltip' });
    sqTip.replaceChildren(...build());
    document.body.append(sqTip);
    const r = node.getBoundingClientRect(), t = sqTip.getBoundingClientRect();
    sqTip.style.left = `${Math.max(8, Math.min(r.left, innerWidth - t.width - 8))}px`;
    sqTip.style.top = `${r.top - t.height - 10 > 8 ? r.top - t.height - 10 : r.bottom + 10}px`;
    sqTip.classList.add('visible');
  };
  const hide = () => sqTip?.classList.remove('visible');
  node.addEventListener('pointerenter', show); node.addEventListener('pointerleave', hide); node.addEventListener('pointerdown', hide);
}
const tipCard = (kind, color, title, lines, hint) => [el('div', { className: 'sq-tip-kind', style: `--kc:${color}`, textContent: kind }), el('strong', { textContent: title }), ...lines.map((text) => el('p', { textContent: text })), el('em', { className: 'sq-tip-hint', textContent: hint })];
const sectionTip = (s, i) => tipCard('SECTION', s.color || colorOf(i + 3), s.name, [`Bars ${s.at} to ${s.at + s.bars - 1} (${s.bars} bars) · ${clockOf(s.at, s.bars)}`, 'A section plays nothing by itself: it labels the part so you can see the structure and repeat just that part with the loop button.'], 'Click to rename, move or recolour it · ⟳ repeats it · right-click for more');
const clipTip = (c, p, track) => tipCard(`CLIP · TRACK ${track}`, p?.color || colorOf(Q.seq.patterns.indexOf(p)), c.pattern, [`Starts at bar ${c.at} for ${c.bars} bars · ${clockOf(c.at, c.bars)}`, `Plays the pattern "${c.pattern}"${p ? ` (${p.bars} bar${p.bars === 1 ? '' : 's'} long)` : ' (this pattern is missing)'} at this place. A pattern holds the steps and notes of the channels; the clip puts it on the timeline.`], 'Drag to move · drag the right edge to resize · double-click edits the pattern · right-click for more');
function sectionMenu(event, s, i) {
  showMenu(event.clientX, event.clientY, [{ head: s.name }, { label: 'Repeat this section', fn: () => repeatRange({ at: s.at, bars: s.bars }) }, { label: 'Name, place or colour...', fn: () => sectionDialog(i) }]);
}
function clipMenu(event, index, c, locked) {
  const others = Q.seq.patterns.filter((p) => p.name !== c.pattern);
  showMenu(event.clientX, event.clientY, [
    { head: c.pattern },
    { label: 'Edit this pattern', fn: () => { Q.pattern = c.pattern; render(); } },
    ...(locked ? [] : [
      { label: 'Duplicate after this one', fn: () => { Q.clip = Q.seq.clips.length; commit(addClip(Q.seq, c.pattern, c.at + c.bars, c.bars, c.track)); } },
      ...(others.length ? [{ label: 'Use another pattern', children: others.map((p) => ({ label: p.name, fn: () => commit(updateClip(Q.seq, index, { pattern: p.name })) })) }] : []),
      '-', { label: 'Delete clip', danger: true, fn: () => { Q.clip = -1; commit(removeClip(Q.seq, index)); } },
    ]),
  ]);
}
function trackMenu(event, track, bar, locked) {
  if (locked || !Q.seq.patterns.length) return;
  showMenu(event.clientX, event.clientY, [
    { head: `Bar ${bar} · track ${track}` },
    ...(Q.pattern ? [{ label: `Add "${Q.pattern}" here`, fn: () => { const p = Q.seq.patterns.find((x) => x.name === Q.pattern); Q.clip = Q.seq.clips.length; commit(addClip(Q.seq, Q.pattern, bar, p?.bars ?? 1, track)); } }] : []),
    { label: 'Add a pattern here', children: Q.seq.patterns.map((p) => ({ label: p.name, fn: () => { Q.clip = Q.seq.clips.length; commit(addClip(Q.seq, p.name, bar, p.bars ?? 1, track)); } })) },
  ]);
}
function paintPlaylist(host, locked) {
  const total = Math.max(16, songBars(Q.seq) + 4);
  const px = Q.barPx;
  const box = el('div', { className: 'sqplaylist', id: 'sq-playlist', role: 'region', tabIndex: 0, 'aria-label': 'Sequencer playlist' });
  const zoom = el('span', { className: 'row', style: 'margin:0' }, [el('b', { textContent: 'Playlist' }), el('span', { className: 'note', style: 'margin:0 6px', textContent: 'drag over the bar numbers to choose a part to repeat · click a number to start there' }), el('button', { textContent: '−', title: 'Zoom out', onclick: () => { Q.barPx = Math.max(4, Q.barPx - 6); render(); } }), el('button', { textContent: '+', title: 'Zoom in', onclick: () => { Q.barPx = Math.min(80, Q.barPx + 6); render(); } }),
    el('button', { id: 'sq-zoom-fit', textContent: 'Fit', title: 'Zoom so that the whole song is in view', onclick: () => { const room = ($('#sq-playlist .sqscroll')?.clientWidth || 600) - 4; Q.barPx = Math.max(4, Math.min(80, Math.floor(room / total))); render(); } }),
    el('button', { id: 'sq-add-section', textContent: '+ Section', disabled: locked, onclick: () => sectionDialog(-1) }),
    el('button', { id: 'sq-add-marker', textContent: '+ Marker', disabled: locked, onclick: () => markerDialog(-1) }),
    el('button', { id: 'sq-loop', textContent: Q.seq.loop ? `Loop: bar ${Q.seq.loop.at} for ${Q.seq.loop.bars}` : '+ Loop', title: 'From the loop start on, the bars of the loop repeat', disabled: locked, onclick: () => loopDialog() })]);
  box.append(zoom);
  const scroll = el('div', { className: 'sqscroll' });
  const width = total * px;
  const ruler = el('div', { className: 'sqruler' });
  ruler.style.width = `${width}px`;
  for (let bar = 1; bar <= total; bar += 1) { const tick = el('span', { className: 'sqtick', textContent: bar % 4 === 1 ? String(bar) : '' }); tick.style.left = `${(bar - 1) * px}px`; tick.style.width = `${px}px`; ruler.append(tick); }
  ruler.title = 'Drag over some bars to repeat them; click a bar to start from there';
  ruler.onpointerdown = (event) => {
    event.preventDefault();
    const rect = ruler.getBoundingClientRect(), x0 = event.clientX - rect.left;
    ruler.setPointerCapture(event.pointerId);
    let dragged = null;
    ruler.onpointermove = (move) => { dragged = rangeFromDrag(x0, move.clientX - rect.left, px) ?? dragged; if (dragged) { Q.range = dragged; paintRangeBand(scroll); } };
    ruler.onpointerup = (up) => {
      ruler.onpointermove = null; ruler.onpointerup = null;
      if (dragged) { chooseRange(dragged).catch(() => {}); return; }
      Q.cursor = barOfClick(x0, px);
      if (loopState().playing) seekLoopPlayer((Q.cursor - 1) * barSeconds(tempo()));
      render();
    };
  };
  // the wheel scrolls the playlist sideways, Ctrl + wheel zooms (around the left edge)
  scroll.addEventListener('wheel', (event) => {
    if (event.ctrlKey) { event.preventDefault(); Q.barPx = Math.round(Math.min(80, Math.max(4, Q.barPx * (event.deltaY < 0 ? 1.15 : 0.87)))); render(); }
    else if (!event.shiftKey && Math.abs(event.deltaY) > Math.abs(event.deltaX) && scroll.scrollWidth > scroll.clientWidth) { event.preventDefault(); scroll.scrollLeft += event.deltaY; }
  }, { passive: false });
  scroll.append(ruler);
  const sections = el('div', { className: 'sqsections' });
  sections.style.width = `${width}px`;
  Q.seq.sections.forEach((s, i) => { const band = el('button', { className: 'sqsection', textContent: s.name, 'aria-label': `Section ${s.name}: bar ${s.at} for ${s.bars} bars`, onclick: () => sectionDialog(i) }); band.dataset.section = String(i); band.style.left = `${(s.at - 1) * px}px`; band.style.width = `${s.bars * px}px`; band.style.setProperty('--c', s.color || colorOf(i + 3)); wireSqTip(band, () => sectionTip(s, i)); band.oncontextmenu = (event) => { event.preventDefault(); sectionMenu(event, s, i); }; sections.append(band); });
  Q.seq.sections.forEach((sec) => { const again = el('button', { className: 'sqsecloop', textContent: '⟳', title: `Repeat ${sec.name} (bars ${sec.at} to ${sec.at + sec.bars - 1})`, onclick: (event) => { event.stopPropagation(); repeatRange({ at: sec.at, bars: sec.bars }); } }); again.style.left = `${(sec.at - 1 + sec.bars) * px - 24}px`; sections.append(again); });
  scroll.append(sections);
  if (Q.seq.markers.length || Q.seq.loop) {
    const marks = el('div', { className: 'sqmarkers' });
    marks.style.width = `${width}px`;
    if (Q.seq.loop) { const band = el('button', { className: 'sqloop', textContent: '⟳ loop', title: `The loop: bar ${Q.seq.loop.at} for ${Q.seq.loop.bars} bars, repeating to the end`, onclick: () => loopDialog() }); band.style.left = `${(Q.seq.loop.at - 1) * px}px`; band.style.width = `${Q.seq.loop.bars * px}px`; marks.append(band); }
    Q.seq.markers.forEach((m, i) => { const flag = el('button', { className: 'sqmarker', textContent: `⚑ ${m.name}`, title: `${m.name}: bar ${m.at}`, onclick: () => markerDialog(i) }); flag.dataset.marker = String(i); flag.style.left = `${(m.at - 1) * px}px`; marks.append(flag); });
    scroll.append(marks);
  }
  const tracks = trackCount(Q.seq);
  for (let t = 1; t <= tracks; t++) {
    const lane = el('div', { className: 'sqtrack', 'aria-label': `Track ${t}` });
    lane.dataset.track = String(t);   // the number of the track is drawn at its left edge by the style sheet
    if (!Q.seq.clips.some((c) => c.track === t)) lane.classList.add('empty');   // a track without clips is lower
    lane.style.width = `${width}px`;
    lane.style.setProperty('--bar', `${px}px`);
    lane.onclick = (event) => {
      if (locked || event.target !== lane || !Q.pattern) return;
      const bar = Math.floor((event.clientX - lane.getBoundingClientRect().left) / px) + 1;
      const p = Q.seq.patterns.find((x) => x.name === Q.pattern);
      Q.clip = Q.seq.clips.length;
      commit(addClip(Q.seq, Q.pattern, bar, p?.bars ?? 1, t));
    };
    lane.ondragover = (event) => { if (locked || !draggingPattern) return; event.preventDefault(); event.dataTransfer.dropEffect = 'copy'; };
    lane.ondrop = (event) => {
      if (locked || !draggingPattern) return;
      event.preventDefault();
      const name = draggingPattern, p = Q.seq.patterns.find((x) => x.name === name);
      draggingPattern = null;
      Q.clip = Q.seq.clips.length;
      commit(addClip(Q.seq, name, Math.max(1, Math.floor((event.clientX - lane.getBoundingClientRect().left) / px) + 1), p?.bars ?? 1, t));
    };
    lane.oncontextmenu = (event) => { if (event.target !== lane) return; event.preventDefault(); trackMenu(event, t, Math.floor((event.clientX - lane.getBoundingClientRect().left) / px) + 1, locked); };
    Q.seq.clips.forEach((c, index) => {
      if (c.track !== t) return;
      const p = Q.seq.patterns.find((x) => x.name === c.pattern);
      const clip = el('div', { className: `sqclip${index === Q.clip ? ' on' : ''}${p ? '' : ' missing'}${c.pattern === Q.pattern ? ' ofchosen' : ''}`, textContent: c.pattern, tabIndex: 0, 'aria-label': `Clip ${c.pattern}: bar ${c.at} for ${c.bars} bars` });
      clip.dataset.clip = String(index);
      wireSqTip(clip, () => clipTip(c, p, t));
      clip.oncontextmenu = (event) => { event.preventDefault(); event.stopPropagation(); Q.clip = index; clipMenu(event, index, c, locked); };
      clip.style.left = `${(c.at - 1) * px}px`; clip.style.width = `${Math.max(px * 0.8, c.bars * px - 2)}px`;
      clip.style.setProperty('--c', p?.color || colorOf(Q.seq.patterns.indexOf(p)));
      const grip = el('span', { className: 'sqgrip', title: 'Resize' });
      clip.append(grip);
      if (!locked) {
        const drag = (mode) => (event) => {
          event.preventDefault(); event.stopPropagation();
          Q.clip = index;
          const startX = event.clientX, original = { ...c };
          clip.setPointerCapture(event.pointerId);
          let latest = Q.seq;
          clip.onpointermove = (move) => {
            const delta = Math.round((move.clientX - startX) / px);
            latest = mode === 'move' ? updateClip(Q.seq, index, { at: original.at + delta }) : updateClip(Q.seq, index, { bars: original.bars + delta });
            const c2 = latest.clips[index];
            clip.style.left = `${(c2.at - 1) * px}px`; clip.style.width = `${Math.max(px * 0.8, c2.bars * px - 2)}px`;
          };
          clip.onpointerup = () => { clip.onpointermove = null; clip.onpointerup = null; if (latest !== Q.seq) commit(latest); else render(); };
        };
        clip.onpointerdown = drag('move');
        grip.onpointerdown = drag('resize');
        clip.onkeydown = (event) => { if (event.key === 'Delete' || event.key === 'Backspace') { event.preventDefault(); Q.clip = -1; commit(removeClip(Q.seq, index)); } else if (event.key === 'ArrowLeft') { event.preventDefault(); commit(updateClip(Q.seq, index, { at: c.at - 1 })); } else if (event.key === 'ArrowRight') { event.preventDefault(); commit(updateClip(Q.seq, index, { at: c.at + 1 })); } };
        clip.ondblclick = () => { Q.pattern = c.pattern; render(); };
      }
      lane.append(clip);
    });
    scroll.append(lane);
  }
  paintRangeBand(scroll);
  const head = el('div', { id: 'sq-playhead', className: 'sqplayhead' });
  head.style.display = 'none';
  scroll.append(head);
  box.append(scroll, el('p', { className: 'note', textContent: 'Click an empty spot of a track to put the selected pattern there; drag a clip to move it, its right edge to make it longer (the pattern repeats); Delete removes it.' }));
  host.append(box);
}

/** The shaded band over the bars that repeat. */
function paintRangeBand(scroll) {
  scroll.querySelector('.sqrange')?.remove();
  if (!Q.range) return;
  const band = el('div', { className: 'sqrange' });
  band.style.left = `${(Q.range.at - 1) * Q.barPx}px`;
  band.style.width = `${Q.range.bars * Q.barPx}px`;
  scroll.append(band);
}

async function sectionDialog(index) {
  const s = index >= 0 ? Q.seq.sections[index] : { name: 'Section', at: 1, bars: 8, color: '' };
  const name = el('input', { value: s.name, 'aria-label': 'Name' }), at = el('input', { type: 'number', value: s.at, min: 1, 'aria-label': 'First bar' }), bars = el('input', { type: 'number', value: s.bars, min: 1, 'aria-label': 'Bars' });
  const color = el('input', { type: 'color', value: /^#[0-9a-f]{6}$/i.test(s.color) ? s.color : colorOf(index >= 0 ? index + 3 : Q.seq.sections.length + 3), 'aria-label': 'Colour' });
  const buttons = [{ label: 'Cancel', value: false }, ...(index >= 0 ? [{ label: 'Remove', value: 'remove' }] : []), { label: 'OK', value: true, primary: true }];
  const result = await ask(index >= 0 ? 'Section' : 'New section', 'A named stretch of the song, shown as a band above the tracks.', buttons, [name, at, bars, color]);
  if (result === 'remove') commit(removeSection(Q.seq, index));
  else if (result === true) {
    if (index >= 0) commit(updateSection(Q.seq, index, { name: name.value.trim() || s.name, at: Number(at.value) || 1, bars: Number(bars.value) || 1, color: color.value }));
    else commit(addSection(Q.seq, name.value.trim() || 'Section', Number(at.value) || 1, Number(bars.value) || 1, color.value));
  }
}

async function markerDialog(index) {
  const m = index >= 0 ? Q.seq.markers[index] : { name: 'Marker', at: 1 };
  const name = el('input', { value: m.name, 'aria-label': 'Name' }), at = el('input', { type: 'number', value: m.at, min: 1, 'aria-label': 'Bar' });
  const buttons = [{ label: 'Cancel', value: false }, ...(index >= 0 ? [{ label: 'Remove', value: 'remove' }] : []), { label: 'OK', value: true, primary: true }];
  const result = await ask(index >= 0 ? 'Marker' : 'New marker', 'A named point of the song. It does not change the sound.', buttons, [name, at]);
  if (result === 'remove') commit(removeMarker(Q.seq, index));
  else if (result === true) commit(index >= 0 ? updateMarker(Q.seq, index, { name: name.value.trim() || m.name, at: Number(at.value) || 1 }) : addMarker(Q.seq, name.value.trim() || 'Marker', Number(at.value) || 1));
}

async function loopDialog() {
  const l = Q.seq.loop ?? { at: 1, bars: 4 };
  const at = el('input', { type: 'number', value: l.at, min: 1, 'aria-label': 'First bar of the loop' }), bars = el('input', { type: 'number', value: l.bars, min: 1, 'aria-label': 'Bars of the loop' });
  const buttons = [{ label: 'Cancel', value: false }, ...(Q.seq.loop ? [{ label: 'Remove', value: 'remove' }] : []), { label: 'OK', value: true, primary: true }];
  const result = await ask('Loop', 'From the first bar on, the bars of the loop play again and again to the end of the block.', buttons, [at, bars]);
  if (result === 'remove') commit(setLoop(Q.seq, null));
  else if (result === true) commit(setLoop(Q.seq, { at: Number(at.value) || 1, bars: Number(bars.value) || 1 }));
}

// ---------- the lanes ----------
function paintLanes(host) {
  host.innerHTML = '';
  const lanes = lanesNow();
  host.append(el('h4', { textContent: 'Lanes', style: 'padding:0 0 4px' }), el('p', { className: 'note', textContent: 'Every channel is in the mix. A lane is an extra output of the block that carries the channels you pick; connect it to any block, or render it to a file.' }));
  const locked = readOnly();
  for (let n = 1; n <= 8; n++) {
    const names = lanes[n] ?? [];
    const row = el('div', { className: 'sqlane' });
    row.dataset.lane = String(n);
    row.append(el('span', { className: 'seqname', textContent: `lane ${n}` }));
    Q.seq.channels.forEach((c) => {
      const on = names.includes(c.name);
      row.append(el('button', { className: `sqchip${on ? ' on' : ''}`, textContent: c.name, 'aria-pressed': String(on), disabled: locked, title: on ? `Take ${c.name} out of lane ${n}` : `Put ${c.name} in lane ${n}`, onclick: () => setLanes({ ...lanes, [n]: on ? names.filter((x) => x !== c.name) : [...names, c.name] }) }));
    });
    if (names.length) row.append(el('button', { className: 'sqrender', textContent: 'Render to file...', title: 'Build the block and write this lane as a WAV or MP3 file of the project', onclick: () => renderLane(n).catch((e) => toast(e.message || String(e))) }));
    host.append(row);
  }
}

async function renderLane(n) {
  if (dirty()) await writeFile();
  await build(Q.blockId);
  const take = S.build.takes?.[Q.blockId];
  if (!take) { toast('The block could not be built.'); return; }
  const name = el('input', { value: `${Q.blockId}-lane${n}`, 'aria-label': 'File name without extension' });
  const folder = el('input', { value: 'renders', 'aria-label': 'Project folder' });
  const format = el('select', { 'aria-label': 'Format' }, [el('option', { value: 'wav', textContent: 'WAV' }), el('option', { value: 'mp3', textContent: 'MP3' })]);
  if (!(await ask(`Render lane ${n}`, `Writes lane ${n} of ${Q.blockId} to a file of the project.`, [{ label: 'Render', value: true, primary: true }, { label: 'Cancel', value: false }], [name, folder, format]))) return;
  const made = await api.post(`/api/takes/${encodeURIComponent(take)}/export`, { name: name.value.trim(), folder: folder.value.trim(), format: format.value, role: `lane${n}` });
  toast(`Wrote ${made.path}`);
}
