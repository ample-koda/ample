// The rewind panel: the sound as it is heard (the music, the scratches, the music going on earlier), with a flag at every stop, and its own play bar on top (Play, Loop, A/B),
// all worked out in the browser with the arithmetic of the engine. On the wave: click or drag on empty wave to scrub (the playhead), Shift-drag to choose a loop range,
// double-click empty wave to add a rewind, drag a flag to move the stop, drag the end of a bracket to change the length of the scratch, drag the inside of a bracket to move
// the whole rewind, drag the "back" label to change the jump back, double-click a flag to take it away, right-click for the rest. It writes the `events` text. @ample:rewind

import { getContext } from '../../../lib/audio-engine.mjs';
import { el, showMenu } from '../../ui.js';
import { S } from '../../store.js';
import { registerStudioPanel } from './registry.js';
import { onPlayer, playerState, stop as stopSong } from '../../player.js';
import { parseRewindEvents, rewindAudio } from '../../../lib/clientside-rewind.mjs';
import { barBeats } from '../../../lib/meter.mjs';

const WIDTH = 640, HEIGHT = 120;
const round = (n) => Math.round(n * 100) / 100;
const formatEvent = (e) => `at ${round(e.at)}s scratch ${round(e.scratch)}s back ${round(e.back)}s`;
const clock = (s) => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}.${String(Math.floor((s % 1) * 10))}`;

// the player lives outside the panel, because the panel is built again after every change of the song
const live = { ctx: null, source: null, startedAt: 0, offset: 0, show: null, loop: false, range: null, original: false, stop: null, watch: 0 };
// one sound at a time: when the song (or another preview) starts, this one stops; and it stops when the window is closed
onPlayer((state) => { if (state.playing && live.source) live.stop?.(); });

function rewindPanel(context) {
  const root = el('section', { className: 'studio-effect-panel studio-rewind-panel', 'aria-label': 'Rewind' });
  const canvas = el('canvas', { width: WIDTH, height: HEIGHT, className: 'rewind-wave', role: 'img', tabIndex: 0, 'aria-label': 'The sound as it is heard, with a flag at every rewind: drag a flag to move it, space plays' });
  const readout = el('output', { className: 'rewind-readout' });
  const rows = el('div', { className: 'rewind-rows' });
  const play = el('button', { type: 'button', className: 'rewind-play', textContent: '▶ Play', title: 'Play the rewound sound (space); a change of a stop is heard at once, from the same place', 'aria-pressed': 'false' });
  const loopButton = el('button', { type: 'button', textContent: '↻ Loop', title: 'Repeat the sound, or the range you chose with Shift-drag on the wave', 'aria-pressed': String(live.loop), className: live.loop ? 'on' : '' });
  const abButton = el('button', { type: 'button', textContent: 'A/B', title: 'Hear the original sound without the rewinds', 'aria-pressed': String(live.original), className: live.original ? 'on' : '' });
  const time = el('output', { className: 'rewind-time', textContent: '0:00.0' });
  let duration = 0, rate = 0, channel = null, peaks = null, events = [], allChannels = [], heardChannels = [], scrubTo = null, frame = 0;
  const tempo = Number(context.tempo) || Number(S.doc?.project?.tempo) || 120;

  const audible = () => (live.original ? allChannels : heardChannels);
  const here = () => {
    if (!(live.source && live.ctx)) return live.offset;
    let pos = live.ctx.currentTime - live.startedAt;
    if (live.loop) {
      const lo = live.range ? live.range[0] : 0, hi = live.range ? live.range[1] : duration;
      if (hi > lo && pos >= hi) pos = lo + ((pos - lo) % (hi - lo));
    }
    return Math.min(pos, duration);
  };
  const show = (on) => { play.textContent = on ? '■ Stop' : '▶ Play'; play.setAttribute('aria-pressed', String(on)); if (on) tick(); };
  const stopLive = () => { try { live.source?.stop(); } catch { /* already stopped */ } live.source = null; clearInterval(live.watch); live.watch = 0; live.show?.(false); draw(); };
  live.stop = stopLive;
  const startLive = (from) => {
    const channels = audible();
    if (!channels.length) return;
    stopSong();   // nothing else is heard under it: the song, a live local preview or an audition of another window
    live.ctx ||= getContext();
    const ctx = live.ctx;
    const buffer = ctx.createBuffer(channels.length, channels[0].length, rate);
    channels.forEach((data, i) => buffer.copyToChannel(data, i));
    try { live.source?.stop(); } catch { /* already stopped */ }
    const source = ctx.createBufferSource();
    live.source = source;
    source.buffer = buffer;
    source.connect(ctx.destination);
    if (live.loop) { source.loop = true; if (live.range) { source.loopStart = live.range[0]; source.loopEnd = live.range[1]; } }
    live.offset = Math.min(Math.max(0, from), Math.max(0, buffer.duration - 0.05));
    live.startedAt = ctx.currentTime - live.offset;
    source.onended = () => { if (live.source === source) { live.offset = 0; stopLive(); } };
    source.start(0, live.offset);
    if (!live.watch) { let misses = 0; live.watch = setInterval(() => { misses = document.querySelector('.studio-rewind-panel') ? 0 : misses + 1; if (misses >= 2) live.stop?.(); }, 300); }
    live.show = show;
    show(true);
  };
  const seek = (t) => { live.offset = Math.min(Math.max(0, t), duration); if (live.source) startLive(live.offset); else draw(); };
  const tick = () => {
    cancelAnimationFrame(frame);
    if (!live.source || !root.isConnected) return;
    draw();
    frame = requestAnimationFrame(tick);
  };
  live.show = show;
  show(Boolean(live.source));
  play.onclick = () => { if (live.source) { live.offset = here(); stopLive(); } else startLive(live.offset); };
  loopButton.onclick = () => { live.loop = !live.loop; loopButton.setAttribute('aria-pressed', String(live.loop)); loopButton.classList.toggle('on', live.loop); if (live.source) startLive(here()); };
  abButton.onclick = () => { live.original = !live.original; abButton.setAttribute('aria-pressed', String(live.original)); abButton.classList.toggle('on', live.original); if (live.source) startLive(here()); };
  canvas.onkeydown = (event) => { if (event.key === ' ') { event.preventDefault(); play.onclick(); } };

  // seconds: the events are read at a "rate" of 1000, so every sample is a millisecond
  const read = () => {
    try { return parseRewindEvents(String(context.params.events || '').replace(/^"|"$/g, ''), 1000, Math.round(240000 / tempo)).map((e) => ({ at: e.at / 1000, scratch: e.scratch / 1000, back: e.back / 1000 })); } catch { return []; }
  };
  const text = () => events.map(formatEvent).join('; ');
  const commit = () => {
    context.params.events = text();
    if (context.commitMany) context.commitMany([{ name: 'events', value: text() }]); else context.commit('events', text());
    heard();
    say();
  };
  const say = () => { readout.textContent = events.length ? `${events.length} rewind${events.length === 1 ? '' : 's'}: ${text()}` : 'No rewinds yet: double-click on the wave where the music should stop (or right-click).'; };

  // the wave that is heard: the input pulled through the same arithmetic as the engine
  const heard = () => {
    if (!channel) return;
    const params = { ...context.params, events: text(), sound: 'spin', smear: context.params.smear, brake: context.params.brake };
    const through = (data) => rewindAudio({ sampleRate: rate, channels: [data] }, params, Math.round(rate * (barBeats() * 60) / tempo), null).audio.channels[0];
    let shown = channel;
    try { heardChannels = allChannels.map(through); shown = heardChannels[0]; } catch { shown = channel; heardChannels = allChannels; }
    if (live.source && root.isConnected) startLive(here());
    const step = Math.max(1, Math.floor(shown.length / WIDTH));
    peaks = new Float32Array(WIDTH);
    for (let i = 0; i < WIDTH; i++) {
      let max = 0;
      for (let k = i * step; k < Math.min(shown.length, (i + 1) * step); k += 8) max = Math.max(max, Math.abs(shown[k]));
      peaks[i] = max;
    }
    draw();
  };

  const x = (seconds) => (duration ? (seconds / duration) * WIDTH : 0);
  function draw() {
    const g = canvas.getContext('2d'), styles = getComputedStyle(document.documentElement);
    g.fillStyle = styles.getPropertyValue('--panel2').trim() || '#222';
    g.fillRect(0, 0, WIDTH, HEIGHT);
    if (live.range) { g.fillStyle = 'rgba(255, 255, 255, 0.10)'; g.fillRect(x(live.range[0]), 0, x(live.range[1]) - x(live.range[0]), HEIGHT); }
    if (peaks) {
      g.fillStyle = styles.getPropertyValue('--acc').trim() || '#6cf';
      for (let i = 0; i < WIDTH; i++) { const h = Math.max(1, peaks[i] * (HEIGHT - 10)); g.fillRect(i, (HEIGHT - h) / 2, 1, h); }
    }
    g.font = '11px system-ui';
    events.forEach((e, index) => {
      g.fillStyle = 'rgba(229, 83, 75, 0.28)';
      g.fillRect(x(e.at), 0, Math.max(2, x(e.scratch)), HEIGHT);
      g.fillStyle = '#e5534b';
      g.fillRect(x(e.at) - 1, 0, 3, HEIGHT);
      g.fillRect(x(e.at + e.scratch) - 2, HEIGHT / 2 - 10, 5, 20);
      g.fillText(`↩ ${round(e.back)} s`, x(e.at) + 4, 12 + (index % 2) * 12);
    });
    const at = scrubTo ?? here();
    g.fillStyle = '#fff';
    g.fillRect(Math.round(x(at)) - 1, 0, 2, HEIGHT);
    time.textContent = `${clock(at)} / ${clock(duration)}`;
  }

  const rowsFor = () => {
    rows.replaceChildren(...events.map((e, index) => {
      const field = (name, label, step) => {
        const input = el('input', { type: 'number', step, value: String(round(e[name])), disabled: context.readOnly, 'aria-label': `${label} of rewind ${index + 1}` });
        input.onchange = () => { const n = Number.parseFloat(input.value); if (Number.isFinite(n)) { e[name] = name === 'scratch' ? Math.max(0, n) : n; events.sort((a, b) => a.at - b.at); commit(); rowsFor(); } };
        return el('label', {}, [`${label} `, input]);
      };
      return el('div', { className: 'rewind-row' }, [`#${index + 1} `, field('at', 'at (s)', '0.1'), field('scratch', 'scratch (s)', '0.1'), field('back', 'back (s, negative = forward)', '0.1'),
        el('button', { type: 'button', textContent: '×', title: 'Remove this rewind', 'aria-label': `Remove rewind ${index + 1}`, disabled: context.readOnly, onclick: () => { events.splice(index, 1); commit(); rowsFor(); } })]);
    }));
  };

  Promise.resolve(context.inputBuffer?.()).then((buffer) => {
    if (!buffer) { say(); return; }
    duration = buffer.duration; rate = buffer.sampleRate; channel = buffer.getChannelData(0);
    allChannels = Array.from({ length: Math.min(2, buffer.numberOfChannels) }, (_, i) => buffer.getChannelData(i));
    events = read();
    heard(); say(); rowsFor();
  }).catch(() => say());

  // pointer: where on the wave, and what is there
  const place = (event) => {
    const box = canvas.getBoundingClientRect();
    const px = Math.min(1, Math.max(0, (event.clientX - box.left) / box.width)), py = (event.clientY - box.top) / box.height * HEIGHT;
    return { xp: px * WIDTH, yp: py, t: px * duration };
  };
  const hit = (xp, yp) => {
    for (let i = events.length - 1; i >= 0; i--) {
      const e = events[i], a = x(e.at), b = x(e.at + e.scratch);
      if (Math.abs(xp - b) <= 6) return { index: i, part: 'end' };
      if (Math.abs(xp - a) <= 6) return { index: i, part: 'at' };
      if (yp < 26 && xp > a && xp <= a + 54) return { index: i, part: 'back' };
      if (xp > a && xp < b) return { index: i, part: 'body' };
    }
    return null;
  };
  const sort = () => events.sort((a, b) => a.at - b.at);
  const add = (t) => { events.push({ at: round(Math.min(t, Math.max(0, duration - 1))), scratch: 1, back: 2 }); sort(); commit(); rowsFor(); };

  if (!context.readOnly) {
    let drag = null;
    canvas.onpointerdown = (event) => {
      if (!duration || event.button !== 0) return;
      canvas.focus();
      canvas.setPointerCapture(event.pointerId);
      const { xp, yp, t } = place(event), found = hit(xp, yp);
      if (found) {
        const e = events[found.index];
        drag = { ...found, t0: t, at0: e.at, back0: e.back };
        return;
      }
      drag = event.shiftKey ? { part: 'range', t0: t } : { part: 'scrub' };
      if (drag.part === 'scrub') { scrubTo = t; draw(); }
    };
    canvas.onpointermove = (event) => {
      const { xp, yp, t } = place(event);
      if (!drag) {
        const found = hit(xp, yp);
        canvas.style.cursor = !found ? 'pointer' : found.part === 'body' ? 'grab' : found.part === 'back' ? 'col-resize' : 'ew-resize';
        return;
      }
      if (drag.part === 'scrub') { scrubTo = t; draw(); return; }
      if (drag.part === 'range') { live.range = [Math.min(drag.t0, t), Math.max(drag.t0, t)]; draw(); return; }
      const e = events[drag.index];
      if (drag.part === 'at') e.at = round(Math.min(t, duration));
      else if (drag.part === 'end') e.scratch = round(Math.max(0.05, t - e.at));
      else if (drag.part === 'body') { canvas.style.cursor = 'grabbing'; e.at = round(Math.min(Math.max(0, drag.at0 + t - drag.t0), Math.max(0, duration - 0.05))); }
      else if (drag.part === 'back') e.back = round(drag.back0 - (t - drag.t0));
      draw();
    };
    canvas.onpointerup = () => {
      if (!drag) return;
      const was = drag;
      drag = null;
      if (was.part === 'scrub') { const t = scrubTo; scrubTo = null; seek(t ?? 0); return; }
      if (was.part === 'range') {
        if (live.range && live.range[1] - live.range[0] < 0.05) live.range = null;
        if (live.range && !live.loop) loopButton.onclick(); else if (live.source) startLive(here());
        draw();
        return;
      }
      sort(); commit(); rowsFor();
    };
    canvas.ondblclick = (event) => {
      const { xp, yp, t } = place(event), found = hit(xp, yp);
      if (found) { events.splice(found.index, 1); commit(); rowsFor(); } else add(t);
    };
    canvas.oncontextmenu = (event) => {
      event.preventDefault();
      const { xp, yp, t } = place(event), found = hit(xp, yp);
      const items = [];
      if (found) {
        const e = events[found.index], change = (fn) => () => { fn(e); sort(); commit(); rowsFor(); };
        items.push({ head: `Rewind at ${round(e.at)} s` },
          { label: 'Play from a second before', fn: () => { seek(Math.max(0, e.at - 1)); if (!live.source) startLive(live.offset); } },
          { label: 'Duplicate, 2 s later', fn: change(() => events.push({ ...e, at: round(Math.min(e.at + 2, Math.max(0, duration - 0.05))) })) },
          { label: 'Longer scratch (+0.5 s)', fn: change((v) => { v.scratch = round(v.scratch + 0.5); }) },
          { label: 'Shorter scratch (-0.5 s)', fn: change((v) => { v.scratch = round(Math.max(0.05, v.scratch - 0.5)); }) },
          { label: e.back < 0 ? 'Jump back instead of forward' : 'Jump forward instead of back', fn: change((v) => { v.back = -v.back; }) },
          '-',
          { label: 'Remove this rewind', fn: () => { events.splice(found.index, 1); commit(); rowsFor(); } });
      } else {
        items.push({ head: 'Rewind' }, { label: `Add a rewind here (${round(t)} s)`, fn: () => add(t) },
          { label: 'Play from here', fn: () => { seek(t); if (!live.source) startLive(live.offset); } });
        if (live.range) items.push({ label: 'Clear the loop range', fn: () => { live.range = null; if (live.source) startLive(here()); draw(); } });
      }
      showMenu(event.clientX, event.clientY, items);
    };
  }

  const bar = el('div', { className: 'rewind-bar' }, [play, loopButton, abButton, time]);
  root.append(bar, canvas, readout, rows);
  say();
  draw();
  return root;
}

registerStudioPanel('rewind', rewindPanel);
