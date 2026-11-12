// The autotune panel: the pitch that is sung (a thin line) on a piano-roll grid with a key strip on the left (the notes of the key marked, a click plays a note), the notes it is
// pulled to (bars) and the pitch as it comes out (the bright line). Its own play bar on top (Play, Loop, A/B, Fit, the range), all worked out in the browser with the steps of
// the engine. The view: the wheel scrolls the window as anywhere, Alt+wheel moves the notes, Ctrl+wheel zooms them, Shift+wheel zooms the time, a sideways wheel moves in time;
// the curve and the notes follow a change at once, the sound is worked out only when it is played. It opens on the notes that are sung (without
// the odd stray value) and is kept per block. On the grid: click or drag empty space to scrub, drag a note bar up or down to sing another note there, double-click a bar to
// leave that part alone (or to take a fix away), right-click for the rest. It writes the `fixes` text. A melody on the Melody input is used when the song is built; the
// preview here uses the key and the fixes. @ample:autotune

import { getContext } from '../../../lib/audio-engine.mjs';
import { el, showMenu } from '../../ui.js';
import { S } from '../../store.js';
import { registerStudioPanel } from './registry.js';
import { onPlayer, stop as stopSong } from '../../player.js';
import { applyCorrections, autotuneSettings, corrections, detectPitch, formatFixes, mergeRuns, noteName, noteRuns, parseFixes } from '../../../lib/clientside-autotune.mjs';
import { knob } from '../knob.js';
import { selector } from '../selector.js';

const KEYS = 44;
const ROOTS = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];
const SCALES = [['major', 'Major'], ['minor', 'Minor'], ['harmonic-minor', 'Harmonic minor'], ['dorian', 'Dorian'], ['mixolydian', 'Mixolydian'], ['pentatonic', 'Pentatonic'], ['minor-pentatonic', 'Minor pentatonic'], ['blues', 'Blues']];
const round = (n) => Math.round(n * 100) / 100;
const clock = (s) => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}.${String(Math.floor((s % 1) * 10))}`;
const clean = (v) => String(v ?? '').replace(/^"|"$/g, '');
const BLACK = new Set([1, 3, 6, 8, 10]);
const pc = (n) => ((Math.round(n) % 12) + 12) % 12;

// the player lives outside the panel, because the panel is built again after every change of the song; the view (zoom and scroll) is kept per block
const live = { ctx: null, source: null, startedAt: 0, offset: 0, show: null, loop: false, original: false, stop: null, watch: 0 };
const views = new Map();
const tracks = new WeakMap();   // AudioBuffer -> its pitch track
onPlayer((state) => { if (state.playing && live.source) live.stop?.(); });

function autotunePanel(context) {
  const root = el('section', { className: 'studio-effect-panel studio-autotune-panel', 'aria-label': 'Autotune' });
  const canvas = el('canvas', { className: 'autotune-roll', role: 'img', tabIndex: 0, 'aria-label': 'The pitch that is sung and the notes it is pulled to: drag a note bar up or down, space plays' });
  const readout = el('output', { className: 'autotune-readout' });
  const rows = el('div', { className: 'autotune-rows' });
  const play = el('button', { type: 'button', className: 'autotune-play', textContent: '▶ Play', title: 'Play the tuned sound (space); a change is heard at once, from the same place', 'aria-pressed': 'false' });
  const loopButton = el('button', { type: 'button', textContent: '↻ Loop', title: 'Repeat the sound', 'aria-pressed': String(live.loop), className: live.loop ? 'on' : '' });
  const abButton = el('button', { type: 'button', textContent: 'A/B', title: 'Hear the voice as it was sung, without the autotune', 'aria-pressed': String(live.original), className: live.original ? 'on' : '' });
  const fitButton = el('button', { type: 'button', className: 'autotune-fit', textContent: 'Fit', title: 'Show all of the sound and the notes that are sung' });
  const range = el('input', { type: 'number', className: 'autotune-range', min: 2, max: 30, step: 1, title: 'How many semitones above and below the middle are shown (Ctrl+wheel zooms too, Alt+wheel moves)', 'aria-label': 'Range in semitones above and below the middle' });
  const time = el('output', { className: 'autotune-time', textContent: '0:00.0' });
  const keyOut = el('output', { className: 'autotune-key' });
  let audioDirty = true, audioTimer = 0, height = 320;
  let duration = 0, rate = 0, allChannels = [], tunedChannels = [], track = null, correction = null, runs = [], fixes = [], scrubTo = null, frame = 0, scale = [], width = 640;
  let view = views.get(context.block) || null;
  const tempo = Number(context.tempo) || Number(S.doc?.project?.tempo) || 120;

  const here = () => {
    if (!(live.source && live.ctx)) return live.offset;
    let pos = live.ctx.currentTime - live.startedAt;
    if (live.loop && duration > 0) pos %= duration;
    return Math.min(pos, duration);
  };
  const show = (on) => { play.textContent = on ? '■ Stop' : '▶ Play'; play.setAttribute('aria-pressed', String(on)); if (on) tick(); };
  const stopLive = () => { try { live.source?.stop(); } catch { /* already stopped */ } live.source = null; clearInterval(live.watch); live.watch = 0; live.show?.(false); draw(); };
  live.stop = stopLive;
  const audioContext = () => (live.ctx ||= getContext());
  // the tuned sound is worked out here, when it is played (turning a knob only redraws the curve)
  const ensureAudio = () => {
    if (!audioDirty || !track) return;
    try { tunedChannels = applyCorrections(allChannels, rate, track, correction, clean(context.params.method) || 'spectral'); } catch { tunedChannels = allChannels; }
    audioDirty = false;
  };
  const startLive = (from) => {
    if (!live.original) ensureAudio();
    const channels = live.original ? allChannels : tunedChannels;
    if (!channels.length) return;
    stopSong();   // nothing else is heard under it
    const ctx = audioContext(), buffer = ctx.createBuffer(channels.length, channels[0].length, rate);
    channels.forEach((data, i) => buffer.copyToChannel(data, i));
    try { live.source?.stop(); } catch { /* already stopped */ }
    const source = ctx.createBufferSource();
    live.source = source;
    source.buffer = buffer;
    source.loop = live.loop;
    source.connect(ctx.destination);
    live.offset = Math.min(Math.max(0, from), Math.max(0, buffer.duration - 0.05));
    live.startedAt = ctx.currentTime - live.offset;
    source.onended = () => { if (live.source === source) { live.offset = 0; stopLive(); } };
    source.start(0, live.offset);
    if (!live.watch) { let misses = 0; live.watch = setInterval(() => { misses = document.querySelector('.studio-autotune-panel') ? 0 : misses + 1; if (misses >= 2) live.stop?.(); }, 300); }
    live.show = show;
    show(true);
  };
  // a short sine of a note (the key strip), as a reference to sing or listen against
  const beep = (note) => {
    const ctx = audioContext(), osc = ctx.createOscillator(), gain = ctx.createGain();
    osc.frequency.value = 440 * 2 ** ((note - 69) / 12);
    gain.gain.setValueAtTime(0.0001, ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.2, ctx.currentTime + 0.02);
    gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.6);
    osc.connect(gain).connect(ctx.destination);
    osc.start(); osc.stop(ctx.currentTime + 0.65);
  };
  const seek = (t) => { live.offset = Math.min(Math.max(0, t), duration); if (live.source) startLive(live.offset); else draw(); };
  const tick = () => { cancelAnimationFrame(frame); if (!live.source || !root.isConnected) return; draw(); frame = requestAnimationFrame(tick); };
  live.show = show;
  show(Boolean(live.source));
  play.onclick = () => {
    if (live.source) { live.offset = here(); stopLive(); return; }
    if (audioDirty && !live.original) { play.textContent = 'Working…'; setTimeout(() => startLive(live.offset), 20); } else startLive(live.offset);
  };
  loopButton.onclick = () => { live.loop = !live.loop; loopButton.setAttribute('aria-pressed', String(live.loop)); loopButton.classList.toggle('on', live.loop); if (live.source) startLive(here()); };
  abButton.onclick = () => { live.original = !live.original; abButton.setAttribute('aria-pressed', String(live.original)); abButton.classList.toggle('on', live.original); if (live.source) startLive(here()); };
  canvas.onkeydown = (event) => { if (event.key === ' ') { event.preventDefault(); play.onclick(); } };

  // ---- the view: which time and which notes are shown
  const fit = () => {
    const sung = track ? [...track.midi].filter((m) => !Number.isNaN(m)).sort((a, b) => a - b) : [];
    // the 5th to the 95th percent of what is sung: one stray value (a breath read as a low note) does not stretch the grid
    const lo = sung.length ? Math.floor(sung[Math.floor(sung.length * 0.05)]) - 2 : 48, hi = sung.length ? Math.ceil(sung[Math.floor(sung.length * 0.95)]) + 2 : 72;
    view = { t0: 0, t1: duration || 1, lo, hi: Math.max(hi, lo + 6) };
    remember();
  };
  const remember = () => { views.set(context.block, view); range.value = String(Math.round((view.hi - view.lo) / 2)); canvas.dataset.rows = `${view.lo},${view.hi}`; canvas.dataset.view = `${round(view.t0)},${round(view.t1)}`; };
  const clampView = () => {
    const span = Math.min(60, Math.max(4, view.hi - view.lo));
    view.lo = Math.min(108 - span, Math.max(12, view.lo)); view.hi = view.lo + span;
    const length = Math.min(duration || 1, Math.max(0.5, view.t1 - view.t0));
    view.t0 = Math.min(Math.max(0, view.t0), Math.max(0, (duration || 1) - length)); view.t1 = view.t0 + length;
    remember();
  };
  fitButton.onclick = () => { fit(); draw(); };
  range.onchange = () => { const half = Math.min(30, Math.max(2, Number(range.value) || 6)), middle = Math.round((view.lo + view.hi) / 2); view.lo = middle - half; view.hi = middle + half; clampView(); draw(); };

  const plotWidth = () => Math.max(10, width - KEYS);
  const x = (seconds) => KEYS + ((seconds - view.t0) / Math.max(1e-6, view.t1 - view.t0)) * plotWidth();
  const timeAt = (px) => view.t0 + Math.min(1, Math.max(0, (px - KEYS) / plotWidth())) * (view.t1 - view.t0);
  const rowHeight = () => height / (view.hi - view.lo + 1);
  const y = (midi) => height - ((midi - view.lo + 0.5) / (view.hi - view.lo + 1)) * height;
  const noteAt = (py) => view.lo - 0.5 + (1 - py / height) * (view.hi - view.lo + 1);

  // the canvas is as wide as the window and sharp on any screen
  const resize = () => {
    width = Math.max(320, Math.floor(root.clientWidth || 640));
    // the height: what the window has left under the rack and the bars (at least 220 px), so a taller window gives a taller graph
    const body = root.closest('.studio-dialog-body'), resizable = root.closest('.studio-resizable');
    const others = [...root.children].filter((child) => child !== canvas).reduce((sum, child) => sum + child.offsetHeight, 0);
    height = resizable && body ? Math.max(220, Math.floor(body.clientHeight - others - 48)) : 320;
    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.round(width * dpr); canvas.height = Math.round(height * dpr);
    canvas.style.width = `${width}px`; canvas.style.height = `${height}px`;
    draw();
  };
  const watcher = new ResizeObserver(() => resize());
  watcher.observe(root);
  requestAnimationFrame(() => { const body = root.closest('.studio-dialog-body'); if (body) watcher.observe(body); });

  // the fixes in seconds (read at a "rate" of 1000: every sample is a millisecond)
  const read = () => { try { return parseFixes(clean(context.params.fixes), 1000, Math.round(240000 / tempo)).map((f) => ({ ...f, from: f.from / 1000, to: f.to / 1000 })); } catch { return []; } };
  const text = () => formatFixes(fixes);
  const commit = () => {
    context.params.fixes = text();
    if (context.commitMany) context.commitMany([{ name: 'fixes', value: text() }]); else context.commit('fixes', text());
    tuned();
    rowsFor();
  };

  // what is heard: the steps of the engine with the settings of the block and the fixes of the window
  const tuned = () => {
    if (!track) return;
    let settings;
    try { settings = autotuneSettings(context.params, track); } catch (error) { readout.textContent = error.message; return; }
    scale = settings.scale;
    keyOut.textContent = `Key: ${settings.key}${clean(context.params.key) === 'auto' || !clean(context.params.key) ? ' (found)' : ''}`;
    let target;
    ({ target, correction } = corrections(track, settings, [], fixes.map((f) => ({ ...f, from: Math.round(f.from * rate), to: Math.round(f.to * rate) }))));
    // the notes as they are heard: a piece shorter than 80 ms joins the note next to it
    runs = mergeRuns(noteRuns(track, target, rate));
    canvas.dataset.runs = JSON.stringify(runs.map((r) => [round(r.from), round(r.to), r.note]));
    canvas.dataset.duration = String(round(duration));
    canvas.dataset.keys = String(KEYS);
    audioDirty = true;
    // while it plays, the new sound follows a moment after the last change
    clearTimeout(audioTimer);
    if (live.source && root.isConnected && !live.original) audioTimer = setTimeout(() => { if (live.source) startLive(here()); }, 350);
    readout.textContent = runs.length
      ? `${runs.length} note${runs.length === 1 ? '' : 's'} · ${fixes.length ? `${fixes.length} fix${fixes.length === 1 ? '' : 'es'}: ${text()}` : 'no fixes: drag a note bar up or down to change it'}${context.melody ? ' · the melody on Melody is used when the song is built (not in this preview)' : ''}`
      : 'No sung notes found: autotune works on one voice or one monophonic instrument.';
    draw();
  };

  function draw() {
    if (!view) {
      // still reading the voice: an empty grid with a word, never a blank window
      const g = canvas.getContext('2d'), dpr = canvas.width / width;
      g.setTransform(dpr, 0, 0, dpr, 0, 0);
      g.fillStyle = getComputedStyle(document.documentElement).getPropertyValue('--panel2').trim() || '#222';
      g.fillRect(0, 0, width, height);
      g.fillStyle = 'rgba(255, 255, 255, 0.55)'; g.font = '13px system-ui'; g.textAlign = 'center';
      g.fillText(readout.textContent || 'Reading the pitch of the voice…', width / 2, height / 2);
      g.textAlign = 'start';
      return;
    }
    const g = canvas.getContext('2d'), styles = getComputedStyle(document.documentElement), dpr = canvas.width / width;
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    const accent = styles.getPropertyValue('--acc').trim() || '#6cf';
    g.fillStyle = styles.getPropertyValue('--panel2').trim() || '#222';
    g.fillRect(0, 0, width, height);
    const rh = rowHeight();
    g.font = '10px system-ui';
    g.textBaseline = 'middle';
    for (let n = Math.ceil(view.lo - 0.5); n <= Math.floor(view.hi + 0.5); n++) {
      const top = y(n) - rh / 2;
      // the rows: the notes of the key are lighter, a line under every C
      if (scale[pc(n)]) { g.fillStyle = 'rgba(255, 255, 255, 0.05)'; g.fillRect(KEYS, top, plotWidth(), rh); }
      if (pc(n) === 0) { g.fillStyle = 'rgba(255, 255, 255, 0.16)'; g.fillRect(KEYS, top + rh - 1, plotWidth(), 1); }
      // the key strip: white and black keys, the name of every note when there is room, a dot for the notes of the key
      g.fillStyle = BLACK.has(pc(n)) ? '#1b1d22' : '#d9dce2';
      g.fillRect(0, top, KEYS - 2, rh - 0.5);
      if (rh >= 10 || pc(n) === 0) { g.fillStyle = BLACK.has(pc(n)) ? '#cfd3da' : '#22252b'; g.fillText(noteName(n), 4, top + rh / 2); }
      if (scale[pc(n)]) { g.fillStyle = accent; g.beginPath(); g.arc(KEYS - 8, top + rh / 2, Math.min(3, rh / 3), 0, 2 * Math.PI); g.fill(); }
    }
    g.save();
    g.beginPath(); g.rect(KEYS, 0, plotWidth(), height); g.clip();
    for (const f of fixes) { g.fillStyle = f.note === null ? 'rgba(160, 160, 160, 0.18)' : 'rgba(229, 83, 75, 0.16)'; g.fillRect(x(f.from), 0, Math.max(2, x(f.to) - x(f.from)), height); }
    for (const r of runs) {
      const left = x(r.from), w = Math.max(2, x(r.to) - left), top = y(r.note) - rh / 2 + 1, h = Math.max(3, rh - 2);
      g.globalAlpha = 0.28; g.fillStyle = accent; g.fillRect(left, top, w, h);
      g.globalAlpha = 0.9; g.strokeStyle = accent; g.lineWidth = 1; g.strokeRect(left + 0.5, top + 0.5, w - 1, h - 1);
      g.globalAlpha = 1;
      if (w > 30 && rh >= 9) { g.fillStyle = 'rgba(255, 255, 255, 0.85)'; g.fillText(noteName(r.note), left + 3, top + h / 2); }
    }
    if (track) {
      // a line that breaks where nothing is sung and at jumps of more than two semitones; a value outside the view is a small mark at the edge
      const line = (value, color, lineWidth) => {
        g.strokeStyle = color; g.lineWidth = lineWidth; g.beginPath();
        let previous = NaN;
        for (let f = 0; f < track.midi.length; f++) {
          const v = value(f), t = f * track.hop / rate;
          if (t < view.t0 - 0.05 || t > view.t1 + 0.05) { previous = NaN; continue; }
          if (Number.isNaN(v)) { previous = NaN; continue; }
          const px = x(t), py = y(v);
          if (Number.isNaN(previous) || Math.abs(v - previous) > 2) g.moveTo(px, py); else g.lineTo(px, py);
          previous = v;
        }
        g.stroke();
      };
      line((f) => track.midi[f], 'rgba(255, 255, 255, 0.45)', 1);
      if (correction) line((f) => (Number.isNaN(track.midi[f]) ? NaN : track.midi[f] + correction[f]), accent, 2);
      g.fillStyle = 'rgba(255, 196, 0, 0.8)';
      for (let f = 0; f < track.midi.length; f += 3) {
        const m = track.midi[f], t = f * track.hop / rate;
        if (Number.isNaN(m) || t < view.t0 || t > view.t1) continue;
        if (m > view.hi + 0.5) g.fillRect(x(t) - 1, 0, 3, 4); else if (m < view.lo - 0.5) g.fillRect(x(t) - 1, height - 4, 3, 4);
      }
    }
    const at = scrubTo ?? here();
    if (at >= view.t0 && at <= view.t1) { g.fillStyle = '#fff'; g.fillRect(Math.round(x(at)) - 1, 0, 2, height); }
    g.restore();
    time.textContent = `${clock(at)} / ${clock(duration)}`;
  }

  const rowsFor = () => {
    rows.replaceChildren(...fixes.map((f, index) => el('div', { className: 'autotune-row' }, [
      `${round(f.from)}–${round(f.to)} s → ${f.note === null ? 'left alone' : noteName(f.note)}${f.vibrato === null || f.vibrato === undefined ? '' : ` · vibrato ${f.vibrato}%`} `,
      el('button', { type: 'button', textContent: '×', title: 'Remove this fix', 'aria-label': `Remove fix ${index + 1}`, disabled: context.readOnly, onclick: () => { fixes.splice(index, 1); commit(); } }),
    ])));
  };

  Promise.resolve(context.inputBuffer?.()).then(async (buffer) => {
    if (!buffer) { readout.textContent = 'Connect a voice to the input and build it to see its pitch here.'; draw(); return; }
    await new Promise((resolve) => setTimeout(resolve, 30));   // the window and its "reading" word are drawn before the pitch is worked out
    duration = buffer.duration; rate = buffer.sampleRate;
    allChannels = Array.from({ length: Math.min(2, buffer.numberOfChannels) }, (_, i) => buffer.getChannelData(i));
    const mono = allChannels.length > 1 ? allChannels[0].map((v, i) => (v + allChannels[1][i]) / 2) : allChannels[0];
    track = tracks.get(buffer) || detectPitch(mono, rate);
    tracks.set(buffer, track);
    if (!view || view.t1 > duration + 0.01) fit(); else remember();
    fixes = read();
    tuned(); rowsFor();
  }).catch(() => { readout.textContent = 'The input could not be read.'; });

  const place = (event) => {
    const box = canvas.getBoundingClientRect();
    const px = (event.clientX - box.left) * width / box.width, py = (event.clientY - box.top) * height / box.height;
    return { px, py, t: timeAt(px), note: Math.round(noteAt(py)), keys: px < KEYS };
  };
  const runAt = (t, note) => runs.find((r) => r.from <= t && t < r.to && Math.abs(r.note - note) <= 1);
  // a fix for exactly the span of a run (an earlier fix of that span is replaced)
  const setFix = (r, change) => {
    const existing = fixes.find((f) => Math.abs(f.from - r.from) < 0.02 && Math.abs(f.to - r.to) < 0.02);
    const fix = existing ?? { from: round(r.from), to: round(r.to), note: r.note, vibrato: null };
    change(fix);
    if (!existing) fixes.push(fix);
    commit();
  };
  const describe = (r) => { const cents = Math.round((r.sung - r.note) * 100); return `${noteName(r.note)} · sung ${cents >= 0 ? '+' : ''}${cents} cents from it · ${round(r.from)}–${round(r.to)} s`; };

  let drawQueued = false;
  const drawSoon = () => { if (drawQueued) return; drawQueued = true; requestAnimationFrame(() => { drawQueued = false; draw(); }); };
  canvas.onwheel = (event) => {
    if (!view || !duration) return;
    const sideways = Math.abs(event.deltaX) > Math.abs(event.deltaY);
    if (!event.ctrlKey && !event.shiftKey && !event.altKey && !sideways) return;   // a plain wheel scrolls the window
    event.preventDefault();
    const { t, py } = place(event), out = event.deltaY > 0;
    if (event.ctrlKey) {
      const anchor = noteAt(py), span = view.hi - view.lo, next = Math.min(60, Math.max(4, Math.round(span * (out ? 1.25 : 0.8))));
      view.lo = Math.round(anchor - (anchor - view.lo) * next / span); view.hi = view.lo + next;
    } else if (event.shiftKey) {
      const length = view.t1 - view.t0, next = Math.min(duration, Math.max(0.5, length * (out ? 1.25 : 0.8)));
      view.t0 = t - (t - view.t0) * next / length; view.t1 = view.t0 + next;
    } else if (sideways) {
      const shift = (view.t1 - view.t0) * 0.15 * Math.sign(event.deltaX);
      view.t0 += shift; view.t1 += shift;
    } else {
      const shift = out ? -1 : 1;
      view.lo += shift; view.hi += shift;
    }
    clampView();
    drawSoon();
  };

  let drag = null;
  canvas.onpointerdown = (event) => {
    if (!duration || event.button !== 0) return;
    const { t, note, keys } = place(event);
    if (keys) { beep(note); return; }
    canvas.focus();
    if (context.readOnly) { seek(t); return; }
    canvas.setPointerCapture(event.pointerId);
    const r = runAt(t, note);
    drag = r ? { part: 'note', run: r, start: note, note: r.note } : { part: 'scrub' };
    if (!r) { scrubTo = t; draw(); }
  };
  canvas.onpointermove = (event) => {
    const { t, note, keys } = place(event);
    if (!drag) {
      const r = keys ? null : runAt(t, note);
      canvas.style.cursor = keys ? 'pointer' : r && !context.readOnly ? 'ns-resize' : 'crosshair';
      canvas.title = keys ? `Play ${noteName(note)}` : r ? describe(r) : 'Click to play from here · Alt+wheel: notes up and down · Ctrl+wheel: zoom the notes · Shift+wheel: zoom the time · sideways wheel: move in time';
      return;
    }
    if (drag.part === 'scrub') { scrubTo = t; draw(); return; }
    drag.note = drag.run.note + (note - drag.start);
    draw();
    const g = canvas.getContext('2d');
    g.strokeStyle = '#e5534b'; g.lineWidth = 2;
    g.strokeRect(x(drag.run.from), y(drag.note) - rowHeight() / 2, Math.max(2, x(drag.run.to) - x(drag.run.from)), rowHeight());
  };
  canvas.onpointerup = () => {
    if (!drag) return;
    const was = drag;
    drag = null;
    if (was.part === 'scrub') { const t = scrubTo; scrubTo = null; seek(t ?? 0); return; }
    if (was.note !== was.run.note) setFix(was.run, (f) => { f.note = was.note; }); else draw();
  };
  canvas.ondblclick = (event) => {
    if (context.readOnly) return;
    const { t, note, keys } = place(event);
    if (keys) return;
    const r = runAt(t, note), inFix = fixes.findIndex((f) => f.from <= t && t < f.to);
    if (inFix >= 0) { fixes.splice(inFix, 1); commit(); } else if (r) setFix(r, (f) => { f.note = null; });
  };
  canvas.oncontextmenu = (event) => {
    event.preventDefault();
    const { t, note, keys } = place(event);
    if (keys) return;
    const r = runAt(t, note), inFix = fixes.findIndex((f) => f.from <= t && t < f.to);
    const items = [];
    if (r && !context.readOnly) {
      items.push({ head: describe(r) },
        { label: 'A semitone up', fn: () => setFix(r, (f) => { f.note = (f.note ?? r.note) + 1; }) },
        { label: 'A semitone down', fn: () => setFix(r, (f) => { f.note = (f.note ?? r.note) - 1; }) },
        { label: 'Hold it straight (no vibrato)', fn: () => setFix(r, (f) => { f.vibrato = 0; }) },
        { label: 'Leave this note alone', fn: () => setFix(r, (f) => { f.note = null; }) },
        { label: 'Play from a second before', fn: () => { seek(Math.max(0, r.from - 1)); if (!live.source) startLive(live.offset); } });
    } else items.push({ head: 'Autotune' }, { label: 'Play from here', fn: () => { seek(t); if (!live.source) startLive(live.offset); } });
    items.push({ label: 'Fit the view', fn: () => { fit(); draw(); } });
    if (inFix >= 0 && !context.readOnly) items.push('-', { label: 'Remove the fix here', fn: () => { fixes.splice(inFix, 1); commit(); } });
    if (fixes.length && !context.readOnly) items.push({ label: 'Remove all fixes', fn: () => { fixes = []; commit(); } });
    showMenu(event.clientX, event.clientY, items);
  };

  // ---- the rack: the settings of the block, like the front of an autotune; turning a knob is heard at once in the preview, letting go writes it into the song
  let pending = 0;
  const previewSoon = () => { clearTimeout(pending); pending = setTimeout(() => tuned(), 120); };
  const setParam = (name, value, write) => { context.params[name] = value; if (write) context.commit(name, value); else previewSoon(); };
  const keyText = clean(context.params.key) || 'auto';
  const keyWords = keyText.split(/\s+/);
  const rootNow = /^(auto|chromatic)$/i.test(keyText) ? keyText.toLowerCase() : keyWords[0];
  const scaleNow = rootNow === 'auto' || rootNow === 'chromatic' ? 'minor' : (keyWords.slice(1).join('-') || 'major').toLowerCase();
  const scaleSelect = selector({ label: 'Scale', value: scaleNow, disabled: context.readOnly || rootNow === 'auto' || rootNow === 'chromatic', choices: SCALES.map(([value, label]) => ({ value, label })), onCommit: (value) => setParam('key', `${rootSelect.value} ${value}`, true) });
  const rootSelect = selector({ label: 'Key', value: rootNow, disabled: context.readOnly, choices: [{ value: 'auto', label: 'Auto (found)' }, { value: 'chromatic', label: 'Chromatic' }, ...ROOTS.map((r) => ({ value: r, label: r }))], onCommit: (value) => setParam('key', value === 'auto' || value === 'chromatic' ? value : `${value} ${scaleSelect.value}`, true) });
  const methodSelect = selector({ label: 'Method', value: clean(context.params.method) || 'spectral', disabled: context.readOnly, choices: [{ value: 'spectral', label: 'Spectral (natural)' }, { value: 'psola', label: 'PSOLA (tight)' }], onCommit: (value) => setParam('method', value, true) });
  const numberOf = (name, fallback) => { const v = Number.parseFloat(clean(context.params[name])); return Number.isFinite(v) ? v : fallback; };
  const vibratoNow = clean(context.params.vibrato) === 'flatten' ? 0 : clean(context.params.vibrato) === 'keep' || !clean(context.params.vibrato) ? 100 : numberOf('vibrato', 100);
  const dial = (name, label, value, min, max, unit, format, hint) => el('div', { className: 'studio-panel-control autotune-dial', title: hint }, [knob({
    label, value, min, max, unit, size: 64, step: 1, disabled: context.readOnly,
    onPreview: (next) => setParam(name, format(Math.round(next)), false),
    onCommit: (next) => setParam(name, format(Math.round(next)), true),
  })]);
  const rack = el('div', { className: 'autotune-rack' }, [
    el('div', { className: 'autotune-pickers' }, [
      el('label', {}, [el('span', { textContent: 'Key' }), rootSelect]),
      el('label', {}, [el('span', { textContent: 'Scale' }), scaleSelect]),
      el('label', {}, [el('span', { textContent: 'Method' }), methodSelect]),
    ]),
    dial('speed', 'Retune speed', numberOf('speed', 20), 0, 400, 'ms', (v) => `${v} ms`, '0 ms is the hard robot sound; 20 to 50 ms a clear correction; 100 ms and more a soft natural one'),
    dial('amount', 'Amount', numberOf('amount', 100), 0, 100, '%', (v) => `${v}%`, 'How far the voice is pulled to the note'),
    dial('humanize', 'Humanize', numberOf('humanize', 30), 0, 100, '%', (v) => `${v}%`, 'How much slides and the start of a note keep what was sung'),
    dial('vibrato', 'Vibrato', vibratoNow, 0, 100, '%', (v) => (v >= 100 ? 'keep' : v <= 0 ? 'flatten' : `${v}%`), 'How much of the vibrato of the voice stays: 100% keeps it, 0% holds every note straight'),
  ]);

  const bar = el('div', { className: 'rewind-bar autotune-bar' }, [play, loopButton, abButton, time, el('span', { className: 'autotune-gap' }), fitButton, el('label', { className: 'autotune-range-label' }, ['± ', range, ' st']), keyOut]);
  const legend = el('div', { className: 'autotune-legend', 'aria-hidden': 'true' }, [el('span', { className: 'sung', textContent: 'as sung' }), el('span', { className: 'tuned', textContent: 'tuned' }), el('span', { className: 'note', textContent: 'note' }), el('span', { className: 'fix', textContent: 'fix' })]);
  root.append(rack, bar, canvas, el('div', { className: 'autotune-under' }, [readout, legend]), rows);
  readout.textContent = 'Reading the pitch of the voice…';
  return root;
}

registerStudioPanel('autotune', autotunePanel);
