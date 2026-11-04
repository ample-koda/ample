// The note editor of Remix: a piano roll for the `notes` line of a midi-clip. It reads and writes the compact notes text with the lossless model of Studio's Jam
// (web/lib/notes-model.mjs: notes you do not touch keep their exact text) and tells the page the new text of the line after every edit. It has no Studio state, no server and
// no store: the page owns the text. The sound of "Play" is a simple tone made in the browser (a preview); Build & play makes the real sound.
//
// What it does: click to add a note, drag a note (or a selection) to move it, drag the right end to resize, drag on empty space to select several (Shift adds), a velocity
// lane under the roll, Delete, arrows to nudge and transpose, Ctrl+A / Ctrl+D / Ctrl+Z / Ctrl+Y, quantize, click a key to hear it, play with a playhead.

import { absolute, addNote, format, parse, pasteNotes, quantizeNotes, removeNote, resizeNote, updateMany } from '../lib/notes-model.mjs';
import { barBeats } from '../lib/meter.mjs';
import { composedNotes } from './lite-roll-source.mjs';

const GUTTER = 56, EDGE = 8, HEAD = 34, LANE = 86, MIN_ROW = 12, MAX_ROW = 30;
const NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];
const BLACK = new Set([1, 3, 6, 8, 10]);
const $ = (selector) => document.querySelector(selector);

/**
 * Starts the note editor on the dialog of the page. `read()` gives the clips of the open song, `write(clip, value)` puts the new value of a clip's notes line into the song,
 * `tempo()` gives the tempo.
 * @returns {{ open(name?: string): boolean, close(): void }}
 */
export function createRoll({ read, write, tempo, goto, stopMain }) {
  const dialog = $('#lite-roll'), canvas = $('#lite-roll-canvas'), ctx = canvas.getContext('2d'), lane = $('#lite-roll-vel'), lctx = lane.getContext('2d'), scroll = $('#lite-roll-scroll');
  let clips = [], clip = null, model = null, ppq = 1_000_000, bars = 4, low = 36, high = 96, pxBeat = 80, row = 20, snap = 0.25;
  let follow = true, cursor = 0, loop = null, rdrag = null, bus = null, readonly = false, selected = new Set(), drag = null, ghost = null, history = [], at = 0, audio = null, playing = null;

  const barTicks = () => barBeats() * ppq;
  const beatsOf = (ticks) => ticks / ppq;
  // the song of the page may be playing: then the button says Stop and stops that (the roll's own tone and the song never play together)
  const mainPlaying = () => { const a = document.getElementById('lite-output'); return !!a && !a.paused && !a.ended; };
  const syncPlay = () => { $('#lite-roll-play').textContent = playing || mainPlaying() ? 'Stop' : 'Play'; };
  const info = (text) => { $('#lite-roll-info').textContent = text; };
  const rowOf = (pitch) => HEAD + (high - pitch) * row;
  const pitchAt = (y) => Math.max(0, Math.min(127, high - Math.floor((y - HEAD) / row)));
  const xOf = (ticks) => GUTTER + beatsOf(ticks) * pxBeat;
  const snapTicks = () => Math.max(1, Math.round(snap * ppq));
  const label = (pitch) => `${NAMES[pitch % 12]}${Math.floor(pitch / 12) - 1}`;
  const notesOf = (current) => absolute(current);
  const shown = () => drag?.preview ?? model;
  const theme = () => { const css = getComputedStyle(document.documentElement); return { text: css.getPropertyValue('--text').trim() || '#ddd', dim: css.getPropertyValue('--dim').trim() || '#8a94a3', accent: css.getPropertyValue('--acc').trim() || '#4aa3ff' }; };

  // ---- layout

  function layout(refit = false) {
    const all = notesOf(model);
    const last = all.reduce((m, n) => Math.max(m, n.at + n.length), 0);
    bars = Math.max(clip.bars || 0, Math.ceil(last / barTicks()), 1);
    const pitches = all.map((n) => n.pitch);
    const lo = pitches.length ? Math.min(...pitches) : 60, hi = pitches.length ? Math.max(...pitches) : 60;
    low = Math.max(0, Math.min(lo - 12, 48));
    high = Math.min(127, Math.max(hi + 12, 84));
    if (refit) {
      pxBeat = Math.max(30, Math.min(160, Math.floor((scroll.clientWidth - GUTTER - 16) / (bars * barBeats()))));
      $('#lite-roll-zoom').value = String(pxBeat);
    }
    canvas.width = GUTTER + Math.round(bars * barBeats() * pxBeat) + 24;
    canvas.height = HEAD + (high - low + 1) * row;
    lane.width = canvas.width;
    lane.height = LANE;
    Object.assign(canvas.dataset, { gutter: GUTTER, row, pxbeat: pxBeat, high, head: HEAD });
  }

  function centre() {
    const pitches = notesOf(model).map((n) => n.pitch);
    const mid = pitches.length ? (Math.min(...pitches) + Math.max(...pitches)) / 2 : 62;
    scroll.scrollTop = Math.max(0, rowOf(mid) - scroll.clientHeight / 2);
    // the first note may be far to the right (a clip built from sections): show it, not an empty start
    const first = notesOf(model).reduce((m, n) => Math.min(m, n.at), Infinity);
    scroll.scrollLeft = Number.isFinite(first) && xOf(first) > scroll.clientWidth - 80 ? Math.max(0, xOf(first) - GUTTER - 40) : 0;
  }

  // ---- drawing

  function draw() {
    const { text, dim, accent } = theme();
    const list = notesOf(shown());
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    for (let pitch = high; pitch >= low; pitch--) {
      const y = rowOf(pitch);
      ctx.fillStyle = BLACK.has(pitch % 12) ? 'rgba(0,0,0,0.30)' : 'rgba(255,255,255,0.045)';
      ctx.fillRect(GUTTER, y, canvas.width - GUTTER, row);
      ctx.fillStyle = pitch % 12 === 0 ? 'rgba(255,255,255,0.22)' : 'rgba(255,255,255,0.04)';
      ctx.fillRect(GUTTER, y + row - 1, canvas.width - GUTTER, 1);
    }
    const stepBeats = Math.max(snap, 0.125);
    for (let step = 0; step <= (bars * barBeats()) / stepBeats; step++) {
      const beat = step * stepBeats, x = GUTTER + Math.round(beat * pxBeat), onBeat = Math.abs(beat - Math.round(beat)) < 1e-9, onBar = onBeat && Math.round(beat) % 4 === 0;
      ctx.fillStyle = onBar ? 'rgba(255,255,255,0.5)' : onBeat ? 'rgba(255,255,255,0.2)' : 'rgba(255,255,255,0.07)';
      ctx.fillRect(x, HEAD, 1, canvas.height - HEAD);
    }
    // the ruler
    ctx.fillStyle = 'rgba(255,255,255,0.06)';
    ctx.fillRect(GUTTER, 0, canvas.width - GUTTER, HEAD);
    ctx.fillStyle = dim;
    ctx.font = '600 11px sans-serif';
    for (let bar = 0; bar < bars; bar++) { ctx.fillText(String(bar + 1), GUTTER + bar * barBeats() * pxBeat + 5, 13); for (let beat = 1; beat < barBeats(); beat++) ctx.fillRect(GUTTER + (bar * 4 + beat) * pxBeat, HEAD - 7, 1, 7); }
    if (loop) { const lx = GUTTER + loop.from * pxBeat, lw = (loop.to - loop.from) * pxBeat; ctx.fillStyle = 'rgba(120,200,255,0.10)'; ctx.fillRect(lx, HEAD, lw, canvas.height - HEAD); ctx.fillStyle = 'rgba(120,200,255,0.35)'; ctx.fillRect(lx, 16, lw, HEAD - 16); ctx.fillStyle = accent; ctx.fillRect(lx, 16, 2, canvas.height - 16); ctx.fillRect(lx + lw - 2, 16, 2, canvas.height - 16); }
    // the keys
    ctx.fillStyle = '#10151c';
    ctx.fillRect(0, 0, GUTTER, canvas.height);
    for (let pitch = high; pitch >= low; pitch--) {
      const y = rowOf(pitch), black = BLACK.has(pitch % 12);
      ctx.fillStyle = black ? '#1c2430' : '#dfe5ee';
      ctx.fillRect(0, y + 1, black ? GUTTER * 0.6 : GUTTER - 1, row - 1);
      if (pitch % 12 === 0) { ctx.fillStyle = '#26303f'; ctx.font = '600 11px sans-serif'; ctx.fillText(label(pitch), GUTTER - 28, y + row - 5); }
    }
    // the ghost of the note a click would add
    if (ghost && !drag) {
      ctx.fillStyle = 'rgba(120,200,255,0.25)';
      ctx.fillRect(xOf(ghost.at), rowOf(ghost.pitch) + 1, Math.max(4, beatsOf(ghost.length) * pxBeat - 1), row - 2);
    }
    // the notes
    for (const n of list) {
      const x = xOf(n.at), w = Math.max(5, beatsOf(n.length) * pxBeat - 1), y = rowOf(n.pitch) + 1, chosen = selected.has(n.index);
      ctx.fillStyle = `hsl(${150 - n.velocity * 0.6}, 62%, ${chosen ? 64 : 36 + n.velocity * 0.15}%)`;
      ctx.beginPath(); ctx.roundRect(x, y, w, row - 2, 4); ctx.fill();
      ctx.strokeStyle = chosen ? text : 'rgba(0,0,0,0.5)'; ctx.lineWidth = chosen ? 1.8 : 1; ctx.stroke();
      if (w > 22 && row >= 16) { ctx.fillStyle = 'rgba(0,0,0,0.55)'; ctx.font = '600 10px sans-serif'; ctx.fillText(label(n.pitch), x + 4, y + row - 6); }
      if (w > 14) { ctx.fillStyle = 'rgba(0,0,0,0.4)'; ctx.fillRect(x + w - 4, y + 4, 2, row - 10); }
    }
    if (drag?.marquee) {
      const m = drag.marquee;
      ctx.fillStyle = 'rgba(120,200,255,0.16)'; ctx.strokeStyle = 'rgba(120,200,255,0.8)'; ctx.lineWidth = 1;
      ctx.fillRect(Math.min(m.x0, m.x1), Math.min(m.y0, m.y1), Math.abs(m.x1 - m.x0), Math.abs(m.y1 - m.y0));
      ctx.strokeRect(Math.min(m.x0, m.x1) + 0.5, Math.min(m.y0, m.y1) + 0.5, Math.abs(m.x1 - m.x0), Math.abs(m.y1 - m.y0));
    }
    const hx = GUTTER + Math.round((playing ? playing.beat : cursor) * pxBeat); // the playhead: a line with a handle in the ruler (drag the handle, or click the ruler)
    ctx.fillStyle = accent; ctx.fillRect(hx - 1, 16, 2, canvas.height - 16);
    ctx.beginPath(); ctx.moveTo(hx - 7, HEAD - 17); ctx.lineTo(hx + 7, HEAD - 17); ctx.lineTo(hx, HEAD - 3); ctx.closePath(); ctx.fill();
    drawLane(list, text, dim, accent);
  }

  function drawLane(list, text, dim, accent) {
    lctx.clearRect(0, 0, lane.width, lane.height);
    lctx.fillStyle = '#10151c'; lctx.fillRect(0, 0, GUTTER, LANE);
    lctx.fillStyle = dim; lctx.font = '600 10px sans-serif'; lctx.fillText('VELOCITY', 6, 14); lctx.fillText('127', 6, 28); lctx.fillText('0', 6, LANE - 6);
    lctx.fillStyle = 'rgba(255,255,255,0.05)'; lctx.fillRect(GUTTER, 0, lane.width - GUTTER, LANE);
    for (const f of [0.25, 0.5, 0.75]) { lctx.fillStyle = 'rgba(255,255,255,0.07)'; lctx.fillRect(GUTTER, Math.round(LANE * (1 - f)), lane.width - GUTTER, 1); }
    for (let bar = 0; bar <= bars; bar++) { lctx.fillStyle = 'rgba(255,255,255,0.3)'; lctx.fillRect(GUTTER + bar * barBeats() * pxBeat, 0, 1, LANE); }
    for (const n of list) {
      const x = xOf(n.at), h = Math.round(((LANE - 8) * n.velocity) / 127), chosen = selected.has(n.index);
      lctx.fillStyle = chosen ? text : `hsl(${150 - n.velocity * 0.6}, 62%, 46%)`;
      lctx.fillRect(x, LANE - h, 5, h);
      lctx.fillStyle = accent; lctx.fillRect(x, LANE - h, 5, 2);
    }
  }

  // ---- state changes

  function refresh() {
    $('#lite-roll-loopclear').disabled = !loop;
    $('#lite-roll-undo').disabled = at <= 0;
    $('#lite-roll-redo').disabled = at >= history.length - 1;
    const some = selected.size > 0;
    for (const id of ['delete', 'dup', 'up', 'down', 'oct-up', 'oct-down']) $(`#lite-roll-${id}`).disabled = readonly || !some;
    for (const id of ['all', 'quant']) $(`#lite-roll-${id}`).disabled = readonly;
    const note = $('#lite-roll-note');
    note.textContent = readonly ? `Calculated from ${readonly}. This clip is read only: change those clips, or press Show in code to go to the line.` : '';
    note.classList.toggle('on', !!readonly);
    if (readonly) { $('#lite-roll-undo').disabled = $('#lite-roll-redo').disabled = true; info(`${model.notes.length} notes, read only`); return; }
    info(`${model.notes.length} note${model.notes.length === 1 ? '' : 's'}${some ? `, ${selected.size} selected` : ''}`);
  }

  function commit(next, keep = null) {
    if (readonly) return;
    model = next;
    history = history.slice(0, at + 1);
    history.push(format(next));
    at = history.length - 1;
    publish(history[at]);
    selected = keep ?? new Set();
    layout();
    draw();
    refresh();
  }

  function publish(text) {
    write(clip, text);
    clip.to = clip.from + text.length;
    clip.value = text;
  }

  function jump(step) {
    if (readonly) return;
    const target = at + step;
    if (target < 0 || target >= history.length) return;
    at = target;
    publish(history[at]);
    model = parse(history[at], ppq);
    selected = new Set();
    layout();
    draw();
    refresh();
  }

  /** The indexes in `selected` that the model can still move (notes tied across a bar line are edited as text). */
  const movable = () => [...selected].filter((i) => model.notes[i] && model.notes[i].note.tie === undefined);

  function nudge(dAt, dPitch) {
    const list = notesOf(model), changes = new Map();
    for (const i of movable()) {
      const n = list[i], when = Math.max(0, n.at + dAt), bar = Math.floor(when / barTicks()), start = when - bar * barTicks();
      changes.set(i, { bar: bar + 1, start, length: Math.max(1, Math.min(n.length, barTicks() - start)), pitch: Math.max(0, Math.min(127, n.pitch + dPitch)) });
    }
    if (changes.size) commit(updateMany(model, changes), new Set(changes.keys()));
  }

  function remove() {
    if (!selected.size) return;
    try {
      let next = model;
      for (const i of [...selected].sort((a, b) => b - a)) next = removeNote(next, i);
      commit(next);
    } catch (error) { info(error.message); }
  }

  function duplicate() {
    const list = notesOf(model), picked = [...selected].map((i) => list[i]).filter(Boolean);
    if (!picked.length) return;
    const first = Math.min(...picked.map((n) => n.at)), end = Math.max(...picked.map((n) => n.at + n.length));
    const shift = Math.max(barTicks(), Math.ceil((end - first) / barTicks()) * barTicks());
    try {
      const next = pasteNotes(model, picked, shift, 0);
      commit(next, new Set(next.notes.map((_, i) => i).slice(model.notes.length)));
    } catch (error) { info(error.message); }
  }

  function quantize() {
    const targets = (selected.size ? [...selected] : model.notes.map((_, i) => i)).filter((i) => model.notes[i]?.note.tie === undefined);
    if (targets.length) commit(quantizeNotes(model, targets, snapTicks()), new Set(selected));
  }

  // ---- sound

  function tone(pitch, when, length, velocity, out) {
    audio ??= new AudioContext();
    const osc = audio.createOscillator(), gain = audio.createGain(), start = audio.currentTime + when;
    osc.type = 'triangle';
    osc.frequency.value = 440 * 2 ** ((pitch - 69) / 12);
    gain.gain.setValueAtTime(0.0001, start);
    gain.gain.exponentialRampToValueAtTime(0.2 * (velocity / 127), start + 0.01);
    gain.gain.exponentialRampToValueAtTime(0.0001, start + Math.max(0.08, length));
    osc.connect(gain).connect(out ?? audio.destination);
    osc.start(start);
    osc.stop(start + Math.max(0.1, length) + 0.05);
  }

  function cutBus() {
    if (!bus) return;
    const old = bus; bus = null;
    old.gain.setTargetAtTime(0, audio.currentTime, 0.01);
    setTimeout(() => old.disconnect(), 250);
  }

  function stop() {
    if (!playing) return;
    cancelAnimationFrame(playing.frame);
    cutBus();
    playing = null;
    syncPlay();
    draw();
  }

  // schedules the notes from a beat to the end of the clip (or of the looped region)
  function schedule(from) {
    cutBus();
    bus = audio.createGain();
    bus.connect(audio.destination);
    const seconds = 60 / Math.max(20, tempo()), to = loop ? loop.to : bars * barBeats();
    notesOf(model).forEach((n) => {
      const a = beatsOf(n.at), b = a + beatsOf(n.length);
      if (b <= from || a >= to) return;
      const s = Math.max(a, from);
      tone(n.pitch, (s - from) * seconds, (Math.min(b, to) - s) * seconds, n.velocity, bus);
    });
    Object.assign(playing, { from, to, seconds, origin: performance.now() });
  }

  function play() {
    if (playing || mainPlaying()) { if (mainPlaying()) stopMain?.(); stop(); syncPlay(); return; }
    audio ??= new AudioContext();
    audio.resume?.();
    playing = { beat: 0, frame: 0 };
    follow = true;
    schedule(loop && (cursor < loop.from || cursor >= loop.to) ? loop.from : cursor);
    syncPlay();
    const step = () => {
      if (!playing) return;
      let beat = playing.from + (performance.now() - playing.origin) / 1000 / playing.seconds;
      if (beat >= playing.to) { if (loop) { schedule(loop.from); beat = loop.from; } else { stop(); return; } }
      playing.beat = beat;
      const x = GUTTER + beat * pxBeat;
      const inView = x >= scroll.scrollLeft && x <= scroll.scrollLeft + scroll.clientWidth - 40;
      if (inView) follow = true; // the view follows the playhead again once it is in sight
      else if (follow) scroll.scrollLeft = Math.max(0, x - GUTTER - 20);
      draw();
      playing.frame = requestAnimationFrame(step);
    };
    step();
  }

  // ---- pointer

  const point = (event, target = canvas) => { const box = target.getBoundingClientRect(); return { x: (event.clientX - box.left) * (target.width / box.width), y: (event.clientY - box.top) * (target.height / box.height) }; };
  const snapped = (ticks) => Math.round(ticks / snapTicks()) * snapTicks();

  const rulerBeat = (x) => Math.max(0, Math.min(bars * barBeats(), Math.round((x - GUTTER) / pxBeat / snap) * snap));
  const playheadX = () => GUTTER + (playing ? playing.beat : cursor) * pxBeat;
  const moveHead = () => { if (playing) schedule(cursor); };
  function rulerDown(x) {
    const grab = Math.abs(x - playheadX()) <= 10;
    rdrag = { kind: grab ? 'head' : 'region', from: rulerBeat(x), moved: false, x };
    if (grab) { cursor = playing ? playing.beat : cursor; }
  }
  function rulerMove(x) {
    if (rdrag.kind === 'head') { cursor = rulerBeat(x); rdrag.moved = true; if (playing) playing.beat = cursor; draw(); return; }
    if (Math.abs(x - rdrag.x) > 4) rdrag.moved = true;
    if (!rdrag.moved) return;
    const to = rulerBeat(x), from = Math.min(rdrag.from, to), end = Math.max(rdrag.from, to);
    loop = end - from >= snap ? { from, to: end } : null;
    draw(); refresh();
  }
  function rulerUp() {
    const done = rdrag; rdrag = null;
    if (done.kind === 'region' && !done.moved) cursor = done.from; // a click puts the playhead there
    if (done.kind === 'region' && done.moved && loop) cursor = loop.from;
    moveHead(); draw(); refresh();
  }

  function hit(x, y) {
    const list = notesOf(model);
    for (let i = list.length - 1; i >= 0; i--) {
      const n = list[i], left = xOf(n.at), right = left + Math.max(5, beatsOf(n.length) * pxBeat - 1), top = rowOf(n.pitch);
      if (x >= left && x <= right && y >= top && y < top + row) return { index: n.index, edge: x >= right - EDGE && right - left > EDGE * 2 };
    }
    return null;
  }

  canvas.onpointerdown = (event) => {
    if (!model?.representable) return;
    const first = point(event);
    if (first.x >= GUTTER && first.y < HEAD) { canvas.setPointerCapture(event.pointerId); rulerDown(first.x); return; }
    if (readonly) return;
    canvas.setPointerCapture(event.pointerId);
    const { x, y } = point(event);
    if (x < GUTTER) { if (y > HEAD) tone(pitchAt(y), 0, 0.4, 100); return; }
    if (y < HEAD) return;
    const found = hit(x, y);
    if (found) {
      if (event.shiftKey) { if (selected.has(found.index)) selected.delete(found.index); else selected.add(found.index); }
      else if (!selected.has(found.index)) selected = new Set([found.index]);
      const note = notesOf(model)[found.index];
      if (note.tie !== undefined) { info('A note tied across bars is edited as text.'); draw(); refresh(); return; }
      drag = { kind: found.edge && selected.size === 1 ? 'resize' : 'move', index: found.index, x, y, base: model, baseNotes: notesOf(model), preview: null, moved: false };
      tone(note.pitch, 0, 0.25, note.velocity);
    } else {
      drag = { kind: 'empty', x, y, shift: event.shiftKey, base: model, marquee: null, moved: false };
    }
    draw();
    refresh();
  };

  canvas.onpointermove = (event) => {
    const { x, y } = point(event);
    if (rdrag) { rulerMove(x); return; }
    if (!drag && y < HEAD && x >= GUTTER) { canvas.style.cursor = Math.abs(x - playheadX()) <= 10 ? 'ew-resize' : 'pointer'; if (ghost) { ghost = null; draw(); } return; }
    if (!drag) {
      const found = hit(x, y);
      canvas.style.cursor = x < GUTTER ? 'pointer' : found ? (found.edge ? 'ew-resize' : 'grab') : 'crosshair';
      const old = ghost;
      ghost = !found && x >= GUTTER && y >= HEAD ? { at: Math.max(0, Math.floor(((x - GUTTER) / pxBeat) * ppq / snapTicks()) * snapTicks()), pitch: pitchAt(y), length: snapTicks() } : null;
      if (ghost?.at !== old?.at || ghost?.pitch !== old?.pitch) draw();
      return;
    }
    if (Math.abs(x - drag.x) + Math.abs(y - drag.y) > 3) drag.moved = true;
    if (drag.kind === 'empty') {
      if (!drag.moved) return;
      drag.marquee = { x0: drag.x, y0: drag.y, x1: x, y1: y };
      const m = drag.marquee, hits = new Set(notesOf(drag.base).filter((n) => {
        const left = xOf(n.at), right = left + beatsOf(n.length) * pxBeat, top = rowOf(n.pitch);
        return right >= Math.min(m.x0, m.x1) && left <= Math.max(m.x0, m.x1) && top + row >= Math.min(m.y0, m.y1) && top <= Math.max(m.y0, m.y1);
      }).map((n) => n.index));
      selected = drag.shift ? new Set([...(drag.kept ??= [...selected]), ...hits]) : hits;
      draw();
      refresh();
      return;
    }
    if (!drag.moved) return;
    try {
      if (drag.kind === 'move') {
        const dAt = snapped(((x - drag.x) / pxBeat) * ppq), dPitch = Math.round((drag.y - y) / row), changes = new Map();
        for (const i of [...selected]) {
          const n = drag.baseNotes[i];
          if (!n || n.tie !== undefined) continue;
          const when = Math.max(0, n.at + dAt), bar = Math.floor(when / barTicks()), start = when - bar * barTicks();
          changes.set(i, { bar: bar + 1, start, length: Math.max(1, Math.min(n.length, barTicks() - start)), pitch: Math.max(0, Math.min(127, n.pitch + dPitch)) });
        }
        drag.preview = updateMany(drag.base, changes);
      } else {
        const n = drag.baseNotes[drag.index];
        drag.preview = resizeNote(drag.base, drag.index, Math.max(snapTicks(), snapped(n.length + ((x - drag.x) / pxBeat) * ppq)));
      }
    } catch { drag.preview = null; }
    draw();
  };

  canvas.onpointerup = (event) => {
    if (rdrag) { rulerUp(); return; }
    if (!drag) return;
    const done = drag;
    drag = null;
    if (done.kind === 'empty') {
      if (done.moved) { draw(); refresh(); return; }
      // a click on empty space adds a note of the snap length (with Shift: the selection is cleared)
      const { x, y } = point(event);
      if (done.shift) { selected = new Set(); draw(); refresh(); return; }
      const tick = Math.max(0, Math.floor(((x - GUTTER) / pxBeat) * ppq / snapTicks()) * snapTicks()), bar = Math.floor(tick / barTicks()) + 1, start = tick % barTicks();
      const pitch = pitchAt(y);
      try {
        const next = addNote(model, { bar, start, length: Math.min(snapTicks(), barTicks() - start), pitch, velocity: 100 });
        tone(pitch, 0, 0.3, 100);
        commit(next, new Set([next.notes.length - 1]));
      } catch (error) { info(error.message); }
      return;
    }
    if (done.preview && format(done.preview) !== format(done.base)) commit(done.preview, done.kind === 'move' ? new Set(selected) : new Set());
    else { draw(); refresh(); }
  };
  canvas.onpointercancel = () => { drag = null; rdrag = null; draw(); };
  canvas.onpointerleave = () => { if (!drag && ghost) { ghost = null; draw(); } };

  // the velocity lane: drag a bar up or down (a selected note moves the whole selection to the same value)
  function setVelocity(event) {
    const { y } = point(event, lane), velocity = Math.max(1, Math.min(127, Math.round(127 * (1 - (y - 4) / (LANE - 8)))));
    drag.preview = updateMany(drag.base, new Map([...selected].map((i) => [i, { velocity }])));
    draw();
  }
  lane.onpointerdown = (event) => {
    if (readonly) return;
    lane.setPointerCapture(event.pointerId);
    const { x } = point(event, lane);
    const near = notesOf(model).filter((n) => x >= xOf(n.at) - 3 && x <= xOf(n.at) + 8).sort((a, b) => b.velocity - a.velocity)[0];
    if (!near) return;
    if (!selected.has(near.index)) selected = new Set([near.index]);
    drag = { kind: 'velocity', base: model, preview: null };
    setVelocity(event);
  };
  lane.onpointermove = (event) => { if (drag?.kind === 'velocity') setVelocity(event); };
  lane.onpointerup = () => {
    if (drag?.kind !== 'velocity') return;
    const done = drag;
    drag = null;
    if (done.preview && format(done.preview) !== format(done.base)) commit(done.preview, new Set(selected)); else draw();
  };

  scroll.addEventListener('scroll', () => { $('#lite-roll-velwrap').scrollLeft = scroll.scrollLeft; });
  scroll.addEventListener('wheel', (event) => {
    if (!event.ctrlKey) return;
    event.preventDefault();
    pxBeat = Math.max(30, Math.min(200, pxBeat * (event.deltaY < 0 ? 1.12 : 0.89)));
    $('#lite-roll-zoom').value = String(Math.round(pxBeat));
    layout();
    draw();
  }, { passive: false });

  dialog.onkeydown = (event) => {
    if (event.target instanceof HTMLSelectElement || event.target instanceof HTMLInputElement) return;
    const mod = event.ctrlKey || event.metaKey, key = event.key;
    if (key === 'Delete' || key === 'Backspace') { event.preventDefault(); remove(); }
    else if (mod && key.toLowerCase() === 'z') { event.preventDefault(); jump(event.shiftKey ? 1 : -1); }
    else if (mod && key.toLowerCase() === 'y') { event.preventDefault(); jump(1); }
    else if (mod && key.toLowerCase() === 'a') { event.preventDefault(); selected = new Set(model.notes.map((_, i) => i)); draw(); refresh(); }
    else if (mod && key.toLowerCase() === 'd') { event.preventDefault(); duplicate(); }
    else if (key === 'ArrowLeft' || key === 'ArrowRight') { event.preventDefault(); nudge((key === 'ArrowLeft' ? -1 : 1) * snapTicks(), 0); }
    else if (key === 'ArrowUp' || key === 'ArrowDown') { event.preventDefault(); nudge(0, (key === 'ArrowUp' ? 1 : -1) * (event.shiftKey ? 12 : 1)); }
    else if (key.toLowerCase() === 'q' && !mod) { event.preventDefault(); quantize(); }
    else if (key === ' ') { event.preventDefault(); play(); }
  };

  // ---- the dialog

  function load(next) {
    stop(); // another clip: the playing one stops by itself, the Stop button is not needed
    clip = next;
    ppq = 1_000_000;
    readonly = false;
    cursor = 0;
    loop = null;
    let text = clip.value;
    if (clip.composed) {
      const built = composedNotes(clips, clip);
      if (built) { text = built.text; readonly = [...new Set(built.names)].join(', '); }
    }
    model = parse(text, ppq);
    history = [text];
    at = 0;
    selected = new Set();
    drag = null;
    ghost = null;
    if (!model.representable) {
      info(`This clip cannot be shown as a roll: ${model.reasons[0] ?? 'unknown notes'}. Edit it as text.`);
      canvas.width = GUTTER + 16 * pxBeat;
      canvas.height = 6 * row;
      ctx.clearRect(0, 0, canvas.width, canvas.height);
      return;
    }
    layout(true);
    draw();
    refresh();
    centre();
    syncPlay();
  }

  $('#lite-roll-clip').onchange = (event) => { const next = clips.find((c) => c.name === event.target.value); if (next) load(next); };
  $('#lite-roll-snap').onchange = (event) => { snap = Number(event.target.value); draw(); };
  $('#lite-roll-zoom').oninput = (event) => { pxBeat = Number(event.target.value); if (model?.representable) { layout(); draw(); } };
  $('#lite-roll-height').oninput = (event) => { row = Math.max(MIN_ROW, Math.min(MAX_ROW, Number(event.target.value))); if (model?.representable) { layout(); draw(); } };
  $('#lite-roll-fit').onclick = () => { if (model?.representable) { layout(true); draw(); centre(); } };
  $('#lite-roll-play').onclick = play;
  $('#lite-roll-delete').onclick = remove;
  $('#lite-roll-dup').onclick = duplicate;
  $('#lite-roll-quant').onclick = quantize;
  $('#lite-roll-all').onclick = () => { selected = new Set(model.notes.map((_, i) => i)); draw(); refresh(); };
  $('#lite-roll-up').onclick = () => nudge(0, 1);
  $('#lite-roll-down').onclick = () => nudge(0, -1);
  $('#lite-roll-oct-up').onclick = () => nudge(0, 12);
  $('#lite-roll-oct-down').onclick = () => nudge(0, -12);
  $('#lite-roll-undo').onclick = () => jump(-1);
  $('#lite-roll-redo').onclick = () => jump(1);
  // scrolling by hand (wheel, scroll bar, touch) stops the view from following the playhead, until the playhead is in sight again
  for (const type of ['wheel', 'touchmove']) scroll.addEventListener(type, () => { follow = false; }, { passive: true });
  scroll.addEventListener('pointerdown', (event) => { if (event.target === scroll) follow = false; });
  $('#lite-roll-loopclear').onclick = () => { loop = null; if (playing) schedule(playing.beat); draw(); refresh(); };
  $('#lite-roll-close').onclick = () => dialog.close();
  $('#lite-roll-code').onclick = () => { const line = clip?.line; dialog.close(); if (line != null) goto?.(line); }; // close the roll and go to the clip in the code
  for (const type of ['play', 'pause', 'ended', 'emptied']) document.getElementById('lite-output')?.addEventListener(type, syncPlay);
  dialog.addEventListener('close', () => { stop(); audio?.close?.(); audio = null; });

  return {
    open(name) {
      const all = read();
      clips = all.filter((c) => c.rollable || c.composed);
      if (!clips.length) return false;
      const select = $('#lite-roll-clip');
      select.textContent = '';
      for (const c of clips) { const option = document.createElement('option'); option.value = c.name; option.textContent = (c.composed ? '🧮 ' : '🎹 ') + c.name; option.title = c.composed ? 'Calculated from other clips (read only)' : 'Notes you can edit'; select.append(option); }
      const pick = clips.find((c) => c.name === name) ?? clips[0];
      select.value = pick.name;
      if (!dialog.open) dialog.showModal();
      load(pick);
      return true;
    },
    close() { dialog.close(); },
  };
}
