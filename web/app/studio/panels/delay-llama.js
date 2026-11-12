// The Delay Llama window: the singing llama (optional: the character setting of the block hides it), a vowel pad (left to right a e i o u, bottom to top a closed to an
// open mouth) that the llama follows at once, the knobs of the voice, and Sing: the notes of the clip on its input sung in the browser by the same voice as the engine,
// with the llama singing along in time. The look of the llama (its colours, sitting or standing) is a choice of this browser only: the song does not change. @ample:delay-llama

import { getContext } from '../../../lib/audio-engine.mjs';
import { el } from '../../ui.js';
import { S, flat } from '../../store.js';
import { registerStudioPanel } from './registry.js';
import { onPlayer, peaksOf, stop as stopSong } from '../../player.js';
import { knob } from '../knob.js';
import { selector } from '../selector.js';
import { auraOf, createLlama, SCHEMES } from '../../llama-art.js';
import { llamaCurve, llamaNotes, llamaSettings, renderLlama } from '../../../lib/clientside-delay-llama.mjs';
import { llamaLive, llamaTweaks, tweakLlama } from '../../llama-live.js';
import { createVirtualKeyboard } from '../../virtual-keyboard.js';
import { midiActive, midiCan, midiNames, onMidiNote, onMidiState, restoreMidi, toggleMidi } from '../../midi-hub.js';
import '../../keys-play.js';
import { registerReceiver } from '../../keys-router.js';
import { singerOf } from '../../llama-avatar.js';
import { noteName } from '../../../lib/clientside-autotune.mjs';
import { parse, absolute } from '../../../lib/notes-model.mjs';
import { barBeats } from '../../../lib/meter.mjs';

const clean = (v) => String(v ?? '').replace(/^"|"$/g, '').trim();
const live = { ctx: null, source: null, timer: 0, stop: null };
// the keys of the window and a MIDI input sing the voice of this block; the MIDI input lives outside the window (it is built again after every change of the song) and plays the window that is shown now
const keysNow = { on: null, off: null, lit: null };
// the last recording of each block: writing the curves rebuilds the window, so what Undo needs lives here and not in the window
const recorded = new Map();
onPlayer((state) => { if (state.playing && live.source) live.stop?.(); });
const remembered = (key, fallback) => { try { return localStorage.getItem(key) || fallback; } catch { return fallback; } };
const remember = (key, value) => { try { localStorage.setItem(key, value); } catch { /* storage can be unavailable */ } };

function delayLlamaPanel(context) {
  const root = el('section', { className: 'studio-effect-panel studio-llama-panel', 'aria-label': 'Delay Llama' });
  const showLlama = clean(context.params.character) !== 'off';
  let scheme = remembered('ample.llama.scheme', 'trans'), pose = remembered('ample.llama.pose', 'sit');
  if (!SCHEMES[scheme]) scheme = 'trans';   // a look that does not exist any more (the old default) becomes the new one
  const stageBox = el('div', { className: 'llama-stage' });
  let llama = null;
  const placeLlama = () => { llama?.destroy(); llama = createLlama({ size: 210, scheme, pose }); stageBox.style.background = auraOf(scheme); stageBox.replaceChildren(llama.element); face(); };
  const vowelOf = () => { const t = clean(context.params.vowel); return /^[aeiou]$/i.test(t) ? 'aeiou'.indexOf(t.toLowerCase()) / 4 : Number.isFinite(Number.parseFloat(t)) ? Number.parseFloat(t) / 100 : 0.5; };
  const mouthOf = () => { const v = Number.parseFloat(clean(context.params.mouth)); return Number.isFinite(v) ? v / 100 : 0.6; };
  // the face at rest follows the vowel and the mouth of the block
  const face = (singing = false) => llama?.set({ pitch: singing ? 62 : null, vowel: vowelOf(), mouth: mouthOf(), level: singing ? 0.8 : 0, vibrato: llamaSettings(context.params).vibrato });

  // ---- the pad: x is the vowel, y the mouth. Four modes: View (the dot follows the voice while it sings, read only), Play (hold it to sing a note with it),
  // Rec (press Sing and move: your moves are written as vowel and mouth curves) and Set (click or drag for a fixed vowel and mouth)
  let mode = remembered('ample.llama.padmode', 'view');
  if (context.readOnly) mode = 'view';
  let playNote = Number(remembered('ample.llama.padnote', '57')) || 57;
  const pad = el('div', { className: 'llama-pad', role: 'slider', tabIndex: 0, 'aria-label': 'Vowel (left to right a e i o u) and mouth (bottom closed, top open)' });
  const dot = el('i', { className: 'llama-pad-dot' });
  pad.append(...['a', 'e', 'i', 'o', 'u'].map((v, i) => el('span', { className: 'llama-pad-vowel', textContent: v, style: `left:${i * 25}%` })), el('span', { className: 'llama-pad-axis', textContent: 'mouth ↑' }), dot);
  const placeDot = (vowel, mouth) => { dot.style.left = `${vowel * 100}%`; dot.style.top = `${(1 - mouth) * 100}%`; };
  const automated = (name) => /^\s*(auto|lfo|env)\(/.test(clean(context.params[name]));
  placeDot(vowelOf(), mouthOf());
  const HINTS = {
    view: 'The dot shows the vowel and the mouth of the voice while it sings (press Sing). It only looks; it changes nothing.',
    play: 'Hold the pad to sing the note below with that vowel and mouth. Nothing is written.',
    rec: 'Record a performance: 1. Press Record (it counts in three beats and the clip starts to sing). 2. Hold the pad and move: left to right is the vowel, up and down the mouth. 3. When the clip ends your moves are written into the block as curves. Undo brings the old values back.',
    set: 'Click or drag to set a fixed vowel and mouth in the block (a curve is left alone).',
  };
  const modeButtons = ['view', 'play', 'rec', 'set'].map((id) => el('button', { type: 'button', textContent: { view: 'View', play: 'Play', rec: '● Rec', set: 'Set' }[id], 'data-mode': id, disabled: context.readOnly && id !== 'view', title: HINTS[id], onclick: () => { mode = id; remember('ample.llama.padmode', id); paint(); } }));
  const modeBar = el('div', { className: 'llama-modes', role: 'group', 'aria-label': 'What the pad does' }, modeButtons);
  const hint = el('small', { className: 'llama-pad-hint' });
  const noteNames = Array.from({ length: 40 }, (_, i) => 45 + i);
  const noteSelect = selector({ label: 'Note to sing', value: String(playNote), choices: noteNames.map((n) => ({ value: String(n), label: noteName(n) })), onCommit: (value) => { playNote = Number(value); remember('ample.llama.padnote', value); } });
  const noteLabel = el('label', { className: 'llama-pad-note' }, [el('span', { textContent: 'Note' }), noteSelect]);
  let paintSing = () => {};
  const paint = () => {
    for (const b of modeButtons) b.setAttribute('aria-pressed', String(b.dataset.mode === mode));
    hint.textContent = HINTS[mode];
    noteLabel.hidden = mode !== 'play';
    pad.dataset.mode = mode;
    paintSing();
    pad.style.cursor = mode === 'view' ? 'default' : 'crosshair';
  };

  // Play: the live voice (the worklet) sings the chosen note while the pointer is down; vowel and mouth under the pointer follow at once, nothing is rendered in advance
  const player = { voice: null, held: null };
  const playSet = (p) => player.voice?.set({ ...llamaSettings(context.params), vowel: p.vowel, mouth: p.mouth });
  const playStart = async (p) => {
    live.ctx ||= getContext();
    live.ctx.resume?.();
    const voice = await llamaLive(live.ctx);
    voice.connect(live.ctx.destination);
    player.voice = voice; player.held = playNote;
    playSet(p); voice.noteOn(playNote, 100);
    if (!dragging) playStop();   // let go before the worklet was there
  };
  const playMove = (p) => playSet(p);
  const playStop = () => { if (player.voice && player.held !== null) player.voice.noteOff(player.held); player.held = null; };

  // Rec: while the clip sings, the position of the pad is sampled; when it stops the moves become two curves in bars of the song
  const rec = { on: false, points: [], info: null, cur: null, timer: 0 };
  // while it records, the clip is sung LIVE by the worklet and the pad turns its vowel and mouth at once, so what you do is what you hear
  let recordVoice = null;
  const recLive = (p) => recordVoice?.set({ ...llamaSettings(context.params), vowel: p.vowel, mouth: p.mouth });
  const curveText = (points, key, info) => {
    const kept = [];
    for (const p of points) {
      const value = Math.round(p[key] * 100), bar = Math.round((info.firstBar + p.t * info.tempo / (barBeats() * 60)) * 100) / 100;
      const last = kept.at(-1);
      if (!last || Math.abs(value - last.value) >= 4 || p === points.at(-1)) kept.push({ bar: last && bar <= last.bar ? last.bar + 0.01 : bar, value });
    }
    return kept.length > 1 ? `auto(${kept.map((k) => `${Math.max(1, k.bar)}:${k.value}`).join(', ')})` : `${kept[0]?.value ?? 50}`;
  };
  const undo = el('button', { type: 'button', className: 'llama-undo', textContent: '↶ Undo recording', hidden: true, title: 'Bring back the vowel and mouth the block had before this recording' });
  const clearTrail = () => pad.querySelectorAll('.llama-pad-trail').forEach((n) => n.remove());
  const padApi = {
    // the voice sings: the dot follows it (except in Rec, where it shows your own moves)
    follow(vowel, mouth) { if (mode === 'view' || (mode !== 'rec' && !dragging)) placeDot(vowel, mouth); },
    begin(info) {
      if (mode !== 'rec' || context.readOnly) return;
      rec.on = true; rec.points = []; rec.info = { ...info }; rec.cur = null; rec.before = { vowel: context.params.vowel ?? null, mouth: context.params.mouth ?? null };
      undo.hidden = true; clearTrail();
      pad.classList.add('recording');
      rec.timer = setInterval(() => {
        const t = info.ctx.currentTime - info.started;
        status.textContent = '● REC ' + Math.floor(t / 60) + ':' + String(Math.floor(t % 60)).padStart(2, '0') + (rec.cur ? '' : ' · hold the pad and move');
        if (!rec.cur) return;
        rec.points.push({ t, vowel: rec.cur.vowel, mouth: rec.cur.mouth });
        pad.insertBefore(el('i', { className: 'llama-pad-trail', style: 'left:' + rec.cur.vowel * 100 + '%;top:' + (1 - rec.cur.mouth) * 100 + '%' }), dot);
      }, 120);
    },
    end() {
      clearInterval(rec.timer);
      pad.classList.remove('recording');
      if (!rec.on) return;
      rec.on = false;
      setTimeout(clearTrail, 1500);
      if (rec.points.length < 2) { status.textContent = 'Nothing was recorded: hold the pad and move it while the clip sings. Press Record to try again.'; return; }
      const values = [{ name: 'vowel', value: curveText(rec.points, 'vowel', rec.info) }, { name: 'mouth', value: curveText(rec.points, 'mouth', rec.info) }];
      recorded.set(context.block, { before: rec.before, seconds: Math.round(rec.points.at(-1).t) });
      for (const v of values) context.params[v.name] = v.value;
      if (context.commitMany) context.commitMany(values); else values.forEach((v) => context.commit(v.name, v.value));
      status.textContent = 'Recorded ' + Math.round(rec.points.at(-1).t) + ' s: vowel and mouth are now curves in this block.';
      undo.hidden = false;
    },
    undo() {
      const before = recorded.get(context.block)?.before ?? rec.before;
      if (!before) return;
      recorded.delete(context.block);
      const back = [];
      for (const name of ['vowel', 'mouth']) back.push({ name, value: before[name] ?? { vowel: '50', mouth: '60' }[name] });
      for (const v of back) context.params[v.name] = v.value;
      if (context.commitMany) context.commitMany(back); else back.forEach((v) => context.commit(v.name, v.value));
      undo.hidden = true; status.textContent = 'The old vowel and mouth are back.'; face(); placeDot(vowelOf(), mouthOf());
    },
  };
  undo.onclick = () => padApi.undo();
  // the count-in of a recording: three beats at the tempo of the song, shown on the pad
  const countIn = () => new Promise((resolve) => {
    const beat = 60000 / (Number(S.doc?.project?.tempo) || 120), shown = el('div', { className: 'llama-count' });
    pad.append(shown);
    let n = 3;
    const tick = () => { if (n === 0) { shown.remove(); resolve(); return; } shown.textContent = String(n); n--; setTimeout(tick, beat); };
    tick();
  });

  let dragging = false;
  const at = (event) => { const box = pad.getBoundingClientRect(); return { vowel: Math.min(1, Math.max(0, (event.clientX - box.left) / box.width)), mouth: Math.min(1, Math.max(0, 1 - (event.clientY - box.top) / box.height)) }; };
  const show = (p) => { placeDot(p.vowel, p.mouth); llama?.set({ pitch: mode === 'play' ? playNote : 62, vowel: p.vowel, mouth: p.mouth, level: 0.85 }); };
  pad.onpointerdown = (event) => {
    if (mode === 'view') return;
    dragging = true; pad.setPointerCapture(event.pointerId);
    const p = at(event); show(p);
    if (mode === 'rec') { rec.cur = p; recLive(p); }
    if (mode === 'play') playStart(p);
  };
  pad.onpointermove = (event) => {
    if (!dragging) return;
    const p = at(event); show(p);
    if (mode === 'rec') { rec.cur = p; recLive(p); }
    if (mode === 'play') playMove(p);
  };
  pad.onpointerup = (event) => {
    if (!dragging) return;
    dragging = false;
    playStop();
    const p = at(event);
    if (mode === 'set' && !context.readOnly) {
      const values = [];
      if (!automated('vowel')) values.push({ name: 'vowel', value: String(Math.round(p.vowel * 100)) });
      if (!automated('mouth')) values.push({ name: 'mouth', value: String(Math.round(p.mouth * 100)) });
      for (const v of values) context.params[v.name] = v.value;
      if (values.length) { if (context.commitMany) context.commitMany(values); else values.forEach((v) => context.commit(v.name, v.value)); }
    }
    if (mode !== 'rec') face();
    if (mode === 'view' || mode === 'play') placeDot(vowelOf(), mouthOf());
  };
  paint();

  // ---- the knobs
  const numberOf = (name, fallback) => { const v = Number.parseFloat(clean(context.params[name])); return Number.isFinite(v) ? v : fallback; };
  const dial = (name, label, value, min, max, unit, write) => el('div', { className: 'studio-panel-control llama-dial' }, [knob({
    label, value, min, max, unit, size: 58, step: 1, disabled: context.readOnly || automated(name),
    // turning a knob is heard at once, also while it plays (Jam, Sing, a recording): the live voices take the new value before it is written into the song
    onPreview: (next) => { context.params[name] = write(Math.round(next)); tweakLlama(context.block, { [name]: write(Math.round(next)) }); if (name === 'vibrato') face(); },
    onCommit: (next) => { context.params[name] = write(Math.round(next)); tweakLlama(context.block, { [name]: write(Math.round(next)) }); context.commit(name, write(Math.round(next))); },
  })]);
  const knobs = el('div', { className: 'llama-knobs' }, [
    dial('voice', 'Voice', numberOf('voice', 50), 0, 100, '%', (v) => String(v)),
    dial('glide', 'Glide', numberOf('glide', 60), 0, 600, 'ms', (v) => `${v} ms`),
    dial('vibrato', 'Vibrato', numberOf('vibrato', 30), 0, 100, '%', (v) => `${v}%`),
    dial('breath', 'Breath', numberOf('breath', 50), 0, 100, '%', (v) => `${v}%`),
    dial('unison', 'Unison', numberOf('unison', 1), 1, 7, '', (v) => String(v)),
    dial('echo', 'Echo', numberOf('echo', 0), 0, 100, '%', (v) => `${v}%`),
    dial('gain', 'Level', numberOf('gain', -6), -36, 6, 'dB', (v) => `${v} dB`),
    dial('leveler', 'Leveler', numberOf('leveler', 50), 0, 100, '%', (v) => `${v}%`),
  ]);

  // ---- Sing: the notes of the clip on the input, sung in the browser, the llama in time with it
  const status = el('output', { className: 'llama-status' });
  const sing = el('button', { type: 'button', className: 'primary llama-sing', textContent: '▶ Sing', title: 'Sing the notes of the clip on the input (the first 8 bars), in the browser' });
  const idleLabel = () => (mode === 'rec' ? '● Record' : '▶ Sing');
  paintSing = () => { if (!live.source && !sing.dataset.busy) { sing.textContent = idleLabel(); sing.classList.toggle('rec', mode === 'rec'); sing.title = mode === 'rec' ? 'Record your moves on the pad while the clip sings (counts in three beats)' : 'Sing the notes of the clip on the input (the first 8 bars), in the browser'; } };
  const stopLive = () => { try { live.source?.stop(); } catch { /* already stopped */ } live.source = null; recordVoice?.allOff(); recordVoice = null; clearInterval(live.timer); live.timer = 0; strip.pos = strip.from; drawStrip(); sing.textContent = idleLabel(); padApi.end(); face(); };
  live.stop = stopLive;
  // ---- the strip: the waveform of the block as built (else the notes of the clip), the place Sing starts from, and the playhead that runs while it sings or records; click or drag to move
  const strip = { from: 0, pos: 0, peaks: null, total: 0, drag: false, width: 600, height: 76, el: el('canvas', { className: 'llama-strip', 'aria-label': 'The block as built: click or drag to start singing from another place', title: 'Click or drag to start from another place; while it sings the line follows it' }) };
  const stripSeconds = () => strip.peaks?.seconds || strip.total || 8;
  function drawStrip() {
    const c = strip.el, g = c.getContext('2d'), dpr = c.width / strip.width, w = strip.width, h = strip.height, styles = getComputedStyle(document.documentElement);
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.fillStyle = styles.getPropertyValue('--panel2').trim() || '#222'; g.fillRect(0, 0, w, h);
    const accent = styles.getPropertyValue('--acc').trim() || '#4ad6ff', dur = stripSeconds();
    if (strip.peaks?.peaks?.length) {
      const peaks = strip.peaks.peaks, n = peaks.length / 2, mid = h / 2;
      g.fillStyle = accent; g.globalAlpha = 0.85;
      for (let x = 0; x < w; x++) { const i = Math.min(n - 1, Math.floor(x / w * n)); g.fillRect(x, mid - peaks[i * 2 + 1] * (mid - 4), 1, Math.max(1, (peaks[i * 2 + 1] - peaks[i * 2]) * (mid - 4))); }
      g.globalAlpha = 1;
    } else {
      const singer = singerOf(context.block), tempo = Number(S.doc?.project?.tempo) || 120;
      if (singer?.notes.length) {
        const pitches = singer.notes.map((n) => n.pitch), lo = Math.min(...pitches) - 1, hi = Math.max(...pitches) + 1;
        g.fillStyle = accent; g.globalAlpha = 0.8;
        for (const n of singer.notes) g.fillRect(n.at / singer.ppq * 60 / tempo / dur * w, h - ((n.pitch - lo) / Math.max(1, hi - lo)) * (h - 10) - 8, Math.max(2, n.length / singer.ppq * 60 / tempo / dur * w), 5);
        g.globalAlpha = 1;
      } else { g.fillStyle = 'rgba(255,255,255,.5)'; g.font = '12px system-ui'; g.fillText('Build the song to see the block here', 10, h / 2 + 4); }
    }
    // where it starts from (dim) and the playhead (white, with a flag)
    const x0 = Math.round(strip.from / dur * w), x1 = Math.round(strip.pos / dur * w);
    g.fillStyle = 'rgba(255,255,255,.28)'; g.fillRect(x0, 0, 1, h);
    g.fillStyle = '#fff'; g.fillRect(x1 - 1, 0, 2, h); g.beginPath(); g.moveTo(x1 - 6, 0); g.lineTo(x1 + 6, 0); g.lineTo(x1, 8); g.fill();
  }
  const refreshStrip = () => {
    const singer = singerOf(context.block), tempo = Number(S.doc?.project?.tempo) || 120;
    strip.total = singer?.notes.length ? Math.max(...singer.notes.map((n) => n.at + n.length)) / singer.ppq * 60 / tempo + 1 : 0;
    // it starts at the first note of the clip (a little before it) until a place is chosen on the strip, not at the beginning of the song where nothing sings yet
    if (!strip.chosen && singer?.notes.length) { strip.from = strip.pos = Math.max(0, Math.min(...singer.notes.map((n) => n.at)) / singer.ppq * 60 / tempo - 0.5); }
    const take = S.build?.takes?.[context.block];
    if (take) peaksOf(take, 900).then((p) => { strip.peaks = p; drawStrip(); }).catch(() => {});
    drawStrip();
  };
  const stripAt = (e) => { const r = strip.el.getBoundingClientRect(); return Math.min(1, Math.max(0, (e.clientX - r.left) / r.width)) * stripSeconds(); };
  strip.el.onpointerdown = (e) => { strip.el.setPointerCapture(e.pointerId); strip.drag = true; strip.pos = stripAt(e); drawStrip(); };
  strip.el.onpointermove = (e) => { if (strip.drag) { strip.pos = stripAt(e); drawStrip(); } };
  strip.el.onpointerup = (e) => {
    if (!strip.drag) return;
    strip.drag = false; strip.from = stripAt(e); strip.chosen = true;
    // sings (not records) on from the new place when it was playing; otherwise only the place is moved
    if (live.source && mode !== 'rec') { stopLive(); startRecording(false, strip.from); } else { strip.pos = strip.from; drawStrip(); }
  };
  const stripWatcher = new ResizeObserver(() => {
    strip.width = Math.max(240, Math.floor(strip.el.parentElement?.clientWidth || 600));
    const dpr = window.devicePixelRatio || 1;
    strip.el.width = Math.round(strip.width * dpr); strip.el.height = Math.round(strip.height * dpr);
    strip.el.style.width = strip.width + 'px'; strip.el.style.height = strip.height + 'px';
    drawStrip();
  });
  requestAnimationFrame(() => { if (strip.el.parentElement) stripWatcher.observe(strip.el.parentElement); refreshStrip(); });

  // recording: the notes of the clip go to the live voice on the clock of the audio context; the pad changes its vowel and mouth while it sings
  const startRecording = async (record = true, from = strip.from) => {
    const link = flat().links.find((l) => l.to === context.block && l.type === 'midi' && !l.disabled);
    const clip = link && flat().nodes.find((n) => n.id === link.from), text = clean(clip?.params?.notes);
    if (!text) { sing.textContent = idleLabel(); status.textContent = 'Connect a midi-clip with notes to record against.'; return; }
    try {
      stopSong();
      live.ctx ||= getContext();
      await live.ctx.resume?.();
      const tempo = Number(S.doc?.project?.tempo) || 120, ppq = Number(clip.params.ppq) > 0 ? Number(clip.params.ppq) : 1_000_000, all = absolute(parse(text, ppq));
      const seconds = 60 / tempo, firstBar = 1 + from / (4 * seconds), notes = all;
      const voice = await llamaLive(live.ctx);
      voice.connect(live.ctx.destination); voice.allOff();
      const start = rec.cur ?? { vowel: vowelOf(), mouth: mouthOf() };
      voice.set({ ...llamaSettings(context.params), vowel: start.vowel, mouth: start.mouth });
      const t0 = live.ctx.currentTime + 0.15;
      let end = t0;
      for (const n of notes) {
        const place = ((n.bar - 1) * 4 + n.start / ppq) * seconds, length = Math.max(0.05, (n.length / ppq) * seconds);
        if (place + length <= from) continue;   // already over at the place it starts from
        const at = t0 + Math.max(0, place - from), stop = t0 + place + length - from;
        voice.noteOn(n.pitch, n.velocity, at, n.fine || 0); voice.noteOff(n.pitch, stop); end = Math.max(end, stop);
      }
      recordVoice = voice;
      live.source = { stop: () => voice.allOff() };
      sing.textContent = '■ Stop';
      if (record) padApi.begin({ firstBar, tempo, ctx: live.ctx, started: t0 });
      else status.textContent = 'Singing ' + (clip.title || clip.id) + ' · turn the knobs while it sings';
      // while it sings: the knobs, the curves of vowel and mouth (or the pad when recording) and the face of the llama follow every 30 ms; it stops a moment after the last note
      const singer = singerOf(context.block);
      live.timer = setInterval(() => {
        const now = live.ctx.currentTime;
        if (now > end + 0.7) { stopLive(); return; }
        if (!strip.drag) strip.pos = from + Math.max(0, now - t0);
        drawStrip();
        const bar = 1 + (from + now - t0) / ((barBeats() * 60) / tempo), held = record ? (rec.cur ?? start) : null;
        const vowel = held ? held.vowel : singer ? singer.vowel(bar) : start.vowel, mouth = held ? held.mouth : singer ? singer.mouth(bar) : start.mouth;
        voice.set({ ...llamaSettings(context.params), vowel, mouth });
        if (!record && singer) {
          let active = null;
          const tick = (bar - 1) * barBeats() * singer.ppq;
          for (const n of singer.notes) if (n.at <= tick && tick < n.at + n.length) active = n;
          llama?.set(active ? { pitch: active.pitch, vowel, mouth, level: 0.35 + 0.65 * (active.velocity / 127), vibrato: singer.vibrato } : { pitch: null, level: 0 });
          padApi.follow(vowel, mouth);
        }
      }, 30);
    } catch (error) { sing.textContent = idleLabel(); status.textContent = error.message || String(error); }
  };
  sing.onclick = () => { if (live.source) { stopLive(); return; } if (mode === 'rec' && !context.readOnly) { sing.textContent = 'Get ready…'; countIn().then(() => startRecording(true, strip.from)); } else startRecording(false, strip.from); };
  paintSing();
  if (recorded.has(context.block)) { undo.hidden = false; status.textContent = 'Recorded ' + recorded.get(context.block).seconds + ' s: vowel and mouth are now curves in this block.'; }

  // ---- the look of the llama (this browser only)
  const look = selector({ label: 'Look', value: scheme, choices: Object.keys(SCHEMES).map((value) => ({ value, label: value[0].toUpperCase() + value.slice(1) })), onCommit: (value) => { scheme = value; remember('ample.llama.scheme', value); placeLlama(); } });
  const posture = selector({ label: 'Pose', value: pose, choices: [{ value: 'sit', label: 'Sitting (holy)' }, { value: 'stand', label: 'Standing' }], onCommit: (value) => { pose = value; remember('ample.llama.pose', value); placeLlama(); } });

  const controls = el('div', { className: 'llama-controls' }, [el('div', { className: 'llama-bar' }, [sing, status, undo]), el('div', { className: 'llama-pad-label' }, [el('span', { textContent: 'Vowel and mouth' }), modeBar, pad, el('div', { className: 'llama-pad-foot' }, [hint, noteLabel])]), knobs,
    ...(showLlama ? [el('div', { className: 'llama-look' }, [el('label', {}, [el('span', { textContent: 'Look' }), look]), el('label', {}, [el('span', { textContent: 'Pose' }), posture])])] : [])]);
  // ---- the keys: a touch keyboard (and MIDI in) that sing this voice with the vowel and the mouth of the pad, several keys at once glide as one voice
  const keyState = { voice: null };
  const keyOn = async (pitch, velocity) => {
    live.ctx ||= getContext();
    live.ctx.resume?.();
    const voice = await llamaLive(live.ctx);
    voice.connect(live.ctx.destination);
    keyState.voice = voice;
    voice.set({ ...llamaSettings({ ...context.params, ...llamaTweaks(context.block) }), vowel: vowelOf(), mouth: mouthOf() });
    voice.noteOn(pitch, velocity);
  };
  const keyOff = (pitch) => keyState.voice?.noteOff(pitch);
  const keys = createVirtualKeyboard({ onNoteOn: keyOn, onNoteOff: keyOff, octave: 3, octaves: 2, compact: true });
  keysNow.on = (pitch, velocity) => { keys.lit(pitch, true); keyOn(pitch, velocity); };
  keysNow.off = (pitch) => { keys.lit(pitch, false); keyOff(pitch); };
  const midiButton = midiCan() ? el('button', { type: 'button', className: 'llama-midi', textContent: 'MIDI in', 'aria-pressed': String(midiActive()), title: 'Sing with a MIDI keyboard or a virtual MIDI cable' }) : null;
  if (midiButton) {
    // one MIDI input for the whole app: this button shows it and flips it; the notes arrive through the receiver below
    const paint = (on) => { midiButton.setAttribute('aria-pressed', String(on)); midiButton.classList.toggle('on', on); };
    paint(midiActive());
    midiButton.onclick = () => toggleMidi().then(() => { if (midiActive() && !midiNames().length) status.textContent = 'No MIDI input found yet: plug a keyboard in or start a virtual MIDI cable, it is picked up at once.'; }).catch((error) => { status.textContent = error.message || String(error); });
    onMidiState((on) => { if (midiButton.isConnected) paint(on); });
    restoreMidi();
  }
  const keysBox = el('div', { className: 'llama-keys' }, [el('div', { className: 'llama-keys-head' }, [el('small', { textContent: 'Keys: sing a note with the vowel and the mouth of the pad' }), ...(midiButton ? [midiButton] : [])]), keys.element]);
  root.append(...(showLlama ? [stageBox] : []), controls, strip.el, keysBox);
  // the keyboard dock plays this window while it is the one that was used last (the window is built again after every change, so it is found again here; it may not be in the page yet)
  const born = performance.now();
  registerReceiver({ id: 'llama:' + context.block, kind: 'llama', title: 'Delay Llama · ' + (flat().nodes.find((n) => n.id === context.block)?.title || context.block), alive: () => root.isConnected || performance.now() - born < 2000, get node() { return flat().nodes.find((n) => n.id === context.block); }, noteOn: (pitch, velocity) => keysNow.on?.(pitch, velocity), noteOff: (pitch) => keysNow.off?.(pitch), bend: (semitones) => keyState.voice?.bend(semitones) });
  if (showLlama) placeLlama();
  return root;
}

registerStudioPanel('delay-llama', delayLlamaPanel);
