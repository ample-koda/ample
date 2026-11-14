// "AMPLE Jam" (plan midi-loop-studio MLS04): a lighter, FL-style view of the same compact `notes` text the Advanced roll edits.
// A non-modal floating panel (the Studio behind it stays usable), piano keys fixed left, one icon toolbar, a ruler with a draggable loop bracket,
// an always-on velocity strip. The text stays the single truth: every gesture is ONE edit written through notes-model and the serialized commit queue,
// fields this view does not show (pan, fine, slide, group ...) stay in the text untouched. Maths lives in web/lib/lite-roll-model.mjs. @ample:pianoroll-lite

import { ask, el, toast } from './ui.js';
import { addWindowChrome, jamIcon } from './window-chrome.js';
import { createJamInstrument } from './jam-instrument.js';
import { S, applyText, edit, flat, notify, readOnly, redo, select, songClock, subscribe, undo } from './store.js';
import { startLive, liveAudioContext, armAudioUnlock } from './live-play.js';
import { playbackOutput } from './playback-volume.js';
import { createNoteAudition } from './note-audition.js';
import { createVirtualKeyboard } from './virtual-keyboard.js';
import { createTakeReview } from './jam-take-review.js';
import { activateReceiver, registerReceiver } from './keys-router.js';
import { midiActive, midiCan, midiNames, onMidiNote, onMidiState, restoreMidi, toggleMidi } from './midi-hub.js';
import './keys-play.js';
import { snapTicks, splitAtBars } from '../lib/virtual-keys.mjs';
import { freeName, isPipeline, openEditablePiece, openRoll, rememberedView, rememberView, rollable } from './roll-views.js';
import { drawPianoRollNote, sizePianoRollCanvas } from './pianoroll-draw.js';
import { pianoRollHit, pianoRollPoint } from './pianoroll-input.js';
import { mergeTies } from '../lib/live-synth.mjs';
import { absolute, format, parse, pasteNotes, removeNote, updateMany } from '../lib/notes-model.mjs';
import { openJamForm } from './jam-forms.js';
import { play as playSong } from './player.js';
import { STRUM_CURVES, STRUM_DIRECTIONS, groupNotesByStart, strum } from '../lib/strum.mjs';
import { CHOP_DIVISIONS, chop } from '../lib/chop.mjs';
import { previewHumanizeDrift } from '../lib/humanize-preview.mjs';
import { api } from './api.js';
import { createJamLanes } from './jam-lanes.js';
import { generateRiff } from '../lib/riff.mjs';
import { CHORDS, chordPitches } from '../lib/chords.mjs';
import { LIMITS, centerOnNotes, fitRange, keysToIndexes, pasteTick, restoreView, serializeView, zoomAnchor, zoomPreset } from '../lib/lite-flow.mjs';
import { cellInfo, cellShade, columnOf, coveringRows, drumChoices, gridPitches, noteLabel, pitchLabel, rowAt, stepTicks, zoomGridRows } from '../lib/lite-grid.mjs';
import { chipLabel, chipsSignature, neighbourChips, popBack, pushBack } from '../lib/lite-strip.mjs';
import { ROOTS, SCALE_DEGREES, detectKey, isRoot, nearestScaleTone, parseScale, scaleSet } from '../lib/scales.mjs';
import { velocityColor, shadeHsl } from '../lib/pianoroll-theme.mjs';
import { arpeggiate, flipPitch, legato, removeOverlaps, reverseTime, scaleTime, staccato } from '../lib/roll-tools.mjs';
import { LANE_DEFS, laneFraction, laneFromY, laneRamp, laneValueOf } from '../lib/note-lanes.mjs';
import { knobFactor, rowVolume, scaleVelocity } from '../lib/lite-grid.mjs';
import { createMetronomeCursor } from '../lib/metronome-cursor.mjs';
import { SNAPS, TOOLS, autoScrollDelta, clipEndBars, createCommitQueue, dragLoop, gridLevels, hasHiddenData, hitLoop, litePlayRange, nearestStart, openCentre, scrollYForCentre, sliceNote, snapBeats, snapTick, toolForKey, typingPitch } from '../lib/lite-roll-model.mjs';
import { discardedMessage, onCloseTake, playedCount } from '../lib/jam-close.mjs';
import { GRID_KEYS, LONG_PRESS_MS, ROLL_KEYS, isLongPress, longPressAction, pinchStart, pinchView } from '../lib/lite-touch.mjs';
import { adjustRows, clampMove, clipboardText, cloneRows, copyItems, createThrottle, drawRow, duplicateRows, itemsFromText, mergedRows, moveRows, pasteRows, quantizeRows, removeRows, resizeRows, rowIndexAt, sliceRow, soundingPitches, soundingRows, velocityRows } from '../lib/lite-gestures.mjs';
import { barBeats } from '../lib/meter.mjs';
import { songBarNow } from './play-fx.js';
import { clipTickAt } from '../lib/song-follow.mjs';

let sessionRect = null;   // where the last AMPLE Jam window was (this page session only), so Advanced -> Lite opens it where it was
let RACK_W = 252, LED_X = 14, PAN_X = 40, KNOB_X = 67, NUM_X = 85, NAME_X = 113;   // the Grid's channel rack: LED, pan knob, volume knob, row number, name, and a selection bar on the right
/** The channel rack is narrower on a phone (no row number), so the cells keep most of the screen. */
function setRackLayout(compact) { if (compact) { RACK_W = 124; LED_X = 11; PAN_X = 29; KNOB_X = 53; NUM_X = NAME_X = 70; } else { RACK_W = 252; LED_X = 14; PAN_X = 40; KNOB_X = 67; NUM_X = 85; NAME_X = 113; } }
const GROUP_COLORS = ['#e0a030', '#30c0e0', '#c060e0', '#60e090', '#e05060', '#a0a030', '#3060e0', '#e0309a'];
const KEY_W = 48, RULER_H = 26, VEL_H = 64, MIN_BEAT_PX = 6, MAX_BEAT_PX = 480;
const BLACK = new Set([1, 3, 6, 8, 10]);
const NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];
const openPanels = new Map();   // block id -> panel, one Lite panel per clip
let backStack = [];             // clips left through the neighbouring-clips strip, newest last (Alt+Left returns)
let liteClipboard = [];         // copied notes (all their fields), shared by the Lite panels so a copy can go to another clip
const SHORTCUT_GROUPS = [
  { title: 'Notes', rows: [
    [['Click', 'Drag'], 'Draw a note; drag its right edge to resize; right-click deletes'],
    [['Shift', 'Drag'], 'Clone the note you drag'],
    [['P', 'B', 'D', 'C', 'E', 'Y', 'Z'], 'Tools: Draw, Paint (Shift: one note per step), Delete, Slice, Select, Playback, Zoom'],
    [['Ctrl', 'B'], 'Duplicate the selection to the right'],
    [['Ctrl', 'Q'], 'Quantize the selection (or all notes); Ctrl+Alt+Q: the whole clip'],
    [['Shift', 'Arrows'], 'Move the selection; Ctrl+Up/Down an octave; Alt+Left/Right nudge'],
  ] },
  { title: 'Selecting', rows: [
    [['Ctrl', 'Drag'], 'Rubber-band selection (on the ruler: a time range)'],
    [['Ctrl', 'A'], 'Select everything; Ctrl+D deselects'],
  ] },
  { title: 'Clipboard', rows: [
    [['Ctrl', 'C', 'X', 'V'], 'Copy, cut, paste (at the pointer, else the playhead); copy also puts the notes on the system clipboard as text'],
    [['Shift', 'Ctrl', 'V'], 'Paste note text from the system clipboard (from another clip, roll or app)'],
  ] },
  { title: 'View', rows: [
    [['Wheel'], 'Scroll pitch; Shift+wheel scrolls time'],
    [['Ctrl', 'Wheel'], 'Zoom time (the rows grow a little with it); Alt+wheel zooms the rows'],
    [['Alt', 'Wheel'], 'Over a note: velocity; with Ctrl: fine tune'],
    [['PgUp', 'PgDn'], 'Zoom; Shift+4 fits the clip, Shift+5 the selection'],
  ] },
  { title: 'Playing', rows: [
    [['Space'], 'Start / stop; Ctrl+Space plays / pauses from the playhead'],
    [['Backspace'], 'Snap on / off (hold Alt: free)'],
    [['Ctrl', 'T'], 'Typing keyboard on / off'],
    [['K'], 'Selected notes (or all) sound as the drum kit; again: back as the instrument'],
  ] },
  { title: 'Grid view', rows: [
    [['Click', 'Shift+Click'], 'Switch a step on or off; accent it'],
    [['Drag'], 'Paint a run of steps; drag from a filled step to clear a run'],
    [['Alt', 'Drag'], 'Draw one long note over the steps you drag across'],
    [['Alt', 'Click'], 'On a long note: split it into separate steps'],
    [['S'], 'Split the long notes of the selection (or all) into separate steps'],
    [['J'], 'Join the selected notes that sit next to each other into long notes (three pairs: three long notes)'],
    [['B'], 'Bend the selected notes: how many semitones each glides up (or down, negative) over its length; 0 takes the bend away'],
    [['Alt', 'Wheel'], 'On a step: its velocity. Elsewhere: taller or shorter rows'],
    [['LED'], 'Mute the row while you play (the notes stay)'],
    [['Knobs'], 'Pan and volume of the row; double-click the pan knob to centre it'],
    [['Right-click', 'Ctrl+Click'], 'On a lane name: name it, change its sound or note, make it a drum kit lane'],
  ] },
  { title: 'Window', rows: [
    [['Double-click'], 'Roll the window up to its title bar (or back); on a maximized window: restore it'],
    [['Esc'], 'Leave the maximized window'],
    [['?'], 'This card'],
  ] },
];

/** Opens AMPLE Jam for a midi-clip block. A clip written with chords or a melody is converted by the Advanced roll's flow first. */
export function openPianoRollLite(blockId, options = {}) {
  const node = options.pseudo ?? flat().nodes.find((n) => n.id === blockId);
  if (!node) return null;
  if (openPanels.get(blockId)?.isConnected) { openPanels.get(blockId).querySelector('.mls-grid')?.focus(); return openPanels.get(blockId); }
  if (!options.pseudo && isPipeline(node)) { openRoll(blockId, options); return null; }   // a note pipeline opens read-only, as what it makes
  if (!options.pseudo && !rollable(node)) { toast('This clip is written with chords, a melody or expanded notes. Open it from its block to write it out as notes first.'); return null; }
  armAudioUnlock();
  const ppq = options.ppq ?? (Number.parseInt(node.params.ppq ?? '', 10) || 1_000_000), bar = barBeats() * ppq;
  const locked = options.readOnly || readOnly() || node.flags?.includes('lock');
  const paramText = () => options.pseudo ? (options.text ?? '') : (flat().nodes.find((n) => n.id === node.id) ?? node).params.notes ?? '';
  let model = parse(paramText(), ppq);
  if (!model.representable && model.notes.length === 0 && model.items.length === 0 && model.reasons.length) { toast(`AMPLE Jam cannot show this block: ${model.reasons[0]}`); return null; }
  const bpm = () => Number(S.doc?.project?.tempo) || 120;
  const state = { showGhosts: true, lane: 'velocity', pxBeat: 60, rowH: 14, scrollX: 0, scrollY: 0, snap: 'auto', snapBefore: 'auto', tool: 'draw', loopOn: false, bracket: null, playhead: 0, songHead: null, follow: true, metronome: false,
    typing: false, octave: 4, selected: new Set(), drag: null, extraBars: 0, litKey: new Set(), litTyped: new Map(), litMidi: new Map(), cursor: null, playOrigin: 0, player: null, altKey: false, lastLen: 0, pointer: null, litNotes: new Set(), litPitches: new Set(), followPaused: false, hintDismissed: false, overGrid: false, wheelTimer: 0, mode: 'roll', gridScrollY: 0, gridPins: [], laneNames: new Map(), laneDrum: new Set(), activeRow: null, stepVel: 96, gridRowH: LIMITS.gridRowH, muted: new Set() };
  function setAlt(on) { if (state.altKey === on) return; state.altKey = on; snapSelect.value = on ? 'free' : state.snap; }
  const calls = [];   // the last audition calls, for the DOM specs (panel.mlsDebug)
  const jamVoice = options.instrument ? createJamInstrument(options.instrument) : null;   // a sequencer channel: its soundfont, bank and program sound for real (rendered by this computer's AMPLE server), the sketch voice is for the rest
  const audition = createNoteAudition({ clipId: node.id, ppq, bpm, onBadge: (badge) => { badgeEl.textContent = badge.label; badgeEl.dataset.approximate = badge.approximate ? '1' : '0'; } });

  // ---- commits: optimistic model, one serialized queue (decision 28) ----
  const textOf = (m) => { let text = format(m); if (!m.quote && /\s/.test(text.trim())) text = `"${text.trim()}"`; return text.trim() === '' || text.trim() === '""' ? '' : text; };
  let inflight = 0;
  // a sequencer row (pseudo clip) has its own undo in the sequencer; here every commit keeps the previous text so Ctrl+Z never touches the project
  const history = { undo: [], redo: [] };
  let currentText = textOf(model);
  const queue = createCommitQueue({ initial: textOf(model),
    write: async (text) => (options.write ? options.write(text) : edit([text === '' ? { type: 'removeParameter', block: node.id, name: 'notes' } : { type: 'setParameter', block: node.id, name: 'notes', value: text }])),
    onRollback: (text) => { model = parse(text, ppq); state.selected = new Set(); toast('The change could not be saved; the clip is back as it was.'); requestDraw(); } });
  function commit(next, replay) {
    next = withDrumLanes(next);
    if (options.pseudo && !replay && textOf(next) !== currentText) { history.undo.push(currentText); history.redo = []; }
    currentText = textOf(next);
    model = next; inflight++;
    return queue.enqueue(textOf(next)).finally(() => { inflight--; requestDraw(); });
  }
  function stepHistory(back) {
    const from = back ? history.undo : history.redo, to = back ? history.redo : history.undo;
    if (!from.length) return;
    to.push(currentText);
    state.selected = new Set();
    commit(parse(from.pop(), ppq), true);
  }
  const reload = () => { model = parse(paramText(), ppq); queue.reset(textOf(model)); state.selected = new Set(); requestDraw(); };
  const stopStore = subscribe(() => { if (options.derived) refreshDerived(); renderStrip(); lanesCtl.refresh(); if (panel.isConnected && !state.drag && !inflight && !options.pseudo && textOf(parse(paramText(), ppq)) !== queue.committed) reload(); });

  // ---- view helpers ----
  const rows = () => mergeTies(absolute(model));
  const explicitBars = () => { const part = (flat().nodes.find((n) => n.id === node.id) ?? node).parts?.[0]; return part ? part.length : 0; };
  const endBars = () => clipEndBars(explicitBars(), rows(), ppq, state.extraBars);
  const endTick = () => endBars() * bar;
  const rowsOf = (m) => mergeTies(absolute(m));
  // a drum lane: every note of that pitch is written for the General MIDI drum channel (10, wire value 9); the notes tell it, so it is known again when the clip is opened
  for (const pitch of new Set(rows().map((r) => r.pitch))) if (rows().filter((r) => r.pitch === pitch).every((r) => r.channel === 9)) state.laneDrum.add(pitch);
  const withDrumLanes = (next) => { if (!state.laneDrum.size) return next; const need = rowsOf(next).filter((r) => state.laneDrum.has(r.pitch) && r.channel !== 9); return need.length ? adjustRows(next, need, () => ({ channel: 9, bank: 0, program: 0 })) : next; };
  /** The colour of the end line of the clip: mint while there is room, orange when the notes touch it, red when notes reach past the length the clip has in the arrangement (they would be cut off when it plays). */
  function endTone() {
    const end = endTick(), list = rows(), explicit = explicitBars() * bar, extent = list.reduce((m, r) => Math.max(m, r.at + r.length), 0);
    if (explicit > 0 && extent > explicit) return '#ff6b6b';
    if (list.some((r) => r.at + r.length >= end - ppq / 4)) return '#f0a040';
    return '#3fd6b0';
  }
  const gridW = () => grid.clientWidth, gridH = () => grid.clientHeight;
  const xOf = (tick) => (tick / ppq) * state.pxBeat - state.scrollX, atOf = (x) => ((x + state.scrollX) / state.pxBeat) * ppq;
  const yOf = (pitch) => (127 - pitch) * state.rowH - state.scrollY, pitchOf = (y) => Math.max(0, Math.min(127, 127 - Math.floor((y + state.scrollY) / state.rowH)));
  const beats = () => snapBeats(state.snap, state.pxBeat);
  const snap = (tick, bypass = state.altKey) => snapTick(tick, beats(), ppq, bypass);
  const maxScrollX = () => Math.max(0, (endTick() / ppq) * state.pxBeat + 120 - gridW());
  const clampScroll = () => { state.scrollX = Math.max(0, Math.min(maxScrollX(), state.scrollX)); state.scrollY = Math.max(0, Math.min(128 * state.rowH - gridH(), state.scrollY)); state.gridScrollY = Math.max(0, Math.min(Math.max(0, gridList().length * state.gridRowH - gridH()), state.gridScrollY)); };
  const lastLength = () => state.lastLen > 0 ? state.lastLen : Math.max(1, Math.round(beats() ? beats() * ppq : ppq / 4));
  const gridList = () => gridPitches(rows().map((r) => r.pitch), state.gridPins);
  const stepLen = () => stepTicks(beats(), ppq);
  const gridPitchAt = (y) => { const list = gridList(), i = rowAt(y, state.gridScrollY, state.gridRowH, list.length); return i < 0 ? null : list[i]; };
  const hit = (x, y) => pianoRollHit(rows(), x, y, state.pxBeat, state.rowH, xOf, yOf, ppq, 10);

  // ---- DOM ----
  const panel = el('dialog', { className: 'mls-panel metal-window', 'aria-label': `AMPLE Jam of ${node.title}`, 'data-block': blockId, 'data-sequencer': options.sequencerBlock || '', 'data-pattern': options.pseudo?.title || '' });
  const touchLayout = window.matchMedia('(max-width: 700px), (pointer: coarse)').matches;   // phone / tablet: Hand tool, touch gestures, full-screen panel (css)
  setRackLayout(touchLayout);
  const toolButtons = new Map(TOOLS.map((tool) => [tool.id, el('button', { type: 'button', className: 'mls-tool', textContent: tool.icon, title: `${tool.label} (${tool.key.toUpperCase()})`, 'aria-label': `${tool.label} tool`, 'aria-pressed': String(tool.id === state.tool), onclick: () => setTool(tool.id) })]));
  if (touchLayout) toolButtons.set('pan', el('button', { type: 'button', className: 'mls-tool mls-hand', textContent: '✋', title: 'Hand: drag with one finger to scroll', 'aria-label': 'Hand tool', 'aria-pressed': 'false', onclick: () => setTool('pan') }));
  const btn = (text, title, onclick, extra = {}) => el('button', { type: 'button', className: 'mls-btn', textContent: text, title, 'aria-label': title, onclick: (event) => { onclick(event); grid.focus(); }, ...extra });
  const toggle = (button, on) => { button.classList.toggle('on', on); button.setAttribute('aria-pressed', String(on)); };
  const modeButtons = new Map([['roll', 'Roll', 'Roll: notes as bars'], ['grid', 'Grid', 'Grid: one row per sound']].map(([id, text, title]) => [id, el('button', { type: 'button', className: 'mls-tool mls-mode', textContent: text, title, 'aria-label': `${text} view`, 'aria-pressed': String(id === 'roll'), onclick: () => { setMode(id); grid.focus(); } })]));
  const rowAdd = el('select', { className: 'mls-rowadd', 'aria-label': 'Add a row', title: 'Add a row', hidden: true, onmousedown: () => fillRowAdd(), onfocus: () => fillRowAdd(), onchange: () => { const p = Number(rowAdd.value), drumPick = rowAdd.selectedOptions[0]?.dataset.kind === 'drum'; rowAdd.value = ''; if (drumPick && Number.isInteger(p)) state.laneDrum.add(p); if (Number.isInteger(p) && p >= 0) { state.gridPins = [...new Set([...state.gridPins, p])]; requestDraw(); } grid.focus(); } }, [el('option', { value: '', textContent: '+ Row' }), ...drumChoices().map((c) => el('option', { value: String(c.pitch), textContent: c.label }))]);
  const typingButton = btn('⌨', 'Typing keyboard (Ctrl+T)', () => setTyping(!state.typing), { 'aria-pressed': 'false' });
  const octaveLabel = el('span', { className: 'mls-octave', 'aria-live': 'polite', hidden: true });
  const octaveDown = btn('◀', 'Typing keyboard octave down ([)', () => shiftOctave(-1), { hidden: true });
  const octaveUp = btn('▶', 'Typing keyboard octave up (])', () => shiftOctave(1), { hidden: true });
  const midiButton = midiCan() ? btn('MIDI', 'MIDI in: a keyboard or a virtual MIDI cable plays the instrument of this clip (and records with ●)', () => { toggleMidi().then(() => { if (midiActive() && !midiNamesNow()) toast('No MIDI input found yet. Connect one and try again.'); }).catch((error) => toast(error.message || String(error))); }, { 'aria-pressed': 'false' }) : null;
  const touchButton = btn('🎹', 'Touch keyboard: play the instrument of this clip on the screen', () => { touchStrip.hidden = !touchStrip.hidden; toggle(touchButton, !touchStrip.hidden); touchButton.dataset.open = touchStrip.hidden ? '0' : '1'; vkb.releaseAll(); requestDraw(); }, { 'aria-pressed': 'false' });
  const recordButton = btn('●', 'Record: play the touch keyboard, a MIDI keyboard or the typing keyboard while the clip plays; the notes are written into the clip when it stops', () => toggleRecording(), { 'aria-pressed': 'false' });
  const vkb = createVirtualKeyboard({ onNoteOn: (pitch, velocity) => liveOn(pitch, velocity), onNoteOff: (pitch) => liveOff(pitch), octave: 3, octaves: 2, compact: true });
  const touchStrip = el('div', { className: 'mls-touchkeys', hidden: true }, [vkb.element]);
  const review = createTakeReview({ ppq, clipBars: () => endBars(), preview: (rows, bars, done) => previewTake(rows, bars, done), keep: (choice) => keepTake(choice) });
  const playButton = btn('▶', 'Play / pause (Ctrl+Space)', () => togglePlay(false), { 'aria-pressed': 'false' });
  const selButton = btn('▶ sel', 'Play only the selected notes', () => togglePlay(true));
  const stopButton = btn('■', 'Stop and return to the start', () => { stopPlayback(true); });
  const metroButton = btn('♪', 'Metronome', () => { state.metronome = !state.metronome; toggle(metroButton, state.metronome); }, { 'aria-pressed': 'false' });
  const loopButton = btn('⟲', 'Loop on/off', () => { state.loopOn = !state.loopOn; toggle(loopButton, state.loopOn); if (state.player) { const sel = state.player.sel; stopPlayback(false); togglePlay(sel); } requestDraw(); }, { 'aria-pressed': 'false' });
  const followButton = btn('⇥', 'Follow the playhead', () => { state.follow = !state.follow; toggle(followButton, state.follow); }, { 'aria-pressed': 'true' });
  toggle(followButton, true);
  const snapSelect = el('select', { className: 'mls-snap', 'aria-label': 'Snap to grid', title: 'Snap (hold Alt for free)', onchange: () => { state.snap = snapSelect.value; grid.focus(); requestDraw(); } }, [el('option', { value: 'free', textContent: 'Snap: free', hidden: true }), ...SNAPS.map((s) => el('option', { value: s.id, textContent: s.id === 'auto' ? 'Snap: Auto' : s.id === 'off' ? 'Snap: none' : `Snap: ${s.label}` }))]);
  const zoomOut = btn('−', 'Zoom out', () => zoomBy(0.8)), zoomIn = btn('+', 'Zoom in', () => zoomBy(1.25));
  const fitButton = btn('⤢', 'Fit the clip', () => fitView());
  // when the clip feeds more than one instrument (two llamas, a llama and a piano): the block that sounds it here is picked, not each lane; the song plays all of them
  const consumerEl = el('select', { className: 'mls-consumer', 'aria-label': 'Which block sounds this clip here', title: 'The block that sounds this clip while you work here (a preview: the song plays every block it feeds)', hidden: true });
  let consumerSign = '';
  const paintConsumers = () => {
    const list = audition.preview.consumers(), chosen = audition.preview.chosen(), sign = JSON.stringify([list, chosen]);
    if (sign === consumerSign) return;
    consumerSign = sign;
    consumerEl.hidden = list.length < 2;
    consumerEl.replaceChildren(...list.map((c) => el('option', { value: c.id, textContent: c.title + ' (' + c.tpl + ')', selected: c.id === chosen })));
    if (list.length > 1 && !consumerEl.isConnected) badgeEl.after(consumerEl);
  };
  consumerEl.onchange = async () => { audition.preview.choose(consumerEl.value); consumerSign = ''; try { await audition.preview.prepare(); } catch { /* the badge says it */ } paintConsumers(); requestDraw(); };
  const consumerTimer = setInterval(() => { if (!panel.isConnected) clearInterval(consumerTimer); else paintConsumers(); }, 1200);
  setTimeout(paintConsumers, 0);
  const badgeEl = el('span', { className: 'mls-badge', role: 'status', title: 'What you hear when you play a single note' });
  const closeButton = el('button', { type: 'button', className: 'mls-close', textContent: '✕', title: 'Close', 'aria-label': 'Close AMPLE Jam', onclick: () => { backStack = []; saveViewNow(); panel.close(); } });
  const menuButton = el('button', { type: 'button', className: 'mls-wbtn mls-menu-btn', textContent: '☰', title: 'Menu', 'aria-label': 'Menu', 'aria-haspopup': 'menu', 'aria-expanded': 'false' });
  const title = el('div', { className: 'mls-title metal-title' }, [el('span', { className: 'mls-eqbg', 'aria-hidden': 'true' }, Array.from({ length: 46 }, (_, i) => { const bar = el('i'); bar.style.setProperty('--d', (0.7 + ((i * 37) % 11) / 10 * 1.5).toFixed(2)); bar.style.setProperty('--dl', (((i * 13) % 10) / 10).toFixed(2)); return bar; })), jamIcon(), el('strong', { textContent: 'AMPLE Jam' }), el('span', { className: 'mls-clip', textContent: node.title }), badgeEl, menuButton, closeButton]);
  const toolsGroup = el('span', { className: 'mls-tools', role: 'group', 'aria-label': 'Tools' }, [...toolButtons.values()]);
  const toolbar = el('div', { className: 'mls-toolbar', role: 'toolbar', 'aria-label': 'AMPLE Jam tools' }, [el('span', { className: 'mls-tools mls-modes', role: 'group', 'aria-label': 'View' }, [...modeButtons.values()]), rowAdd, toolsGroup, typingButton, octaveDown, octaveLabel, octaveUp, ...(midiButton ? [midiButton] : []), touchButton, recordButton, playButton, selButton, stopButton, metroButton, loopButton, followButton, snapSelect, zoomOut, zoomIn, fitButton]);
  const ruler = el('canvas', { className: 'mls-ruler', 'aria-label': 'Ruler. Drag to make a loop bracket, click to set the playhead, double-click for the whole clip, right-click to clear.' });
  const keys = el('canvas', { className: 'mls-keys', 'aria-label': 'Piano keys. Press to hear a note.' });
  const grid = el('canvas', { className: 'mls-grid', tabIndex: 0, 'aria-label': 'Notes. Draw with the mouse, right-click deletes, Delete removes the selection, Space plays.' });
  const velocity = el('canvas', { className: 'mls-velocity', 'aria-label': 'Value of each note in the lower lane. Drag a bar to set it; Shift+drag draws a ramp; double-click resets it.' });
  const laneSelect = el('select', { className: 'mls-lane-select', 'aria-label': 'Lower lane', title: 'What the lower lane shows and edits', onchange: () => { state.lane = laneSelect.value; velocity.setAttribute('aria-label', `${LANE_DEFS[state.lane].label} of each note. Drag a bar to set it; Shift+drag draws a ramp; double-click resets it.`); requestDraw(); } }, Object.entries(LANE_DEFS).map(([id, def]) => el('option', { value: id, textContent: def.short, title: def.label })));
  const laneCorner = el('div', { className: 'mls-corner' }, [laneSelect]);
  const status = el('div', { className: 'mls-status', role: 'status', 'aria-live': 'polite' });
  const hintClose = el('button', { type: 'button', className: 'mls-hint-x', textContent: '✕', title: 'Hide this hint', 'aria-label': 'Hide the hint', onclick: () => { state.hintDismissed = true; markView(true); requestDraw(); grid.focus(); } });
  const hint = el('div', { className: 'mls-hint', role: 'note' }, [el('span', { textContent: touchLayout ? 'Tap to draw · long press a note deletes it · two fingers zoom and scroll · ✋ scrolls with one finger' : 'Click to draw · right-click deletes · drag an edge to resize · ? for shortcuts' }), hintClose]);
  const helpCard = el('div', { className: 'mls-help', role: 'dialog', 'aria-label': 'Shortcuts', hidden: true }, [
    el('div', { className: 'mls-help-head' }, [el('strong', { textContent: 'Shortcuts' }), el('button', { type: 'button', className: 'mls-help-x', textContent: '\u2715', title: 'Close', 'aria-label': 'Close the shortcuts', onclick: () => { helpCard.hidden = true; } })]),
    el('div', { className: 'mls-help-grid' }, SHORTCUT_GROUPS.map((group) => el('section', { className: 'mls-help-group' }, [
      el('h4', { textContent: group.title }),
      ...group.rows.map(([keys, what]) => el('div', { className: 'mls-help-row' }, [el('span', { className: 'mls-keys-cap' }, keys.flatMap((key, index) => [...(index ? [el('i', { textContent: '+' })] : []), el('kbd', { textContent: key })])), el('span', { className: 'mls-help-what', textContent: what })])),
    ]))),
  ]);
  const strip = el('div', { className: 'mls-strip', role: 'navigation', 'aria-label': 'Neighbouring clips', hidden: true });
  const sbH = el('div', { className: 'mls-sb mls-sb-h', hidden: true, 'aria-hidden': 'true' }, [el('i')]), sbV = el('div', { className: 'mls-sb mls-sb-v', hidden: true, 'aria-hidden': 'true' }, [el('i')]);
  const body = el('div', { className: 'mls-body' }, [el('div', { className: 'mls-corner' }), ruler, keys, grid, laneCorner, velocity, sbH, sbV, hint, helpCard]);
  const lanesCtl = createJamLanes({ panel, node, ppq, bar, locked, keyW: () => keyW, width: () => gridW(), xOf: (tick) => xOf(tick), atOf: (x) => atOf(x), requestDraw: () => requestDraw(), announce: (text) => { status.textContent = text; } });
  // the key of every tool is written on its button, and the voices behind the notes are named in their colours
  for (const [toolId, button] of toolButtons) { const key = TOOLS.find((t) => t.id === toolId)?.key; if (key) button.append(el('i', { className: 'mls-toolkey', textContent: key.toUpperCase(), 'aria-hidden': 'true' })); }
  const legend = options.ghostRows?.length ? el('div', { className: 'mls-legend', role: 'note', 'aria-label': 'The voices behind the notes' }, [el('span', { className: 'note', textContent: 'Behind:' }), ...options.ghostRows.map((row) => el('span', { className: 'mls-legend-item' }, [el('i', { className: 'mls-legend-dot', style: `background:${row.color}` }), row.title]))]) : null;
  panel.append(title, toolbar, strip, body, lanesCtl.element, review.element, touchStrip, ...(legend ? [legend] : []), status);
  document.body.append(panel);
  // the drag position is kept as a transform (see "floating" below); the window chrome slides the panel sideways through this when it changes width
  let offX = 0, offY = 0;
  const shiftPanel = (dx) => { const r = panel.getBoundingClientRect(); offX += Math.max(-r.left, Math.min(window.innerWidth - 80 - r.left, dx)); panel.style.transform = `translate3d(${offX}px, ${offY}px, 0)`; };
  const windowChrome = addWindowChrome(panel, title, closeButton, shiftPanel);
  openPanels.set(blockId, panel);
  panel.show();

  // ---- sizes and drawing ----
  const ratio = () => window.devicePixelRatio || 1;
  let keyW = KEY_W;   // 48 px of piano keys in the Roll, the 200 px channel rack in the Grid
  function layout() {
    const w = Math.max(200, body.clientWidth - keyW), h = Math.max(120, body.clientHeight - RULER_H - VEL_H);
    sizePianoRollCanvas(ruler, w, RULER_H, ratio()); sizePianoRollCanvas(keys, keyW, h, ratio());
    sizePianoRollCanvas(grid, w, h, ratio()); sizePianoRollCanvas(velocity, w, VEL_H, ratio());
    clampScroll(); lanesCtl.layout();
    // resizing a canvas clears it: draw in the same frame, not the next one, or a resize drag shows blank flashes
    if (frame) { cancelAnimationFrame(frame); frame = 0; }
    if (panel.isConnected) draw();
  }
  let frame = 0;
  function requestDraw() { if (!frame && panel.isConnected) frame = requestAnimationFrame(() => { frame = 0; draw(); }); }
  const tokens = () => { const css = getComputedStyle(panel), get = (name) => css.getPropertyValue(name).trim(); return { panel: get('--panel'), panel2: get('--panel2'), panel3: get('--panel3'), line: get('--line'), strong: get('--line-strong'), text: get('--text'), dim: get('--dim'), acc: get('--acc'), bad: get('--bad'), ok: get('--ok') }; };

  function draw() {
    const t = tokens(), list = rows(), end = endTick(), w = gridW(), h = gridH();
    if (state.player) { state.litNotes = soundingRows(list, state.playhead); state.litPitches = soundingPitches(list, state.playhead); } else if (state.litNotes.size) { state.litNotes = new Set(); state.litPitches = new Set(); }
    drawGrid(grid.getContext('2d'), t, list, end, w, h);
    drawKeys(keys.getContext('2d'), t, h);
    drawRuler(ruler.getContext('2d'), t, end, w);
    drawVelocity(velocity.getContext('2d'), t, list, w); lanesCtl.redraw();
    updateScrollbars();
    panel.dataset.playing = state.player ? '1' : '';   // the little equaliser in the title bar moves while something plays
    if (state.player) panel.style.setProperty('--beat', `${Math.max(0.25, Math.min(1.2, 60 / bpm()))}s`);
    // the glow of the window follows the music while playing: one soft swell per beat (a little higher on the first beat of the bar) plus the energy of the notes that sound, smoothed so nothing flashes
    if (state.player) {
      const beat = state.playhead / ppq, frac = beat - Math.floor(beat), swell = Math.pow(0.5 + 0.5 * Math.cos(2 * Math.PI * frac), 1.4), accent = Math.floor(beat) % 4 === 0 ? 1 : 0.75;
      const energy = Math.min(1, list.reduce((sum, r) => sum + (state.litNotes.has(r.index) ? r.velocity : 0), 0) / 260);
      state.glow = (state.glow ?? 0) * 0.85 + energy * 0.15;
      panel.style.setProperty('--glow-pulse', Math.min(1, 0.2 + 0.3 * swell * accent + 0.5 * state.glow).toFixed(3));
    } else { state.glow = 0; panel.style.removeProperty('--glow-pulse'); }
    Object.assign(panel.dataset, { loop: state.bracket ? `${state.bracket.start}-${state.bracket.end}` : '', playhead: String(Math.round(state.playhead)), songhead: state.songHead == null ? '' : String(state.songHead), pxBeat: state.pxBeat.toFixed(2), ghosts: String(options.ghostRows?.length ?? ''), tool: state.tool, snapTicks: String(Math.round(beats() * ppq)), scrollX: String(Math.round(state.scrollX)), hidden: String(list.filter(hasHiddenData).length), mode: state.mode, gridRows: state.mode === 'grid' ? gridList().join(',') : '', gridRowH: String(state.gridRowH), muted: [...state.muted].sort((a, b) => a - b).join(','), gridScrollY: String(Math.round(state.gridScrollY)), stepTicks: String(stepLen()), scrollY: String(Math.round(state.scrollY)), rowH: state.rowH.toFixed(2), snap: state.snap, follow: state.followPaused ? 'paused' : state.follow ? 'on' : 'off', lit: [...state.litNotes].join(','), litKeys: [...litKeysNow()].sort((a, b) => a - b).join(','), selected: [...state.selected].sort((a, b) => a - b).join(',') });
    hint.hidden = !(model.notes.length === 0 && !state.hintDismissed) || state.mode === 'grid';
    markView(false);
    status.textContent = `${model.notes.length} note${model.notes.length === 1 ? '' : 's'} · ${endBars()} bar${endBars() === 1 ? '' : 's'}${state.selected.size ? ` · ${state.selected.size} selected` : ''}${locked ? ' · read-only' : ''}`;
  }
  /** The piano keys that are lit: pressed with the mouse, typed, played on a MIDI keyboard or sounding in the playback. */
  const litKeysNow = () => new Set([...state.litKey, ...state.litTyped.values(), ...state.litMidi.values(), ...state.litPitches]);
  // the bend of a note: a dashed line from its own pitch at the start to where it glides to at the end, a dot there and the semitones next to it
  function drawBend(c, x, width, note) {
    const y0 = yOf(note.pitch) + state.rowH / 2, y1 = yOf(note.pitch + note.bend / 100) + state.rowH / 2, x1 = x + width;
    c.save(); c.strokeStyle = '#ffd34d'; c.lineWidth = 2; c.globalAlpha = 0.95; c.setLineDash([4, 3]);
    c.beginPath(); c.moveTo(x + 2, y0); c.lineTo(x1 - 2, y1); c.stroke(); c.setLineDash([]);
    c.fillStyle = '#ffd34d'; c.beginPath(); c.arc(x1 - 2, y1, 3, 0, 2 * Math.PI); c.fill();
    c.font = '600 11px system-ui'; c.fillText(`${note.bend > 0 ? '+' : ''}${Math.round(note.bend) / 100}`, x1 + 4, y1 + (note.bend > 0 ? -3 : 12)); c.restore();
  }

  function drawGrid(c, t, list, end, w, h) {
    c.clearRect(0, 0, w, h);
    if (state.mode === 'grid') { drawSteps(c, t, list, end, w, h); return; }
    const scale = currentScale(), inScale = scaleSet(scale);
    const first = Math.max(0, 127 - Math.ceil((state.scrollY + h) / state.rowH)), last = Math.min(127, 127 - Math.floor(state.scrollY / state.rowH));
    for (let p = first; p <= last; p++) { c.fillStyle = BLACK.has(p % 12) ? t.panel3 : t.panel2; c.fillRect(0, yOf(p), w, state.rowH); if (inScale) { if (!inScale.has(p % 12)) { c.fillStyle = '#000'; c.globalAlpha = 0.16; c.fillRect(0, yOf(p), w, state.rowH); } else if (isRoot(p, scale)) { c.fillStyle = t.acc; c.globalAlpha = 0.14; c.fillRect(0, yOf(p), w, state.rowH); } c.globalAlpha = 1; } c.fillStyle = t.line; c.globalAlpha = 0.5; c.fillRect(0, yOf(p) + state.rowH - 1, w, 1); c.globalAlpha = 1; }
    for (const level of gridLevels(state.pxBeat)) {
      const step = level.beats * state.pxBeat, from = Math.floor(state.scrollX / step);
      c.strokeStyle = level.kind === 'bar' ? t.strong : t.line; c.globalAlpha = level.kind === 'step' ? 0.7 * level.alpha : level.kind === 'beat' ? 0.9 * level.alpha : 1; c.lineWidth = 1; c.beginPath();
      for (let i = from; i * step - state.scrollX < w; i++) { const x = Math.round(i * step - state.scrollX) + 0.5; c.moveTo(x, 0); c.lineTo(x, h); }
      c.stroke();
    }
    c.globalAlpha = 1;
    const endX = xOf(end);
    if (endX < w) { c.fillStyle = t.panel; c.globalAlpha = 0.6; c.fillRect(Math.max(0, endX), 0, w, h); c.globalAlpha = 1; c.fillStyle = endTone(); c.fillRect(Math.round(endX) - 1, 0, 2, h); }
    if (state.bracket) { c.fillStyle = t.acc; c.globalAlpha = 0.08; c.fillRect(xOf(state.bracket.start), 0, xOf(state.bracket.end) - xOf(state.bracket.start), h); c.globalAlpha = 1; }
    const high = document.body.classList.contains('hc') || document.documentElement.dataset.theme === 'high-contrast';
    if (state.mode !== 'grid') for (const gh of ghostList()) {
      c.strokeStyle = gh.color; c.globalAlpha = 0.5;
      for (const n of gh.notes) { const x = xOf(n.at), gw = Math.max(2, (n.length / ppq) * state.pxBeat - 1), y = yOf(n.pitch); if (x + gw < 0 || x > w || y + state.rowH < 0 || y > h) continue; c.strokeRect(x + 0.5, y + 1.5, Math.max(1, gw - 1), state.rowH - 3); }
      c.globalAlpha = 1;
    }
    for (const note of list) {
      const x = xOf(note.at), width = Math.max(3, (note.length / ppq) * state.pxBeat), y = yOf(note.pitch);
      if (x + width < 0 || x > w || y + state.rowH < 0 || y > h) continue;
      const fill = options.voiceColor ? shadeOf(options.voiceColor, note.velocity) : velocityColor(note.velocity), selected = state.selected.has(note.index), outside = note.at >= end;
      drawPianoRollNote(c, note, { x, y: y + 1, width, height: state.rowH - 2 }, { alpha: (outside ? 0.4 : 1) * (state.groupFilter && note.grp !== state.groupFilter ? 0.3 : 1), fill, shade: shadeHsl(fill, -18), selected, highContrast: high, drum: note.channel === 9 });
      if (note.grp) { c.fillStyle = GROUP_COLORS[(note.grp - 1) % GROUP_COLORS.length]; c.fillRect(x, y + 1, Math.min(3, width), state.rowH - 2); }
      if (state.litNotes.has(note.index)) { c.fillStyle = '#fff'; c.globalAlpha = 0.55; c.fillRect(x, y + 1, width, state.rowH - 2); c.globalAlpha = 1; }
      if (selected) { c.strokeStyle = t.text; c.lineWidth = 1; c.strokeRect(x + 0.5, y + 1.5, width - 1, state.rowH - 3); }
      if (width > 10) { c.fillStyle = t.text; c.globalAlpha = 0.5; c.fillRect(x + width - 4, y + 3, 2, state.rowH - 6); c.globalAlpha = 1; }   // the resize tab
      if (hasHiddenData(note)) { c.fillStyle = t.text; c.beginPath(); c.arc(x + 4, y + 4, 2, 0, 6.3); c.fill(); }
      if (note.bend) drawBend(c, x, width, note);
      if (width > 28 && state.rowH >= 12) { c.fillStyle = t.text; const fs = Math.max(10, Math.min(18, Math.round(state.rowH * 0.62))); c.font = `600 ${fs}px system-ui`; c.fillText(`${NAMES[note.pitch % 12]}${Math.floor(note.pitch / 12) - 1}`, x + 6, y + state.rowH / 2 + fs * 0.34); }
    }
    if (state.drag?.kind === 'zoom') { const d = state.drag; c.strokeStyle = t.ok; c.setLineDash([4, 3]); c.strokeRect(Math.min(d.x0, d.x1), Math.min(d.y0, d.y1), Math.abs(d.x1 - d.x0), Math.abs(d.y1 - d.y0)); c.setLineDash([]); }
    if (state.drag?.kind === 'rubber') { const d = state.drag; c.strokeStyle = t.acc; c.strokeRect(Math.min(d.x0, d.x1), Math.min(d.y0, d.y1), Math.abs(d.x1 - d.x0), Math.abs(d.y1 - d.y0)); }
    const px = xOf(state.playhead);
    if (px >= 0 && px <= w) { c.fillStyle = t.ok; c.fillRect(Math.round(px), 0, 1, h); }
    if (state.songHead != null) { const sx = xOf(state.songHead); if (sx >= 0 && sx <= w) { c.fillStyle = t.acc; c.globalAlpha = 0.22; c.fillRect(Math.round(sx) - 3, 0, 8, h); c.globalAlpha = 1; c.fillRect(Math.round(sx), 0, 2, h); } }
    if (state.drag?.kind === 'range') { const d = state.drag, a = xOf(Math.min(d.a, d.b)), b = xOf(Math.max(d.a, d.b)); c.fillStyle = t.acc; c.globalAlpha = 0.18; c.fillRect(a, 0, b - a, h); c.globalAlpha = 1; }
  }
  const roundRect = (c, x, y, w, h, left, right) => { c.beginPath(); c.moveTo(x + left, y); c.lineTo(x + w - right, y); c.arcTo(x + w, y, x + w, y + right, right); c.lineTo(x + w, y + h - right); c.arcTo(x + w, y + h, x + w - right, y + h, right); c.lineTo(x + left, y + h); c.arcTo(x, y + h, x, y + h - left, left); c.lineTo(x, y + left); c.arcTo(x, y, x + left, y, left); c.closePath(); };
  const laneLabel = (pitch) => state.laneNames.get(pitch) || (options.instrument && !state.laneDrum.has(pitch) ? noteLabel(pitch) : pitchLabel(pitch));
  function knobCap(c, cx, cy, r, muted) {
    c.save(); c.globalAlpha = muted ? 0.45 : 1; c.lineCap = 'round';
    c.strokeStyle = 'rgba(0,0,0,0.38)'; c.lineWidth = 1; c.beginPath();
    for (let i = 0; i < 18; i++) { const a = (i / 18) * Math.PI * 2; c.moveTo(cx + Math.cos(a) * (r - 2.2), cy + Math.sin(a) * (r - 2.2)); c.lineTo(cx + Math.cos(a) * (r - 0.6), cy + Math.sin(a) * (r - 0.6)); }
    c.stroke();
    c.strokeStyle = 'rgba(255,255,255,0.42)'; c.lineWidth = 1.4; c.beginPath(); c.arc(cx, cy, r - 1, Math.PI * 1.05, Math.PI * 1.7); c.stroke();
    c.strokeStyle = 'rgba(0,0,0,0.5)'; c.lineWidth = 1.6; c.beginPath(); c.arc(cx, cy, r - 0.8, Math.PI * 0.1, Math.PI * 0.7); c.stroke();
    c.restore();
  }
  /** The Grid's left side, like a channel rack: a LED (on = the row sounds), a volume knob (the velocity of the row) and the name of the sound. */
  function drawRack(c, t, h, litKeys) {
    const rh = state.gridRowH, volumes = new Map(), pans = new Map(), selectedPitches = new Set();
    for (const r of rows()) { const a = volumes.get(r.pitch) || [0, 0]; a[0] += r.velocity; a[1]++; volumes.set(r.pitch, a); const p = pans.get(r.pitch) || [0, 0]; p[0] += r.pan ?? 0; p[1]++; pans.set(r.pitch, p); if (state.selected.has(r.index)) selectedPitches.add(r.pitch); }
    gridList().forEach((pitch, i) => {
      const y = i * rh - state.gridScrollY; if (y + rh < 0 || y > h) return;
      const lit = litKeys.has(pitch), muted = state.muted.has(pitch), cy = y + rh / 2, sum = volumes.get(pitch);
      c.fillStyle = i % 2 ? t.panel2 : t.panel3; c.fillRect(0, y, keyW, rh);
      // the LED: a metal bezel round a dark well, a glass lens (a bright core and a glow when on) and a small reflection
      c.save();
      const bezel = c.createLinearGradient(LED_X, cy - 8, LED_X, cy + 8); bezel.addColorStop(0, '#9aa7b5'); bezel.addColorStop(0.5, '#4a5560'); bezel.addColorStop(1, '#262d35');
      c.beginPath(); c.arc(LED_X, cy, 7.6, 0, Math.PI * 2); c.fillStyle = bezel; c.fill(); c.lineWidth = 1; c.strokeStyle = '#0a0d11'; c.stroke();
      c.beginPath(); c.arc(LED_X, cy, 5.6, 0, Math.PI * 2); c.fillStyle = '#0d1116'; c.fill();
      if (!muted) { c.shadowColor = '#9cf04f'; c.shadowBlur = 10; }
      const lens = c.createRadialGradient(LED_X - 1.2, cy - 1.4, 0.4, LED_X, cy, 5.2);
      if (muted) { lens.addColorStop(0, '#46544c'); lens.addColorStop(1, '#161d1a'); } else { lens.addColorStop(0, '#f4ffd9'); lens.addColorStop(0.35, '#a6f04f'); lens.addColorStop(1, '#3f9014'); }
      c.beginPath(); c.arc(LED_X, cy, 4.8, 0, Math.PI * 2); c.fillStyle = lens; c.fill(); c.shadowBlur = 0;
      c.fillStyle = '#ffffff'; c.globalAlpha = muted ? 0.18 : 0.6; c.beginPath(); c.ellipse(LED_X - 1.5, cy - 2, 1.9, 1.1, -0.5, 0, Math.PI * 2); c.fill();
      c.restore();
      // the pan knob: an arc from the middle (straight up) to the left or the right
      const panSum = pans.get(pitch), pan = panSum ? panSum[0] / panSum[1] : 0, pr = Math.min(11, rh / 2 - 3), pa = -Math.PI / 2 + (pan / 100) * Math.PI * 0.75;
      c.save(); c.globalAlpha = muted ? 0.45 : 1;
      const pface = c.createRadialGradient(PAN_X - 3, cy - 4, 1, PAN_X, cy, pr); pface.addColorStop(0, '#6a7686'); pface.addColorStop(1, '#2a323c');
      c.beginPath(); c.arc(PAN_X, cy, pr, 0, Math.PI * 2); c.fillStyle = pface; c.fill(); c.lineWidth = 1; c.strokeStyle = '#10151b'; c.stroke();
      c.lineCap = 'round'; c.lineWidth = 2.4; c.strokeStyle = '#141a21'; c.beginPath(); c.arc(PAN_X, cy, pr + 3, -Math.PI * 1.25, Math.PI * 0.25); c.stroke();
      if (Math.abs(pan) > 1) { c.strokeStyle = '#2de2ff'; c.beginPath(); c.arc(PAN_X, cy, pr + 3, Math.min(-Math.PI / 2, pa), Math.max(-Math.PI / 2, pa)); c.stroke(); }
      c.lineWidth = 1.6; c.strokeStyle = '#e8eef5'; c.beginPath(); c.moveTo(PAN_X + Math.cos(pa) * pr * 0.35, cy + Math.sin(pa) * pr * 0.35); c.lineTo(PAN_X + Math.cos(pa) * pr * 0.9, cy + Math.sin(pa) * pr * 0.9); c.stroke();
      c.restore(); knobCap(c, PAN_X, cy, pr, muted);
      // the volume knob: an arc from the lower left over the top to the lower right
      const kr = Math.min(11, rh / 2 - 3), volume = sum ? sum[0] / sum[1] : 0, a0 = Math.PI * 0.75, a1 = a0 + Math.PI * 1.5 * (volume / 127);
      c.save(); c.globalAlpha = muted ? 0.45 : 1;
      const face = c.createRadialGradient(KNOB_X - 3, cy - 4, 1, KNOB_X, cy, kr); face.addColorStop(0, '#6a7686'); face.addColorStop(1, '#2a323c');
      c.beginPath(); c.arc(KNOB_X, cy, kr, 0, Math.PI * 2); c.fillStyle = face; c.fill(); c.lineWidth = 1; c.strokeStyle = '#10151b'; c.stroke();
      c.lineCap = 'round'; c.lineWidth = 2.4; c.strokeStyle = '#141a21'; c.beginPath(); c.arc(KNOB_X, cy, kr + 3, a0, a0 + Math.PI * 1.5); c.stroke();
      if (sum) { c.strokeStyle = velocityColor(volume); c.beginPath(); c.arc(KNOB_X, cy, kr + 3, a0, a1); c.stroke(); }
      c.lineWidth = 1.6; c.strokeStyle = '#e8eef5'; c.beginPath(); c.moveTo(KNOB_X + Math.cos(a1) * kr * 0.35, cy + Math.sin(a1) * kr * 0.35); c.lineTo(KNOB_X + Math.cos(a1) * kr * 0.9, cy + Math.sin(a1) * kr * 0.9); c.stroke();
      c.restore(); knobCap(c, KNOB_X, cy, kr, muted);
      // the row number, in a small box (a click selects the row's notes)
      if (NUM_X < NAME_X - 2) { roundRect(c, NUM_X, y + 3, 22, rh - 6, 3, 3); c.fillStyle = selectedPitches.has(pitch) ? '#46566a' : '#1d242d'; c.fill(); c.strokeStyle = '#0b0f14'; c.lineWidth = 1; c.stroke();
      c.fillStyle = muted ? '#7d8a99' : '#cfd8e3'; c.font = '11px system-ui'; c.textAlign = 'center'; c.textBaseline = 'middle'; c.fillText(String(i + 1), NUM_X + 11, cy + 0.5); c.textAlign = 'start'; c.textBaseline = 'alphabetic'; }   // no row number on a phone: the rack is narrower
      // the name, as a button
      const nx = NAME_X, nw = keyW - NAME_X - 14, nh = rh - 6;
      roundRect(c, nx, y + 3, nw, nh, 4, 4);
      const plate = c.createLinearGradient(0, y + 3, 0, y + 3 + nh); plate.addColorStop(0, lit ? shadeHsl(t.acc, 6) : '#445060'); plate.addColorStop(1, lit ? t.acc : '#2f3946');
      c.fillStyle = plate; c.fill(); c.strokeStyle = lit ? '#fff' : '#0b0f14'; c.globalAlpha = lit ? 0.7 : 1; c.lineWidth = 1; c.stroke(); c.globalAlpha = 1;
      c.fillStyle = lit ? t.panel : t.text; c.globalAlpha = muted ? 0.45 : 1; c.font = `600 ${Math.min(13, Math.max(11, Math.round(rh * 0.46)))}px system-ui`; c.textBaseline = 'middle';
      c.fillText(laneLabel(pitch), nx + 8, cy + 0.5, nw - 14); c.globalAlpha = 1; c.textBaseline = 'alphabetic';
      // the selection bar: lit for the row that holds selected notes or was touched last
      const active = selectedPitches.has(pitch) || state.activeRow === pitch;
      roundRect(c, keyW - 9, y + 4, 4, rh - 8, 2, 2); c.fillStyle = active ? '#9cf04f' : '#1b222b'; if (active) { c.save(); c.shadowColor = '#9cf04f'; c.shadowBlur = 6; c.fill(); c.restore(); } else c.fill();
    });
  }
  /** Grid view: one row per sound, one cell per step; a filled cell is shaded by its velocity (soft = light, loud = saturated). */
  function drawSteps(c, t, list, end, w, h) {
    const pitches = gridList(), step = stepLen(), first = Math.max(0, Math.floor(atOf(0) / step)), last = Math.min(Math.ceil(end / step) - 1, Math.ceil(atOf(w) / step)), gap = (step / ppq) * state.pxBeat > 14 ? 2 : (step / ppq) * state.pxBeat > 8 ? 1 : 0;
    pitches.forEach((pitch, i) => {
      const y = i * state.gridRowH - state.gridScrollY; if (y + state.gridRowH < 0 || y > h) return;
      const muted = state.muted.has(pitch);
      c.fillStyle = i % 2 ? t.panel2 : t.panel3; c.fillRect(0, y, w, state.gridRowH);
      for (let col = first; col <= last; col++) {
        let x = xOf(col * step), width = (step / ppq) * state.pxBeat - gap;
        const info = cellInfo(list, col, pitch, step), group = Math.floor((col * step) / ppq) % 2;
        if (x + width < 0 || x > w) continue;
        const py = y + 3, ph = state.gridRowH - 6, rad = Math.min(4, ph / 4);
        if (!info.rows.length) {
          const barStart = col * step % (barBeats() * ppq) === 0, face = group ? (barStart ? ['#6b565e', '#4a3a40'] : ['#54444a', '#382d32']) : (barStart ? ['#66717f', '#454e59'] : ['#444e5b', '#2c343e']);
          roundRect(c, x, py, width, ph, rad, rad); c.fillStyle = '#080b0f'; c.fill();   // the rim
          if (width > 5 && ph > 8) {
            roundRect(c, x + 1, py + 1, width - 2, ph - 2, Math.max(1, rad - 1), Math.max(1, rad - 1));
            const body = c.createLinearGradient(0, py, 0, py + ph); body.addColorStop(0, face[0]); body.addColorStop(1, face[1]); c.fillStyle = body; c.fill();
            const mid = py + Math.round(ph / 2);
            c.fillStyle = '#ffffff'; c.globalAlpha = 0.09; c.fillRect(x + 1, py + 1, Math.max(0, width - 2), Math.max(0, mid - py - 1));   // the upper half catches the light
            c.fillStyle = '#000000'; c.globalAlpha = 0.16; c.fillRect(x + 1, mid + 1, Math.max(0, width - 2), Math.max(0, py + ph - mid - 2));   // the lower half is in shade
            c.globalAlpha = 0.55; c.fillRect(x + 1, mid, Math.max(0, width - 2), 1);   // the seam between them
            c.fillStyle = '#ffffff'; c.globalAlpha = 0.2; c.fillRect(x + 2, py + 1, Math.max(0, width - 4), 1);   // the light edge on top
            c.fillStyle = '#ffffff'; c.globalAlpha = 0.07; c.fillRect(x + 2, py + ph - 2, Math.max(0, width - 4), 1); c.globalAlpha = 1;   // a faint edge below the seam
          }
          continue;
        }
        if (info.tail) { x -= gap; width += gap; } else if (!info.last) width += gap;   // the cells of one long note join up
        const loud = Math.max(...info.rows.map((r) => r.velocity));
        const base = velocityColor(loud), left = info.tail ? 0 : rad, right = info.last ? rad : 0;
        c.globalAlpha = muted ? 0.3 : 1;
        roundRect(c, x, py, width, ph, left, right); c.fillStyle = shadeHsl(base, -30); c.fill();   // the rim, a dark shade of the colour
        c.save(); if (!muted && width > 8) { c.shadowColor = base; c.shadowBlur = 7; roundRect(c, x + 1, py + 1, width - 2, ph - 2, Math.max(1, left - 1), Math.max(1, right - 1)); c.fillStyle = base; c.fill(); } c.restore();   // a glow round a lit pad
        roundRect(c, x + 1, py + 1, Math.max(1, width - 2), Math.max(1, ph - 2), Math.max(1, left - 1), Math.max(1, right - 1));
        const lit = c.createLinearGradient(0, py, 0, py + ph); lit.addColorStop(0, shadeHsl(base, 20)); lit.addColorStop(0.5, base); lit.addColorStop(1, shadeHsl(base, -14));
        c.fillStyle = lit; c.globalAlpha = (muted ? 0.3 : 1) * (0.55 + 0.45 * cellShade(loud)) * (info.tail ? 0.85 : 1); c.fill(); c.globalAlpha = muted ? 0.3 : 1;
        const midLit = py + Math.round(ph / 2), dim = muted ? 0.3 : 1;
        c.fillStyle = '#000000'; c.globalAlpha = dim * 0.14; c.fillRect(x + 1, midLit + 1, Math.max(0, width - 2), Math.max(0, py + ph - midLit - 2));   // the lower half is in shade
        c.globalAlpha = dim * 0.45; c.fillRect(x + 1, midLit, Math.max(0, width - 2), 1);   // the seam of the rocker
        c.fillStyle = '#ffffff'; c.globalAlpha = dim * 0.4; c.fillRect(x + 2, py + 1, Math.max(0, width - 4), 1);   // the light edge on top
        c.globalAlpha = dim * 0.12; c.fillRect(x + 2, midLit + 1, Math.max(0, width - 4), 1); c.globalAlpha = 1;
        if (info.rows.some((r) => state.litNotes.has(r.index))) { c.fillStyle = '#fff'; c.globalAlpha = 0.55; c.fill(); c.globalAlpha = 1; }
      }
    });
    // the bend of a note: a line from its own pitch at the start to where it glides to at the end, with the semitones next to it
    for (const row of rows()) if (row.bend) drawBend(c, xOf(row.at), xOf(row.at + row.length) - xOf(row.at), row);
    for (const level of gridLevels(state.pxBeat)) { if (level.kind === 'step') continue; const gs = level.beats * state.pxBeat; c.strokeStyle = level.kind === 'bar' ? t.strong : t.line; c.globalAlpha = level.kind === 'bar' ? 1 : 0.5; c.beginPath(); for (let i = Math.floor(state.scrollX / gs); i * gs - state.scrollX < w; i++) { const x = Math.round(i * gs - state.scrollX) + 0.5; c.moveTo(x, 0); c.lineTo(x, h); } c.stroke(); c.globalAlpha = 1; }
    const endX = xOf(end);
    if (endX < w) { c.fillStyle = t.panel; c.globalAlpha = 0.6; c.fillRect(Math.max(0, endX), 0, w, h); c.globalAlpha = 1; c.fillStyle = endTone(); c.fillRect(Math.round(endX) - 1, 0, 2, h); }
    if (state.bracket) { c.fillStyle = t.acc; c.globalAlpha = 0.08; c.fillRect(xOf(state.bracket.start), 0, xOf(state.bracket.end) - xOf(state.bracket.start), h); c.globalAlpha = 1; }
    const px = xOf(state.playhead);
    if (px >= 0 && px <= w) { c.fillStyle = t.ok; c.fillRect(Math.round(px), 0, 1, h); }
    if (state.songHead != null) { const sx = xOf(state.songHead); if (sx >= 0 && sx <= w) { c.fillStyle = t.acc; c.globalAlpha = 0.22; c.fillRect(Math.round(sx) - 3, 0, 8, h); c.globalAlpha = 1; c.fillRect(Math.round(sx), 0, 2, h); } }
  }
  function drawKeys(c, t, h) {
    c.clearRect(0, 0, keyW, h);
    const litKeys = litKeysNow(), drumPitches = new Set(rows().filter((r) => r.channel === 9).map((r) => r.pitch));
    if (state.mode === 'grid') { drawRack(c, t, h, litKeys); return; }
    for (let p = 0; p <= 127; p++) {
      const y = yOf(p); if (y + state.rowH < 0 || y > h) continue;
      const black = BLACK.has(p % 12), lit = litKeys.has(p);
      c.fillStyle = lit ? t.acc : black ? t.panel3 : t.text; c.globalAlpha = lit || black ? 1 : 0.9; c.fillRect(0, y, black ? keyW * 0.62 : keyW, state.rowH - 1); c.globalAlpha = 1;
      if (drumPitches.has(p)) { c.fillStyle = '#ff4fd8'; c.beginPath(); c.arc(keyW - 7, y + state.rowH / 2, Math.max(2, Math.min(3.5, state.rowH / 4)), 0, 6.3); c.fill(); }
      if (p % 12 === 0) { c.fillStyle = lit ? t.panel : t.panel3; const fs = Math.max(10, Math.min(16, Math.round(state.rowH * 0.6))); c.font = `600 ${fs}px system-ui`; c.fillText(`C${Math.floor(p / 12) - 1}`, keyW - 4 - fs * 1.5, y + state.rowH / 2 + fs * 0.34); }
    }
  }
  function drawRuler(c, t, end, w) {
    c.clearRect(0, 0, w, RULER_H); c.fillStyle = t.panel2; c.fillRect(0, 0, w, RULER_H);
    if (state.bracket) {
      const a = xOf(state.bracket.start), b = xOf(state.bracket.end);
      c.fillStyle = t.acc; c.globalAlpha = state.loopOn ? 0.55 : 0.28; c.fillRect(a, 2, b - a, RULER_H - 4); c.globalAlpha = 1; c.fillRect(a - 1, 2, 3, RULER_H - 4); c.fillRect(b - 2, 2, 3, RULER_H - 4);
    }
    c.fillStyle = t.dim; c.font = '10px system-ui'; c.strokeStyle = t.strong;
    const barPx = 4 * state.pxBeat, labelEvery = Math.max(1, Math.ceil(36 / barPx));
    for (let i = Math.floor(state.scrollX / barPx); i * barPx - state.scrollX < w; i++) { const x = Math.round(i * barPx - state.scrollX) + 0.5; c.beginPath(); c.moveTo(x, 12); c.lineTo(x, RULER_H); c.stroke(); if (i % labelEvery === 0) c.fillText(String(i + 1), x + 3, 10); }
    if (state.pxBeat >= 24) { c.beginPath(); for (let i = Math.floor(state.scrollX / state.pxBeat); i * state.pxBeat - state.scrollX < w; i++) { const x = Math.round(i * state.pxBeat - state.scrollX) + 0.5; c.moveTo(x, 20); c.lineTo(x, RULER_H); } c.stroke(); }
    const endX = xOf(end);
    if (endX >= 0 && endX < w + 60) { c.fillStyle = endTone(); c.fillRect(Math.round(endX) - 1, 0, 2, RULER_H); c.font = '10px system-ui'; c.fillText('+ bar', Math.round(endX) + 4, RULER_H - 6); }
    const px = xOf(state.playhead);
    if (px >= 0 && px <= w) { c.fillStyle = t.ok; c.beginPath(); c.moveTo(px - 5, 0); c.lineTo(px + 5, 0); c.lineTo(px, 9); c.fill(); }
    if (state.songHead != null) { const sx = xOf(state.songHead); if (sx >= 0 && sx <= w) { c.fillStyle = t.acc; c.beginPath(); c.moveTo(sx - 6, 0); c.lineTo(sx + 6, 0); c.lineTo(sx, 12); c.fill(); } }
  }
  function drawVelocity(c, t, list, w) {
    const def = LANE_DEFS[state.lane], inner = VEL_H - 6, mid = 3 + inner / 2;
    c.clearRect(0, 0, w, VEL_H); c.fillStyle = t.panel3; c.fillRect(0, 0, w, VEL_H);
    if (def.mid) { c.fillStyle = t.line; c.fillRect(0, Math.round(mid), w, 1); }
    // the scale (laneScale): a faint line and its value at a quarter, half, three quarters and the top of the lane, so a bar can be read and dragged to a value
    const marks = [0.25, 0.5, 0.75, 1].map((frac) => ({ frac, value: Math.round(def.min + frac * (def.max - def.min)) }));
    velocity.dataset.laneScale = marks.map((m) => m.value).join(' ');
    c.save(); c.font = '700 12px ui-monospace,Consolas,monospace'; c.textBaseline = 'middle';
    for (const m of marks) {
      const y = Math.round(VEL_H - 3 - inner * m.frac) + 0.5;
      c.strokeStyle = t.line; c.globalAlpha = 0.6; c.beginPath(); c.moveTo(0, y); c.lineTo(w, y); c.stroke();
      c.globalAlpha = 1; c.fillStyle = t.dim || '#8a94a3'; c.fillText(String(m.value), 3, Math.max(5, y - (m.frac === 1 ? -4 : 0)));
    }
    c.restore();
    for (const note of list) {
      const x = xOf(note.at); if (x < -4 || x > w) continue;
      const value = laneValueOf(note, def), frac = laneFraction(value, def);
      c.fillStyle = state.lane === 'velocity' ? velocityColor(note.velocity) : t.ok; c.globalAlpha = state.selected.has(note.index) ? 1 : 0.75;
      if (def.mid) { const y = 3 + inner * (1 - frac), top = Math.min(y, mid), height = Math.max(2, Math.abs(y - mid)); c.fillRect(Math.round(x), Math.round(top), 4, Math.round(height)); }
      else { const height = Math.max(2, inner * frac); c.fillRect(Math.round(x), Math.round(VEL_H - 3 - height), 4, Math.round(height)); }
      c.globalAlpha = 1;
    }
  }

  // ---- view actions ----
  function fitView() {
    const list = rows(), end = endTick();
    state.pxBeat = Math.max(MIN_BEAT_PX, Math.min(MAX_BEAT_PX, (gridW() - 24) / (end / ppq)));
    state.scrollX = 0; state.scrollY = centerOnNotes(list, state.rowH, gridH()); clampScroll(); requestDraw();
  }
  const viewNow = () => ({ pxBeat: state.pxBeat, rowH: state.rowH, scrollX: state.scrollX, scrollY: state.scrollY });
  function applyView(v) { state.pxBeat = v.pxBeat; state.rowH = v.rowH; state.scrollX = v.scrollX; state.scrollY = v.scrollY; clampScroll(); requestDraw(); }
  const pauseFollow = () => { if (state.player) state.followPaused = true; };
  // zooming in on the time also makes the rows a little taller (and zooming out a little lower), but much less than the time: the notes grow with the view without the keys running away
  function zoomBy(factor, anchorX = gridW() / 2, axis = 'time', anchorY = gridH() / 2) {
    const pointer = { x: anchorX, y: anchorY };
    let view = zoomAnchor(viewNow(), pointer, factor, axis);
    if (axis === 'time' && state.mode !== 'grid') view = zoomAnchor(view, pointer, Math.pow(factor, 0.3), 'rows');
    applyView(view);
  }
  function setTimeView(v) { state.pxBeat = v.pxBeat; state.scrollX = v.scrollX; clampScroll(); requestDraw(); }
  /** Grid view: Alt+wheel makes the rows taller or shorter around the pointer (14-56 px); the row under the pointer stays under it. */
  function zoomGridRowsAt(y, factor) {
    const row = (y + state.gridScrollY) / state.gridRowH;
    state.gridRowH = zoomGridRows(state.gridRowH, factor); state.gridScrollY = row * state.gridRowH - y;
    clampScroll(); requestDraw(); markView(true);
  }
  /** Grid view: mutes or unmutes the row of a sound while playing (a view/playback setting of this browser, never written to the song). */
  function toggleMute(pitch) {
    if (state.muted.has(pitch)) state.muted.delete(pitch); else state.muted.add(pitch);
    requestDraw(); markView(true);
  }
  /** Roll <-> Grid: the same notes drawn as bars or as step cells. Entering the Grid pins the rows it shows so a row does not vanish when its last step is cleared. */
  function setMode(mode) {
    if (state.mode === mode || !modeButtons.has(mode)) return;
    flushWheel(); state.drag = null;
    if (mode === 'grid') state.gridPins = gridList();
    state.mode = mode; state.selected = new Set();
    keyW = mode === 'grid' ? RACK_W : KEY_W; panel.style.setProperty('--mls-key-w', `${keyW}px`); layout();
    for (const [id, b] of modeButtons) b.setAttribute('aria-pressed', String(id === mode));
    toolsGroup.hidden = mode === 'grid' && !touchLayout; if (touchLayout) for (const [id, b] of toolButtons) b.hidden = mode === 'grid' && id !== 'pan'; rowAdd.hidden = mode !== 'grid'; grid.dataset.mode = mode;
    clampScroll(); requestDraw(); markView(true);
  }
  function setTool(id) { state.tool = id; for (const [tool, b] of toolButtons) b.setAttribute('aria-pressed', String(tool === id)); grid.dataset.tool = id; requestDraw(); }
  function setTyping(on) { state.typing = on; toggle(typingButton, on); for (const e of [octaveLabel, octaveDown, octaveUp]) e.hidden = !on; octaveLabel.textContent = `C${state.octave}`; octaveLabel.dataset.octave = String(state.octave); }
  function setOctave(octave) { state.octave = Math.max(0, Math.min(9, octave)); octaveLabel.textContent = `C${state.octave}`; octaveLabel.dataset.octave = String(state.octave); }
  function shiftOctave(d) { setOctave(state.octave + d); }

  // ---- live MIDI pass-through: a connected keyboard sounds through the bridge while the roll is open (listen only, decision 10) ----
  /** A note that is played live (touch keyboard or MIDI): it sounds through the instrument of this clip, lights its key and, when recording, is kept. */
  function liveOn(pitch, velocity) {
    const old = midiHeld.get(pitch); if (old) audition.release(old);
    midiHeld.set(pitch, trig({ pitch, velocity, length: ppq }, { heldMs: 4000, startedAt: performance.now() }));
    state.litMidi.set(pitch, pitch); recordOn(pitch, velocity); requestDraw();
  }
  function liveOff(pitch) {
    const id = midiHeld.get(pitch); if (id) { midiHeld.delete(pitch); audition.release(id); }
    state.litMidi.delete(pitch); recordOff(pitch); requestDraw();
  }
  // ---- recording: the notes that are played while the clip plays are written into it when it stops
  // waiting: the clip loops (metronome on) until the first note is played; then one pass is recorded and it stops by itself. stopping: Stop was pressed, so the loop must not start again.
  const rec = { on: false, open: new Map(), notes: [], waiting: false, stopping: false, retiming: false, t0: null, mode: 'pass', loopWas: false, wheel: 0 };
  // once the first note has started the take, time is counted from that note (its own clock), so a note is where it was played whatever the player is doing
  const nowTick = () => (rec.t0 !== null ? (performance.now() - rec.t0) / 1000 / (60 / bpm() / ppq) : state.player ? state.player.time() / (60 / bpm() / ppq) : null);
  function recordOn(pitch, velocity) {
    if (!rec.on || (!state.player && rec.t0 === null)) return;
    if (rec.waiting) { rec.waiting = false; retime(); }
    rec.open.set(pitch, { at: nowTick(), velocity, bend: Math.round(rec.wheel * 100) });
  }
  // the pitch wheel while a take runs: a note keeps the bend that moved it furthest (cents), written as ;bend= on the note
  function recordBend(semitones) {
    rec.wheel = semitones;
    for (const held of rec.open.values()) if (Math.abs(semitones * 100) > Math.abs(held.bend)) held.bend = Math.round(semitones * 100);
  }
  /** The first note is the start of the take: the clip and the metronome begin again from the beginning, in time with that note, and one full pass is recorded. */
  function retime() {
    rec.t0 = performance.now(); rec.retiming = true;
    stopPlayback(false);
    rec.retiming = false;
    state.playhead = 0; requestDraw();
    if (rec.mode === 'pass') loopFor(false);   // one full pass: it ends by itself at the end of the clip
    togglePlay(false);
    toast(rec.mode === 'endless' ? 'Endless take from your first note: press ● to finish.' : 'Recording from the beginning, in time with your first note.');
  }
  /** Plays rows of a take from the beginning (the review's Preview); returns the way to stop it. */
  function previewTake(rows, bars, done) {
    let cancelled = false, player = null;
    liveAudioContext();
    (async () => {
      try { await audition.preview.prepare(); } catch { /* the sketch voice plays */ }
      if (cancelled) return;
      const secondsPerTick = 60 / bpm() / ppq, list = audition.preview.transform(rows);
      await Promise.race([audition.warmClip(rows), new Promise((resolve) => setTimeout(resolve, 1500))]).catch(() => {});   // a short head start for the real voices
      if (cancelled) return;
      player = startLive({ rows: () => list, bpm, ppq, instrument, from: 0, loopStart: 0, loop: 0, end: bars * bar * secondsPerTick, renderEvent: (ctx, out, event, at) => audition.preview.schedule(ctx, out, event, at, bpm()), onStop: () => { player = null; if (!cancelled) done(); } });
    })();
    return () => { cancelled = true; player?.stop(); };
  }
  /** Writes the chosen bars of a take into the clip; the clip gets longer when they are longer than it and that was asked. */
  async function keepTake({ rows, bars, extend }) {
    if (locked) return;
    const clipEnd = endTick(), items = [];
    for (const r of rows) {
      if (!extend && r.at >= clipEnd) continue;
      items.push(...splitAtBars(r.at, extend ? r.length : Math.min(r.length, clipEnd - r.at), r.pitch, r.velocity, ppq, r.bend ? { bend: r.bend } : {}));
    }
    if (extend && bars > endBars()) {
      const part = (flat().nodes.find((n) => n.id === node.id) ?? node).parts?.[0];
      await edit([{ type: 'setParts', block: node.id, parts: [{ at: part?.at || 1, length: bars, name: part?.name || '' }] }]);
    }
    if (!items.length) { toast('Nothing to keep in that part.'); return; }
    addNotes(items);
    toast(`${items.length} note${items.length === 1 ? '' : 's'} kept${extend && bars > endBars() ? ', the clip is longer now' : ''}.`);
  }
  function recordOff(pitch) {
    const held = rec.open.get(pitch); if (!held) return;
    rec.open.delete(pitch);
    const end = nowTick() ?? held.at;
    rec.notes.push({ at: held.at, end: end > held.at ? end : held.at + ppq / 2, pitch, velocity: held.velocity, bend: held.bend || 0 });
  }
  const loopFor = (on) => { state.loopOn = on; toggle(loopButton, on); };
  function paintRecord() { recordButton.textContent = rec.on && rec.mode === 'endless' ? '●∞' : '●'; recordButton.dataset.mode = rec.on ? rec.mode : ''; }
  // restarts the clip so that it loops (the take waits for you, and an endless take can run past the end); no take is finished by this
  function restartLooping() { rec.retiming = true; const was = state.player; if (was) stopPlayback(false); rec.retiming = false; togglePlay(false); }
  function startRecording(mode) {
    rec.on = true; rec.waiting = true; rec.stopping = false; rec.t0 = null; rec.mode = mode; rec.open.clear(); rec.notes = []; rec.loopWas = state.loopOn;
    toggle(recordButton, true); paintRecord(); review.hide();
    if (!state.metronome) { state.metronome = true; toggle(metroButton, true); }
    if (touchStrip.hidden && !state.typing) { touchStrip.hidden = false; toggle(touchButton, true); }
    loopFor(true);
    if (state.player) restartLooping(); else togglePlay(false);
    toast('Waiting for you: the metronome keeps going. Your first note starts the take from the beginning. Press ● again for an endless take (it can run past the end of the clip).');
  }
  function cancelRecording() {
    rec.on = false; rec.waiting = false; rec.t0 = null; rec.notes = []; rec.open.clear(); loopFor(rec.loopWas); toggle(recordButton, false); paintRecord();
    stopPlayback(false);
    toast('Recording cancelled.');
  }
  function toggleRecording() {
    if (locked) { toast('This clip is locked.'); return; }
    if (!rec.on) { startRecording('pass'); return; }
    if (rec.waiting) {
      if (rec.mode === 'pass') { rec.mode = 'endless'; paintRecord(); toast('Endless take: play as long as you like, the clip loops underneath. Press ● to finish.'); } else cancelRecording();
      return;
    }
    // a take is running: finish it
    rec.on = false; rec.waiting = false; toggle(recordButton, false);
    stopPlayback(false); finishRecording(state.playhead);
    paintRecord();
  }
  /** Writes what was played into the clip: on the grid of the snap setting, cut at the bar lines. */
  function finishRecording(stopAt = null) {
    if (rec.retiming) return;
    // the clip ran out while nothing was played yet: go round again, from the start
    if (rec.on && rec.waiting && !rec.stopping) { state.playhead = state.loopOn && state.bracket ? state.bracket.start : 0; setTimeout(() => { if (rec.on && rec.waiting && !state.player) togglePlay(false); }, 0); return; }
    if (!rec.on && !rec.notes.length && !rec.open.size) return;
    rec.waiting = false; rec.t0 = null; loopFor(rec.loopWas);
    const wasOn = rec.on; rec.on = false; toggle(recordButton, false);
    for (const [pitch, held] of rec.open) rec.notes.push({ at: held.at, end: stopAt ?? held.at + ppq, pitch, velocity: held.velocity });
    rec.open.clear();
    const grid = state.snap === 'off' || state.snap === 'free' ? 0 : beats() * ppq, items = [];
    const endless = rec.mode === 'endless', limit = endless ? Infinity : endTick(), taken = [];
    for (const n0 of rec.notes) {
      const n = { ...n0, end: Math.min(n0.end, limit) };
      if (n.at >= limit) continue;   // played after the clip had ended
      const at = snapTicks(n.at, grid), length = grid > 0 ? Math.max(grid, snapTicks(n.end - n.at, grid)) : Math.max(1, Math.round(n.end - n.at));
      if (endless) taken.push({ at, length, pitch: n.pitch, velocity: n.velocity, ...(n.bend ? { bend: n.bend } : {}) }); else items.push(...splitAtBars(at, length, n.pitch, n.velocity, ppq, n.bend ? { bend: n.bend } : {}));
    }
    rec.notes = [];
    paintRecord();
    if (endless) {
      if (!taken.length) { toast('Nothing was played.'); return; }
      review.show(taken); toast('The take is ready: choose the bars to keep.'); return;
    }
    if (!items.length) { if (wasOn) toast('Nothing was played.'); return; }
    addNotes(items);
    toast(`${items.length} note${items.length === 1 ? '' : 's'} recorded.`);
  }
  const midiHeld = new Map();
  const midiNamesNow = () => midiNames().length;
  // the shared MIDI input: this window shows the switch, and the keys of its own keyboard light for a note that arrives
  const paintMidi = (on) => { if (midiButton) toggle(midiButton, on); panel.dataset.midi = on ? '1' : ''; };
  const stopMidiState = onMidiState(paintMidi), stopMidiNotes = onMidiNote((type, pitch) => { if (type === 'on' || type === 'off') vkb.lit(pitch, type === 'on'); });
  paintMidi(midiActive());
  restoreMidi();
  // ---- transport ----
  const instrument = () => {
    const g = flat().nodes.find((n) => n.tpl === 'gm' && flat().links.some((l) => l.from === node.id && l.to === n.id));
    return { program: Number(g?.params.program || 0), channel: Number(g?.params.channel || 0) };
  };
  let liveFor = null, liveRevision = -1, liveCache = [], liveMuted = '';
  const mutedSig = () => [...state.muted].sort((a, b) => a - b).join(',');
  const liveRows = () => { if (liveFor !== model || liveRevision !== audition.preview.revision || liveMuted !== mutedSig()) { liveCache = audition.preview.transform(rows().filter((n) => !state.muted.has(n.pitch))); liveFor = model; liveRevision = audition.preview.revision; liveMuted = mutedSig(); } return liveCache; };
  // metroCursor: knows which beat was scheduled last (by tick, so a stale playhead cannot schedule one twice)
  let starting = false, tickFrame = 0, metroTimer = 0, metroCursor = null;
  async function togglePlay(selectionOnly) {
    if (state.player || starting) { stopPlayback(false); return; }
    starting = true; state.followPaused = false; liveAudioContext();
    await audition.preview.prepare();
    if (!panel.isConnected || !starting) return;
    starting = false;
    const picked = selectionOnly ? new Set(rows().filter((n) => state.selected.has(n.index)).map((n) => n.index)) : null;
    if (selectionOnly && !picked.size) { toast('Select some notes first.'); return; }
    const secondsPerTick = 60 / bpm() / ppq, end = endTick();
    const range = selectionOnly ? { from: Math.min(...rows().filter((n) => picked.has(n.index)).map((n) => n.at)), loopStart: 0, loopEnd: 0, end } : litePlayRange(state.playhead, state.loopOn, state.bracket, end);
    state.playOrigin = state.playhead; state.playhead = range.from;
    const source = selectionOnly ? () => liveRows().filter((n) => picked.has(n.index)) : liveRows;
    audition.warmClip(source()).catch(() => {});   // the notes the clip really has are rendered ahead (otherwise the sketch voice sounds for the ones not yet rendered)
    state.player = startLive({ rows: source, bpm, ppq, instrument, from: range.from * secondsPerTick, loopStart: range.loopStart * secondsPerTick, loop: range.loopEnd * secondsPerTick, end: range.end * secondsPerTick,
      renderEvent: (ctx, out, event, at) => audition.preview.schedule(ctx, out, event, at, bpm()), onStop: () => { state.player = null; finishRecording(endTick()); playButton.textContent = '▶'; toggle(playButton, false); clearInterval(metroTimer); requestDraw(); } });
    state.player.sel = !!selectionOnly; state.player.range = range;
    playButton.textContent = '❚❚'; toggle(playButton, true);
    const tick = () => { tickFrame = 0; if (!state.player || !panel.isConnected) return; if (state.drag?.kind !== 'seek') state.playhead = state.player.time() / secondsPerTick; follow(); requestDraw(); tickFrame = requestAnimationFrame(tick); };
    tickFrame = requestAnimationFrame(tick);
    clearInterval(metroTimer); metroCursor = newMetroCursor();
    metroTimer = setInterval(metronomeRound, 80);
  }
  // playing: go on from here (stopping reads the playhead back from the player, so the wanted place is put back before it starts again)
  function restartAt(tick) { if (!state.player) return; const sel = state.player.sel; stopPlayback(false); state.playhead = tick; togglePlay(sel); }
  /** back to the start marker (the loop start when looping) when `home`, else stay (pause) */
  function stopPlayback(home) {
    starting = false; const player = state.player;
    if (player) rec.stopping = true;
    if (player) { state.playhead = player.time() / (60 / bpm() / ppq); state.player = null; player.stop(); finishRecording(state.playhead); rec.stopping = false; }
    if (home) state.playhead = state.loopOn && state.bracket ? state.bracket.start : 0;
    playButton.textContent = '▶'; toggle(playButton, false); clearInterval(metroTimer); requestDraw();
  }
  function follow(tick = state.playhead) { if (!state.follow || state.followPaused) return; const x = xOf(tick); if (x > gridW() - 30 || x < 0) { state.scrollX = Math.max(0, Math.min(maxScrollX(), (tick / ppq) * state.pxBeat - 40)); } }
  // the song that plays in the Studio: a second head (the brand colour) runs over the clip at the place that sounds now and the roll scrolls with it; the Jam's own player keeps the green one
  let songFrame = 0, songWaited = 0;
  const songRun = () => {
    if (!panel.isConnected) { if (songWaited++ < 300) songFrame = requestAnimationFrame(songRun); else songFrame = 0; return; }   // the window may be put on the page a moment after it is made
    songWaited = 0;
    const bar = (options.pseudo && !options.derived) || state.player ? null : songBarNow();   // a sequencer row (pseudo clip) is not placed in the song; the read-only view of a pipeline clip is
    const tick = bar == null ? null : clipTickAt((flat().nodes.find((n) => n.id === (options.derived?.block ?? node.id)) ?? node).parts, bar, barBeats() * ppq);
    if (tick !== state.songHead) { state.songHead = tick; if (tick != null) follow(tick); requestDraw(); }
    songFrame = requestAnimationFrame(songRun);
  };
  songFrame = requestAnimationFrame(songRun);

  const newMetroCursor = () => createMetronomeCursor({
    ppq, loopStart: state.player?.range.loopStart ?? 0, loopEnd: state.player?.range.loopEnd ?? 0,
  });
  function metronomeRound() {
    if (!state.player) return;
    // switched off: the next click starts clean (laps passed meanwhile are not counted)
    if (!state.metronome) { metroCursor = newMetroCursor(); return; }
    const ctx = liveAudioContext(), secondsPerTick = 60 / bpm() / ppq;
    // the position is read from the player now (state.playhead is only refreshed per animation frame, up to a frame old)
    for (const click of metroCursor.round(state.player.time() / secondsPerTick, 0.3 / secondsPerTick)) {
      const at = ctx.currentTime + click.offset * secondsPerTick;
      const osc = ctx.createOscillator(), gain = ctx.createGain();
      osc.frequency.value = click.accent ? 1500 : 1000; gain.gain.setValueAtTime(0.0001, at); gain.gain.linearRampToValueAtTime(0.2, at + 0.002); gain.gain.exponentialRampToValueAtTime(0.0001, at + 0.04);
      osc.connect(gain).connect(playbackOutput(ctx)); osc.start(at); osc.stop(at + 0.06);
    }
  }

  // ---- pointer: grid ----
  const local = (event, canvas) => pianoRollPoint(canvas, event);
  /** Every audition goes through here so the DOM specs can see the pitch, hold and mono flags (panel.mlsDebug.calls). */
  const trig = (note, options) => { calls.push({ pitch: note.pitch, mono: !!options?.mono, held: options?.heldMs ?? 0 }); if (calls.length > 200) calls.shift(); return audition.trigger(note, options); };
  const hear = (note) => trig({ pitch: note.pitch, velocity: note.velocity ?? 100, length: note.length ?? ppq / 4, channel: note.channel, program: note.program }, { startedAt: performance.now() });
  const reaudition = createThrottle(90);
  function noteAt(x, y) { const h = hit(x, y); return h ? h.note : null; }
  const rowsBySelection = () => rows().filter((row) => state.selected.has(row.index));
  const rowOf = (n) => mergedRows(model).filter((r) => r.index === n.index);
  /** Selects the rows that sit at the given (start:pitch) keys; indexes shift when a gesture rewrites the text. */
  const selectKeys = (keys) => { state.selected = new Set(rows().filter((row) => keys.has(`${row.at}:${row.pitch}`)).map((row) => row.index)); };
  const keysOfRows = (list, dTick = 0, dPitch = 0) => new Set(list.map((row) => `${row.at + dTick}:${row.pitch + dPitch}`));
  // ---- the clips behind this one (ghosts): other midi-clips that overlap in the arrangement, drawn as faint outlines under the notes, read-only ----
  /** The colour of a voice, a little darker for a soft note (the notes of a sequencer pattern take the colour of their channel). */
  const shadeOf = (hex, velocity) => { const n = parseInt(String(hex).replace('#', ''), 16); if (!Number.isFinite(n)) return velocityColor(velocity); const f = 0.55 + 0.45 * Math.min(127, velocity) / 127; return `rgb(${Math.round(((n >> 16) & 255) * f)}, ${Math.round(((n >> 8) & 255) * f)}, ${Math.round((n & 255) * f)})`; };
  const GHOST_COLORS = ['#e0a030', '#30c0e0', '#c060e0', '#60e090', '#e05060'];
  const isRollable = (n) => n?.tpl === 'midi-clip' && !n.params.chords && !n.params.melody && !/^\s*notes\s*\{/.test(n.params.notes ?? '');
  const clipOffsetBars = (n) => (n.parts?.length ? Math.min(...n.parts.map((p) => p.at)) : 1);
  const builtGhosts = new Map(), loadingGhosts = new Set();
  async function loadGhost(src) {
    if (isRollable(src) || builtGhosts.has(src.id) || loadingGhosts.has(src.id)) return;
    loadingGhosts.add(src.id);
    try { const data = await api.post('/api/midi/clip-notes', { path: S.path, block: src.id }); builtGhosts.set(src.id, data.text); requestDraw(); }
    catch { builtGhosts.set(src.id, ''); }
    finally { loadingGhosts.delete(src.id); }
  }
  function ghostSources() {
    if (options.pseudo) return [];
    const mine = flat().nodes.find((n) => n.id === node.id) ?? node, myParts = mine.parts ?? [];
    const mySpan = myParts.length ? [Math.min(...myParts.map((p) => p.at)), Math.max(...myParts.map((p) => p.at + p.length))] : null;
    return flat().nodes.filter((n) => n.id !== node.id && n.tpl === 'midi-clip' && (isRollable(n) ? (n.params.notes ?? '').trim() : n.params.chords || n.params.melody) && (!mySpan || !n.parts?.length || n.parts.some((p) => p.at < mySpan[1] && p.at + p.length > mySpan[0])));
  }
  function ghostList() {
    if (!state.showGhosts) return [];
    // the other voices of a sequencer pattern: read-only outlines in their own colour; a click on one opens that voice
    if (options.ghostRows) {
      return options.ghostRows.map((row, i) => {
        let m; try { m = parse(`"${row.text}"`, ppq); } catch { return null; }
        return { clipId: row.id, title: row.title, open: row.open, chordSource: false, color: row.color || GHOST_COLORS[i % GHOST_COLORS.length], notes: absolute(m).map((nn) => ({ pitch: nn.pitch, at: nn.at, length: Math.max(1, nn.length), velocity: nn.velocity })) };
      }).filter(Boolean);
    }
    const myOffset = clipOffsetBars(flat().nodes.find((n) => n.id === node.id) ?? node);
    return ghostSources().map((src, i) => {
      loadGhost(src);
      const srcPpq = Number.parseInt(src.params.ppq ?? '', 10) || 1_000_000;
      let m; try { m = parse(isRollable(src) ? src.params.notes : builtGhosts.get(src.id) || '', srcPpq); } catch { return null; }
      const barOffset = clipOffsetBars(src) - myOffset, factor = ppq / srcPpq;
      const notes = absolute(m).map((nn) => ({ pitch: nn.pitch, at: nn.at * factor + barOffset * bar, length: Math.max(1, nn.length * factor), velocity: nn.velocity }));
      return { clipId: src.id, title: src.title, chordSource: Boolean(src.params.chords || src.params.melody), color: GHOST_COLORS[i % GHOST_COLORS.length], notes };
    }).filter(Boolean);
  }
  function ghostHit(x, y) {
    for (const g of ghostList()) for (const n of g.notes) { const nx = xOf(n.at), nw = Math.max(2, (n.length / ppq) * state.pxBeat), ny = yOf(n.pitch); if (x >= nx && x <= nx + nw && y >= ny && y <= ny + state.rowH) return g; }
    return null;
  }
  /** The pitch a new note gets: snapped to the scale when that lock is on, and to the chord of a chord clip behind it when the chord lock is on. */
  function lockPitch(pitch, tick) {
    if (state.mode === 'grid') return pitch;
    const active = state.chordLock ? ghostList().filter((g) => g.chordSource).flatMap((g) => g.notes.filter((n) => n.at <= tick && n.at + n.length > tick).map((n) => n.pitch % 12)) : [];
    const scale = currentScale();
    if (!active.length) return state.scaleLock && scale ? nearestScaleTone(pitch, scale) : pitch;
    const allowed = scale && state.scaleLock ? scaleSet(scale) : null;
    return Array.from({ length: 128 }, (_, p) => p).filter((p) => active.includes(p % 12) && (!allowed || allowed.has(p % 12))).sort((a, b) => Math.abs(a - pitch) - Math.abs(b - pitch) || a - b)[0] ?? pitch;
  }
  const currentScale = () => parseScale((flat().nodes.find((n) => n.id === node.id) ?? node).params.scale);
  const drawNew = (model0, tick, pitch, length) => drawRow(model0, tick, lockPitch(pitch, tick), length, endTick());
  const dragging = (d) => d && ['draw', 'paint', 'erase', 'rubber', 'move', 'clone', 'resize'].includes(d.kind);

  grid.addEventListener('pointerdown', (event) => {
    grid.focus(); setAlt(event.altKey);
    const { x, y } = local(event, grid);
    state.pointer = { x, y };
    if (state.tool === 'pan' && event.button === 0) { state.drag = { kind: 'pan', x0: x, y0: y, sx: state.scrollX, sy: state.mode === 'grid' ? state.gridScrollY : state.scrollY, grid: state.mode === 'grid' }; grid.dataset.drag = 'pan'; pauseFollow(); grid.setPointerCapture(event.pointerId); return; }
    if (state.mode === 'grid') { gridDown(event, x, y); return; }
    if (event.button === 1) { event.preventDefault(); state.drag = { kind: 'pan', x0: x, y0: y, sx: state.scrollX, sy: state.scrollY }; grid.dataset.drag = 'pan'; pauseFollow(); grid.setPointerCapture(event.pointerId); return; }
    if (state.tool === 'playback') { if (event.button === 2) stopPlayback(false); else if (event.button === 0) playFrom(atOf(x)); return; }
    if (state.tool === 'zoom') { if (event.button === 2) zoomBy(1 / 1.6, x, 'both', y); else if (event.button === 0) { grid.setPointerCapture(event.pointerId); state.drag = { kind: 'zoom', x0: x, y0: y, x1: x, y1: y }; grid.dataset.drag = 'zoom'; } return; }
    if (locked) return;
    grid.setPointerCapture(event.pointerId);
    if (event.button === 2) { state.drag = { kind: 'erase', base: model }; grid.dataset.drag = 'erase'; const n = noteAt(x, y); if (n) { model = removeRows(model, rowOf(n)); state.selected = new Set(); requestDraw(); } startAuto(); return; }
    if (event.button !== 0) return;
    const n = noteAt(x, y), tool = state.tool, mod = event.ctrlKey || event.metaKey;
    if (tool === 'delete') { state.drag = { kind: 'erase', base: model }; if (n) { model = removeRows(model, rowOf(n)); state.selected = new Set(); requestDraw(); } startAuto(); return; }
    if (tool === 'slice') { state.drag = { kind: 'slice', base: model, cut: new Set() }; sliceAt(x, y); return; }
    if (n && (!mod || event.shiftKey)) { startNoteDrag(n, event, x, y); return; }
    if (tool === 'select' || mod) { state.drag = { kind: 'rubber', x0: x, y0: y, x1: x, y1: y, add: event.shiftKey, base: state.selected }; if (!event.shiftKey) state.selected = new Set(); startAuto(); return; }
    const pitch = pitchOf(y), cell = beats() * ppq, tickStart = cell > 0 && !state.altKey ? Math.max(0, Math.floor(atOf(x) / cell) * cell) : Math.max(0, Math.round(atOf(x)));
    if (tool === 'paint') { state.drag = { kind: 'paint', painted: new Set(), cols: new Map(), mono: event.shiftKey, base: model }; paint(x, y); startAuto(); return; }
    if (state.stamp && state.mode !== 'grid') { stampChordAt(tickStart, pitch); requestDraw(); return; }
    state.drag = { kind: 'draw', base: model, tickStart, pitch, length: lastLength() };
    const drawn = drawNew(model, tickStart, pitch, state.drag.length);
    if (drawn !== model) { model = drawn; state.selected = new Set([model.notes.length - 1]); hear({ pitch, velocity: 96 }); }
    requestDraw(); startAuto();
  });
  /** Grid view: left click toggles a step (drag paints or clears a run like the first cell did), Shift+click accents, right-click and right-drag clear. */
  function gridDown(event, x, y) {
    if (locked || (event.button !== 0 && event.button !== 2)) return;
    // Ctrl and drag: a rectangle selects the notes it touches (Shift adds to the selection)
    if ((event.ctrlKey || event.metaKey) && event.button === 0 && !event.altKey) {
      grid.setPointerCapture(event.pointerId);
      state.drag = { kind: 'rubber', x0: x, y0: y, x1: x, y1: y, add: event.shiftKey, base: state.selected, grid: true };
      if (!event.shiftKey) state.selected = new Set();
      startAuto();
      return;
    }
    const pitch = gridPitchAt(y), step = stepLen(), filled = pitch !== null && coveringRows(rows(), columnOf(atOf(x), step), pitch, step).length > 0;
    if (event.altKey && event.button === 0 && filled) { splitAt(x, y); return; }   // Alt+click on a long note: separate steps
    grid.setPointerCapture(event.pointerId);
    state.drag = { kind: 'step', action: event.button === 2 ? 'off' : event.altKey && !filled ? 'long' : event.shiftKey ? 'accent' : filled ? 'off' : 'on', base: model, seen: new Set() };
    grid.dataset.drag = 'step'; stepAt(x, y);
  }
  const EXTRA_FIELDS = ['pan', 'fine', 'channel', 'bank', 'program', 'cut', 'rel', 'slide', 'port', 'grp'];
  const extrasOf = (row) => { const out = {}; for (const key of EXTRA_FIELDS) if (row[key] !== undefined) out[key] = row[key]; return out; };
  /** Long notes become separate one-step notes (every field of the note goes along). Only notes longer than a step change. */
  function splitRows(group) {
    const step = stepLen(), long = group.filter((r) => r.length > step * 1.01);
    if (locked || !long.length) return false;
    let next = removeRows(model, long);
    const made = [];
    for (const row of long) for (let i = 0, count = Math.ceil(row.length / step); i < count; i++) { const at = row.at + i * step; next = drawRow(next, at, row.pitch, step, endTick(), row.velocity); made.push({ at, pitch: row.pitch, extras: extrasOf(row) }); }
    for (const piece of made) if (Object.keys(piece.extras).length) next = adjustRows(next, rowsOf(next).filter((r) => r.pitch === piece.pitch && r.at === piece.at), () => piece.extras);
    model = next; commit(model); requestDraw();
    return true;
  }
  function splitAt(x, y) {
    const pitch = gridPitchAt(y), step = stepLen(), col = columnOf(atOf(x), step);
    const row = pitch === null ? null : coveringRows(rows(), col, pitch, step).find((r) => r.length > step * 1.01);
    if (row) splitRows([row]);
  }
  /** Notes of the selection that sit right next to each other on the same row become one long note each (three pairs give three long notes); the first note of a run keeps its fields. */
  /** The tools of the Selection menu: a pure function over the selected notes (ticks), written back as ONE edit by index; Undo brings the notes back. */
  function applyTool(fn) {
    if (locked) return;
    const chosen = absolute(model).filter((n) => state.selected.has(n.index));
    if (!chosen.length) { toast('Select notes first.'); return; }
    const changes = new Map();
    for (const r of fn(chosen)) { const b = Math.floor(r.at / bar) + 1, start = r.at - (b - 1) * bar; changes.set(r.index, { bar: b, start, length: Math.min(r.length, bar - start), pitch: r.pitch, velocity: r.velocity }); }
    const next = updateMany(model, changes); if (next !== model) commit(next);
  }
  const selectAdded = (base, count) => { state.selected = new Set(Array.from({ length: count }, (_, i) => base + i)); };
  /** Adds new notes (ticks) after the existing ones as ONE edit and selects them; notes listed in removeIndexes are taken out in the same edit. */
  function addNotes(items, removeIndexes = []) {
    if (locked || !items.length) return;
    let next = model;
    for (const i of [...removeIndexes].sort((a, b) => b - a)) next = removeNote(next, i);
    const first = next.notes.length;
    next = pasteNotes(next, items, 0, 0);
    selectAdded(first, items.length); commit(next);
  }
  function openArpeggiateForm() {
    const chosen = absolute(model).filter((n) => state.selected.has(n.index));
    if (chosen.length < 2) { toast('Select a chord (two or more notes) first.'); return; }
    openJamForm(panel, { title: 'Arpeggiate', apply: 'Apply', fields: [
      { key: 'mode', label: 'Pattern', type: 'select', value: 'up', options: ['up', 'down', 'updown', 'random'].map((m) => ({ value: m, label: m })) },
      { key: 'rate', label: 'Step (beats)', type: 'number', value: 0.25, min: 0.03125, step: 0.0625 },
      { key: 'gate', label: 'Gate %', type: 'number', value: 80, min: 1, max: 100 },
      { key: 'octaves', label: 'Octaves', type: 'number', value: 1, min: 1, max: 4 },
      { key: 'seed', label: 'Seed (random pattern)', type: 'number', value: 1, min: 1 },
    ], onApply: (v) => { addNotes(arpeggiate(chosen, { mode: v.mode, rateTicks: Math.round((v.rate ?? 0.25) * ppq) || ppq, gatePercent: v.gate || 80, octaveRange: v.octaves || 1, seed: v.seed || 1 }), chosen.map((n) => n.index)); } });
  }
  function openRiffForm() {
    const chosen = absolute(model).filter((n) => state.selected.has(n.index)).sort((a, b) => a.at - b.at);
    const at0 = chosen.length ? chosen[0].at : Math.max(0, snap(state.playhead));
    openJamForm(panel, { title: 'Riff machine', hint: chosen.length ? 'Starts at the first selected note.' : 'Starts at the playhead. New notes only; nothing is changed.', apply: 'Generate', fields: [
      { key: 'bars', label: 'Bars', type: 'number', value: 1, min: 1, max: 32 },
      { key: 'density', label: 'Density %', type: 'number', value: 60, min: 1, max: 100 },
      { key: 'accent', label: 'Accent every N steps', type: 'number', value: 4, min: 0, max: 16 },
      { key: 'root', label: 'Root pitch (MIDI)', type: 'number', value: chosen.length ? chosen[0].pitch : 60, min: 0, max: 127 },
      { key: 'spread', label: 'Spread (semitones)', type: 'number', value: 7, min: 0, max: 24 },
      { key: 'gate', label: 'Gate %', type: 'number', value: 80, min: 1, max: 100 },
      { key: 'seed', label: 'Seed', type: 'number', value: 1, min: 1 },
      { key: 'scale', label: "Snap to the clip's scale", type: 'check', value: !!currentScale() },
    ], onApply: (v) => {
      const scale = v.scale ? currentScale() : null;
      const notes = generateRiff({ at0, barTicks: bar, bars: v.bars || 1, density: v.density || 60, accentEvery: v.accent || 0, rootPitch: Math.max(0, Math.min(127, v.root ?? 60)), spread: v.spread ?? 7, gatePercent: v.gate || 80, seed: v.seed || 1, snapToScale: scale ? (p) => nearestScaleTone(p, scale) : null });
      if (!notes.length) { toast('Nothing generated at this density; raise it or try another seed.'); return false; }
      addNotes(notes);
    } });
  }
  function openStampForm() {
    const old = state.stamp?.voicing;
    openJamForm(panel, { title: 'Stamp a chord', hint: 'Then click a row in the roll to plot the chord there. Esc stops stamping.', apply: 'Start stamping', fields: [
      { key: 'chord', label: 'Chord', type: 'select', value: state.stamp?.chord ?? 'major', options: CHORDS.map((c) => ({ value: c.id, label: c.category + ': ' + c.name })) },
      { key: 'inversion', label: 'Inversion', type: 'select', value: String(old?.inversion ?? 0), options: [0, 1, 2, 3, 4].map((n) => ({ value: n, label: String(n) })) },
      { key: 'spread', label: 'Spread', type: 'check', value: old?.spread },
      { key: 'drop2', label: 'Drop 2', type: 'check', value: old?.drop2 },
      { key: 'octave', label: 'Double the root an octave up', type: 'check', value: old?.octaveDouble },
      { key: 'bass', label: 'Bass note (MIDI, empty = none)', type: 'number', value: old?.bassNote ?? '', min: 0, max: 127 },
    ], onApply: (v) => { state.stamp = { chord: v.chord, voicing: { inversion: Number(v.inversion), spread: v.spread, drop2: v.drop2, octaveDouble: v.octave, bassNote: v.bass === null ? null : Math.max(0, Math.min(127, v.bass)) } }; toast('Click a row to stamp the chord. Esc stops.'); } });
  }
  function stampChordAt(tick, root) {
    const items = (chordPitches(Math.max(0, Math.min(127, root)), state.stamp.chord, state.stamp.voicing) ?? []).map((p) => ({ at: tick, length: lastLength(), pitch: p, velocity: 96 }));
    if (!items.length) return;
    addNotes(items);
    for (const item of items) hear({ pitch: item.pitch, velocity: 96, length: item.length });
  }
  /** The properties of the selected notes in one card: exact position, length, pitch and velocity for a single note; colour group, slide and portamento for any selection (an empty field changes nothing). */
  function openPropertiesForm() {
    const chosen = absolute(model).filter((n) => state.selected.has(n.index));
    if (!chosen.length) { toast('Select notes first.'); return; }
    if (locked) return;
    const one = chosen.length === 1 ? chosen[0] : null, tied = chosen.some((n) => n.tie);
    const place = one && !tied ? [
      { key: 'bar', label: 'Bar', type: 'number', value: Math.floor(one.at / bar) + 1, min: 1 },
      { key: 'beat', label: 'Beat in the bar', type: 'number', value: Math.round(((one.at % bar) / ppq) * 100) / 100, min: 0, step: 0.25 },
      { key: 'length', label: 'Length (beats)', type: 'number', value: Math.round((one.length / ppq) * 100) / 100, min: 0.01, step: 0.25 },
      { key: 'pitch', label: 'Pitch (MIDI)', type: 'number', value: one.pitch, min: 0, max: 127 },
      { key: 'velocity', label: 'Velocity', type: 'number', value: one.velocity, min: 1, max: 127 },
    ] : [];
    openJamForm(panel, { title: one ? 'Note properties' : chosen.length + ' notes', hint: tied ? 'Tied notes keep their position; move them in the source.' : undefined, apply: 'Apply', fields: [
      ...place,
      { key: 'grp', label: 'Colour group (0 = none)', type: 'number', value: one ? one.grp ?? 0 : '', min: 0, max: 8, placeholder: 'unchanged' },
      { key: 'slide', label: 'Slide', type: 'select', value: one ? (one.slide ? 'on' : 'off') : '', options: [...(one ? [] : [{ value: '', label: 'unchanged' }]), { value: 'on', label: 'on' }, { value: 'off', label: 'off' }] },
      { key: 'port', label: 'Portamento (ms)', type: 'number', value: one ? one.port ?? 0 : '', min: 0, max: 2000, placeholder: 'unchanged' },
    ], onApply: (v) => {
      const changes = new Map();
      for (const n of chosen) {
        const c = {};
        if (one && !tied) {
          const b = Math.max(1, Math.round(v.bar ?? 1)), start = Math.max(0, Math.round((v.beat ?? 0) * ppq));
          Object.assign(c, { bar: b, start: Math.min(bar - 1, start), length: Math.max(1, Math.round((v.length ?? 1) * ppq)), pitch: Math.max(0, Math.min(127, Math.round(v.pitch ?? n.pitch))), velocity: Math.max(1, Math.min(127, Math.round(v.velocity ?? n.velocity))) });
        }
        if (v.grp !== null) c.grp = Math.max(0, Math.min(8, Math.round(v.grp))) || undefined;
        if (v.slide === 'on') c.slide = 1; else if (v.slide === 'off') c.slide = undefined;
        if (v.port !== null) c.port = Math.max(0, Math.min(2000, Math.round(v.port))) || undefined;
        if (Object.keys(c).length) changes.set(n.index, c);
      }
      const next = updateMany(model, changes); if (next !== model) commit(next);
    } });
  }
  function selectGroup() {
    if (!state.groupFilter) { toast('Choose a colour group first.'); return; }
    state.selected = new Set(rows().filter((n) => n.grp === state.groupFilter).map((n) => n.index)); requestDraw();
  }
  function openGroupChannelForm() {
    if (!state.groupFilter) { toast('Choose a colour group first.'); return; }
    openJamForm(panel, { title: 'MIDI channel of group ' + state.groupFilter, apply: 'Apply', fields: [
      { key: 'channel', label: 'Channel', type: 'select', value: '0', options: Array.from({ length: 16 }, (_, i) => ({ value: i, label: String(i + 1) })) },
    ], onApply: (v) => {
      const changes = new Map(rows().filter((n) => n.grp === state.groupFilter).map((n) => [n.index, { channel: Number(v.channel) }]));
      if (!changes.size) { toast('This colour group has no notes.'); return; }
      if (!locked) commit(updateMany(model, changes));
    } });
  }
  function openStrumForm() {
    const chosen = absolute(model).filter((n) => state.selected.has(n.index));
    if (chosen.length < 2) { toast('Select at least two notes that start together.'); return; }
    openJamForm(panel, { title: 'Strum', apply: 'Apply', fields: [
      { key: 'direction', label: 'Direction', type: 'select', value: STRUM_DIRECTIONS[0], options: STRUM_DIRECTIONS.map((d) => ({ value: d, label: d })) },
      { key: 'curve', label: 'Curve', type: 'select', value: STRUM_CURVES[0], options: STRUM_CURVES.map((c) => ({ value: c, label: c })) },
      { key: 'span', label: 'Span (beats)', type: 'number', value: 0.25, min: 0.05, step: 0.05 },
      { key: 'accent', label: 'Accent %', type: 'number', value: 20, min: 0, max: 100 },
      { key: 'ends', label: 'Keep the ends', type: 'check', value: true },
    ], onApply: (v) => {
      const groups = groupNotesByStart(chosen, Math.round(ppq / 8)), totalTicks = Math.round((v.span ?? 0.25) * ppq) || Math.round(ppq / 4);
      const opts = { direction: v.direction, curve: v.curve, accentPercent: v.accent || 0, keepEnds: v.ends, totalTicks }, changes = new Map();
      groups.forEach((group, gi) => { for (const n of strum(group, { ...opts, groupIndex: gi })) { const b = Math.floor(n.at / bar) + 1, start = n.at - (b - 1) * bar; changes.set(n.index, { bar: b, start, length: Math.min(n.length, bar - start), velocity: n.velocity }); } });
      const next = updateMany(model, changes); if (next !== model) commit(next);
    } });
  }
  function openChopForm() {
    const chosen = absolute(model).filter((n) => state.selected.has(n.index) && !n.tie);
    if (!chosen.length) { toast('Select a note first.'); return; }
    openJamForm(panel, { title: 'Hi-hat roll chopper', hint: 'Cuts each selected note into a roll.', apply: 'Apply', fields: [
      { key: 'division', label: 'Division', type: 'select', value: CHOP_DIVISIONS[0], options: CHOP_DIVISIONS.map((d) => ({ value: d, label: d })) },
      { key: 'ramp', label: 'Velocity', type: 'select', value: 'flat', options: ['flat', 'rising', 'falling', 'humanised'].map((r) => ({ value: r, label: r })) },
      { key: 'gate', label: 'Gate %', type: 'number', value: 100, min: 1, max: 100 },
      { key: 'seed', label: 'Seed', type: 'number', value: 1, min: 1 },
    ], onApply: (v) => addNotes(chosen.flatMap((n) => chop({ at: n.at, length: n.length, pitch: n.pitch, velocity: n.velocity }, v.division, { ramp: v.ramp, gatePercent: v.gate || 100, seed: v.seed || 1 })), chosen.map((n) => n.index)) });
  }
  const existingHumanize = () => flat().links.filter((l) => l.from === node.id).map((l) => flat().nodes.find((n) => n.id === l.to)).find((n) => n?.tpl === 'midi-humanize') ?? null;
  async function openHumanizeForm() {
    const existing = existingHumanize();
    let exactSeed = null, seedAvailable = !existing;
    if (existing) try { exactSeed = (await api.get('/api/control/block?path=' + encodeURIComponent(S.path) + '&block=' + encodeURIComponent(existing.id))).seeds.find((seed) => seed.name === 'seed')?.state.valueText ?? null; seedAvailable = exactSeed !== null; } catch { seedAvailable = false; }
    const drift = (v) => { const h = existingHumanize(), masterSeed = S.doc?.project?.masterSeed; if (masterSeed === undefined || !h || !seedAvailable) return null; try { return previewHumanizeDrift(model, { masterSeed, seed: exactSeed ?? undefined, humanizeBlockId: h.id, velocityRange: v.velocity || 0, timingThousandths: v.timing || 0 }); } catch { return null; } };
    openJamForm(panel, { title: 'Humanize', hint: 'Apply keeps the notes as they are and adds a humanize block (seeded, so it can be reproduced). Bake writes the drift into the notes.', apply: 'Apply to the block', fields: [
      { key: 'velocity', label: 'Velocity drift (0-60)', type: 'number', value: existing ? Number.parseInt(existing.params.velocity, 10) || 10 : 10, min: 0, max: 60 },
      { key: 'timing', label: 'Timing drift (0-200, thousandths of a beat)', type: 'number', value: existing ? Number.parseInt(existing.params.timing, 10) || 20 : 20, min: 0, max: 200 },
    ], secondary: { label: 'Bake', title: 'Writes the previewed drift into the notes, one undo step', run: (v) => { const d = drift(v); if (!d) { toast('Apply the humanize block first, then Bake.'); return false; } if (!locked) commit(updateMany(model, d)); } },
    onApply: async (v) => {
      let h = existingHumanize();
      const ops = [];
      if (!h) {
        const used = new Set(flat().nodes.map((n) => n.id));
        let name = node.id + '-humanize', i = 2;
        while (used.has(name)) name = node.id + '-humanize' + i++;
        const downstream = flat().links.filter((link) => link.from === node.id && link.outName === 'notes');
        ops.push({ type: 'addBlock', blockType: 'midi-humanize', name });
        for (const link of downstream) ops.push({ type: 'disconnect', to: link.to, input: link.inName });
        ops.push({ type: 'connect', from: node.id, output: 'notes', to: name, input: 'notes' });
        for (const link of downstream) { ops.push({ type: 'connect', from: name, output: 'notes', to: link.to, input: link.inName }); if (link.disabled) ops.push({ type: 'setConnectionDisabled', to: link.to, input: link.inName, disabled: true }); }
        h = { id: name };
      }
      ops.push({ type: 'setParameter', block: h.id, name: 'velocity', value: String(Math.max(0, Math.min(60, Math.round(v.velocity || 0)))) });
      ops.push({ type: 'setParameter', block: h.id, name: 'timing', value: String(Math.max(0, Math.min(200, Math.round(v.timing || 0)))) });
      ops.push({ type: 'setParameter', block: h.id, name: 'enabled', value: '"on"' });
      if (await edit(ops)) toast('Humanize set on ' + h.id + '.');
    } });
  }
  /** Closes the roll and opens the source of this clip (the text always holds everything). */
  async function editAsText() { const { gotoSource } = await import('./panels.js'); flushWheel(); await commit(model); saveViewNow(); panel.close(); gotoSource(node); }
  /** Plays the built song from the left edge of the view (the roll's own play button plays only these notes, without a build). */
  async function playSongFromView() {
    const take = S.build?.takes?.master || Object.values(S.build?.takes || {}).at(-1) || null;
    if (!take) { toast('Build the song first to hear it here; the play button plays the notes without a build.'); return; }
    stopPlayback(false);
    try { await playSong(take, Math.max(0, (state.scrollX / state.pxBeat) * 60 / bpm())); } catch (error) { toast(error.message); }
  }
  async function humMelody() {
    if (locked) return;
    const { startHum } = await import('./hum.js');
    startHum({ tempo: bpm(), bar: 1, acceptLabel: 'Add these notes to the clip', onAccept: async ({ result }) => {
      const found = absolute(parse(result.notesText, model.ppq)).map(({ at, length, pitch, velocity, channel, bank, program }) => ({ at, length, pitch, velocity, channel, bank, program }));
      if (!found.length) { toast('No notes could be added.'); return; }
      addNotes(found); toast('Added ' + found.length + ' notes: an attempt, correct them here.');
    } }).catch((error) => toast(error.message || String(error)));
  }
  // the bend of the selected notes, in semitones (two decimals): written as ;bend=<cents> on each note, 0 takes it away
  async function bendSelected() {
    const picked = rowsBySelection();
    if (!picked.length) return;
    const now = picked[0].bend ? picked[0].bend / 100 : 2;
    const input = el('input', { type: 'number', step: 0.25, min: -24, max: 24, value: String(now), 'aria-label': 'Bend in semitones' });
    const answer = await ask('Bend the notes', `How many semitones ${picked.length === 1 ? 'this note glides' : `these ${picked.length} notes glide`} over ${picked.length === 1 ? 'its' : 'their'} length: 2 is up a whole tone, -12 down an octave, 0 takes the bend away. The sound of the block that plays the clip bends with it.`, [{ label: 'Cancel', value: false }, { label: 'Bend', value: true, primary: true }], [input]);
    if (answer !== true) return;
    const cents = Math.max(-2400, Math.min(2400, Math.round((Number(input.value) || 0) * 100)));
    const next = updateMany(model, new Map(picked.map((row) => [row.index, { bend: cents }])));
    if (next !== model) { commit(next); status.textContent = cents ? `Bend ${cents / 100} semitones on ${picked.length} note${picked.length === 1 ? '' : 's'}` : 'Bend taken away'; requestDraw(); }
  }

  function joinSelected() {
    const picked = state.selected.size ? rowsBySelection() : [];
    if (locked || picked.length < 2) return false;
    const byPitch = new Map();
    for (const r of picked) { if (!byPitch.has(r.pitch)) byPitch.set(r.pitch, []); byPitch.get(r.pitch).push(r); }
    const remove = [], draw = [];
    for (const [pitch, list] of byPitch) {
      list.sort((x, y) => x.at - y.at);
      let run = [list[0]];
      const flush = () => { if (run.length > 1) { remove.push(...run); draw.push({ pitch, at: run[0].at, length: run.reduce((sum, r) => sum + r.length, 0), velocity: run[0].velocity, extras: extrasOf(run[0]) }); } };
      for (const r of list.slice(1)) { const last = run.at(-1); if (r.at === last.at + last.length) run.push(r); else { flush(); run = [r]; } }
      flush();
    }
    if (!draw.length) return false;
    let next = removeRows(model, remove);
    for (const piece of draw) next = drawRow(next, piece.at, piece.pitch, piece.length, endTick(), piece.velocity);
    for (const piece of draw) if (Object.keys(piece.extras).length) next = adjustRows(next, rowsOf(next).filter((r) => r.pitch === piece.pitch && r.at === piece.at), () => piece.extras);
    state.selected = new Set();
    model = next; commit(model); requestDraw();
    return true;
  }
  function stepAt(x, y) {
    const d = state.drag, pitch = gridPitchAt(y), step = stepLen(), tick = atOf(x);
    if (d.action === 'long') {   // Alt+drag: one note from the first step to the step under the pointer, on the row where it began
      if (tick < 0 || tick >= endTick()) return;
      const col = columnOf(tick, step);
      d.pitch ??= pitch; d.from ??= col;
      if (d.pitch === null) return;
      model = drawRow(d.base, Math.min(d.from, col) * step, d.pitch, (Math.abs(col - d.from) + 1) * step, endTick(), state.stepVel);
      requestDraw(); return;
    }
    if (pitch === null || tick < 0 || tick >= endTick()) return;
    const col = columnOf(tick, step), key = `${col}:${pitch}`;
    if (d.seen.has(key)) return; d.seen.add(key);
    const here = coveringRows(rows(), col, pitch, step);
    let next = model;
    if (d.action === 'off') { if (here.length) next = removeRows(model, here); }
    else if (d.action === 'accent') next = here.length ? velocityRows(model, here, 127) : drawRow(model, col * step, pitch, step, endTick(), 127);
    else if (!here.length) next = drawRow(model, col * step, pitch, step, endTick(), state.stepVel);
    if (next === model) return;
    model = next; if (d.action !== 'off' && !state.muted.has(pitch)) hear({ pitch, velocity: d.action === 'accent' ? 127 : state.stepVel, length: step }); requestDraw();
  }
  /** A pointer-down on a note: select it, then move it (body), resize it (right edge) or clone it (Shift); a plain click only selects, sets the default length and plays it. */
  function startNoteDrag(n, event, x, y) {
    const additive = (event.ctrlKey || event.metaKey) && event.shiftKey;
    if (additive) state.selected = new Set([...state.selected, n.index]);
    else if (!state.selected.has(n.index)) state.selected = new Set([n.index]);
    const picked = rowsBySelection(), edge = !!hit(x, y)?.edge;
    state.lastLen = n.length;
    state.drag = { kind: edge ? 'resize' : event.shiftKey && !additive ? 'clone' : 'move', base: model, rows: picked, primary: n, grabTick: atOf(x), grabPitch: pitchOf(y), x0: x, y0: y, shift: 0, heard: 0, started: false };
    hear(n); requestDraw(); startAuto();
  }
  /** The Slice tool (FL: drag across the notes to cut them): each note the pointer meets is cut once at the snapped pointer position; one edit for the whole drag. */
  function sliceAt(x, y) {
    const d = state.drag, n = noteAt(x, y);
    if (!n || d.cut.has(`${n.at}:${n.pitch}`)) return;
    const tick = snap(atOf(x)), next = sliceRow(model, n, tick, sliceNote);
    if (next === model) return;
    d.cut.add(`${n.at}:${n.pitch}`); d.cut.add(`${tick}:${n.pitch}`); model = next; state.selected = new Set(); requestDraw();
  }
  /** The Paint tool. With Shift held (monophonic) each step column keeps ONE painted note: moving to another pitch in the same column replaces it. */
  function paint(x, y) {
    const step = lastLength(), tick = Math.floor(atOf(x) / step) * step, pitch = pitchOf(y), key = `${tick}:${pitch}`, d = state.drag;
    if (d.mono) {
      const old = d.cols.get(tick);
      if (old === pitch) return;
      let from = model;
      if (old !== undefined) { const index = rowIndexAt(model, tick, old); if (index >= 0) from = removeRows(model, mergedRows(model).filter((r) => r.index === index)); }
      const next = drawNew(from, tick, pitch, step);
      if (next !== from) { d.cols.set(tick, pitch); d.painted.add(key); hear({ pitch, velocity: 96 }); } else d.cols.delete(tick);
      if (next !== model) { model = next; requestDraw(); }
      return;
    }
    if (d.painted.has(key)) return; d.painted.add(key);
    const next = drawNew(model, tick, pitch, step); if (next !== model) { model = next; d.cols.set(tick, pitch); hear({ pitch, velocity: 96 }); requestDraw(); }
  }
  /** The Playback tool: a click starts playing from the snapped position (a playing roll jumps there). */
  function playFrom(tick) {
    state.playhead = Math.max(0, Math.min(endTick() - 1, snap(tick)));
    if (state.player || starting) stopPlayback(false);
    state.playhead = Math.max(0, Math.min(endTick() - 1, snap(tick)));
    followPausedReset(); togglePlay(false);
  }
  const followPausedReset = () => { state.followPaused = false; };
  /** The Zoom tool: a dragged box fills the view (time and rows), a plain click zooms in around the pointer. */
  function zoomBox(d) {
    const w = Math.abs(d.x1 - d.x0), h = Math.abs(d.y1 - d.y0);
    if (w < 6 && h < 6) { zoomBy(1.6, d.x0, 'both', d.y0); return; }
    const lo = Math.max(0, atOf(Math.min(d.x0, d.x1))), hi = Math.max(lo + 1, atOf(Math.max(d.x0, d.x1)));
    const time = fitRange(lo, hi, ppq, gridW(), 0), rowH = Math.max(LIMITS.minRowH, Math.min(LIMITS.maxRowH, gridH() / Math.max(1, h / state.rowH))), topPitch = pitchOf(Math.min(d.y0, d.y1));
    applyView({ pxBeat: time.pxBeat, rowH, scrollX: time.scrollX, scrollY: (127 - topPitch) * rowH });
  }
  /** One step of a pointer drag at grid coordinates (x, y); also called by the auto-scroll timer after the view moved. */
  function dragTo(x, y) {
    const d = state.drag;
    if (!d) return;
    if (d.kind === 'step') { stepAt(x, y); return; }
    if (d.kind === 'pan') { state.scrollX = d.sx - (x - d.x0); if (d.grid) state.gridScrollY = d.sy - (y - d.y0); else state.scrollY = d.sy - (y - d.y0); clampScroll(); requestDraw(); return; }
    if (d.kind === 'slice') { sliceAt(x, y); return; }
    if (d.kind === 'zoom') { d.x1 = x; d.y1 = y; requestDraw(); return; }
    if (d.kind === 'erase') { const n = noteAt(x, y); if (n) { model = removeRows(model, rowOf(n)); requestDraw(); } return; }
    if (d.kind === 'paint') { paint(x, y); return; }
    if (d.kind === 'rubber') { d.x1 = x; d.y1 = y; const picked = rows().filter((n) => { const nx = xOf(n.at), lane = d.grid ? gridList().indexOf(n.pitch) : 0, ny = d.grid ? lane * state.gridRowH - state.gridScrollY : yOf(n.pitch), rh = d.grid ? state.gridRowH : state.rowH; return lane >= 0 && nx < Math.max(d.x0, d.x1) && nx + (n.length / ppq) * state.pxBeat > Math.min(d.x0, d.x1) && ny < Math.max(d.y0, d.y1) && ny + rh > Math.min(d.y0, d.y1); }).map((n) => n.index); state.selected = new Set([...(d.add ? d.base : []), ...picked]); requestDraw(); return; }
    if (d.kind === 'draw') {
      const end = Math.max(d.tickStart + 1, beats() && !state.altKey ? Math.ceil(atOf(x) / (beats() * ppq)) * beats() * ppq : Math.round(atOf(x)));
      d.length = Math.max(1, end - d.tickStart); model = drawNew(d.base, d.tickStart, d.pitch, d.length); requestDraw(); return;
    }
    if (d.kind === 'move' || d.kind === 'clone') {
      if (!d.started && Math.abs(x - d.x0) < 3 && Math.abs(y - d.y0) < 3) return;
      d.started = true;
      const dTick = snap(d.primary.at + atOf(x) - d.grabTick) - d.primary.at, dPitch = pitchOf(y) - d.grabPitch, end = endTick();
      const shift = clampMove(d.rows, dTick, dPitch, end);
      model = d.kind === 'move' ? moveRows(d.base, d.rows, shift.dTick, shift.dPitch, end) : cloneRows(d.base, d.rows, shift.dTick, shift.dPitch, end);
      selectKeys(keysOfRows(d.rows, shift.dTick, shift.dPitch));
      if (shift.dPitch !== d.shift) { d.shift = shift.dPitch; if (reaudition()) { d.heard = shift.dPitch; hear({ pitch: d.primary.pitch + shift.dPitch, velocity: d.primary.velocity, length: d.primary.length, channel: d.primary.channel, program: d.primary.program }); } }
      requestDraw(); return;
    }
    if (d.kind === 'resize') {
      if (!d.started && Math.abs(x - d.x0) < 3) return;
      d.started = true;
      const min = state.altKey ? 1 : Math.max(1, Math.round(beats() * ppq)), length = Math.max(min, snap(atOf(x)) - d.primary.at);
      model = resizeRows(d.base, d.rows, length - d.primary.length, endTick());
      selectKeys(keysOfRows(d.rows)); state.lastLen = length; requestDraw();
    }
  }
  grid.addEventListener('pointermove', (event) => {
    setAlt(event.altKey);
    const { x, y } = local(event, grid);
    state.cursor = { x, y }; state.pointer = { x, y };
    if (state.drag?.kind === 'paint') state.drag.mono = event.shiftKey;
    if (!state.drag && state.mode === 'grid') { grid.dataset.over = ''; return; }
    if (!state.drag) { const h = hit(x, y); grid.dataset.over = h ? (h.edge ? 'edge' : 'note') : ''; grid.dataset.copy = h && !h.edge && event.shiftKey ? '1' : ''; return; }
    dragTo(x, y);
  });
  // edge auto-scroll: a frame timer, so holding the pointer past the edge keeps scrolling (decision 20.12)
  let autoFrame = 0;
  function startAuto() {
    if (autoFrame) return;
    const step = () => {
      autoFrame = 0;
      const d = state.drag, p = state.pointer;
      if (!panel.isConnected || !dragging(d) || !p) return;
      const dx = autoScrollDelta(p.x, gridW()), dy = autoScrollDelta(p.y, gridH());
      if (dx || dy) { const sx = state.scrollX, sy = state.scrollY; state.scrollX += dx; state.scrollY += dy; clampScroll(); if (sx !== state.scrollX || sy !== state.scrollY) { dragTo(p.x, p.y); requestDraw(); } }
      autoFrame = requestAnimationFrame(step);
    };
    autoFrame = requestAnimationFrame(step);
  }
  const finish = () => {
    const d = state.drag; if (!d) return; state.drag = null; delete grid.dataset.drag;
    if (d.kind === 'zoom') { zoomBox(d); requestDraw(); return; }
    if (d.kind === 'draw') state.lastLen = d.length;
    if ((d.kind === 'move' || d.kind === 'clone') && d.shift !== d.heard) hear({ pitch: d.primary.pitch + d.shift, velocity: d.primary.velocity, length: d.primary.length, channel: d.primary.channel, program: d.primary.program });   // the throttle may have dropped the last pitch
    if (['draw', 'paint', 'erase', 'move', 'clone', 'resize', 'slice', 'step'].includes(d.kind)) { if (model !== d.base) { if (d.kind === 'erase') state.selected = new Set(); commit(model); } else requestDraw(); }
    else requestDraw();
  };
  grid.addEventListener('pointerenter', () => { state.overGrid = true; });
  grid.addEventListener('pointerleave', () => { state.overGrid = false; });
  grid.addEventListener('pointerup', finish);
  grid.addEventListener('pointercancel', finish);
  // ---- touch (MUI06): two fingers pinch-zoom (time and rows) and pan, a long press deletes a note (accents a Grid step) ----
  const fingers = new Map();   // pointer id -> grid point, touch pointers only
  let pinch = null, press = null;
  const dropOneFingerGesture = () => {
    clearTimeout(press?.timer); press = null;
    const d = state.drag; if (!d) return;
    if (d.base && model !== d.base) model = d.base;
    if (d.kind === 'draw' || d.kind === 'paint') state.selected = new Set();
    state.drag = null; delete grid.dataset.drag; requestDraw();
  };
  function longPress() {
    const p = press, d = state.drag; press = null;
    if (!p || !d || pinch) return;
    const action = longPressAction({ mode: state.mode, hitNote: ['move', 'resize', 'clone'].includes(d.kind) });
    if (action === 'delete') {
      model = d.base; const rowsOfNote = rowOf(d.primary); model = removeRows(d.base, rowsOfNote); state.selected = new Set();
      state.drag = null; delete grid.dataset.drag; commit(model); requestDraw();
    } else if (action === 'accent' && d.kind === 'step') {
      model = d.base; state.drag = { kind: 'step', action: 'accent', base: d.base, seen: new Set() }; stepAt(p.x0, p.y0); finish();
    } else if (action === 'cancel' && ['draw', 'paint'].includes(d.kind)) dropOneFingerGesture();
    else return;
    navigator.vibrate?.(15);
  }
  grid.addEventListener('pointerdown', (event) => {
    if (event.pointerType !== 'touch') return;
    const pt = local(event, grid);
    fingers.set(event.pointerId, pt);
    if (fingers.size >= 2) {
      event.stopImmediatePropagation(); event.preventDefault();
      if (pinch) return;
      dropOneFingerGesture();
      const [a, b] = [...fingers.keys()];
      const gridMode = state.mode === 'grid';
      pinch = { a, b, keys: gridMode ? GRID_KEYS : ROLL_KEYS, start: pinchStart(fingers.get(a), fingers.get(b)), view0: gridMode ? { pxBeat: state.pxBeat, scrollX: state.scrollX, gridRowH: state.gridRowH, gridScrollY: state.gridScrollY } : viewNow() };
      return;
    }
    const client = { x: event.clientX, y: event.clientY };
    clearTimeout(press?.timer);
    press = { start: client, now: client, downAt: performance.now(), x0: pt.x, y0: pt.y, timer: setTimeout(() => { if (press && isLongPress({ start: press.start, now: press.now, downAt: press.downAt, at: performance.now() })) longPress(); }, LONG_PRESS_MS) };
  }, { capture: true });
  grid.addEventListener('pointermove', (event) => {
    if (event.pointerType !== 'touch' || !fingers.has(event.pointerId)) return;
    fingers.set(event.pointerId, local(event, grid));
    if (pinch) {
      event.stopImmediatePropagation();
      if (!fingers.has(pinch.a) || !fingers.has(pinch.b)) return;
      const v = pinchView(pinch.view0, pinch.start, pinchStart(fingers.get(pinch.a), fingers.get(pinch.b)), pinch.keys);
      state.pxBeat = v.pxBeat; state.scrollX = v.scrollX;
      if (pinch.keys === GRID_KEYS) { state.gridRowH = v.gridRowH; state.gridScrollY = v.gridScrollY; } else { state.rowH = v.rowH; state.scrollY = v.scrollY; }
      pauseFollow(); clampScroll(); requestDraw(); markView(false);
      return;
    }
    if (press) press.now = { x: event.clientX, y: event.clientY };
  }, { capture: true });
  const touchEnd = (event) => {
    if (event.pointerType !== 'touch') return;
    fingers.delete(event.pointerId);
    clearTimeout(press?.timer); press = null;
    if (pinch) { event.stopImmediatePropagation(); if (!fingers.has(pinch.a) || !fingers.has(pinch.b)) { pinch = null; markView(true); } }
  };
  grid.addEventListener('pointerup', touchEnd, { capture: true });
  grid.addEventListener('pointercancel', touchEnd, { capture: true });
  grid.addEventListener('dblclick', (event) => {
    if (locked || state.mode === 'grid' || ['slice', 'delete', 'playback', 'zoom'].includes(state.tool)) return;
    const { x, y } = local(event, grid), n = noteAt(x, y);
    if (!n) { const g = state.showGhosts ? ghostHit(x, y) : null; if (g) { if (g.open) g.open(); else goTo(g.clipId, false); } return; }
    const group = rowsBySelection(), next = velocityRows(model, group.length > 1 && state.selected.has(n.index) ? group : rowOf(n), 100);
    if (next !== model) { model = next; commit(next); requestDraw(); }
  });
  grid.addEventListener('contextmenu', (event) => event.preventDefault());
  /** Alt+wheel over a note: velocity (Shift = 5 steps); with Ctrl: fine tune. The change shows at once and is ONE edit a moment after the last wheel step. */
  function wheelAdjust(n, fine, dir, amount) { adjustGroup(state.selected.has(n.index) ? rowsBySelection() : rowOf(n), fine, dir, amount); }
  function adjustGroup(group, fine, dir, amount) {
    const next = adjustRows(model, group, (note) => (fine ? { fine: (note.fine ?? 0) + dir * amount } : { velocity: note.velocity + dir * amount }));
    if (next === model) return;
    model = next; requestDraw();
    clearTimeout(state.wheelTimer); state.wheelTimer = setTimeout(flushWheel, 250);
  }
  function flushWheel() { if (!state.wheelTimer) return; clearTimeout(state.wheelTimer); state.wheelTimer = 0; commit(model); }
  /** Grid view: the wheel over a filled step changes its velocity (Shift = 5); elsewhere it scrolls the rows, Shift+wheel the time, Ctrl+wheel zooms. */
  function gridWheel(event, x, y, dir, mod) {
    const pitch = gridPitchAt(y), step = stepLen(), here = pitch !== null && !locked ? coveringRows(rows(), columnOf(atOf(x), step), pitch, step) : [];
    if (event.altKey) { zoomGridRowsAt(y, dir > 0 ? 1.15 : 0.87); return; }
    if (mod) { zoomBy(dir > 0 ? 1.15 : 0.87, x, 'time', y); return; }
    if (here.length) { adjustGroup(here, false, dir, event.shiftKey ? 5 : 1); state.stepVel = Math.max(1, Math.min(127, here[0].velocity + dir * (event.shiftKey ? 5 : 1))); return; }
    pauseFollow();
    if (event.shiftKey || Math.abs(event.deltaX) > Math.abs(event.deltaY)) state.scrollX += event.shiftKey ? event.deltaY : event.deltaX; else state.gridScrollY += event.deltaY;
    clampScroll(); requestDraw();
  }
  grid.addEventListener('wheel', (event) => {
    event.preventDefault();
    const { x, y } = local(event, grid), dir = event.deltaY < 0 ? 1 : -1, mod = event.ctrlKey || event.metaKey;
    if (state.mode === 'grid') { gridWheel(event, x, y, dir, mod); return; }
    const n = noteAt(x, y);
    if (event.altKey && n && !locked) { wheelAdjust(n, mod, dir, event.shiftKey ? 5 : 1); return; }
    if (mod) { zoomBy(dir > 0 ? 1.15 : 0.87, x, 'time', y); return; }
    if (event.altKey) { zoomBy(dir > 0 ? 1.15 : 0.87, x, 'rows', y); return; }
    pauseFollow();
    if (event.shiftKey || Math.abs(event.deltaX) > Math.abs(event.deltaY)) { state.scrollX += event.shiftKey ? event.deltaY : event.deltaX; clampScroll(); requestDraw(); }
    else { state.scrollY += event.deltaY; clampScroll(); requestDraw(); }
  }, { passive: false });

  // ---- pointer: ruler (loop bracket, playhead, clip end) ----
  ruler.addEventListener('pointerdown', (event) => {
    setAlt(event.altKey); const { x } = local(event, ruler), tick = atOf(x);
    if (event.button === 2) { state.bracket = null; requestDraw(); return; }
    if (event.button !== 0) return;
    if (event.ctrlKey || event.metaKey) { ruler.setPointerCapture(event.pointerId); state.drag = { kind: 'range', a: tick, b: tick, add: event.shiftKey, base: state.selected }; if (!event.shiftKey) state.selected = new Set(); requestDraw(); return; }
    const endX = xOf(endTick());
    if (x >= endX - 2 && x <= endX + 44) { state.extraBars++; requestDraw(); return; }
    ruler.setPointerCapture(event.pointerId);
    // the green flag of the playhead can be picked up and dragged (also while it plays): back to hear something again, or ahead
    if (Math.abs(x - xOf(state.playhead)) <= 9) { pauseFollow(); state.drag = { kind: 'seek' }; return; }
    const tol = (6 / state.pxBeat) * ppq, part = hitLoop(state.bracket, tick, tol);
    state.drag = { kind: 'loop', mode: part === 'start' ? 'start' : part === 'end' ? 'end' : part === 'body' ? 'move' : 'create', anchor: tick, base: state.bracket, moved: false, x0: x };
  });
  ruler.addEventListener('pointermove', (event) => {
    const d = state.drag; setAlt(event.altKey);
    const { x } = local(event, ruler);
    if (d?.kind === 'seek') { state.playhead = Math.max(0, Math.min(endTick() - 1, snap(atOf(x)))); requestDraw(); return; }
    if (d?.kind === 'range') { d.b = atOf(x); const lo = Math.min(d.a, d.b), hi = Math.max(d.a, d.b); state.selected = new Set([...(d.add ? d.base : []), ...rows().filter((n) => n.at >= lo && n.at <= hi).map((n) => n.index)]); requestDraw(); return; }
    if (!d || d.kind !== 'loop') { const part = hitLoop(state.bracket, atOf(x), (6 / state.pxBeat) * ppq); ruler.style.cursor = Math.abs(x - xOf(state.playhead)) <= 9 ? 'ew-resize' : part === 'start' || part === 'end' ? 'ew-resize' : part === 'body' ? 'grab' : 'pointer'; return; }
    if (!d.moved && Math.abs(x - d.x0) < 3) return;
    d.moved = true;
    state.bracket = dragLoop(d.mode, d.anchor, atOf(x), d.base, endTick(), (tick) => snap(tick), Math.max(1, Math.round((beats() || 0.25) * ppq))); requestDraw();
  });
  ruler.addEventListener('pointerup', (event) => {
    const d0 = state.drag; if (d0?.kind === 'range') { state.drag = null; requestDraw(); return; }
    if (d0?.kind === 'seek') { state.drag = null; restartAt(state.playhead); requestDraw(); return; }
    const d = state.drag; if (!d || d.kind !== 'loop') return; state.drag = null;
    if (!d.moved) { state.playhead = Math.max(0, Math.min(endTick() - 1, snap(atOf(local(event, ruler).x)))); restartAt(state.playhead); }
    requestDraw();
  });
  ruler.addEventListener('dblclick', (event) => { if (event.ctrlKey || event.metaKey) return; state.bracket = { start: 0, end: endTick() }; requestDraw(); });
  ruler.addEventListener('contextmenu', (event) => event.preventDefault());

  // ---- lanes of the Grid: add one, give it another note, name it ----
  /** The pitches nobody uses yet: no notes, and not a row of the Grid either. A row can only move to one of these (two rows cannot be merged into one and split again). */
  const freePitches = () => { const used = new Set(gridList()); return [...Array(128).keys()].filter((p) => !used.has(p)); };
  const noteName = (p) => `${noteLabel(p)}`;
  function fillRowAdd() {
    const free = freePitches(), drums = drumChoices().filter((c) => free.includes(c.pitch));
    const groups = [el('option', { value: '', textContent: '+ Row' })];
    if (drums.length) groups.push(el('optgroup', { label: 'Drum sounds' }, drums.map((c) => el('option', { value: String(c.pitch), textContent: c.label, 'data-kind': 'drum' }))));
    for (let octave = 9; octave >= -1; octave--) {
      const notes = free.filter((p) => Math.floor(p / 12) - 1 === octave); if (notes.length) groups.push(el('optgroup', { label: `Notes, octave ${octave}` }, notes.reverse().map((p) => el('option', { value: String(p), textContent: `${noteName(p)} (${pitchLabel(p)})` }))));
    }
    rowAdd.replaceChildren(...groups);
  }
  /** A row moves to another pitch: its notes follow, and so do its name and its mute. */
  function remapLane(from, to, drum) {
    if (locked || from === to || !freePitches().includes(to)) return;
    const group = rows().filter((r) => r.pitch === from);
    const wasDrum = state.laneDrum.has(from), isDrum = drum === undefined ? wasDrum : drum;
    state.laneDrum.delete(from); if (isDrum) state.laneDrum.add(to);
    const routing = isDrum ? { channel: 9, bank: 0, program: 0 } : { channel: undefined, bank: undefined, program: undefined };
    if (group.length) { model = adjustRows(model, group, () => ({ pitch: to, ...routing })); commit(model); }
    state.gridPins = [...new Set(state.gridPins.filter((p) => p !== from).concat(to))];
    if (state.muted.delete(from)) state.muted.add(to);
    if (state.laneNames.has(from)) { state.laneNames.set(to, state.laneNames.get(from)); state.laneNames.delete(from); }
    state.activeRow = to; requestDraw(); markView(true);
  }
  /** What a lane sounds like: the instrument of the channel, or the General MIDI drum kit (every note of the lane is written for channel 10). */
  function setLaneKind(pitch, drum) {
    if (locked) return;
    if (drum) state.laneDrum.add(pitch); else state.laneDrum.delete(pitch);
    const group = rows().filter((r) => r.pitch === pitch);
    if (group.length) { model = adjustRows(model, group, () => (drum ? { channel: 9, bank: 0, program: 0 } : { channel: undefined, bank: undefined, program: undefined })); commit(model); }
    requestDraw(); markView(true);
  }
  /** The sound of the selected notes (or of all notes when nothing is selected): the instrument of the clip, or the General MIDI drum kit (channel 10). */
  function setSelectionKit(drum) {
    if (locked) return;
    const group = state.selected.size ? rowsBySelection() : rows();
    if (!group.length) return;
    model = adjustRows(model, group, () => (drum ? { channel: 9, bank: 0, program: 0 } : { channel: undefined, bank: undefined, program: undefined }));
    commit(model); requestDraw();
  }
  async function renameLane(pitch) {
    const input = el('input', { type: 'text', value: state.laneNames.get(pitch) || '', placeholder: pitchLabel(pitch), maxLength: 24, 'aria-label': 'Name of the lane' });
    const { ask } = await import('./ui.js');
    if (!(await ask('Name this lane', `Shown in the Grid instead of "${pitchLabel(pitch)}". Empty gives the sound's own name back. The name is kept in this browser with the view of the clip; it is not part of the song.`, [{ label: 'OK', value: true, primary: true }, { label: 'Cancel', value: false }], [input]))) return;
    const name = input.value.trim().slice(0, 24);
    if (name) state.laneNames.set(pitch, name); else state.laneNames.delete(pitch);
    requestDraw(); markView(true);
  }
  // hovering a sound in the lane menu plays it, so it can be chosen by ear
  let hoverId = 0;
  const stopPreview = () => { jamVoice?.stop(); if (hoverId) { audition.release(hoverId); hoverId = 0; } };
  const previewPitch = (p, drum = false) => { stopPreview(); if (jamVoice) { jamVoice.play(p, { drum }); return; } hoverId = trig({ pitch: p, velocity: 100, length: ppq, ...(drum ? { channel: 9 } : {}) }, { mono: true, heldMs: 1500, startedAt: performance.now() }); };   // a drum sound plays as a drum (General MIDI channel 10), a note as a tone
  async function laneMenu(event, pitch) {
    const { showMenu } = await import('./ui.js');
    const free = freePitches(), empty = !rows().some((r) => r.pitch === pitch), isDrum = state.laneDrum.has(pitch), channelKind = !!options.instrument;
    const drums = drumChoices().filter((c) => free.includes(c.pitch)).map((c) => ({ label: c.label, hover: () => previewPitch(c.pitch, true), leave: stopPreview, fn: () => remapLane(pitch, c.pitch, true) }));
    const octaves = [];
    for (let octave = 9; octave >= -1; octave--) { const notes = free.filter((p) => Math.floor(p / 12) - 1 === octave).reverse(); if (notes.length) octaves.push({ label: `Octave ${octave}`, children: notes.map((p) => ({ label: `${noteName(p)} (${pitchLabel(p)})`, hover: () => previewPitch(p), leave: stopPreview, fn: () => remapLane(pitch, p, channelKind ? false : undefined) })) }); }
    showMenu(event.clientX, event.clientY, [
      { head: laneLabel(pitch) },
      { label: 'Name this lane...', fn: () => renameLane(pitch) },
      ...(!locked && channelKind ? [{ label: 'Sound of this lane', children: [{ label: `${isDrum ? '    ' : '\u2713  '}Instrument of the channel`, fn: () => setLaneKind(pitch, false) }, { label: `${isDrum ? '\u2713  ' : '    '}Drum kit (General MIDI drums)`, fn: () => setLaneKind(pitch, true) }] }] : []),
      ...(locked ? [] : [{ label: channelKind ? (isDrum ? 'Give it another drum sound' : 'Give it another note') : 'Give it another sound or note', children: channelKind ? (isDrum ? drums : octaves) : [...(drums.length ? [{ label: 'Drum sounds', children: drums }] : []), ...octaves] }]),
      ...(state.laneNames.has(pitch) ? [{ label: 'Use the sound\'s own name', fn: () => { state.laneNames.delete(pitch); requestDraw(); markView(true); } }] : []),
      ...(empty && !locked ? ['-', { label: 'Remove this empty lane', danger: true, fn: () => { state.gridPins = state.gridPins.filter((p) => p !== pitch); state.laneNames.delete(pitch); state.muted.delete(pitch); requestDraw(); markView(true); } }] : []),
    ]);
  }
  keys.addEventListener('contextmenu', async (event) => {
    event.preventDefault();
    const { y } = local(event, keys);
    if (state.mode === 'grid') { const pitch = gridPitchAt(y); if (pitch !== null) laneMenu(event, pitch); return; }
    // the Roll: a key can sound as the instrument or as the drum kit (all notes of that pitch); the same choice the lane has in the Grid, and the way back
    const pitch = pitchOf(y), isDrum = state.laneDrum.has(pitch) || rows().some((r) => r.pitch === pitch && r.channel === 9), { showMenu } = await import('./ui.js');
    // the choices: a drum sound (the notes move to that drum of the kit) or another note; only a pitch with no notes and no lane of its own (two keys cannot be merged into one and split again), heard on hover
    const free = freePitches();
    const drums = drumChoices().filter((c) => free.includes(c.pitch)).map((c) => ({ label: c.label, hover: () => previewPitch(c.pitch, true), leave: stopPreview, fn: () => remapLane(pitch, c.pitch, true) }));
    const octaves = [];
    for (let octave = 9; octave >= -1; octave--) { const notes = free.filter((p) => Math.floor(p / 12) - 1 === octave).reverse(); if (notes.length) octaves.push({ label: `Octave ${octave}`, children: notes.map((p) => ({ label: `${noteName(p)} (${pitchLabel(p)})`, hover: () => previewPitch(p), leave: stopPreview, fn: () => remapLane(pitch, p, false) })) }); }
    showMenu(event.clientX, event.clientY, [
      { head: `${noteLabel(pitch)} · ${pitchLabel(pitch) !== noteLabel(pitch) ? pitchLabel(pitch) : 'sound of the notes on this key'}` },
      { label: `${isDrum ? '    ' : '✓  '}Instrument sound`, fn: () => setLaneKind(pitch, false) },
      { label: `${isDrum ? '✓  ' : '    '}Drum kit sound`, fn: () => setLaneKind(pitch, true) },
      ...(locked ? [] : ['-', ...(drums.length ? [{ label: 'Give it another drum sound', children: drums }] : []), { label: 'Give it another note', children: octaves }]),
    ]);
  });

  // ---- thin scrollbars over the editor (a horizontal one above the velocity strip, a vertical one at the right edge): they show when the pointer is near them, and drag like any scrollbar ----
  const maxScrollY = () => (state.mode === 'grid' ? Math.max(0, gridList().length * state.gridRowH + 6 - gridH()) : Math.max(0, 128 * state.rowH - gridH()));
  function setThumb(bar, axis, view, max, pos) {
    bar.hidden = max <= 1;
    if (bar.hidden) return;
    const track = axis === 'h' ? bar.clientWidth : bar.clientHeight, size = Math.max(28, track * view / (view + max)), at = (track - size) * Math.min(1, pos / max), thumb = bar.firstChild;
    thumb.style[axis === 'h' ? 'width' : 'height'] = `${size}px`; thumb.style[axis === 'h' ? 'left' : 'top'] = `${at}px`;
  }
  function updateScrollbars() {
    setThumb(sbH, 'h', gridW(), maxScrollX(), state.scrollX);
    setThumb(sbV, 'v', gridH(), maxScrollY(), state.mode === 'grid' ? state.gridScrollY : state.scrollY);
  }
  function scrollTo(axis, value) {
    pauseFollow();
    if (axis === 'h') state.scrollX = value; else if (state.mode === 'grid') state.gridScrollY = value; else state.scrollY = value;
    clampScroll(); requestDraw();
  }
  for (const [bar, axis] of [[sbH, 'h'], [sbV, 'v']]) {
    bar.addEventListener('pointerdown', (event) => {
      event.preventDefault(); bar.setPointerCapture(event.pointerId); bar.classList.add('on', 'drag');
      const thumb = bar.firstChild, track = axis === 'h' ? bar.clientWidth : bar.clientHeight, thumbSize = axis === 'h' ? thumb.offsetWidth : thumb.offsetHeight;
      const max = axis === 'h' ? maxScrollX() : maxScrollY(), cur = () => (axis === 'h' ? state.scrollX : state.mode === 'grid' ? state.gridScrollY : state.scrollY);
      const rect = bar.getBoundingClientRect(), pointer = (e) => (axis === 'h' ? e.clientX - rect.left : e.clientY - rect.top);
      const thumbAt = axis === 'h' ? thumb.offsetLeft : thumb.offsetTop;
      let grab = pointer(event) - thumbAt;
      if (grab < 0 || grab > thumbSize) { grab = thumbSize / 2; scrollTo(axis, (pointer(event) - grab) / Math.max(1, track - thumbSize) * max); }   // a click beside the thumb jumps there
      const start = { p: pointer(event), v: cur() };
      const move = (e) => scrollTo(axis, start.v + (pointer(e) - start.p) / Math.max(1, track - thumbSize) * max);
      const up = () => { bar.classList.remove('drag'); bar.removeEventListener('pointermove', move); bar.removeEventListener('pointerup', up); bar.removeEventListener('pointercancel', up); };
      bar.addEventListener('pointermove', move); bar.addEventListener('pointerup', up); bar.addEventListener('pointercancel', up);
    });
  }
  body.addEventListener('pointermove', (event) => {
    const r = body.getBoundingClientRect(), x = event.clientX - r.left, y = event.clientY - r.top, bottom = r.height - VEL_H;
    sbH.classList.toggle('on', sbH.classList.contains('drag') || (x > keyW && y > bottom - 22 && y < bottom + 4));
    sbV.classList.toggle('on', sbV.classList.contains('drag') || (x > r.width - 24 && y > RULER_H && y < bottom));
  });
  body.addEventListener('pointerleave', () => { if (!sbH.classList.contains('drag')) sbH.classList.remove('on'); if (!sbV.classList.contains('drag')) sbV.classList.remove('on'); });

  // ---- the menu of the window: go to AMPLE Classic, the hover tips of the editor on or off, the shortcuts ----
  const menu = el('div', { className: 'mls-menu', role: 'menu', hidden: true });
  const closeMenu = () => { menu.hidden = true; menuButton.setAttribute('aria-expanded', 'false'); document.removeEventListener('pointerdown', menuOutside, true); };
  const menuOutside = (event) => { if (!menu.contains(event.target) && !menuButton.contains(event.target)) closeMenu(); };
  const menuItem = (label, onclick, extra = {}) => el('button', { type: 'button', className: 'mls-menu-item', role: extra.check ? 'menuitemcheckbox' : 'menuitem', onclick, ...(extra.check ? { 'aria-checked': 'false' } : {}) }, [el('span', { className: 'mls-menu-glyph', 'aria-hidden': 'true', textContent: extra.glyph || '' }), el('span', { className: 'mls-menu-label', textContent: label }), ...(extra.check ? [el('span', { className: 'mls-switch', 'aria-hidden': 'true' })] : [])]);
  const copyItem = options.file ? menuItem('Make an editable copy as notes', async () => {
    closeMenu();
    const name = freeName(node.id + '-notes');
    const ops = [{ type: 'addBlock', blockType: 'midi-clip', name }, { type: 'setParameter', block: name, name: 'ppq', value: String(ppq) }, { type: 'setParameter', block: name, name: 'notes', value: textOf(model) }];
    if (await edit(ops)) { panel.close(); select(name); notify(); toast('Added ' + name + ' with ' + model.notes.length + ' notes; connect it where you need it.'); }
  }, { glyph: '\u29C9' }) : null;
  // a clip whose notes are a pipeline: the roll shows what it makes (read-only); the menu bakes it (stores the notes, signed, in a bake stage) or turns it into plain notes
  const runBake = async (detach) => {
    try {
      const result = await api.post('/api/notes/bake', { path: S.path, block: options.derived.block, detach });
      if (await applyText(result.text)) { panel.close(); toast(detach ? 'The clip is plain notes now.' : `Baked ${result.notes} notes into the stage "${result.stage}".`); }
    } catch (error) { toast(error.message); }
  };
  const showProvenance = async () => {
    try {
      const r = await api.post('/api/notes/provenance', { path: S.path, block: options.derived.block });
      const lines = [];
      for (const u of r.seeds) lines.push(`${u.operator}: seed ${u.seed}  ->  key ${u.key}, ${u.draws} value${u.draws === 1 ? '' : 's'} drawn`);
      for (const st of r.streams) lines.push(`random-stream ${st.name}: seed ${st.seed}  ->  key ${st.key || '(not read)'}, consumed ${st.consumed}`);
      const names = r.names.map((n) => `"${n.name}" (${n.count}x)`).join(', ');
      await ask('Where does the randomness come from?', lines.length ? lines.join('\n') + (names ? `\n\nStable names of this song: ${names}.` : '') : 'This pipeline draws no random values.', [{ label: 'Close', value: true, primary: true }]);
    } catch (error) { toast(error.message); }
  };
  // a pipeline that reads the clock (or any other volatile value) is not reproducible until it is baked: say so where the notes are shown, and follow the song:
  // a new build (a new clock) or an edit of the song gives other notes, and then the roll shows the new ones
  const clockKey = () => (songClock()?.getTime() ?? 0) + '|' + (S.doc?.text ?? '');
  let derivedKey = options.derived ? clockKey() : '', derivedBusy = false, banner = null;
  const dynamicNotes = () => (options.derived ? (S.doc?.stabilityNotes || []).filter((line) => line.startsWith(options.derived.block + ': ')) : []);
  const updateBanner = () => {
    const notes = dynamicNotes();
    if (!notes.length) { banner?.remove(); banner = null; return; }
    const at = songClock();
    const what = [...new Set(notes.map((line) => line.slice(line.indexOf(': ') + 2).replace(/ = .*$/, '')))].join(', ');
    const text = 'Dynamic: shown with the clock of ' + (S.build.clockAt && S.build.clockPath === S.path ? 'the last build' : 'this moment') + (at ? ' (' + at.toLocaleTimeString() + ')' : '') + '. The next build can give other notes, and then this roll follows (' + what + ').';
    if (!banner) {
      banner = el('div', { className: 'mls-dynamic-banner', role: 'status' }, [
        el('span', { textContent: text }),
        el('button', { type: 'button', textContent: 'Bake resulting notes', title: 'Store these notes, signed: the clip stops changing', onclick: () => runBake(false) }),
        el('button', { type: 'button', textContent: 'Make editable', title: 'Turn the pipeline into plain notes you can edit', onclick: () => runBake(true) }),
      ]);
      toolbar.after(banner);
    } else banner.firstChild.textContent = text;
  };
  async function refreshDerived() {
    const key = clockKey();
    if (key === derivedKey || derivedBusy || !panel.isConnected) return;
    derivedBusy = true;
    derivedKey = key;
    try {
      const { pipelineNotes } = await import('./roll-views.js');
      const target = flat().nodes.find((n) => n.id === options.derived.block);
      if (target) {
        const made = await pipelineNotes(target);
        if (made.text !== null) { options.text = '"' + made.text + '"'; reload(); }
      }
      updateBanner();
    } finally { derivedBusy = false; }
    if (clockKey() !== derivedKey) refreshDerived();   // it changed again meanwhile
  }
  if (options.derived) updateBanner();
  const derivedItems = options.derived ? [
    menuItem('Where does the randomness come from?', () => { closeMenu(); showProvenance(); }, { glyph: '\u2684' }),
    menuItem('Bake this pipeline (store its notes, signed)', () => { closeMenu(); runBake(false); }, { glyph: '\u2744' }),
    menuItem('Turn the pipeline into plain notes', () => { closeMenu(); runBake(true); }, { glyph: '\u270E' }),
    ...Array.from({ length: options.derived.pieces || 0 }, (_, i) => menuItem(`Edit the hand-written piece ${i + 1}`, () => { closeMenu(); const target = flat().nodes.find((n) => n.id === options.derived.block); panel.close(); if (target) openEditablePiece(target, i); }, { glyph: '\u266B' })),
  ] : [];
  const tipsItem = menuItem('Hover tips in the editor', () => { try { localStorage.setItem('ample.jamTips', tipsOn() ? '0' : '1'); } catch { /* storage may be blocked */ } tipsItem.setAttribute('aria-checked', String(tipsOn())); hideTip(); }, { check: true, glyph: '\u24D8' });   // switching it does not close the menu
  const fxOn = () => { try { return localStorage.getItem('ample.jamFx') !== '0'; } catch { return true; } };
  const applyFx = () => panel.classList.toggle('no-fx', !fxOn());
  const fxItem = menuItem('Animated title bar and glow', () => { try { localStorage.setItem('ample.jamFx', fxOn() ? '0' : '1'); } catch { /* storage may be blocked */ } fxItem.setAttribute('aria-checked', String(fxOn())); applyFx(); }, { check: true, glyph: '\u266B' });
  applyFx();
  const instrumentItem = menuItem('Selected notes: instrument sound', () => { closeMenu(); setSelectionKit(false); }, { glyph: '\u266A' });
  const kitItem = menuItem('Selected notes: drum kit (K)', () => { closeMenu(); setSelectionKit(true); }, { glyph: '\u25C6' });
  const splitItem = menuItem('Split long notes into steps (S)', () => { closeMenu(); splitRows(state.selected.size ? rowsBySelection() : rows()); }, { glyph: '\u2702' });
  const joinItem = menuItem('Join selected neighbours (J)', () => { closeMenu(); joinSelected(); }, { glyph: '\u2194' });
  const toolItems = [['Reverse in time', reverseTime, '⇄'], ['Double the speed (half the length)', (n) => scaleTime(n, 0.5), '½'], ['Halve the speed (double the length)', (n) => scaleTime(n, 2), '×2'], ['Flip pitch', (n) => flipPitch(n), '⇅'], ['Legato', legato, '─'], ['Staccato (half length)', (n) => staccato(n, 50), '·'], ['Remove overlaps', removeOverlaps, '⊔']]
    .map(([label, fn, glyph]) => menuItem(label, () => { closeMenu(); applyTool(fn); }, { glyph }));
  const rootSelect = el('select', { className: 'mls-menu-select', 'aria-label': 'Scale root' }, [el('option', { value: '', textContent: 'No scale' }), ...ROOTS.map((r, i) => el('option', { value: String(i), textContent: r.toUpperCase() }))]);
  const scaleIdSelect = el('select', { className: 'mls-menu-select', 'aria-label': 'Scale' }, Object.keys(SCALE_DEGREES).map((id) => el('option', { value: id, textContent: id })));
  const refreshScaleUi = () => { const s = currentScale(); rootSelect.value = s ? String(s.root) : ''; if (s) scaleIdSelect.value = s.scaleId; scaleIdSelect.disabled = !s; };
  const writeScale = async () => {
    if (locked) { refreshScaleUi(); return; }
    if (rootSelect.value !== '' && !scaleIdSelect.value) scaleIdSelect.value = 'major';
    const value = rootSelect.value === '' ? 'none' : `${ROOTS[Number(rootSelect.value)]}:${scaleIdSelect.value}`;
    await edit([value === 'none' ? { type: 'removeParameter', block: node.id, name: 'scale' } : { type: 'setParameter', block: node.id, name: 'scale', value: `"${value}"` }]);
    refreshScaleUi(); requestDraw();
  };
  rootSelect.onchange = () => { scaleIdSelect.disabled = rootSelect.value === ''; writeScale(); }; scaleIdSelect.onchange = writeScale;
  const scaleRow = el('div', { className: 'mls-menu-row' }, [rootSelect, scaleIdSelect]);
  const lockItem = menuItem('Snap new notes to the scale', () => { state.scaleLock = !state.scaleLock; lockItem.setAttribute('aria-checked', String(state.scaleLock)); }, { check: true });
  const fitItem = menuItem('Fit selected notes to the scale', () => {
    closeMenu(); const scale = currentScale();
    if (!scale) { toast('Choose a scale first.'); return; }
    const changes = new Map();
    for (const i of state.selected) { const n = model.notes[i]?.note; if (!n) continue; const fit = nearestScaleTone(n.pitch, scale); if (fit !== n.pitch) changes.set(i, { pitch: fit }); }
    if (!changes.size) { toast('Nothing to fit: the selection is already in the scale.'); return; }
    if (!locked) commit(updateMany(model, changes));
  }, { glyph: '♯' });
  const keyItem = menuItem('Guess the key from the notes', () => {
    closeMenu(); const guess = detectKey(rows());
    if (!guess) { toast('Not enough notes to guess a key.'); return; }
    rootSelect.value = String(guess.root); scaleIdSelect.value = guess.mode === 'minor' ? 'minor' : 'major'; scaleIdSelect.disabled = false; writeScale();
  }, { glyph: '?' });
  const createItems = [menuItem('Arpeggiate the selected chord\u2026', () => { closeMenu(); openArpeggiateForm(); }, { glyph: '\u2234' }), menuItem('Riff machine\u2026', () => { closeMenu(); openRiffForm(); }, { glyph: '\u266B' }), menuItem('Stamp a chord\u2026', () => { closeMenu(); openStampForm(); }, { glyph: '\u2630' })];
  const groupSelect = el('select', { className: 'mls-menu-select', 'aria-label': 'Show one colour group', onchange: () => { state.groupFilter = Number(groupSelect.value) || 0; requestDraw(); } }, [el('option', { value: '0', textContent: 'All groups' }), ...Array.from({ length: 8 }, (_, i) => el('option', { value: String(i + 1), textContent: 'Group ' + (i + 1) }))]);
  const groupItems = [el('div', { className: 'mls-menu-row' }, [groupSelect]), menuItem('Select the notes of this group', () => { closeMenu(); selectGroup(); }, { glyph: '\u25A3' }), menuItem('Set the MIDI channel of this group\u2026', () => { closeMenu(); openGroupChannelForm(); }, { glyph: '\u2630' }), menuItem('Properties of the selected notes\u2026', () => { closeMenu(); openPropertiesForm(); }, { glyph: '\u270E' })];
  const ghostItem = menuItem('Show the clips behind as outlines', () => { state.showGhosts = !state.showGhosts; ghostItem.setAttribute('aria-checked', String(state.showGhosts)); requestDraw(); }, { check: true });
  const copyGhostItem = menuItem('Copy those outlines into this clip', () => {
    closeMenu(); const items = ghostList().flatMap((g) => g.notes);
    if (!items.length) { toast('No outlines are visible (turn them on, or no clip behind this one has notes here).'); return; }
    addNotes(items);
  }, { glyph: '\u29C9' });
  const chordLockItem = menuItem('Snap new notes to the chords behind', () => { state.chordLock = !state.chordLock; chordLockItem.setAttribute('aria-checked', String(state.chordLock)); }, { check: true });
  const lanesItem = menuItem('Arrangement, sound and automation lanes', () => { lanesCtl.setVisible(!lanesCtl.visible()); lanesItem.setAttribute('aria-checked', String(lanesCtl.visible())); }, { check: true });
  const moreToolItems = [menuItem('Strum the selected chord\u2026', () => { closeMenu(); openStrumForm(); }, { glyph: '\u2248' }), menuItem('Hi-hat roll chopper\u2026', () => { closeMenu(); openChopForm(); }, { glyph: '\u2261' }), menuItem('Humanize\u2026', () => { closeMenu(); openHumanizeForm(); }, { glyph: '\u223F' }), menuItem('Hum a melody\u2026', () => { closeMenu(); humMelody(); }, { glyph: '\u25CF' }), menuItem('Play the built song from here', () => { closeMenu(); playSongFromView(); }, { glyph: '▶' }), menuItem('Edit as text', () => { closeMenu(); editAsText(); }, { glyph: '{ }' })];
  const shortcutsItem = menuItem('Shortcuts', () => { closeMenu(); helpCard.hidden = !helpCard.hidden; }, { glyph: '?' });
  const [strumItem, chopItem, humanizeItem, humItem, songItem, textItem] = moreToolItems;
  const sectionBodies = [];
  /** A folded group of menu items: a heading that opens it (only one is open at a time, so the menu stays short). */
  function menuSection(titleText, items) {
    const body = el('div', { className: 'mls-menu-section', hidden: true }, items);
    const head = el('button', { type: 'button', className: 'mls-menu-head', 'aria-expanded': 'false', onclick: () => {
      const open = body.hidden;
      for (const s of sectionBodies) { s.body.hidden = true; s.head.setAttribute('aria-expanded', 'false'); }
      body.hidden = !open; head.setAttribute('aria-expanded', String(open));
    } }, [el('span', { textContent: titleText }), el('span', { className: 'mls-menu-chev', 'aria-hidden': 'true', textContent: '\u25B8' })]);
    sectionBodies.push({ head, body });
    return [head, body];
  }
  menu.append(...(derivedItems.length ? [...derivedItems, el('hr', { className: 'mls-menu-sep' })] : []), ...(copyItem ? [copyItem, el('hr', { className: 'mls-menu-sep' })] : []), splitItem, joinItem,
    ...menuSection('On the selected notes', [...toolItems, strumItem, chopItem, humanizeItem]),
    ...menuSection('Create', [...createItems, humItem]),
    ...menuSection('Scale', [scaleRow, lockItem, fitItem, keyItem]),
    ...menuSection('Colour groups and properties', groupItems),
    ...menuSection('Lanes and clips behind', [lanesItem, ghostItem, copyGhostItem, chordLockItem]),
    el('hr', { className: 'mls-menu-sep' }), songItem, textItem, el('hr', { className: 'mls-menu-sep' }), instrumentItem, kitItem, el('hr', { className: 'mls-menu-sep' }), tipsItem, fxItem, el('hr', { className: 'mls-menu-sep' }), shortcutsItem);
  new MutationObserver(() => { if (panel.dataset.window) closeMenu(); }).observe(panel, { attributes: true, attributeFilter: ['data-window'] });   // rolled up or maximized: no menu hanging in the air
  panel.append(menu);
  menuButton.onclick = () => {
    if (!menu.hidden) { closeMenu(); return; }
    refreshScaleUi(); lockItem.setAttribute('aria-checked', String(!!state.scaleLock)); ghostItem.setAttribute('aria-checked', String(!!state.showGhosts)); lanesItem.setAttribute('aria-checked', String(lanesCtl.visible())); chordLockItem.setAttribute('aria-checked', String(!!state.chordLock)); groupSelect.value = String(state.groupFilter || 0);
    tipsItem.setAttribute('aria-checked', String(tipsOn())); fxItem.setAttribute('aria-checked', String(fxOn()));
    menu.hidden = false; menuButton.setAttribute('aria-expanded', 'true');
    const pr = panel.getBoundingClientRect(), br = menuButton.getBoundingClientRect();
    menu.style.left = `${Math.max(4, Math.min(br.right - pr.left - menu.offsetWidth + 6, pr.width - menu.offsetWidth - 4))}px`;
    menu.style.top = `${br.bottom - pr.top + 4}px`;
    document.addEventListener('pointerdown', menuOutside, true);
  };
  menu.addEventListener('keydown', (event) => { if (event.key === 'Escape') { event.stopPropagation(); closeMenu(); menuButton.focus(); } });

  // ---- tooltips on the editor itself: what is under the mouse, and what a click or a drag does there ----
  const tip = el('div', { className: 'mls-tip', role: 'tooltip', hidden: true });
  panel.append(tip);
  let tipTimer = 0;
  const hideTip = () => { clearTimeout(tipTimer); tip.hidden = true; };
  const tipsOn = () => { try { return localStorage.getItem('ample.jamTips') === '1'; } catch { return false; } };   // the hover tips of the editor are off until the menu switches them on
  function showTip(text, event) {
    clearTimeout(tipTimer);
    if (!text || !tipsOn()) { tip.hidden = true; return; }
    const cx = event.clientX, cy = event.clientY;
    tipTimer = setTimeout(() => {
      const pr = panel.getBoundingClientRect();
      tip.textContent = text; tip.hidden = false;
      const w = tip.offsetWidth, h = tip.offsetHeight;
      tip.style.left = `${Math.max(4, Math.min(cx - pr.left + 14, pr.width - w - 4))}px`;
      tip.style.top = `${Math.max(4, Math.min(cy - pr.top + 18, pr.height - h - 4))}px`;
    }, 450);
  }
  const barBeat = (tick) => { const beat = Math.floor(tick / ppq); return `bar ${Math.floor(beat / barBeats()) + 1}, beat ${(beat % barBeats()) + 1}`; };
  function gridTip(x, y) {
    const pitch = gridPitchAt(y); if (pitch === null) return '';
    const step = stepLen(), col = columnOf(atOf(x), step), info = cellInfo(rows(), col, pitch, step), where = `${pitchLabel(pitch)} · ${barBeat(col * step)}`;
    if (info.rows.length) return `${where} · velocity ${Math.max(...info.rows.map((r) => r.velocity))}
Click removes the step, drag paints, right-click deletes. Alt+click on a long note splits it into steps; Alt+wheel changes the velocity.`;
    return `${where}
Click to add a step, drag to paint several; Alt+drag draws one long note.`;
  }
  function rollTip(x, y) {
    const h = hit(x, y);
    if (h) return `${h.note.channel === 9 ? `${pitchLabel(h.note.pitch)} · drum kit` : noteLabel(h.note.pitch)} · velocity ${h.note.velocity} · ${barBeat(h.note.at)}
${h.edge ? 'Drag the edge to change the length.' : 'Drag to move, drag the right edge to resize, Shift+drag copies, right-click deletes.'}`;
    const pitch = pitchOf(y);
    return `${noteLabel(pitch)} · ${barBeat(Math.max(0, atOf(x)))}
${{ draw: 'Click to add a note, drag to stretch it.', paint: 'Drag to paint notes.', delete: 'Click or drag over notes to delete them.', slice: 'Click a note to cut it in two.', select: 'Drag a box to select notes (Ctrl+drag in any tool).', playback: 'Click to move the playhead here.', zoom: 'Drag to zoom into the area.' }[state.tool] || ''}`;
  }
  grid.addEventListener('pointermove', (event) => { if (state.drag) { hideTip(); return; } const { x, y } = local(event, grid); showTip(state.mode === 'grid' ? gridTip(x, y) : rollTip(x, y), event); });
  for (const e of ['pointerleave', 'pointerdown', 'wheel']) { grid.addEventListener(e, hideTip); keys.addEventListener(e, hideTip); ruler.addEventListener(e, hideTip); velocity.addEventListener(e, hideTip); }
  keys.addEventListener('pointermove', (event) => {
    if (state.drag) return; const { x: kx, y } = local(event, keys), pitch = keyPitch(y);
    if (pitch === null) { hideTip(); return; }
    if (state.mode !== 'grid') { showTip(`${noteLabel(pitch)}
Press to hear this note.`, event); return; }
    const name = pitchLabel(pitch), volume = rowVolume(rows(), pitch), muted = state.muted.has(pitch);
    if (kx >= PAN_X - 13 && kx < KNOB_X - 13) { showTip(`${name}: pan\nDrag up or down to pan every note of this row (double-click: back to the middle).`, event); return; }
    if (kx >= NUM_X - 4 && kx < NAME_X - 3) { showTip(`${name}: row ${gridList().indexOf(pitch) + 1}\nClick to select every note of this row.`, event); return; }
    showTip(kx < PAN_X - 13 ? `${name}: sound ${muted ? 'off' : 'on'}
Click to ${muted ? 'switch it on' : 'mute this row while you jam'} (the notes stay).` : kx < NUM_X - 4 ? `${name}: volume ${volume || '-'}
Drag up or down to make every step of this row louder or softer.` : `${name}
Press to hear this sound. Right-click a row's cells to delete.`, event);
  });
  ruler.addEventListener('pointermove', (event) => {
    const { x } = local(event, ruler), endX = xOf(endTick());
    if (x >= endX - 2 && x <= endX + 44) { const tone = endTone(); showTip(`End of the clip (${endBars()} bar${endBars() === 1 ? '' : 's'}). Click to add one more bar.\n${tone === '#ff6b6b' ? 'Red: notes reach past the length this clip has in the arrangement, so they would be cut off when it plays.' : tone === '#f0a040' ? 'Orange: a note touches the end, so there is no room left after it.' : 'Mint: there is room before the end.'}`, event); return; }
    showTip('Ruler: click sets the playhead, drag makes a loop bracket, double-click selects the whole clip, right-click clears the bracket.', event);
  });
  velocity.addEventListener('pointermove', (event) => {
    if (state.drag) return;
    // the bar under the mouse: say which note it belongs to (within a few pixels of its start)
    const { x } = local(event, velocity);
    const near = rows().map((r) => ({ r, d: Math.abs(xOf(r.at) - x) })).filter((c) => c.d <= 5).sort((a, b) => a.d - b.d)[0];
    showTip(near ? `${state.mode === 'grid' ? laneLabel(near.r.pitch) : noteLabel(near.r.pitch)} · ${barBeat(near.r.at)} · velocity ${near.r.velocity}\nDrag this bar up or down to set the velocity, Shift+drag draws a ramp.` : 'Velocity of each note: drag a bar to set it, Shift+drag draws a ramp.', event);
  });

  // ---- pointer: piano keys (hear a note while the button is down) ----
  keys.addEventListener('pointerdown', (event) => {
    keys.setPointerCapture(event.pointerId); const { x: kx, y } = local(event, keys), pitch = keyPitch(y);
    if (pitch === null) return;
    if (state.mode === 'grid') {
      state.activeRow = pitch;
      if ((event.ctrlKey || event.metaKey) && kx >= NAME_X - 3) { event.preventDefault(); laneMenu(event, pitch); return; }
      if (kx < PAN_X - 13) { toggleMute(pitch); return; }   // the LED
      if (kx < NUM_X - 4) { startKnob(pitch, y, kx < KNOB_X - 13 ? 'pan' : 'velocity'); return; }   // the pan and volume knobs
      if (kx < NAME_X - 3) { state.selected = new Set(rows().filter((r) => r.pitch === pitch).map((r) => r.index)); requestDraw(); return; }   // the number: select the row
    }
    state.drag = { kind: 'key', pitch, id: 0 }; playKey(pitch);
  });
  const keyPitch = (y) => (state.mode === 'grid' ? gridPitchAt(y) : pitchOf(y));
  /** The volume knob of a Grid row: drag up or down to make every step of the row louder or softer (one edit when released). */
  function startKnob(pitch, y, field) { const group = rows().filter((r) => r.pitch === pitch); if (locked || !group.length) return; state.drag = { kind: 'knob', field, pitch, y0: y, base: model, group, pan0: Math.round(group.reduce((sum, r) => sum + (r.pan ?? 0), 0) / group.length) }; }
  function playKey(pitch) { const d = state.drag; if (d.id) audition.release(d.id); d.pitch = pitch; state.litKey = new Set([pitch]); const drum = state.laneDrum.has(pitch) || rows().some((r) => r.pitch === pitch && r.channel === 9);   // a key that has drum notes (the magenta dot) plays the drum kit, the others the instrument
    if (jamVoice) { jamVoice.play(pitch, { delay: 0, drum }); requestDraw(); return; } d.id = trig({ pitch, velocity: 100, length: ppq, ...(drum ? { channel: 9 } : {}) }, { mono: true, heldMs: 4000, startedAt: performance.now() }); requestDraw(); }
  keys.addEventListener('pointermove', (event) => { const d = state.drag; if (d?.kind === 'knob') { const dy = local(event, keys).y - d.y0; if (d.field === 'pan') { const pan = Math.max(-100, Math.min(100, Math.round(d.pan0 - dy * 1.2))); model = adjustRows(d.base, d.group, () => ({ pan })); } else { const f = knobFactor(dy); model = adjustRows(d.base, d.group, (note) => ({ velocity: scaleVelocity(note.velocity, f) })); } requestDraw(); return; } if (d?.kind !== 'key') return; const pitch = keyPitch(local(event, keys).y); if (pitch !== null && pitch !== d.pitch) playKey(pitch); });
  const keyUp = () => { const d = state.drag; if (d?.kind === 'knob') { state.drag = null; if (model !== d.base) commit(model); requestDraw(); return; } if (d?.kind !== 'key') return; if (d.id) audition.release(d.id); jamVoice?.stop(); state.drag = null; state.litKey = new Set(); requestDraw(); };
  keys.addEventListener('pointerup', keyUp); keys.addEventListener('pointercancel', keyUp);
  keys.addEventListener('dblclick', (event) => {   // a double-click on the pan knob puts the row back in the middle
    if (state.mode !== 'grid' || locked) return; const { x: kx, y } = local(event, keys), pitch = gridPitchAt(y);
    if (pitch === null || kx < PAN_X - 13 || kx >= KNOB_X - 13) return;
    const group = rows().filter((r) => r.pitch === pitch && (r.pan ?? 0) !== 0); if (!group.length) return;
    model = adjustRows(model, group, () => ({ pan: 0 })); commit(model); requestDraw();
  });
  keys.addEventListener('wheel', (event) => { event.preventDefault(); if (state.mode === 'grid') state.gridScrollY += event.deltaY; else state.scrollY += event.deltaY; clampScroll(); requestDraw(); }, { passive: false });

  // ---- pointer: velocity strip ----
  velocity.addEventListener('pointerdown', (event) => {
    if (locked || event.button !== 0) return; velocity.setPointerCapture(event.pointerId);
    const { x, y } = local(event, velocity), v = laneFromY(y - 3, VEL_H - 6, LANE_DEFS[state.lane]);
    state.drag = { kind: 'velocity', base: model, ramp: event.shiftKey ? { tick: atOf(x), v } : null };
    setVelocityAt(x, v);
  });
  function setVelocityAt(x, v) {
    const d = state.drag, list = rows(), tol = (6 / state.pxBeat) * ppq, def = LANE_DEFS[state.lane];
    if (d.ramp) { const map = laneRamp(list, d.ramp.tick, d.ramp.v, atOf(x), v, def); if (map.size) model = updateMany(d.base, new Map([...map].map(([index, value]) => [index, { [def.key]: value }]))); }
    else { const near = nearestStart(list, atOf(x), tol); if (near) { model = updateMany(model, new Map([[near.index, { [def.key]: v }]])); state.selected = new Set([near.index]); } }
    requestDraw();
  }
  velocity.addEventListener('pointermove', (event) => { if (state.drag?.kind !== 'velocity') return; const { x, y } = local(event, velocity); setVelocityAt(x, laneFromY(y - 3, VEL_H - 6, LANE_DEFS[state.lane])); });
  velocity.addEventListener('dblclick', (event) => {
    if (locked || state.lane === 'velocity') return;
    const near = nearestStart(rows(), atOf(local(event, velocity).x), (6 / state.pxBeat) * ppq); if (!near) return;
    const next = updateMany(model, new Map([[near.index, { [LANE_DEFS[state.lane].key]: undefined }]])); if (next !== model) { model = next; commit(next); requestDraw(); }
  });
  const velocityUp = () => { const d = state.drag; if (d?.kind !== 'velocity') return; state.drag = null; if (model !== d.base) commit(model); };
  velocity.addEventListener('pointerup', velocityUp); velocity.addEventListener('pointercancel', velocityUp);

  // ---- keyboard: only while the panel has focus, so typing in Properties never reaches it ----
  const typed = new Map();   // key -> voice id, for the typing keyboard
  panel.addEventListener('keydown', (event) => {
    event.stopPropagation(); setAlt(event.altKey);
    const target = event.target, field = target instanceof HTMLInputElement || target instanceof HTMLSelectElement || target instanceof HTMLTextAreaElement;
    if (field) return;
    const key = event.key, mod = event.ctrlKey || event.metaKey;
    if (key === 'ArrowLeft' && event.altKey && !mod && !event.shiftKey && !state.selected.size && backStack.length) { event.preventDefault(); goBack(); return; }
    if (mod && key.toLowerCase() === 't') { event.preventDefault(); setTyping(!state.typing); return; }
    if (mod && key.toLowerCase() === 'z') { event.preventDefault(); if (options.pseudo) stepHistory(!event.shiftKey); else (event.shiftKey ? redo : undo)(); return; }
    if (mod && key.toLowerCase() === 'y') { event.preventDefault(); if (options.pseudo) stepHistory(false); else redo(); return; }
    if (mod && key.toLowerCase() === 'a') { event.preventDefault(); state.selected = new Set(rows().map((n) => n.index)); requestDraw(); return; }
    if (key === ' ') { event.preventDefault(); if (event.repeat) return; if (mod) togglePlay(false); else if (state.player) { stopPlayback(false); state.playhead = state.playOrigin; requestDraw(); } else togglePlay(false); return; }
    if (event.altKey && !mod && /^Numpad[2-6]$/.test(event.code)) { event.preventDefault(); setOctave(Number(event.code.slice(6))); return; }
    if (state.typing && !mod && !event.altKey) {
      if (key === '[' || key === ']') { shiftOctave(key === ']' ? 1 : -1); return; }
      const pitch = typingPitch(key, state.octave);
      if (pitch !== null) { event.preventDefault(); if (!typed.has(event.code)) { typed.set(event.code, trig({ pitch, velocity: 100, length: ppq }, { heldMs: 4000, startedAt: performance.now() })); state.litTyped.set(event.code, pitch); recordOn(pitch, 100); requestDraw(); } return; }
    }
    const lower = key.toLowerCase();
    if (key === 'Escape' && state.stamp) { event.preventDefault(); state.stamp = null; toast('Stopped stamping.'); return; }
    if (key === 'Escape' && !helpCard.hidden) { event.preventDefault(); helpCard.hidden = true; return; }
    if (key === '?' && !mod) { event.preventDefault(); helpCard.hidden = !helpCard.hidden; return; }
    if (mod && (lower === 'c' || lower === 'x')) {
      event.preventDefault(); const picked = rowsBySelection(); if (!picked.length) return;
      if (lower === 'x' && locked) return;
      liteClipboard = copyItems(model, picked);
      try { navigator.clipboard?.writeText(clipboardText(liteClipboard, ppq)).catch(() => {}); } catch { /* no clipboard access: the in-app clipboard still works */ }
      if (lower === 'x') { const next = removeRows(model, picked); state.selected = new Set(); if (next !== model) commit(next); }
      requestDraw(); toast(`${picked.length} note${picked.length === 1 ? '' : 's'} ${lower === 'x' ? 'cut' : 'copied'}`); return;
    }
    if (mod && lower === 'v' && event.shiftKey) {
      event.preventDefault(); if (locked) return;
      Promise.resolve().then(() => navigator.clipboard.readText()).then((text) => {
        const items = itemsFromText(text, ppq);
        if (!items.length) { toast('The clipboard holds no notes (it needs note text such as 1:0:1:60:96).'); return; }
        pasteItems(items);
      }).catch(() => toast('The browser did not allow reading the clipboard.'));
      return;
    }
    if (mod && lower === 'v') { event.preventDefault(); if (!locked && liteClipboard.length) pasteItems(liteClipboard); return; }
    if (mod && lower === 'b') {
      event.preventDefault(); const picked = rowsBySelection(); if (locked || !picked.length) return;
      const next = duplicateRows(model, picked, endTick()); if (next === model) { toast('There is no room to the right in this clip.'); return; }
      const first = Math.min(...picked.map((r) => r.at)), span = Math.max(...picked.map((r) => r.at + r.length)) - first;
      model = next; selectKeys(keysOfRows(picked, span, 0)); commit(next); requestDraw(); return;
    }
    if (mod && lower === 'd') { event.preventDefault(); state.selected = new Set(); requestDraw(); return; }
    if (mod && lower === 'q') {
      event.preventDefault(); if (locked) return;
      const sel = rowsBySelection(), picked = sel.length ? sel : rows(), grid0 = Math.max(1, Math.round((beats() || 0.25) * ppq));
      const next = quantizeRows(model, picked, grid0, endTick()); if (next === model) return;
      model = next; if (sel.length) selectKeys(new Set(sel.map((r) => `${Math.round(r.at / grid0) * grid0}:${r.pitch}`))); commit(next); requestDraw(); return;
    }
    if (event.shiftKey && !mod && !event.altKey && /^Digit[1-5]$/.test(event.code)) {
      event.preventDefault(); const n = Number(event.code.slice(5)), centre = gridW() / 2;
      if (n <= 3) { const factor = zoomPreset(n) / state.pxBeat; applyView(zoomAnchor(viewNow(), { x: centre, y: 0 }, factor, 'time')); }
      else if (n === 4 || !state.selected.size) setTimeView(fitRange(0, endTick(), ppq, gridW()));
      else { const picked = rowsBySelection(); setTimeView(fitRange(Math.min(...picked.map((r) => r.at)), Math.max(...picked.map((r) => r.at + r.length)), ppq, gridW())); }
      return;
    }
    if ((key === 'PageUp' || key === 'PageDown') && !mod) { event.preventDefault(); zoomBy(key === 'PageUp' ? 1.25 : 0.8, gridW() / 2); return; }
    if (key.startsWith('Arrow') && event.altKey && !mod && !event.shiftKey && (key === 'ArrowLeft' || key === 'ArrowRight') && !locked && state.selected.size) {
      event.preventDefault(); const picked = rowsBySelection(), d = (key === 'ArrowRight' ? 1 : -1) * Math.max(1, Math.round(ppq / 48)), shift = clampMove(picked, d, 0, endTick()), next = moveRows(model, picked, shift.dTick, 0, endTick());
      if (next !== model) { model = next; selectKeys(keysOfRows(picked, shift.dTick, 0)); commit(next); requestDraw(); }
      return;
    }
    if ((key === 'Delete') && !locked && state.selected.size) { event.preventDefault(); const next = removeRows(model, rowsBySelection()); state.selected = new Set(); if (next !== model) commit(next); requestDraw(); return; }
    if ((key === 'j' || key === 'J') && !mod && !event.altKey && !event.shiftKey) { event.preventDefault(); joinSelected(); return; }
    if ((key === 'b' || key === 'B') && !mod && !event.altKey && !event.shiftKey && !locked && state.selected.size) { event.preventDefault(); bendSelected(); return; }   // B: the bend of the selected notes   // J: neighbours of the selection become long notes
    if ((key === 's' || key === 'S') && !mod && !event.altKey && !event.shiftKey) { event.preventDefault(); splitRows(state.selected.size ? rowsBySelection() : rows()); return; }   // S: long notes of the selection (or all) become steps
    if ((key === 'k' || key === 'K') && !mod && !event.altKey && !event.shiftKey) { event.preventDefault(); const group = state.selected.size ? rowsBySelection() : rows(); setSelectionKit(!(group.length && group.every((r) => r.channel === 9))); return; }   // K: the selected notes (or all) sound as the drum kit, or back as the instrument
    if (key === 'Backspace') { event.preventDefault(); if (state.snap === 'off') state.snap = state.snapBefore; else { state.snapBefore = state.snap; state.snap = 'off'; } snapSelect.value = state.snap; requestDraw(); return; }
    if (key.startsWith('Arrow') && event.shiftKey && !mod && !event.altKey && !locked && state.selected.size) { event.preventDefault(); moveSelection(key); return; }
    if (key.startsWith('Arrow') && mod && (key === 'ArrowUp' || key === 'ArrowDown') && !locked && state.selected.size) { event.preventDefault(); moveSelection(key, 12); return; }
    if (key.startsWith('Arrow') && !mod && !event.shiftKey && !event.altKey) { event.preventDefault(); pauseFollow(); const vk = state.mode === 'grid' ? 'gridScrollY' : 'scrollY', vh = state.mode === 'grid' ? state.gridRowH : state.rowH; if (key === 'ArrowUp') state[vk] -= vh * 3; if (key === 'ArrowDown') state[vk] += vh * 3; if (key === 'ArrowLeft') state.scrollX -= state.pxBeat; if (key === 'ArrowRight') state.scrollX += state.pxBeat; clampScroll(); requestDraw(); return; }
    if (!state.typing && !mod && !event.altKey) { const tool = toolForKey(key); if (tool) { event.preventDefault(); setTool(tool); } }
  });
  panel.addEventListener('keyup', (event) => {
    setAlt(event.altKey);
    const id = typed.get(event.code); if (id === undefined) return;
    const typedPitch = state.litTyped.get(event.code);
    typed.delete(event.code); state.litTyped.delete(event.code); audition.release(id); if (typedPitch !== undefined) recordOff(typedPitch);
    requestDraw();
  });
  /** Pastes clipboard items at the pointer (over the grid) or the playhead, snapped, inside the clip; they replace a note exactly underneath and end up selected. */
  function pasteItems(items) {
    const tick = pasteTick(state.overGrid && state.pointer ? atOf(state.pointer.x) : null, state.playhead, (t) => snap(t)), first = Math.min(...items.map((i) => i.at)), last = Math.max(...items.map((i) => i.at + i.length));
    const start = Math.max(0, Math.min(tick, Math.max(0, endTick() - (last - first)))), next = pasteRows(model, items, tick, endTick());
    if (next === model) return;
    model = next; selectKeys(new Set(items.map((i) => `${i.at - first + start}:${i.note.pitch}`))); commit(next); requestDraw();
  }
  /** Shift+arrows move the selection by a snap step or a semitone, Ctrl+Up/Down by an octave (decision 32); the new pitch is heard (throttled). One edit each. */
  function moveSelection(key, pitchStep = 1) {
    const picked = rowsBySelection(); if (!picked.length) return;
    const step = Math.max(1, Math.round(beats() * ppq)), dTick = key === 'ArrowLeft' ? -step : key === 'ArrowRight' ? step : 0, dPitch = key === 'ArrowUp' ? pitchStep : key === 'ArrowDown' ? -pitchStep : 0;
    const shift = clampMove(picked, dTick, dPitch, endTick()), next = moveRows(model, picked, shift.dTick, shift.dPitch, endTick());
    if (next === model) return;
    model = next; selectKeys(keysOfRows(picked, shift.dTick, shift.dPitch)); commit(next);
    if (shift.dPitch && reaudition()) hear({ pitch: picked[0].pitch + shift.dPitch, velocity: picked[0].velocity, length: picked[0].length });
    requestDraw();
  }
  panel.addEventListener('keyup', (event) => { if (event.key === ' ' && event.target instanceof HTMLButtonElement) event.preventDefault(); });
  panel.addEventListener('cancel', (event) => event.preventDefault());   // Esc never closes the roll (decision 23)

  // ---- view memory per block (decision 20.4) and the switch to the Advanced roll (decision 3 / 20.11) ----
  let savedKey = '', saveTimer = 0, lastPanel = null;
  const viewKey = () => [state.pxBeat, state.rowH, Math.round(state.scrollX), Math.round(state.scrollY), state.snap, state.loopOn, state.follow, state.bracket ? `${state.bracket.start}-${state.bracket.end}` : '', state.lastLen, state.hintDismissed, state.mode, state.gridRowH, mutedSig()].join('|');
  function markView(force) { if (options.pseudo) return; const k = viewKey(); if (!force && k === savedKey) return; savedKey = k; clearTimeout(saveTimer); saveTimer = setTimeout(saveViewNow, 400); }
  function saveViewNow() {
    clearTimeout(saveTimer); saveTimer = 0; if (options.pseudo) return;
    const r = panel.getBoundingClientRect(); if (r.width > 0) lastPanel = { left: r.left, top: r.top, width: r.width, height: r.height };
    rememberView(blockId, { lite: serializeView({ panel: lastPanel, bracket: state.bracket, loopOn: state.loopOn, follow: state.follow, snap: state.snap, pxBeat: state.pxBeat, rowH: state.rowH, scrollX: state.scrollX, scrollY: state.scrollY, lastLen: state.lastLen, hintDismissed: state.hintDismissed, mode: state.mode, gridRowH: state.gridRowH, muted: [...state.muted], names: Object.fromEntries(state.laneNames) }) });
  }
  // ---- neighbouring clips strip (decision 16): the clips on the same track near this one; a click opens one in place, Alt+Left or Back returns ----
  let stripSignature = null;
  function renderStrip() {
    if (options.pseudo || !strip.isConnected) return;
    const chips = neighbourChips(flat().nodes, flat().links, node.id), sig = chipsSignature(chips) + '|' + backStack.length + '|' + backStack[backStack.length - 1];
    if (sig === stripSignature) return;
    stripSignature = sig;
    const kids = [];
    if (backStack.length) kids.push(el('button', { type: 'button', className: 'mls-chip mls-back', textContent: '◀ Back', title: `Back to ${backStack[backStack.length - 1]} (Alt+Left)`, 'aria-label': `Back to ${backStack[backStack.length - 1]}`, onclick: () => goBack() }));
    if (chips.length > 1) for (const chip of chips) kids.push(el('button', { type: 'button', className: `mls-chip${chip.current ? ' current' : ''}`, textContent: chipLabel(chip), title: chip.current ? 'This clip' : `Open ${chip.title} here`, 'aria-label': chip.current ? `This clip: ${chipLabel(chip)}` : `Open ${chipLabel(chip)}`, ...(chip.current ? { 'aria-current': 'true' } : {}), style: `--hue:${chip.hue}`, 'data-clip': chip.id, onclick: () => { if (!chip.current) goTo(chip.id, false); } }));
    strip.replaceChildren(...kids); strip.hidden = kids.length === 0;
  }
  /** Opens another clip where this panel is (same place and size, through the remembered view of that clip); this panel closes, the clip left goes on the back stack. */
  async function goTo(id, isBack) {
    if (options.pseudo) return;
    flushWheel(); await commit(model); saveViewNow();
    const r = panel.getBoundingClientRect(), placeLike = { left: r.left, top: r.top, width: r.width, height: r.height };
    if (!isBack) backStack = pushBack(backStack, node.id);
    panel.close();
    const opened = await openRoll(id, { placeLike });
    if (!opened && !isBack) backStack = backStack.slice(0, -1);
  }
  function goBack() { const { id, stack } = popBack(backStack); if (!id) return false; backStack = stack; goTo(id, true); return true; }

  // ---- floating: drag by the title ----
  // the position is a compositor transform that stays after the drop (left/top are never rewritten, so nothing is laid out or repainted when the mouse is released)
  title.addEventListener('pointerdown', (event) => {
    if (event.target.closest('button') || panel.dataset.window === 'max' || window.matchMedia('(max-width: 700px), (pointer: coarse) and (max-width: 900px)').matches) return;
    const rect = panel.getBoundingClientRect(), sx = event.clientX, sy = event.clientY, baseX = rect.left - offX, baseY = rect.top - offY;
    title.setPointerCapture(event.pointerId);
    const clampX = (x) => Math.max(0, Math.min(window.innerWidth - 80, x)), clampY = (y) => Math.max(0, Math.min(window.innerHeight - 40, y));
    const move = (e) => { offX = clampX(rect.left + e.clientX - sx) - baseX; offY = clampY(rect.top + e.clientY - sy) - baseY; panel.style.transform = `translate3d(${offX}px, ${offY}px, 0)`; };
    const up = () => {
      title.removeEventListener('pointermove', move); title.removeEventListener('pointerup', up); title.removeEventListener('pointercancel', up);
      markView(true); rememberRect();
    };
    title.addEventListener('pointercancel', up);
    title.addEventListener('pointermove', move); title.addEventListener('pointerup', up);
  });

  const rememberRect = () => { if (panel.dataset.window) return; const r = panel.getBoundingClientRect(); if (r.width > 100 && r.height > 100) sessionRect = { left: r.left, top: r.top, width: r.width, height: r.height }; };
  // resizing the window: the drawing is laid out again in steps, at most eight times a second while the edge is being dragged (and once more when it stops), and nothing is stretched in between:
  // the picture keeps its size and the window clips it, so it does not wobble.
  let layoutTimer = 0, lastLayout = 0;
  const resizeObserver = new ResizeObserver(() => {
    clearTimeout(layoutTimer);
    layoutTimer = setTimeout(() => { lastLayout = performance.now(); layout(); markView(true); rememberRect(); }, Math.max(0, 125 - (performance.now() - lastLayout)));
  });
  resizeObserver.observe(body);
  if (options.track) lanesCtl.setVisible(true);
  panel.addEventListener('close', () => {
    flushWheel(); saveViewNow();
    // closing the window is not Stop: a take that is still being recorded is not added to the clip
    const played = playedCount(rec);
    if (onCloseTake({ recording: rec.on, played }) === 'discard') {
      rec.notes = []; rec.open.clear(); rec.waiting = false; rec.on = false;
      toast(discardedMessage(played));
    }
    stopPlayback(false); unregisterKeys(); stopMidiState(); stopMidiNotes(); review.stop(); vkb.dispose(); audition.dispose(); stopStore(); resizeObserver.disconnect(); clearTimeout(layoutTimer); cancelAnimationFrame(frame); cancelAnimationFrame(tickFrame); clearInterval(metroTimer); cancelAnimationFrame(autoFrame);
    jamVoice?.stop(); rememberRect(); windowChrome.dispose(); openPanels.delete(blockId); panel.remove(); options.onClose?.();
  });
  panel.mlsDebug = { calls, history, freePitches, remapLane, endTone, liveRows: () => liveRows(), model: () => model, state, endTick, rows, previewRevision: () => audition.preview.revision };   // read by the DOM specs only
  setTool('draw');
  const saved = options.pseudo ? {} : restoreView(rememberedView(blockId)?.lite, { endTick: endTick(), snaps: SNAPS.map((x) => x.id) });
  if (options.placeLike && ['left', 'top', 'width', 'height'].every((k) => Number.isFinite(options.placeLike[k]))) { const p = options.placeLike; Object.assign(panel.style, { left: `${Math.max(0, p.left)}px`, top: `${Math.max(0, p.top)}px`, width: `${p.width}px`, height: `${p.height}px` }); }
  else if (sessionRect) {
    const step = document.querySelectorAll('dialog.mls-panel[open]').length > 1 ? 32 : 0, p = sessionRect;   // the last window is where the previous one is: a new one next to it steps aside
    Object.assign(panel.style, { left: `${Math.max(0, Math.min(p.left + step, window.innerWidth - Math.min(p.width, window.innerWidth) - 8))}px`, top: `${Math.max(0, Math.min(p.top + step, window.innerHeight - 80))}px`, width: `${p.width}px`, height: `${p.height}px` });
  }
  else {
    // a new window opens in the middle of the screen; the ones opened after it step down and to the right, like a stack of windows (six steps, then it starts again)
    const others = document.querySelectorAll('dialog.mls-panel[open]').length - 1, step = 32, k = Math.max(0, others) % 6;
    const r = panel.getBoundingClientRect();
    const left = Math.max(0, Math.min((window.innerWidth - r.width) / 2 + k * step, window.innerWidth - r.width - 8)), top = Math.max(0, Math.min((window.innerHeight - r.height) / 2 + k * step, window.innerHeight - r.height - 8));
    Object.assign(panel.style, { left: `${Math.round(left)}px`, top: `${Math.round(top)}px` });
  }
  rememberRect();   // at once, so a window opened right after this one steps aside from it
  if (saved.snap) { state.snap = saved.snap; state.snapBefore = saved.snap === 'off' ? 'auto' : saved.snap; snapSelect.value = saved.snap; }
  if (saved.bracket) state.bracket = saved.bracket;
  if (saved.loopOn) { state.loopOn = true; toggle(loopButton, true); }
  if (saved.follow === false) { state.follow = false; toggle(followButton, false); }
  if (saved.lastLen) state.lastLen = saved.lastLen;
  if (saved.hintDismissed) state.hintDismissed = true;
  renderStrip();
  layout();
  const carried = options.initView && [options.initView.pxBeat, options.initView.rowH, options.initView.scrollX, options.initView.scrollY].every(Number.isFinite) ? options.initView : null;
  if (carried) applyView({ pxBeat: Math.max(LIMITS.minPxBeat, Math.min(LIMITS.maxPxBeat, carried.pxBeat)), rowH: Math.max(LIMITS.minRowH, Math.min(LIMITS.maxRowH, carried.rowH)), scrollX: carried.scrollX, scrollY: carried.scrollY });
  else if (saved.pxBeat !== undefined && saved.rowH !== undefined && saved.scrollX !== undefined && saved.scrollY !== undefined) applyView({ pxBeat: saved.pxBeat, rowH: saved.rowH, scrollX: saved.scrollX, scrollY: saved.scrollY });
  else fitView();
  if (saved.gridRowH) state.gridRowH = saved.gridRowH;
  if (saved.muted) state.muted = new Set(saved.muted);
  if (saved.names) state.laneNames = new Map(Object.entries(saved.names).map(([p, n]) => [Number(p), n]));
  if (saved.mode === 'grid') setMode('grid');
  if (options.initSelection) state.selected = new Set(keysToIndexes(rows(), options.initSelection));
  savedKey = viewKey();
  audition.warm(rows().map((n) => n.pitch), 127 - Math.floor(state.scrollY / state.rowH), 127 - Math.floor((state.scrollY + gridH()) / state.rowH));
  grid.focus();
  // the keyboard dock plays this window while it is the one that was used last (notes, and Record)
  const keysId = 'jam:' + node.id;
  const unregisterKeys = registerReceiver({ id: keysId, kind: 'jam', title: `Jam · ${node.title || node.id}`, alive: () => panel.isConnected, noteOn: (pitch, velocity) => liveOn(pitch, velocity), noteOff: (pitch) => liveOff(pitch), bend: (semitones) => recordBend(semitones), record: () => toggleRecording(), recording: () => rec.on });
  panel.addEventListener('pointerdown', () => activateReceiver(keysId), true);
  panel.addEventListener('focusin', () => activateReceiver(keysId));
  return panel;
}
