// The touch keyboard: a piano on the screen for a touch screen (or a mouse). Several fingers at once (chords), a finger that slides over the keys plays the keys it passes (a glissando), the height of the
// touch on a key sets the velocity (low is loud), and the octave buttons move the range. It only reports notes: onNoteOn(pitch, velocity) and onNoteOff(pitch); where they sound is up to the window that
// uses it (the Jam, the Delay Llama window, the keyboard dock). @ample:virtual-keyboard
import { el } from './ui.js';
import { isBlack, keyLayout, moveRange, pitchName, rangeFor, snapRange, velocityAt, zoomRange } from '../lib/virtual-keys.mjs';

/**
 * @param {{ onNoteOn: (pitch: number, velocity: number) => void, onNoteOff: (pitch: number) => void, octave?: number, octaves?: number, compact?: boolean }} options
 * @returns {{ element: HTMLElement, lit: (pitch: number, on: boolean) => void, setOctave: (octave: number) => void, releaseAll: () => void, dispose: () => void }}
 */
export function createVirtualKeyboard({ onNoteOn, onNoteOff, onRange = () => {}, onTakenOver = () => {}, octave = 3, octaves = 2, compact = false }) {
  const start = rangeFor(octave, octaves);
  const state = { first: start.first, last: start.last, custom: false, velocity: 'touch', fixed: 100, scale: null, root: 0 };
  const held = new Map();      // pointer id -> pitch it is holding
  const lit = new Map();       // pitch -> how many things light it (fingers, MIDI, the song)
  const surface = el('div', { className: 'vkb-keys', role: 'group', 'aria-label': 'Touch keyboard' });
  const label = el('output', { className: 'vkb-octave', 'aria-live': 'polite' });
  const down = el('button', { type: 'button', className: 'vkb-btn', textContent: '◀', title: 'Octave down', 'aria-label': 'Keyboard octave down' });
  const up = el('button', { type: 'button', className: 'vkb-btn', textContent: '▶', title: 'Octave up', 'aria-label': 'Keyboard octave up' });
  const fewer = el('button', { type: 'button', className: 'vkb-btn vkb-fewer', textContent: '−', title: 'Fewer keys: bigger keys', 'aria-label': 'Fewer keys' });
  const more = el('button', { type: 'button', className: 'vkb-btn vkb-more', textContent: '+', title: 'More keys: smaller keys', 'aria-label': 'More keys' });
  const mode = el('select', { className: 'vkb-mode', title: 'Velocity: how hard a key sounds', 'aria-label': 'Keyboard velocity' }, [
    el('option', { value: 'touch', textContent: 'Touch height' }), el('option', { value: 'fixed', textContent: 'Fixed 100' }),
  ]);
  // a thin scrollbar under the keys: the track is the whole range of notes, the handle is what is on the screen. Drag the handle to scroll, drag an end of it for fewer or more keys, click the track to jump there
  const handle = el('div', { className: 'vkb-scroll-handle', title: 'Drag to scroll the keys; drag an end for fewer or more keys' }, [el('i', { className: 'vkb-scroll-grip l', title: 'Fewer or more keys' }), el('i', { className: 'vkb-scroll-grip r', title: 'Fewer or more keys' })]);
  const track = el('div', { className: 'vkb-scroll', role: 'scrollbar', 'aria-orientation': 'horizontal', 'aria-label': 'Which keys are shown', 'aria-controls': 'vkb-keys' }, [handle]);
  for (let c = 12; c < 128; c += 12) track.append(el('b', { className: 'vkb-scroll-tick', style: `left:${(c / 128) * 100}%` }));
  const root = el('div', { className: 'vkb' + (compact ? ' compact' : ''), 'data-octave': String(Math.floor(state.first / 12) - 1) }, [
    el('div', { className: 'vkb-bar' }, [down, label, up, fewer, more, mode]), surface, track,
  ]);

  const keyElement = (pitch) => surface.querySelector(`[data-pitch="${pitch}"]`);
  const paint = (pitch) => keyElement(pitch)?.classList.toggle('on', (lit.get(pitch) || 0) > 0);
  const light = (pitch, on) => { lit.set(pitch, Math.max(0, (lit.get(pitch) || 0) + (on ? 1 : -1))); paint(pitch); };

  function build() {
    const { first, last } = state, { keys, whites } = keyLayout(first, last);
    surface.replaceChildren();
    surface.style.setProperty('--whites', String(whites));
    const width = 100 / whites;
    for (const key of keys.filter((k) => !k.black)) surface.append(el('div', { className: 'vkb-key white', 'data-pitch': String(key.pitch), title: pitchName(key.pitch), style: `left:${key.white * width}%;width:${width}%`, 'aria-label': pitchName(key.pitch) }, [key.pitch % 12 === 0 ? el('span', { className: 'vkb-name', textContent: pitchName(key.pitch) }) : '']));
    for (const key of keys.filter((k) => k.black)) surface.append(el('div', { className: 'vkb-key black', 'data-pitch': String(key.pitch), title: pitchName(key.pitch), style: `left:${(key.white + 1) * width - width * 0.3}%;width:${width * 0.6}%`, 'aria-label': pitchName(key.pitch) }));
    label.textContent = `${pitchName(first)} – ${pitchName(last)}`;
    root.dataset.octave = String(Math.floor(first / 12) - 1);
    handle.style.left = `${(first / 128) * 100}%`; handle.style.width = `${((last - first + 1) / 128) * 100}%`;
    track.setAttribute('aria-valuenow', String(first)); track.setAttribute('aria-valuemin', '0'); track.setAttribute('aria-valuemax', '127');
    for (const pitch of lit.keys()) paint(pitch);
    paintScale();
    onRange({ first, last });   // the keys that are on the screen now
  }
  // the scale lock shows on the keys: the notes outside the scale are dimmed and the root is marked
  function paintScale() {
    for (const key of surface.querySelectorAll('.vkb-key')) {
      const pc = ((Number(key.dataset.pitch) % 12) + 12) % 12;
      key.classList.toggle('scale-out', Boolean(state.scale) && !state.scale.has(pc));
      key.classList.toggle('scale-root', Boolean(state.scale) && pc === state.root);
    }
  }

  // which key is under a point: a black key wins over the white key under it
  const keyAt = (x, y) => {
    const node = document.elementFromPoint(x, y)?.closest?.('.vkb-key');
    if (!node || !surface.contains(node)) return null;
    const rect = node.getBoundingClientRect();
    return { pitch: Number(node.dataset.pitch), fraction: (y - rect.top) / Math.max(1, rect.height) };
  };
  const press = (id, pitch, fraction) => {
    held.set(id, pitch); light(pitch, true);
    onNoteOn(pitch, state.velocity === 'fixed' ? state.fixed : velocityAt(fraction, state.velocity));
  };
  const release = (id) => {
    const pitch = held.get(id);
    if (pitch === undefined) return;
    held.delete(id); light(pitch, false);
    // another finger may still hold the same key
    if (![...held.values()].includes(pitch)) onNoteOff(pitch);
  };
  surface.addEventListener('pointerdown', (event) => {
    const hit = keyAt(event.clientX, event.clientY);
    if (!hit) return;
    event.preventDefault();
    surface.setPointerCapture(event.pointerId);
    press(event.pointerId, hit.pitch, hit.fraction);
  });
  surface.addEventListener('pointermove', (event) => {
    if (!held.has(event.pointerId)) return;
    const hit = keyAt(event.clientX, event.clientY);
    if (!hit || hit.pitch === held.get(event.pointerId)) return;
    release(event.pointerId);                       // a glissando: the key that is left stops, the key that is reached starts
    press(event.pointerId, hit.pitch, hit.fraction);
  });
  for (const type of ['pointerup', 'pointercancel', 'lostpointercapture']) surface.addEventListener(type, (event) => release(event.pointerId));
  // a touch that the system or the browser takes away (a gesture of Windows, an edge swipe) ends with a pointercancel instead of a pointerup: it is reported, so it can be seen what takes the touches
  surface.addEventListener('pointercancel', (event) => { if (event.pointerType === 'touch') onTakenOver({ fingers: held.size + 1 }); });
  surface.addEventListener('contextmenu', (event) => event.preventDefault());
  // the browser must not take a touch on the keys for a gesture of its own (zoom, pan, back, a menu): the touch events are not passive here, so they can be cancelled
  for (const type of ['touchstart', 'touchmove']) surface.addEventListener(type, (event) => event.preventDefault(), { passive: false });

  const setRange = (first, last, custom = state.custom) => { const next = snapRange(first, last); if (next.first === state.first && next.last === state.last && custom === state.custom) return; releaseAll(); state.first = next.first; state.last = next.last; state.custom = custom; build(); };
  const setOctave = (next) => { const span = state.last - state.first, first = Math.max(0, Math.min(127 - span, (Math.max(-1, Math.min(8, next)) + 1) * 12)); setRange(first, first + span); };
  // the scrollbar: the handle moves (same number of keys), or one of its ends moves (fewer or more keys)
  track.addEventListener('pointerdown', (event) => {
    if (event.button > 0) return;
    event.preventDefault(); event.stopPropagation();
    const rect = track.getBoundingClientRect(), perPixel = 128 / Math.max(1, rect.width), grip = event.target.closest?.('.vkb-scroll-grip');
    const first0 = state.first, last0 = state.last;
    if (!event.target.closest?.('.vkb-scroll-handle')) {   // the track: jump so that the handle is centred there
      const middle = Math.round(((event.clientX - rect.left) / rect.width) * 128), moved = moveRange(first0, last0, middle - Math.round((first0 + last0) / 2));
      setRange(moved.first, moved.last);
      return;
    }
    const x0 = event.clientX, side = grip ? (grip.classList.contains('l') ? 'l' : 'r') : 'move';
    const move = (e) => {
      if (e.pointerId !== event.pointerId) return;
      const delta = Math.round((e.clientX - x0) * perPixel);
      if (side === 'move') { const moved = moveRange(first0, last0, delta); setRange(moved.first, moved.last); }
      else if (side === 'l') setRange(Math.min(first0 + delta, last0 - 7), last0, true);
      else setRange(first0, Math.max(last0 + delta, first0 + 7), true);
    };
    const end = (e) => { if (e.pointerId !== event.pointerId) return; window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', end); window.removeEventListener('pointercancel', end); };
    window.addEventListener('pointermove', move); window.addEventListener('pointerup', end); window.addEventListener('pointercancel', end);
  });
  track.style.touchAction = 'none';
  for (const type of ['touchstart', 'touchmove']) track.addEventListener(type, (event) => event.preventDefault(), { passive: false });
  function releaseAll() { for (const id of [...held.keys()]) release(id); }
  // fewer or more keys, step by step: one white key at each end, around the same middle
  fewer.onclick = () => { const r = zoomRange(state.first, state.last, false); setRange(r.first, r.last, true); };
  more.onclick = () => { const r = zoomRange(state.first, state.last, true); setRange(r.first, r.last, true); };
  const octaveNow = () => Math.floor(state.first / 12) - 1;
  down.onclick = () => setOctave(octaveNow() - 1);
  up.onclick = () => setOctave(octaveNow() + 1);
  mode.onchange = () => { state.velocity = mode.value; };
  build();

  return {
    element: root,
    /** Lights a key that something else is playing (a MIDI keyboard, the song). */
    lit: (pitch, on) => light(pitch, on),
    setOctave, releaseAll,
    /** The keys on the screen: { first, last } (MIDI pitches). */
    get range() { return { first: state.first, last: state.last }; },
    /** How many octaves are shown, and the octave of the first key. */
    get octaves() { return Math.round((state.last - state.first) / 12); },
    get octave() { return octaveNow(); },
    /** True when the keys shown were chosen with the scrollbar (the size of the window then does not change them). */
    get custom() { return state.custom; },
    /** Shows the keys from first to last (they begin and end on a white key); custom marks it as chosen by hand. */
    setRange,
    /** Back to the number of keys that goes with the size of the window. */
    resetSpan() { state.custom = false; },
    /** Marks the notes of a scale (a Set of pitch classes) and its root; null clears it. */
    setScale(set, root = 0) { state.scale = set; state.root = root; paintScale(); },
    /** How many octaves the keyboard shows (a bigger window can show more). */
    setOctaves(count) { if (state.custom) return; setRange(state.first, state.first + Math.max(1, Math.min(6, count)) * 12); },
    /** The velocity that 'Fixed' gives (the maximized dock has a knob for it). */
    setFixedVelocity(value) { state.fixed = Math.max(1, Math.min(127, Math.round(value))); },
    dispose() { releaseAll(); root.remove(); },
    isBlack,
  };
}

/**
 * The pitch wheel on the screen: drag the handle up or down, it springs back to the middle when you let go. onBend gets -1 (all the way down) to 1 (all the way up).
 * `set(value)` moves the handle for a wheel that is somewhere else (a MIDI keyboard).
 */
export function createBendStrip({ onBend }) {
  const handle = el('i', { className: 'vkb-bend-handle' });
  const strip = el('div', { className: 'vkb-bend', role: 'slider', 'aria-label': 'Pitch bend', 'aria-valuemin': '-1', 'aria-valuemax': '1', 'aria-valuenow': '0', title: 'Pitch bend: drag up or down, it springs back' }, [handle]);
  let dragging = false;
  const show = (value) => { handle.style.top = `${(1 - (value + 1) / 2) * 100}%`; strip.setAttribute('aria-valuenow', value.toFixed(2)); };
  const from = (event) => { const rect = strip.getBoundingClientRect(); return Math.max(-1, Math.min(1, 1 - 2 * ((event.clientY - rect.top) / Math.max(1, rect.height)))); };
  for (const type of ['touchstart', 'touchmove']) strip.addEventListener(type, (event) => event.preventDefault(), { passive: false });
  strip.addEventListener('pointerdown', (event) => { event.preventDefault(); strip.setPointerCapture(event.pointerId); dragging = true; strip.classList.add('held'); const v = from(event); show(v); onBend(v); });
  strip.addEventListener('pointermove', (event) => { if (!dragging) return; const v = from(event); show(v); onBend(v); });
  const release = () => { if (!dragging) return; dragging = false; strip.classList.remove('held'); show(0); onBend(0); };
  for (const type of ['pointerup', 'pointercancel', 'lostpointercapture']) strip.addEventListener(type, release);
  show(0);
  return { element: strip, set: (value) => { if (!dragging) show(value); } };
}
