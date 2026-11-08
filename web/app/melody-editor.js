// The melody and chords editor of a midi-clip: opens from Ctrl+click on a `melody` or `chords` line (the Studio switches to the block view first) or from the block's own
// button. The text of the property stays the single truth: every gesture is one edit of that property (one undo step), the other lines of the clip are never touched.
// Views: piano roll (draw, move, resize, erase, velocity lane), notation and a list for a melody; a bar ribbon with a keyboard for chords. Maths: web/lib/melody-model.mjs. @ample:melody-editor

import { el, toast } from './ui.js';
import { addWindowChrome } from './window-chrome.js';
import { S, edit, flat, readOnly, subscribe, undo, redo } from './store.js';
import { liveAudioContext, armAudioUnlock } from './live-play.js';
import { playbackOutput } from './playback-volume.js';
import { barBeats } from '../lib/meter.mjs';
import { DEFAULT_VELOCITY, LEVELS, QUALITY_CHOICES, chordClasses, chordName, effectiveVelocity, fmt, midiOf, nameOf, overlaps, parseChord, parseChords, parseMelody, pitchClassOf, room, roman, serializeChords, serializeMelody } from '../lib/melody-model.mjs';

const NS = 'http://www.w3.org/2000/svg';
const NOTE_NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];
const LO = 36, HI = 96, ROW = 15, KEYS = 52, PX = 52;
const open = new Map();

const svg = (tag, attrs = {}, parent = null) => { const e = document.createElementNS(NS, tag); for (const k in attrs) e.setAttribute(k, attrs[k]); if (parent) parent.append(e); return e; };

/** Opens (or raises) the editor of a midi-clip block; null when the clip has neither a melody nor chords or a word of it is not understood. */
export function openMelodyEditor(blockId) {
  const found = open.get(blockId);
  if (found?.isConnected) { found.focus?.(); return found; }
  const nodeOf = () => flat().nodes.find((n) => n.id === blockId);
  const node = nodeOf();
  if (!node || node.tpl !== 'midi-clip') return null;
  const hasMelody = node.params.melody !== undefined, hasChords = node.params.chords !== undefined;
  if (!hasMelody && !hasChords) { toast('This clip has no melody or chords line to edit.'); return null; }
  armAudioUnlock();
  const ro = () => readOnly() || nodeOf()?.flags?.includes('lock');

  const st = { mode: hasMelody ? 'melody' : 'chords', view: 'roll', tool: 'draw', grid: 0.5, sel: null, csel: 0, speed: 1, shrinkDiv: 10, shrinkOver: 4, playing: false, head: null, notes: [], marks: [], trailing: 0, bars: [], quoted: true, drag: null, total: 8, lastText: null };
  const raw = (name) => String(nodeOf()?.params[name] ?? '');
  const bars = () => Math.max(1, Number(nodeOf()?.params.bars) || 8);
  const beatsPerBar = () => barBeats();

  function load() {
    if (st.mode === 'melody') {
      const text = raw('melody'); st.quoted = text.trim().startsWith('"') || text.trim() === '';
      const m = parseMelody(text);
      if (!m.ok) { st.problem = m.reasons[0]; st.notes = []; st.marks = []; return false; }
      st.problem = ''; st.notes = m.notes; st.marks = m.marks; st.trailing = m.trailingRest;
      st.total = Math.max(bars() * beatsPerBar(), Math.ceil(m.total / beatsPerBar()) * beatsPerBar());
      st.sel = st.sel && st.notes.find((n) => Math.abs(n.at - st.sel.at) < 1e-9 && n.midi === st.sel.midi) || null;
    } else {
      const text = raw('chords'); st.quoted = text.trim().startsWith('"') || text.trim() === '';
      st.bars = parseChords(text); st.problem = ''; st.csel = Math.min(st.csel, Math.max(0, st.bars.length - 1));
    }
    st.lastText = text0(); return true;
  }
  const text0 = () => (st.mode === 'melody' ? raw('melody') : raw('chords'));
  const quote = (t) => (st.quoted || /\s/.test(t) ? `"${t}"` : t);

  // ---- writing: one edit per finished gesture ----
  let writing = Promise.resolve();
  function commit(extraOps = []) {
    if (ro()) { toast('This song is read-only.'); return; }
    const text = st.mode === 'melody' ? serializeMelody({ notes: st.notes, marks: st.marks }, { trailingRest: st.trailing }) : serializeChords(st.bars);
    const ops = [{ type: 'setParameter', block: blockId, name: st.mode, value: quote(text) }, ...extraOps];
    st.lastText = quote(text);
    writing = writing.then(() => edit(ops)).then((ok) => { if (ok === false) { load(); draw(); } });
  }

  // ---- sound: a small voice of its own through the playback volume of the Studio ----
  let ctx = null, stops = [];
  const voice = (midi, when, seconds, vel) => {
    ctx = ctx || liveAudioContext();
    const o = ctx.createOscillator(), g = ctx.createGain();
    o.type = 'triangle'; o.frequency.value = 440 * Math.pow(2, (midi - 69) / 12);
    g.gain.setValueAtTime(0, when); g.gain.linearRampToValueAtTime(0.2 * vel / 127, when + 0.012); g.gain.setTargetAtTime(0.0001, when + Math.max(0.05, seconds - 0.08), 0.06);
    o.connect(g).connect(playbackOutput(ctx)); o.start(when); o.stop(when + seconds + 0.4); stops.push(o);
  };
  const blip = (midi) => { try { ctx = ctx || liveAudioContext(); voice(midi, ctx.currentTime, 0.3, 100); } catch { /* sound may be blocked until a click */ } };
  const bpm = () => Number(S.doc?.project?.tempo) || 120;
  /** Where Play starts: at the bar of the first note, so a long rest in front is not waited for (the editor shows it, the preview skips it). */
  const startBeat = () => { if (st.mode !== 'melody' || !st.notes.length) return 0; const per = beatsPerBar(); return Math.floor(Math.min(...st.notes.map((n) => n.at)) / per + 1e-9) * per; };
  const silence = () => { for (const o of stops) { try { o.stop(); } catch { /* already ended */ } } stops = []; };
  function stopPlay() { st.playing = false; st.head = null; markPlaying(-1); silence(); playBtn.textContent = '▶ Play'; draw(); }
  /** Plays from a beat (0 is the start); a change of speed while playing comes back here with the beat that sounds now, so the sound goes on from the same place. */
  function play(from = 0) {
    if (st.playing) { stopPlay(); return; }
    try { ctx = ctx || liveAudioContext(); } catch { toast('Click once on the page to allow sound.'); return; }
    const sp = 60 / bpm() / st.speed, t0 = ctx.currentTime + 0.06, start = performance.now() + 60;
    let endBeat = 0;
    if (st.mode === 'melody') for (const n of st.notes) { endBeat = Math.max(endBeat, n.at + n.len); if (n.at + n.len <= from + 1e-9) continue; const s0 = Math.max(n.at, from); voice(n.midi, t0 + (s0 - from) * sp, (n.at + n.len - s0) * sp, effectiveVelocity(n, st.marks)); }
    else st.bars.forEach((bar, i) => bar.forEach((c, k) => { const classes = chordClasses(c); const at = (i + k / bar.length) * beatsPerBar(), len = beatsPerBar() / bar.length; endBeat = Math.max(endBeat, at + len); if (at + len <= from + 1e-9) return; const s0 = Math.max(at, from); classes.forEach((pc, j) => voice(60 + pc - (j === 0 && c.bass ? 12 : 0), t0 + (s0 - from) * sp, (at + len - s0) * sp, 80)); }));
    st.playing = true; playBtn.textContent = '■ Stop';
    const tick = () => { if (!st.playing) return; const b = from + (performance.now() - start) / 1000 / sp; if (b >= endBeat + 0.3) { stopPlay(); return; } st.head = Math.max(0, b); moveHead(); markPlaying(b); requestAnimationFrame(tick); };
    tick();
  }

  // ---- shell ----
  const panel = el('dialog', { className: 'mls-panel metal-window me-panel', 'aria-label': `Melody editor of ${node.title}`, 'data-block': blockId });
  const closeBtn = el('button', { type: 'button', className: 'mls-close', textContent: '✕', title: 'Close', 'aria-label': 'Close the melody editor', onclick: () => panel.close() });
  const title = el('div', { className: 'mls-title metal-title' }, [el('strong', { textContent: 'Melody editor' }), el('span', { className: 'mls-clip', textContent: node.title }), closeBtn]);
  const playBtn = el('button', { type: 'button', className: 'primary', textContent: '\u25B6 Play', onclick: () => play(startBeat()) });
  const speedSel = () => {
    const s = el('select', { title: 'Playback speed of the preview (the song itself is not changed)', 'aria-label': 'Playback speed' }, [[0.1, '0.1x'], [0.25, '0.25x'], [0.5, '0.5x'], [0.75, '0.75x'], [1, '1x'], [1.25, '1.25x'], [1.5, '1.5x'], [2, '2x'], [3, '3x'], [4, '4x'], [6, '6x'], [8, '8x'], [12, '12x'], [16, '16x']].map(([v, l]) => el('option', { value: String(v), textContent: l })));
    s.value = String(st.speed); s.onchange = () => { st.speed = Number(s.value); if (st.playing) { const at = st.head ?? 0; silence(); st.playing = false; play(at); } };
    return s;
  };
  const mkSeg = (items, get, set) => { const box = el('span', { className: 'me-seg' }); for (const [v, label, tip] of items) box.append(el('button', { type: 'button', textContent: label, title: tip || '', 'aria-pressed': String(get() === v), onclick: () => { set(v); draw(); } })); return box; };
  const toolbar = el('div', { className: 'mls-toolbar me-toolbar' });
  const body = el('div', { className: 'me-body' });
  const inspector = el('div', { className: 'me-insp' });
  const textLine = el('div', { className: 'me-text' });
  const stage = el('div', { className: 'me-stage' }, [body, textLine]);
  const main = el('div', { className: 'me-main' }, [stage, inspector]);
  panel.append(title, toolbar, main);
  document.body.append(panel);
  // the window is moved by its title bar (a transform, like AMPLE Jam); the window chrome slides it sideways when it changes width
  let offX = 0, offY = 0;
  const place = () => { panel.style.transform = 'translate3d(' + offX + 'px, ' + offY + 'px, 0)'; };
  const shiftPanel = (dx) => { const r = panel.getBoundingClientRect(); offX += Math.max(-r.left, Math.min(window.innerWidth - 80 - r.left, dx)); place(); };
  addWindowChrome(panel, title, closeBtn, shiftPanel);
  title.addEventListener('pointerdown', (event) => {
    if (event.target.closest('button') || panel.dataset.window === 'max') return;
    const rect = panel.getBoundingClientRect(), sx = event.clientX, sy = event.clientY, baseX = rect.left - offX, baseY = rect.top - offY;
    title.setPointerCapture(event.pointerId);
    const clampX = (x) => Math.max(0, Math.min(window.innerWidth - 80, x)), clampY = (y) => Math.max(0, Math.min(window.innerHeight - 40, y));
    const move = (e) => { offX = clampX(rect.left + e.clientX - sx) - baseX; offY = clampY(rect.top + e.clientY - sy) - baseY; place(); };
    const up = () => { title.removeEventListener('pointermove', move); title.removeEventListener('pointerup', up); title.removeEventListener('pointercancel', up); };
    title.addEventListener('pointermove', move); title.addEventListener('pointerup', up); title.addEventListener('pointercancel', up);
  });
  open.set(blockId, panel);
  panel.show();
  const stopStore = subscribe(() => { if (!panel.isConnected || st.drag) return; if (text0() !== st.lastText) { load(); draw(); } });
  panel.addEventListener('close', () => { stopPlay(); stopStore(); open.delete(blockId); panel.remove(); });
  panel.addEventListener('keydown', (e) => {
    const typing = /INPUT|SELECT|TEXTAREA/.test(e.target.tagName);
    if ((e.key === 'Delete' || e.key === 'Backspace') && !typing && st.sel) { e.preventDefault(); st.notes = st.notes.filter((n) => n !== st.sel); st.sel = null; commit(); draw(); }
    else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'z' && !typing) { e.preventDefault(); (e.shiftKey ? redo : undo)(); }
    else if (e.key === ' ' && !typing) { e.preventDefault(); play(startBeat()); }
    else if (e.key === 'Escape') panel.close();
  });
  panel.tabIndex = -1;

  // ---- melody: roll ----
  const gridW = () => st.total * PX, H = (HI - LO + 1) * ROW;
  const xOf = (beat) => KEYS + beat * PX, yOf = (midi) => 22 + (HI - midi) * ROW;
  function rollView() {
    const w = KEYS + gridW(), h = 22 + H;
    const s = svg('svg', { width: w, height: h, viewBox: `0 0 ${w} ${h}`, class: 'me-roll' });
    for (let m = HI; m >= LO; m--) {
      const black = NOTE_NAMES[m % 12].includes('#');
      svg('rect', { x: KEYS, y: yOf(m), width: gridW(), height: ROW, class: black ? 'row black' : 'row' }, s);
      svg('rect', { x: 0, y: yOf(m), width: KEYS - 1, height: ROW, class: black ? 'key black' : 'key' }, s);
      if (m % 12 === 0) { const t = svg('text', { x: KEYS - 6, y: yOf(m) + 11, 'text-anchor': 'end', class: 'keylabel' }, s); t.textContent = `C${Math.floor(m / 12) - 1}`; }
    }
    const per = beatsPerBar(), sub = Math.round(1 / st.grid);
    for (let b = 0; b <= st.total * sub; b++) {
      const beat = b / sub, bar = Math.abs(beat % per) < 1e-9, whole = Math.abs(beat % 1) < 1e-9;
      svg('line', { x1: xOf(beat), x2: xOf(beat), y1: bar ? 0 : whole ? 16 : 20, y2: h, class: bar ? 'gl bar' : whole ? 'gl beat' : 'gl' }, s);
      if (bar) { const t = svg('text', { x: xOf(beat) + 4, y: 13, class: 'barlabel' }, s); t.textContent = String(Math.round(beat / per) + 1); }
    }
    for (const m of st.marks) { const t = svg('text', { x: xOf(m.at) + 2, y: 13, class: 'mark' }, s); t.textContent = m.text; }
    const bg = svg('rect', { x: KEYS, y: 22, width: gridW(), height: H, fill: 'transparent', class: `hit tool-${st.tool}` }, s);
    const at = (e) => { const r = s.getBoundingClientRect(), k = w / r.width; return { x: (e.clientX - r.left) * k - KEYS, y: (e.clientY - r.top) * k - 22 }; };
    bg.addEventListener('pointerdown', (e) => {
      if (ro()) { toast('This song is read-only.'); return; }
      if (st.tool !== 'draw') { st.sel = null; draw(); return; }
      const p = at(e), start = Math.floor(p.x / PX / st.grid) * st.grid, midi = HI - Math.floor(p.y / ROW);
      if (start < 0 || start >= st.total || overlaps(st.notes, start, st.grid)) return;
      const n = { midi, at: start, len: Math.min(st.grid, room(st.notes, start, st.total)), vel: null };
      st.notes.push(n); st.sel = n; blip(midi); beginDrag(e, n, 'size', s, w);
    });
    for (const n of st.notes) {
      const x = xOf(n.at), y = yOf(n.midi), nw = Math.max(8, n.len * PX - 2), v = effectiveVelocity(n, st.marks);
      const g = svg('g', { class: n === st.sel ? 'note sel' : 'note', 'data-n': String(st.notes.indexOf(n)) }, s);
      svg('rect', { x: x + 1, y: y + 1.5, width: nw, height: ROW - 3, rx: 4, style: `opacity:${0.4 + v / 127 * 0.6}` }, g);
      if (nw > 30) { const t = svg('text', { x: x + 6, y: y + 12, class: 'nlabel' }, g); t.textContent = nameOf(n.midi); }
      svg('rect', { x: x + nw - 5, y: y + 1.5, width: 7, height: ROW - 3, fill: 'transparent', 'data-edge': '1', class: 'edge' }, g);
      g.addEventListener('pointerdown', (e) => {
        e.stopPropagation();
        if (ro()) { toast('This song is read-only.'); return; }
        if (st.tool === 'erase') { st.notes = st.notes.filter((q) => q !== n); st.sel = null; commit(); draw(); return; }
        st.sel = n; blip(n.midi); beginDrag(e, n, e.target.dataset.edge ? 'size' : 'move', s, w);
      });
    }
    if (st.head != null) svg('line', { id: 'me-head', x1: xOf(st.head), x2: xOf(st.head), y1: 0, y2: h, class: 'head' }, s);
    return s;
  }
  function beginDrag(e0, n, how, s, w) {
    e0.preventDefault(); const k = w / s.getBoundingClientRect().width, x0 = e0.clientX, y0 = e0.clientY, at0 = n.at, m0 = n.midi, l0 = n.len;
    st.drag = { n }; let moved = false;
    const move = (e) => {
      moved = true; const dx = (e.clientX - x0) * k / PX, dy = (e.clientY - y0) * k / ROW;
      if (how === 'move') {
        const nat = Math.max(0, Math.min(st.total - n.len, Math.round((at0 + dx) / st.grid) * st.grid)), nm = Math.max(LO, Math.min(HI, m0 - Math.round(dy)));
        if (!overlaps(st.notes, nat, n.len, n)) { if (nm !== n.midi) blip(nm); n.at = nat; n.midi = nm; }
      } else {
        const want = Math.max(st.grid, Math.round((l0 + dx) / st.grid) * st.grid);
        n.len = Math.min(want, room(st.notes, n.at, st.total, n));
      }
      draw(false);
    };
    const up = () => { removeEventListener('pointermove', move); removeEventListener('pointerup', up); st.drag = null; commit(); draw(); };
    addEventListener('pointermove', move); addEventListener('pointerup', up);
  }
  function velocityView() {
    const w = KEYS + gridW(), h = 64, s = svg('svg', { width: w, height: h, viewBox: `0 0 ${w} ${h}`, class: 'me-vel' });
    const t = svg('text', { x: 6, y: 14, class: 'barlabel' }, s); t.textContent = 'Velocity';
    for (const n of st.notes) {
      const v = effectiveVelocity(n, st.marks), bh = v / 127 * (h - 18), bar = svg('rect', { x: xOf(n.at) + 2, y: h - bh - 3, width: 9, height: bh, rx: 2, class: n === st.sel ? 'vbar sel' : 'vbar' }, s);
      bar.addEventListener('pointerdown', (e) => {
        e.preventDefault(); if (ro()) return; st.sel = n; st.drag = { n }; const r = s.getBoundingClientRect();
        const move = (ev) => { n.vel = Math.max(1, Math.min(127, Math.round((r.bottom - ev.clientY) / r.height * h / (h - 18) * 127))); draw(false); };
        const up = () => { removeEventListener('pointermove', move); removeEventListener('pointerup', up); st.drag = null; blip(n.midi); commit(); draw(); };
        addEventListener('pointermove', move); addEventListener('pointerup', up);
      });
    }
    return s;
  }
  function staffView() {
    const w = KEYS + gridW(), s = svg('svg', { width: w, height: 190, viewBox: `0 0 ${w} 190`, class: 'me-staff' });
    const step = (m) => { const o = Math.floor(m / 12) - 1, i = [0, 0, 1, 1, 2, 3, 3, 4, 4, 5, 5, 6][m % 12]; return o * 7 + i; }, base = step(64);
    for (let i = 0; i < 5; i++) svg('line', { x1: 14, x2: w - 8, y1: 70 + i * 14, y2: 70 + i * 14, class: 'stl' }, s);
    const g = svg('text', { x: 18, y: 118, class: 'clef' }, s); g.textContent = '𝄞';
    for (let b = 0; b * beatsPerBar() <= st.total; b++) svg('line', { x1: xOf(b * beatsPerBar()), x2: xOf(b * beatsPerBar()), y1: 70, y2: 126, class: 'stl' }, s);
    for (const n of st.notes) {
      const x = xOf(n.at) + 12, y = 126 - (step(n.midi) - base) * 7;
      for (let ly = 126 + 14; ly <= y; ly += 14) svg('line', { x1: x - 12, x2: x + 12, y1: ly, y2: ly, class: 'stl' }, s);
      for (let ly = 70 - 14; ly >= y; ly -= 14) svg('line', { x1: x - 12, x2: x + 12, y1: ly, y2: ly, class: 'stl' }, s);
      const head = svg('ellipse', { cx: x, cy: y, rx: 8.5, ry: 6.5, class: `nhead${n.len >= 2 ? ' open' : ''}${n === st.sel ? ' sel' : ''}`, transform: `rotate(-20 ${x} ${y})` }, s);
      head.addEventListener('pointerdown', () => { st.sel = n; blip(n.midi); draw(); });
      if (n.len < 4) svg('line', { x1: x + 7.5, x2: x + 7.5, y1: y, y2: y - 34, class: 'nstem' }, s);
      if (NOTE_NAMES[n.midi % 12].includes('#')) { const t = svg('text', { x: x - 22, y: y + 5, class: 'acc' }, s); t.textContent = '♯'; }
    }
    return s;
  }
  function listView() {
    const t = el('table', { className: 'me-list' }, [el('tr', {}, ['#', 'Bar', 'Beat', 'Note', 'Length', 'Velocity'].map((h) => el('th', { textContent: h })))]);
    [...st.notes].sort((a, b) => a.at - b.at).forEach((n, i) => {
      const per = beatsPerBar();
      const tr = el('tr', { className: n === st.sel ? 'sel' : '', 'data-n': String(st.notes.indexOf(n)), onclick: () => { st.sel = n; blip(n.midi); draw(); } }, [i + 1, Math.floor(n.at / per) + 1, fmt(n.at % per + 1), nameOf(n.midi), fmt(n.len), effectiveVelocity(n, st.marks)].map((c) => el('td', { textContent: String(c) })));
      t.append(tr);
    });
    return t;
  }

  // ---- chords ----
  // the key of the song when it says one, else the first chord is taken as the tonic (so Dm first reads as i)
  const keyRoot = () => { const m = /^\s*([A-G][#b]?)\s*(m|minor|major)?/i.exec(String(S.doc?.project?.key ?? '')); if (m) return pitchClassOf(m[1][0].toUpperCase() + m[1].slice(1)); const first = st.bars[0]?.[0]; return first && first.raw === undefined ? pitchClassOf(first.root) : 0; };
  function chordsView() {
    const wrap = el('div', { className: 'me-chords' });
    const minor = /m(inor)?$/i.test(String(S.doc?.project?.key ?? '')) || (st.bars[0]?.[0]?.quality ?? '').startsWith('m');
    const ribbon = el('div', { className: 'me-ribbon' });
    st.bars.forEach((bar, i) => ribbon.append(el('button', { type: 'button', className: `me-cb${i === st.csel ? ' sel' : ''}`, onclick: () => { st.csel = i; playChord(bar); draw(); } }, [
      el('b', { textContent: bar.map(chordName).join(' . ') }), el('small', { textContent: bar.map((c) => roman(c, keyRoot(), minor)).join(' . ') || ' ' }), el('i', { textContent: `bar ${i + 1}` })])));
    [...ribbon.children].forEach((cell, i) => {
      cell.draggable = !ro();
      cell.ondragstart = (e) => { e.dataTransfer.setData('text/plain', String(i)); e.dataTransfer.effectAllowed = 'move'; };
      cell.ondragover = (e) => { e.preventDefault(); cell.classList.add('over'); };
      cell.ondragleave = () => cell.classList.remove('over');
      cell.ondrop = (e) => {
        e.preventDefault(); const from = Number(e.dataTransfer.getData('text/plain'));
        if (!Number.isInteger(from) || from === i || ro()) { draw(); return; }
        const [moved] = st.bars.splice(from, 1); st.bars.splice(i, 0, moved); st.csel = i; commit(); draw();
      };
    });
    ribbon.append(el('button', { type: 'button', className: 'me-cb add', textContent: '+', title: 'Add a bar', onclick: () => { if (ro()) return; const last = st.bars[st.csel] ?? [parseChord('C')]; st.bars.splice(st.csel + 1, 0, last.map((c) => ({ ...c }))); st.csel++; commit(growBars()); draw(); } }));
    wrap.append(ribbon);
    const cur = st.bars[st.csel]?.[0];
    const classes = cur ? chordClasses(cur) : [];
    const kb = el('div', { className: 'me-kb' });
    for (const o of [3, 4, 5]) NOTE_NAMES.forEach((n, k) => kb.append(el('i', { className: `${n.includes('#') ? 'b ' : ''}${classes.includes(k) && o === 4 ? 'lit' : ''}`, title: n + o, onclick: () => blip(12 * (o + 1) + k) })));
    wrap.append(kb, el('p', { className: 'me-hint', textContent: cur?.raw !== undefined ? `"${cur.raw}" is not a chord name AMPLE shows here; it stays as it is.` : 'Click a bar to hear and see its chord; change it on the right. Two chords in one bar are written with a dot (Am.F).' }));
    return wrap;
  }
  const growBars = () => (st.bars.length > bars() ? [{ type: 'setParameter', block: blockId, name: 'bars', value: String(st.bars.length) }] : []);
  function playChord(bar) { for (const c of bar) chordClasses(c).forEach((pc, j) => { try { ctx = ctx || liveAudioContext(); voice(60 + ((pc + 12) % 12) + (j === 0 && c.bass ? -12 : 0), ctx.currentTime + j * 0.03, 0.9, 80); } catch { /* sound blocked */ } }); }

  /** Rests longer than st.shrinkOver beats become their length divided by st.shrinkDiv (a quarter beat at the least, never below 1 beat); notes and dynamics words after them move forward. Returns the beats removed. */
  function shortenRests() {
    const order = [...st.notes].sort((a, b) => a.at - b.at), cuts = [];
    let end = 0, removed = 0;
    for (const n of order) {
      const gap = n.at - end;
      if (gap > st.shrinkOver + 1e-9) { const want = Math.max(1, Math.round(gap / st.shrinkDiv * 4) / 4); if (want < gap) removed += gap - want; }
      cuts.push({ at: n.at, removed });
      end = n.at + n.len;
    }
    if (!removed) return 0;
    const before = new Map(order.map((n, i) => [n, cuts[i].removed]));
    for (const n of order) n.at = Math.round((n.at - before.get(n)) * 1e6) / 1e6;
    st.marks = st.marks.map((m) => { const next = cuts.find((c) => c.at >= m.at - 1e-9); return { ...m, at: Math.max(0, Math.round((m.at - (next ? next.removed : removed)) * 1e6) / 1e6) }; });
    return removed;
  }

  // ---- inspector ----
  const field = (label, control) => el('div', { className: 'me-f' }, [el('span', { textContent: label }), control]);
  const input = (value, on) => { const i = el('input', { value: String(value) }); i.onchange = () => on(i.value.trim()); return i; };
  function drawInspector() {
    inspector.replaceChildren();
    if (st.mode === 'melody') {
      const n = st.sel;
      inspector.append(el('h4', { textContent: 'Note' }));
      if (n) {
        const per = beatsPerBar();
        inspector.append(
          field('Note', input(nameOf(n.midi), (v) => { const m = midiOf(v); if (m != null && m >= 0 && m < 128) { n.midi = m; blip(m); commit(); draw(); } })),
          field('Start', input(fmt(n.at + 1), (v) => { const at = Math.max(0, Number(v) - 1); if (Number.isFinite(at) && !overlaps(st.notes, at, n.len, n)) { n.at = at; commit(); draw(); } })),
          field('Length', input(fmt(n.len), (v) => { const len = Number(v); if (len > 0 && !overlaps(st.notes, n.at, len, n)) { n.len = len; commit(); draw(); } })),
          field('Velocity', input(n.vel ?? '', (v) => { n.vel = v === '' ? null : Math.max(1, Math.min(127, Number(v) || DEFAULT_VELOCITY)); commit(); draw(); })),
          field('Bar', el('span', { textContent: `${Math.floor(n.at / per) + 1}, beat ${fmt(n.at % per + 1)}` })));
        const dyn = el('select', {}, ['', ...Object.keys(LEVELS)].map((v) => el('option', { value: v, textContent: v || 'none' })));
        dyn.value = st.marks.find((m) => Math.abs(m.at - n.at) < 1e-9 && LEVELS[m.text] !== undefined)?.text ?? '';
        dyn.onchange = () => { st.marks = st.marks.filter((m) => !(Math.abs(m.at - n.at) < 1e-9 && LEVELS[m.text] !== undefined)); if (dyn.value) st.marks.push({ at: n.at, text: dyn.value }); commit(); draw(); };
        inspector.append(field('Dynamic', dyn));
      } else inspector.append(el('p', { className: 'me-dim', textContent: 'Click a note to change it, or click an empty cell to draw one.' }));
      inspector.append(el('h4', { textContent: 'Whole melody' }));
      const act = (label, fn, tip) => el('button', { type: 'button', textContent: label, title: tip || '', onclick: () => { if (ro()) { toast('This song is read-only.'); return; } fn(); commit(); draw(); } });
      inspector.append(el('div', { className: 'me-acts' }, [
        act('Up 1', () => st.notes.forEach((n) => { n.midi = Math.min(127, n.midi + 1); }), 'Transpose a semitone up'), act('Down 1', () => st.notes.forEach((n) => { n.midi = Math.max(0, n.midi - 1); })),
        act('Octave +', () => st.notes.forEach((n) => { n.midi = Math.min(127, n.midi + 12); })), act('Octave -', () => st.notes.forEach((n) => { n.midi = Math.max(0, n.midi - 12); })),
        act('Legato', () => { const s = [...st.notes].sort((a, b) => a.at - b.at); s.forEach((n, i) => { n.len = (s[i + 1] ? s[i + 1].at : n.at + n.len) - n.at; }); }, 'Every note lasts until the next one'),
        act('Clear', () => { st.notes = []; st.marks = []; st.sel = null; st.trailing = 0; })]));
      // long rests made short (the notes after a long rest move forward; one undo step)
      const over = el('select', { title: 'Only rests longer than this are shortened', 'aria-label': 'Rests longer than' }, [1, 2, 4, 8, 16].map((v) => el('option', { value: String(v), textContent: v + ' beats' }))); over.value = String(st.shrinkOver); over.onchange = () => { st.shrinkOver = Number(over.value); };
      const div = el('select', { title: 'How much shorter', 'aria-label': 'Shorten by' }, [2, 4, 10, 20].map((v) => el('option', { value: String(v), textContent: 'divide by ' + v }))); div.value = String(st.shrinkDiv); div.onchange = () => { st.shrinkDiv = Number(div.value); };
      inspector.append(el('h4', { textContent: 'Shorten long rests' }), field('Longer than', over), field('Shorten', div), el('div', { className: 'me-acts' }, [
        el('button', { type: 'button', textContent: 'Shorten rests', title: 'Make every rest longer than the chosen length shorter (the notes after it move forward). Undo brings the time back.', onclick: () => { if (ro()) { toast('This song is read-only.'); return; } const cut = shortenRests(); if (!cut) { toast('There is no rest longer than ' + st.shrinkOver + ' beats.'); return; } commit(); draw(); toast('Shortened by ' + fmt(cut) + ' beats.'); } })]));
    } else {
      const bar = st.bars[st.csel];
      inspector.append(el('h4', { textContent: `Bar ${st.csel + 1}` }));
      if (bar) {
        bar.forEach((c, k) => {
          if (c.raw !== undefined) { inspector.append(field('Chord', input(c.raw, (v) => { bar[k] = parseChord(v); commit(); draw(); }))); return; }
          const root = el('select', {}, NOTE_NAMES.map((n) => el('option', { value: n, textContent: n }))); root.value = c.root;
          root.onchange = () => { c.root = root.value; playChord([c]); commit(); draw(); };
          const q = el('select', {}, QUALITY_CHOICES.map(([v, label]) => el('option', { value: v, textContent: label }))); if (![...q.options].some((o) => o.value === c.quality)) q.append(el('option', { value: c.quality, textContent: c.quality })); q.value = c.quality;
          q.onchange = () => { c.quality = q.value; playChord([c]); commit(); draw(); };
          const bass = el('select', {}, [el('option', { value: '', textContent: 'root' }), ...NOTE_NAMES.map((n) => el('option', { value: n, textContent: n }))]); bass.value = c.bass; bass.onchange = () => { c.bass = bass.value; playChord([c]); commit(); draw(); };
          inspector.append(field(bar.length > 1 ? `Chord ${k + 1}` : 'Root', root), field('Quality', q), field('Lowest', bass));
        });
        const act = (label, fn) => el('button', { type: 'button', textContent: label, onclick: () => { if (ro()) { toast('This song is read-only.'); return; } fn(); commit(growBars()); draw(); } });
        inspector.append(el('div', { className: 'me-acts' }, [
          act(bar.length > 1 ? 'One chord' : 'Split bar', () => { if (bar.length > 1) bar.length = 1; else bar.push({ ...bar[0] }); }),
          act('Duplicate', () => { st.bars.splice(st.csel + 1, 0, bar.map((c) => ({ ...c }))); st.csel++; }),
          act('Delete', () => { st.bars.splice(st.csel, 1); st.csel = Math.max(0, st.csel - 1); }),
          act('Left', () => { if (st.csel > 0) { [st.bars[st.csel - 1], st.bars[st.csel]] = [st.bars[st.csel], st.bars[st.csel - 1]]; st.csel--; } }),
          act('Right', () => { if (st.csel < st.bars.length - 1) { [st.bars[st.csel + 1], st.bars[st.csel]] = [st.bars[st.csel], st.bars[st.csel + 1]]; st.csel++; } })]));
      }
    }
    if (hasMelody && hasChords) inspector.append(el('p', { className: 'me-dim', textContent: 'This clip has both lines: switch between them above.' }));
    inspector.append(el('p', { className: 'me-dim', textContent: 'Ctrl+Z undoes the last change, also here.' }));
  }

  // ---- draw everything ----
  /** The block that sounds at this beat (or none, -1): the chord bar, or the note, gets the class "playing" in whichever view is shown. */
  function markPlaying(b) {
    const per = beatsPerBar();
    if (st.mode === 'chords') { const at = b < 0 ? -1 : Math.floor(b / per); body.querySelectorAll('.me-cb:not(.add)').forEach((c, i) => c.classList.toggle('playing', i === at)); return; }
    const on = new Set(); if (b >= 0) st.notes.forEach((n, i) => { if (n.at <= b + 1e-9 && b < n.at + n.len) on.add(String(i)); });
    body.querySelectorAll('[data-n]').forEach((e) => e.classList.toggle('playing', on.has(e.getAttribute('data-n'))));
  }
  const moveHead = () => { const h = body.querySelector('#me-head'); if (h) { h.setAttribute('x1', xOf(st.head)); h.setAttribute('x2', xOf(st.head)); } else if (st.mode === 'melody' && st.view === 'roll') { const r = body.querySelector('.me-roll'); if (r) svg('line', { id: 'me-head', x1: xOf(st.head), x2: xOf(st.head), y1: 0, y2: 22 + H, class: 'head' }, r); } };
  function draw(full = true) {
    toolbar.replaceChildren(playBtn, speedSel());
    if (hasMelody && hasChords) toolbar.append(mkSeg([['melody', 'Melody'], ['chords', 'Chords']], () => st.mode, (v) => { stopPlay(); st.mode = v; load(); }));
    if (st.mode === 'melody') {
      toolbar.append(mkSeg([['draw', 'Draw', 'Click to add a note, drag its edge for the length'], ['select', 'Select'], ['erase', 'Erase', 'Click a note to remove it']], () => st.tool, (v) => { st.tool = v; }));
      const grid = el('select', { title: 'Grid: the smallest step of a note' }, [['1', '1/4'], ['0.5', '1/8'], ['0.25', '1/16'], ['0.125', '1/32']].map(([v, l]) => el('option', { value: v, textContent: l }))); grid.value = String(st.grid); grid.onchange = () => { st.grid = Number(grid.value); draw(); };
      toolbar.append(el('label', { textContent: 'Grid' }), grid, el('span', { className: 'sp' }), mkSeg([['roll', 'Piano roll'], ['staff', 'Notation'], ['list', 'List']], () => st.view, (v) => { st.view = v; }));
    }
    toolbar.append(el('button', { type: 'button', textContent: 'Undo', onclick: () => undo() }), el('button', { type: 'button', textContent: 'Redo', onclick: () => redo() }));
    body.replaceChildren();
    if (st.problem) body.append(el('div', { className: 'me-problem' }, [el('p', { textContent: `The editor cannot show this ${st.mode} yet: ${st.problem}.` }), el('p', { textContent: 'Fix it in the source; the text was not changed.' })]));
    else if (st.mode === 'chords') body.append(chordsView());
    else if (st.view === 'roll') {
      const sc = el('div', { className: 'me-scroll' }, [rollView()]), vw = el('div', { className: 'me-velwrap' }, [velocityView()]);
      body.append(sc, vw);
      if (full && !draw.scrolled) {   // the first time: the view opens where the notes are (high enough, and at the first note), not at the start of a long rest
        sc.scrollTop = Math.max(0, yOf(st.notes.length ? Math.max(...st.notes.map((n) => n.midi)) : 72) - 40);
        draw.top = sc.scrollTop; draw.left = Math.max(0, xOf(startBeat()) - KEYS - 20); draw.scrolled = true;
      }
      sc.scrollTop = draw.top ?? 0; sc.scrollLeft = vw.scrollLeft = draw.left ?? 0;
      sc.onscroll = () => { draw.top = sc.scrollTop; draw.left = sc.scrollLeft; vw.scrollLeft = sc.scrollLeft; };
      vw.onscroll = () => { if (Math.abs(vw.scrollLeft - sc.scrollLeft) > 1) sc.scrollLeft = vw.scrollLeft; };
    }
    else if (st.view === 'staff') body.append(el('div', { className: 'me-scroll' }, [staffView()]));
    else body.append(el('div', { className: 'me-scroll' }, [listView()]));
    const text = st.mode === 'melody' ? serializeMelody({ notes: st.notes, marks: st.marks }, { trailingRest: st.trailing }) : serializeChords(st.bars);
    textLine.replaceChildren(el('code', { textContent: st.mode }), el('span', { className: 'me-val', textContent: `"${text}"` }), el('button', { type: 'button', textContent: 'Copy', onclick: () => navigator.clipboard?.writeText(text).then(() => toast('Copied.'), () => toast('Copying did not work here.')) }));
    drawInspector();
  }
  load();
  draw();
  return panel;
}
