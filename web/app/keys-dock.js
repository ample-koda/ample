// The keyboard dock: a touch keyboard, a pitch wheel and a MIDI-in switch that float over the page. It has three sizes (like Koda): a small pill, the keyboard, and a maximized layout with more octaves, the
// scale lock, the sustain and channel settings and the knobs of what is played. It plays the window that was used last (the Jam, the Delay Llama window: see keys-router.js) and, without one, the block
// that is selected. The record button makes a quick take of its own (keys-take.js) that can be saved as a new MIDI clip. Notes from the keyboard and from a MIDI input (a keyboard or a virtual cable) take
// the same road. A window that is open as a modal dialog makes the rest of the page inert, so the dock moves into it while it is open. @ample:virtual-keyboard
import { el, toast } from './ui.js';
import { edit, subscribe, subscribeSelection } from './store.js';
import { createBendStrip, createVirtualKeyboard } from './virtual-keyboard.js';
import { midiActive, midiCan, midiChannel, midiNames, onMidiNote, onMidiState, restoreMidi, setMidiChannel, toggleMidi } from './midi-hub.js';
import { keysTarget, player } from './keys-play.js';
import { createAdhocTake, previewRows, saveTakeAsClip, TAKE_PPQ } from './keys-take.js';
import { createTakeReview } from './jam-take-review.js';
import { tweakLlama } from './llama-live.js';
import { activeReceiver, onReceivers } from './keys-router.js';
import { knob } from './studio/knob.js';
import { ROOTS, SCALES, scaleSet } from '../lib/virtual-keys.mjs';
import { ownSound, OWN_PACK } from './keys-sound.js';
import { attachGmPicker } from './gm-picker.js';
import { openProjectFilePicker } from './filepicker.js';
import { gmProgramName } from '../lib/gm-programs.mjs';

const STORE = 'ample.keys.dock';
const SIZES = { min: { octaves: 2 }, normal: { octaves: 2 }, max: { octaves: 4 } };
const MIN_WIDTH = 412, MIN_KEYS = 80, MAX_KEYS = 300;   // below this the buttons of the head would not fit (on a narrow screen: the width of the screen)
const minWidth = () => Math.min(MIN_WIDTH, window.innerWidth - 8);
const SNAPS = [['1/4', 1], ['1/8', 0.5], ['1/16', 0.25], ['1/32', 0.125], ['Off', 0]];
let dock = null;

const remember = (patch) => { try { localStorage.setItem(STORE, JSON.stringify({ ...recall(), ...patch })); } catch { /* storage can be blocked */ } };
const recall = () => { try { return JSON.parse(localStorage.getItem(STORE) || '{}'); } catch { return {}; } };

/** The knobs of what is played: the voice of a Delay Llama (live, and written into the song when let go). */
function llamaKnobs(node) {
  if (!node) return [];
  const read = (name, fallback) => { const v = Number.parseFloat(String(node.params?.[name] ?? '').replace(/[^\d.-]/g, '')); return Number.isFinite(v) ? v : fallback; };
  const make = (name, label, fallback, min, max, unit, write) => ({
    label, value: read(name, fallback), min, max, unit, size: 54, step: 1,
    onPreview: (next) => tweakLlama(node.id, { [name]: write(Math.round(next)) }),
    onCommit: (next) => { tweakLlama(node.id, { [name]: write(Math.round(next)) }); edit([{ type: 'setParameter', block: node.id, name, value: write(Math.round(next)) }]).catch(() => {}); },
  });
  return [
    make('voice', 'Voice', 50, 0, 100, '%', (v) => String(v)), make('glide', 'Glide', 60, 0, 600, 'ms', (v) => `${v} ms`), make('vibrato', 'Vibrato', 30, 0, 100, '%', (v) => `${v}%`),
    make('breath', 'Breath', 50, 0, 100, '%', (v) => `${v}%`), make('echo', 'Echo', 0, 0, 100, '%', (v) => `${v}%`), make('gain', 'Level', -6, -36, 6, 'dB', (v) => `${v} dB`),
  ];
}

const choice = (label, title, options, value, onchange) => {
  const select = el('select', { className: 'vkb-mode', title, 'aria-label': label }, options.map(([text, v]) => el('option', { value: String(v), textContent: text, selected: String(v) === String(value) })));
  select.onchange = () => onchange(select.value);
  return el('label', { className: 'keys-choice' }, [el('span', { textContent: label }), select]);
};

function build() {
  const saved = recall();
  const state = { size: ['min', 'normal', 'max'].includes(saved.size) ? saved.size : 'normal', root: Number(saved.root) || 0, scale: saved.scale || 'off', lock: Boolean(saved.lock), snap: saved.snap ?? '0.25', click: saved.click !== false };
  player.settings.bendRange = Number(saved.bendRange) || 2;
  player.settings.plays = saved.plays === 'own' ? 'own' : 'auto';
  let lastTaken = 0;
  const label = el('output', { className: 'keys-target', 'aria-live': 'polite' });
  let keyboard = null;   // (the keyboard reports its keys while it is being made, before the name is set)
  keyboard = createVirtualKeyboard({ onNoteOn: (pitch, velocity) => player.on(pitch, velocity), onNoteOff: (pitch) => player.off(pitch), onTakenOver: ({ fingers }) => { const now = Date.now(); if (now - lastTaken > 4000) { lastTaken = now; toast(`The system took over a touch (${fingers} finger${fingers === 1 ? '' : 's'} were down). Windows gestures and edge swipes do that; the keys let go so nothing hangs.`); } }, onRange: ({ first, last }) => { if (keyboard?.custom) remember({ range: { first, last } }); else if (keyboard && saved.range) remember({ range: null }); const to = Math.min(last, first + 36); if (player.usesOwnSound()) ownSound.warm(first, to); else player.warm(first, to); }, octave: 3, octaves: SIZES[state.size].octaves, compact: true });
  if (saved.range && Number.isFinite(saved.range.first) && Number.isFinite(saved.range.last)) keyboard.setRange(saved.range.first, saved.range.last, true);   // the keys that were chosen with the scrollbar
  const bend = createBendStrip({ onBend: (value) => player.bend(value) });
  // the pitch wheel sits beside the keys
  const keysEl = keyboard.element.querySelector('.vkb-keys'), row = el('div', { className: 'vkb-row' });
  keysEl.replaceWith(row); row.append(bend.element, keysEl);

  const midi = midiCan() ? el('button', { type: 'button', className: 'vkb-btn keys-midi', textContent: 'MIDI in', title: 'Play with a MIDI keyboard or a virtual MIDI cable', 'aria-pressed': 'false' }) : null;
  const pill = el('button', { type: 'button', className: 'keys-pill', title: 'Open the keyboard', 'aria-label': 'Open the keyboard' }, [el('span', { textContent: '🎹' }), el('span', { className: 'keys-pill-label' }), el('i', { className: 'keys-pill-midi', title: 'MIDI in is on' })]);
  const shrink = el('button', { type: 'button', className: 'vkb-btn', textContent: '–', title: 'Minimize the keyboard', 'aria-label': 'Minimize the keyboard' });
  const grow = el('button', { type: 'button', className: 'vkb-btn', textContent: '⤢', title: 'Bigger keyboard with the scale lock, the sustain and channel settings and the knobs of what is played', 'aria-label': 'Maximize the keyboard' });
  const close = el('button', { type: 'button', className: 'vkb-btn', textContent: '×', title: 'Close the keyboard', 'aria-label': 'Close the keyboard' });
  const sound = el('button', { type: 'button', className: 'vkb-btn keys-sound', textContent: '♪ Sound', title: 'The own sound of the keyboard: a piano by default, or another instrument or SoundFont of your choice', 'aria-expanded': 'false' });
  const record = el('button', { type: 'button', className: 'vkb-btn keys-record', textContent: '●', title: 'Record what you play: the click keeps going until your first note, then ● again to finish. With the Jam open it records into the Jam.', 'aria-label': 'Record', 'aria-pressed': 'false' });
  const hold = el('button', { type: 'button', className: 'vkb-btn keys-hold', textContent: 'Hold', title: 'Keep the notes sounding after you let go of the keys', 'aria-pressed': 'false' });
  const pedal = el('span', { className: 'keys-pedal', title: 'The sustain pedal of a MIDI keyboard: lit while it is down' }, [el('i'), ' Sustain']);
  const transposeLabel = el('output', { className: 'keys-transpose', textContent: '0 st' });
  const down = el('button', { type: 'button', className: 'vkb-btn', textContent: '−', title: 'Transpose down a semitone', 'aria-label': 'Transpose down' });
  const up = el('button', { type: 'button', className: 'vkb-btn', textContent: '+', title: 'Transpose up a semitone', 'aria-label': 'Transpose up' });
  const lock = el('button', { type: 'button', className: 'vkb-btn keys-lock', textContent: 'Lock', title: 'Scale lock: a key outside the scale plays the nearest note of the scale', 'aria-pressed': String(state.lock) });
  const knobs = el('div', { className: 'keys-knobs' });
  const velocityKnob = knob({ label: 'Velocity', value: 100, min: 1, max: 127, unit: '', size: 54, step: 1, onPreview: (v) => keyboard.setFixedVelocity(v), onCommit: (v) => keyboard.setFixedVelocity(v) });

  // the take of the record button
  const take = createAdhocTake({ grid: () => Number(state.snap), click: () => state.click, onChange: () => paintRecord() });
  const review = createTakeReview({
    ppq: TAKE_PPQ, clipBars: () => 99999, preview: previewRows, keepLabel: 'Save as clip', keepTitle: 'Write the chosen bars into a new MIDI clip of the song', showExtend: false,
    keep: async ({ rows, bars }) => { const name = await saveTakeAsClip(rows, bars); toast(name ? `Saved as the clip ${name}.` : 'The clip could not be saved.'); },
  });

  const clickBox = el('input', { type: 'checkbox', checked: state.click, 'aria-label': 'Click while recording' });
  clickBox.onchange = () => { state.click = clickBox.checked; remember({ click: clickBox.checked }); };
  const settingsRow = el('div', { className: 'keys-extra' }, [
    el('div', { className: 'keys-tools' }, [hold, pedal, el('span', { className: 'keys-transposer' }, [down, transposeLabel, up])]),
    el('div', { className: 'keys-tools' }, [
      choice('Scale', 'Mark the notes of a scale on the keys', [['Off', 'off'], ...Object.keys(SCALES).map((name) => [name, name])], state.scale, (v) => { state.scale = v; remember({ scale: v }); applyScale(); }),
      choice('Root', 'The root of the scale', ROOTS.map((name, i) => [name, i]), state.root, (v) => { state.root = Number(v); remember({ root: state.root }); applyScale(); }), lock,
    ]),
    el('div', { className: 'keys-tools' }, [
      choice('MIDI channel', 'Listen to one channel of the MIDI input, or all', [['All', 0], ...Array.from({ length: 16 }, (_, i) => [String(i + 1), i + 1])], midiChannel(), (v) => setMidiChannel(Number(v))),
      choice('Wheel', 'How far the pitch wheel bends, in semitones', [['±2', 2], ['±12', 12]], player.settings.bendRange, (v) => { player.settings.bendRange = Number(v); remember({ bendRange: Number(v) }); }),
      choice('Quantize', 'The grid a take is put on when it is saved', SNAPS.map(([text, beats]) => [text, beats]), state.snap, (v) => { state.snap = v; remember({ snap: v }); }),
      el('label', { className: 'keys-choice', title: 'A click while you record' }, [el('span', { textContent: 'Click' }), clickBox]),
    ]),
    el('label', { className: 'keys-velocity', title: 'The velocity that "Fixed 100" gives' }, [el('span', { textContent: 'Fixed velocity' }), velocityKnob]),
    knobs,
  ]);
  const grip = el('div', { className: 'keys-grip', title: 'Drag to resize: wider shows more octaves, taller makes the keys taller. Double-click to go back to the standard size.', role: 'separator', 'aria-label': 'Resize the keyboard' });
  const root = el('section', { id: 'keys-dock', className: 'keys-dock', 'data-size': state.size, 'aria-label': 'Keyboard' }, [
    el('div', { className: 'keys-head' }, [label, sound, record, ...(midi ? [midi] : []), shrink, grow, close]), review.element, keyboard.element, settingsRow, pill, grip,
  ]);

  // the sound menu: which instrument and which SoundFont the dock plays itself, and whether it does that always or only when there is nothing else to play
  let menu = null, stopHear = null, stopProgress = null;
  const closeMenu = () => { if (!menu) return; stopHear?.(); stopHear = null; stopProgress?.(); stopProgress = null; menu.remove(); menu = null; sound.setAttribute('aria-expanded', 'false'); document.removeEventListener('pointerdown', outsideMenu, true); };
  const outsideMenu = (event) => { if (menu && !menu.contains(event.target) && !event.target.closest?.('.gm-picker') && event.target !== sound) closeMenu(); };
  const warmNow = () => { const range = keyboard.range; ownSound.warm(range.first, range.last); };
  const openMenu = () => {
    const state = ownSound.state, status = el('output', { className: 'keys-sound-status' });
    const input = el('input', { type: 'text', className: 'keys-sound-program', value: gmProgramName(state.program), 'aria-label': 'Instrument', placeholder: 'Type or pick an instrument', title: 'A General MIDI instrument: hover a row to hear it' });
    const font = el('output', { className: 'keys-sound-font' });
    const paintFont = () => { font.textContent = ownSound.isPack() ? 'General MIDI pack' : ownSound.state.file; };
    const plays = choice('Plays', 'Auto: the open window or the selected block when there is one, else the own sound. Own sound: always this sound.', [['Auto: window or block, else own sound', 'auto'], ['Always the own sound', 'own']], player.settings.plays, (v) => { player.settings.plays = v; remember({ plays: v }); paintTarget(); });
    const pack = el('button', { type: 'button', className: 'vkb-btn', textContent: 'General MIDI pack', title: 'The SoundFont that comes with AMPLE', onclick: () => { ownSound.set({ file: OWN_PACK }); paintFont(); paintTarget(); warmNow(); } });
    const choose = el('button', { type: 'button', className: 'vkb-btn', textContent: 'Choose a .sf2…', title: 'A SoundFont file from the project: its notes are heard through the same General MIDI instruments', onclick: async () => { const picked = await openProjectFilePicker(ownSound.isPack() ? '' : ownSound.state.file, ['.sf2'], 'file'); if (picked) { ownSound.set({ file: picked }); paintFont(); paintTarget(); warmNow(); } } });
    const reset = el('button', { type: 'button', className: 'vkb-btn', textContent: 'Piano', title: 'Back to the acoustic grand piano of the General MIDI pack', onclick: () => { ownSound.set({ file: OWN_PACK, program: 0, bank: 0 }); input.value = gmProgramName(0); paintFont(); paintTarget(); warmNow(); } });
    menu = el('div', { className: 'keys-sound-pop', role: 'dialog', 'aria-label': 'Sound of the keyboard' }, [
      el('strong', { textContent: 'Own sound of the keyboard' }), plays,
      el('label', { className: 'keys-choice' }, [el('span', { textContent: 'Instrument' }), input]),
      el('div', { className: 'keys-tools' }, [el('span', { className: 'keys-choice' }, [el('span', { textContent: 'SoundFont' }), font]), pack, choose, reset]), status,
    ]);
    root.append(menu); paintFont();
    attachGmPicker(input, {
      hear: async (program) => { stopHear?.(); try { stopHear = await ownSound.hear(program); } catch (error) { status.textContent = error.message || String(error); } },
      stopHear: () => { stopHear?.(); stopHear = null; },
      pick: (program) => { ownSound.set({ program }); input.value = gmProgramName(program); paintTarget(); warmNow(); },
    });
    stopProgress = ownSound.onProgress((p) => { status.textContent = p.total ? `Rendering the notes of the keys… ${p.done}/${p.total}` : ''; });
    sound.setAttribute('aria-expanded', 'true');
    document.addEventListener('pointerdown', outsideMenu, true);
  };
  sound.onclick = () => (menu ? closeMenu() : openMenu());
  const applyScale = () => {
    const set = scaleSet(state.root, state.scale);
    keyboard.setScale(set, state.root);
    player.setScale(set, state.lock);
    lock.setAttribute('aria-pressed', String(state.lock && Boolean(set))); lock.classList.toggle('on', state.lock && Boolean(set));
  };
  const destination = () => { const receiver = activeReceiver(); return receiver ? { receiver, title: receiver.title } : { target: keysTarget() }; };
  function paintRecord() {
    const jam = activeReceiver()?.recording?.(), mode = take.active ? (take.waiting ? 'waiting' : 'recording') : jam ? 'recording' : '';
    record.dataset.state = mode; record.classList.toggle('on', Boolean(mode)); record.setAttribute('aria-pressed', String(Boolean(mode)));
  }
  const paintTarget = () => {
    const dest = destination(), own = player.usesOwnSound(), title = own ? ownSound.name() : dest.receiver ? dest.title : `${dest.target.title}${dest.target.kind === 'llama' ? ' (voice)' : ''}`;
    label.textContent = own ? `Playing: ${title} (own sound)` : `Playing: ${title}`;
    root.classList.remove('idle');
    pill.querySelector('.keys-pill-label').textContent = title;
    const llama = dest.receiver?.kind === 'llama' ? dest.receiver.node : dest.target?.kind === 'llama' ? dest.target.node : null;
    knobs.replaceChildren(...llamaKnobs(llama).map((options) => el('div', { className: 'studio-panel-control llama-dial' }, [el('span', { textContent: options.label }), knob(options)])));
    paintRecord();
  };
  // dragging: take the head (or the pill) and move the dock anywhere; the place is remembered, a double click on the head puts it back
  let pos = saved.pos && Number.isFinite(saved.pos.x) && Number.isFinite(saved.pos.y) ? { x: saved.pos.x, y: saved.pos.y } : null;
  const apply = () => {
    if (!pos) { for (const key of ['left', 'top', 'right', 'bottom', 'transform']) root.style[key] = ''; return; }
    const box = root.getBoundingClientRect(), width = Math.min(box.width || 300, window.innerWidth), height = Math.min(box.height || 100, window.innerHeight);
    pos = { x: Math.max(0, Math.min(window.innerWidth - width, pos.x)), y: Math.max(0, Math.min(window.innerHeight - height, pos.y)) };
    Object.assign(root.style, { left: pos.x + 'px', top: pos.y + 'px', right: 'auto', bottom: 'auto', transform: 'none' });
  };
  const grab = (handle, { skip = 'button, select, input, label, a' } = {}) => {
    handle.style.touchAction = 'none';
    handle.addEventListener('pointerdown', (event) => {
      if (event.button > 0 || (skip && event.target.closest?.(skip) && event.target.closest(skip) !== handle)) return;
      const box = root.getBoundingClientRect(), dx = event.clientX - box.left, dy = event.clientY - box.top;
      let moved = false;
      const move = (e) => {
        if (e.pointerId !== event.pointerId || pinch) return;   // another finger (a key being played, a pinch) is not this drag
        if (!moved && Math.hypot(e.clientX - event.clientX, e.clientY - event.clientY) < 5) return;
        moved = true; handle.dataset.dragged = '1';
        pos = { x: e.clientX - dx, y: e.clientY - dy }; apply();
      };
      const end = (e) => { if (e && e.pointerId !== event.pointerId) return; window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', end); window.removeEventListener('pointercancel', end); if (moved) { remember({ pos }); setTimeout(() => { delete handle.dataset.dragged; }, 0); } };
      window.addEventListener('pointermove', move); window.addEventListener('pointerup', end); window.addEventListener('pointercancel', end);
    });
  };
  // resizing: the corner makes the dock wider (more octaves fit, the keys keep their width) and taller (taller keys); the size is remembered per size of the dock (keyboard and maximized)
  const dims = { ...(saved.dims || {}) };
  const octavesFor = (width) => Math.max(1, Math.min(6, Math.round(Math.floor((width - 24 - 36 - 8) / 40) / 7)));
  const fit = () => {
    const custom = dims[state.size];
    if (state.size === 'min' || !custom) { root.style.width = ''; root.style.removeProperty('--keys-h'); keyboard.setOctaves(SIZES[state.size].octaves); root.classList.remove('narrow'); return; }
    const width = Math.max(minWidth(), Math.min(custom.w, window.innerWidth - 8)), height = Math.max(MIN_KEYS, Math.min(MAX_KEYS, custom.h));
    root.style.width = width + 'px'; root.style.setProperty('--keys-h', height + 'px');
    root.classList.toggle('narrow', width < 540);
    const octaves = octavesFor(width);
    if (keyboard.octaves !== octaves) keyboard.setOctaves(octaves);
  };
  grip.style.touchAction = 'none';
  grip.addEventListener('pointerdown', (event) => {
    if (event.button > 0 || state.size === 'min') return;
    event.preventDefault(); event.stopPropagation();
    const box = root.getBoundingClientRect(), keys = root.querySelector('.vkb-keys').getBoundingClientRect();
    if (!pos) { pos = { x: box.left, y: box.top }; apply(); }   // from the corner it grows from where it is
    const startW = box.width, startH = keys.height, x0 = event.clientX, y0 = event.clientY;
    const move = (e) => {
      if (e.pointerId !== event.pointerId) return;
      dims[state.size] = { w: Math.round(Math.max(minWidth(), Math.min(window.innerWidth - 8, startW + e.clientX - x0))), h: Math.round(Math.max(MIN_KEYS, Math.min(MAX_KEYS, startH + e.clientY - y0))) };
      fit(); apply();
    };
    const end = (e) => { if (e && e.pointerId !== event.pointerId) return; window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', end); window.removeEventListener('pointercancel', end); remember({ dims, pos }); };
    window.addEventListener('pointermove', move); window.addEventListener('pointerup', end); window.addEventListener('pointercancel', end);
  });
  // two fingers on the dock itself (its head, its padding, the settings: not on the keys, which play chords, nor on the wheel or a control) pinch it bigger or smaller, both ways at once
  const fingers = new Map();
  let pinch = null;
  const touchOk = (target) => !target.closest?.('.vkb-keys, .vkb-bend, button, select, input, label, .keys-grip, .keys-sound-pop, .mls-review-strip');
  root.style.touchAction = 'none'; root.style.userSelect = 'none'; root.style.webkitUserSelect = 'none'; root.style.webkitTouchCallout = 'none';
  root.addEventListener('contextmenu', (event) => event.preventDefault());
  // two fingers that do not play (a pinch) or any gesture the page takes over must not leave a key down
  const letGoOfKeys = () => keyboard.releaseAll();
  window.addEventListener('blur', letGoOfKeys); document.addEventListener('visibilitychange', letGoOfKeys);
  root.addEventListener('pointerdown', (event) => {
    if (event.pointerType !== 'touch' || state.size === 'min' || !touchOk(event.target)) return;
    fingers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (fingers.size === 2) {
      const [a, b] = [...fingers.values()], box = root.getBoundingClientRect(), keys = root.querySelector('.vkb-keys').getBoundingClientRect();
      if (!pos) { pos = { x: box.left, y: box.top }; apply(); }
      pinch = { distance: Math.max(20, Math.hypot(a.x - b.x, a.y - b.y)), width: box.width, height: keys.height };
    }
  });
  root.addEventListener('pointermove', (event) => {
    if (!fingers.has(event.pointerId)) return;
    fingers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (!pinch || fingers.size < 2) return;
    const [a, b] = [...fingers.values()], factor = Math.hypot(a.x - b.x, a.y - b.y) / pinch.distance;
    dims[state.size] = { w: Math.round(Math.max(minWidth(), Math.min(window.innerWidth - 8, pinch.width * factor))), h: Math.round(Math.max(MIN_KEYS, Math.min(MAX_KEYS, pinch.height * factor))) };
    fit(); apply();
  });
  const lift = (event) => { if (!fingers.delete(event.pointerId)) return; if (pinch && fingers.size < 2) { pinch = null; remember({ dims, pos }); } };
  root.addEventListener('pointerup', lift); root.addEventListener('pointercancel', lift);
  grip.addEventListener('dblclick', () => { delete dims[state.size]; remember({ dims, range: null }); keyboard.resetSpan(); fit(); apply(); });
  const setSize = (size) => { state.size = size; root.dataset.size = size; remember({ size }); fit(); place(); apply(); };
  // a window that is open as a modal dialog makes everything outside it inert: the dock goes into it, and back to the page when it is gone
  const place = () => {
    const modal = [...document.querySelectorAll('dialog[open]')].reverse().find((dialog) => dialog.matches(':modal'));
    const home = modal ?? document.body;
    if (root.parentElement !== home) { keyboard.releaseAll(); home.append(root); }
    root.classList.toggle('in-dialog', Boolean(modal));
  };
  const watch = new MutationObserver(place);
  watch.observe(document.body, { subtree: true, attributes: true, attributeFilter: ['open'], childList: true });
  // MIDI in is one switch for the whole app (midi-hub.js): this button shows it and flips it
  const paintMidi = (on, names) => { if (!midi) return; midi.setAttribute('aria-pressed', String(on)); midi.classList.toggle('on', on); root.classList.toggle('midi-on', on); midi.title = on ? (names.length ? `MIDI in: ${names.join(', ')}` : 'MIDI in is on, but no input was found yet: plug one in or start a virtual cable') : 'Play with a MIDI keyboard or a virtual MIDI cable'; };
  if (midi) midi.onclick = () => toggleMidi().then(() => { if (midiActive() && !midiNames().length) toast('No MIDI input found yet. Plug a keyboard in or start a virtual MIDI cable: it is picked up at once.'); }).catch((error) => toast(error.message || String(error)));
  hold.onclick = () => { const on = hold.getAttribute('aria-pressed') !== 'true'; hold.setAttribute('aria-pressed', String(on)); hold.classList.toggle('on', on); player.setHold(on); };
  lock.onclick = () => { state.lock = !state.lock; remember({ lock: state.lock }); applyScale(); };
  const transpose = (by) => { player.settings.transpose = Math.max(-24, Math.min(24, player.settings.transpose + by)); transposeLabel.textContent = `${player.settings.transpose > 0 ? '+' : ''}${player.settings.transpose} st`; };
  down.onclick = () => transpose(-1); up.onclick = () => transpose(1);
  record.onclick = () => {
    const receiver = activeReceiver();
    if (receiver?.record) { receiver.record(); setTimeout(paintRecord, 50); return; }   // the Jam that is open records its own take
    if (!take.active) { review.hide(); take.arm(); toast('Recording: the click keeps going until you play a note, and that note is beat one. Press ● again to finish.'); }
    else if (take.waiting) { take.cancel(); toast('Recording cancelled.'); }
    else { const rows = take.finish(); if (rows) { review.show(rows); toast('The take is ready: choose the bars to keep, then save it as a clip.'); } else toast('Nothing was played.'); }
  };
  shrink.onclick = () => setSize('min');
  pill.onclick = () => { if (pill.dataset.dragged) return; setSize('normal'); };   // a drag of the pill is not a click
  const head = root.querySelector('.keys-head');
  grab(head); grab(pill, { skip: '' });
  head.addEventListener('dblclick', (event) => { if (event.target.closest('button, select, input, label')) return; pos = null; remember({ pos: null }); apply(); });
  window.addEventListener('resize', () => { fit(); apply(); });
  grow.onclick = () => setSize(state.size === 'max' ? 'normal' : 'max');
  close.onclick = () => closeKeysDock();
  // keys that a MIDI note plays light up here too, the wheel and the pedal follow a MIDI keyboard
  const stops = [subscribe(paintTarget), subscribeSelection(paintTarget), onReceivers(paintTarget), onMidiState(paintMidi), onMidiNote((type, pitch, value) => {
    if (type === 'on' || type === 'off') keyboard.lit(pitch, type === 'on');
    else if (type === 'sustain') pedal.classList.toggle('on', value >= 64);
    else if (type === 'bend') bend.set(value);
  })];
  applyScale(); paintTarget(); paintMidi(midiActive(), midiNames()); place(); fit(); apply();
  restoreMidi();   // MIDI in stays on for the next time
  return { root, dispose() { window.removeEventListener('blur', letGoOfKeys); document.removeEventListener('visibilitychange', letGoOfKeys); closeMenu(); take.dispose(); review.stop(); watch.disconnect(); stops.forEach((stop) => stop?.()); keyboard.dispose(); player.allOff(); root.remove(); }, setSize };
}

export function keysDockOpen() { return Boolean(dock); }
export function openKeysDock() { if (!dock) dock = build(); document.querySelector('#btn-keys')?.setAttribute('aria-pressed', 'true'); }
export function closeKeysDock() { dock?.dispose(); dock = null; document.querySelector('#btn-keys')?.setAttribute('aria-pressed', 'false'); }
export function toggleKeysDock() { if (dock) closeKeysDock(); else openKeysDock(); }

/** Wires the button of the top bar. */
export function initKeysDock() {
  const button = document.querySelector('#btn-keys');
  if (button) button.onclick = toggleKeysDock;
}
