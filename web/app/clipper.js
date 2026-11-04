import { registerPlaybackAudio } from './playback-volume.js';
// Choosing the part of an audio file that an import-audio block uses (its start and end): the waveform of the file, a drag to select a region, handles to move
// the start and the end, the mouse wheel to zoom (Shift + wheel to move along), a preview of just that part (once or repeated) and Apply, which writes start
// and end to the block. @ample:clipper

import { stop as stopPlayer } from './player.js';
import { el } from './ui.js';

const WIDTH = 720, HEIGHT = 120, GRAB = 8, MIN_PART = 0.02;

const seconds = (value) => { const n = Number(String(value ?? '').replace(/[^0-9.]/g, '')); return Number.isFinite(n) ? n : 0; };

/**
 * Opens the dialog for a project audio file. Resolves with { start, end, whole } in seconds when the part is applied, or null when it is cancelled.
 * @param {string} path project path of the audio file
 * @param {{ start?: string, end?: string }} current the values the block has now (empty: the whole file)
 * @param {{ url?: string }} [source] the audio to show when it is not a file of the project (the take that feeds a cut block); `path` is then only its label
 */
export async function chooseAudioPart(path, current = {}, source = {}) {
  stopPlayer();
  const url = source.url || `/api/project/audio?path=${encodeURIComponent(path)}`;
  const buffer = await new OfflineAudioContext(1, 1, 44100).decodeAudioData(await (await fetch(url)).arrayBuffer());
  const total = buffer.duration, data = buffer.getChannelData(0);
  let start = Math.min(total, seconds(current.start)), end = seconds(current.end) > 0 ? Math.min(total, seconds(current.end)) : total;
  if (end <= start) { start = 0; end = total; }
  let viewStart = 0, viewEnd = total;   // the part of the file that the waveform shows

  const dialog = el('dialog', { className: 'clipper', 'aria-labelledby': 'clipper-title' });
  const canvas = el('canvas', { width: WIDTH, height: HEIGHT, className: 'clipper-wave', title: 'Drag over the waveform to choose a part, drag the yellow handles to adjust it, scroll to zoom (Shift + scroll to move along)' });
  const startInput = el('input', { type: 'number', min: 0, step: 0.01, 'aria-label': 'Start in seconds' });
  const endInput = el('input', { type: 'number', min: 0, step: 0.01, 'aria-label': 'End in seconds' });
  const repeat = el('input', { type: 'checkbox', checked: true, 'aria-label': 'Repeat the preview' });
  const playButton = el('button', { type: 'button', textContent: '▶ Preview part' });
  const info = el('span', { className: 'note' });
  const audio = registerPlaybackAudio(new Audio(url));
  let timer = 0;

  const x = (time) => ((time - viewStart) / (viewEnd - viewStart)) * WIDTH;
  const at = (px) => viewStart + (px / WIDTH) * (viewEnd - viewStart);
  const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

  const draw = (playhead = null) => {
    const g = canvas.getContext('2d');
    g.clearRect(0, 0, WIDTH, HEIGHT);
    g.fillStyle = 'rgba(86,160,240,.18)'; g.fillRect(x(start), 0, x(end) - x(start), HEIGHT);
    // one min and max per pixel column of what is shown
    const perColumn = ((viewEnd - viewStart) * buffer.sampleRate) / WIDTH, mid = HEIGHT / 2;
    g.fillStyle = '#56a0f0';
    for (let column = 0; column < WIDTH; column++) {
      const from = Math.floor(viewStart * buffer.sampleRate + column * perColumn), to = Math.min(data.length, Math.max(from + 1, Math.floor(from + perColumn)));
      let lo = 0, hi = 0;
      const stride = Math.max(1, Math.floor((to - from) / 64));   // enough samples to see the shape, not all of them
      for (let i = from; i < to; i += stride) { const v = data[i]; if (v < lo) lo = v; if (v > hi) hi = v; }
      g.fillRect(column, mid + lo * mid, 1, Math.max(1, (hi - lo) * mid));
    }
    g.fillStyle = '#e8c35b';
    g.fillRect(x(start) - 1, 0, 3, HEIGHT); g.fillRect(x(end) - 2, 0, 3, HEIGHT);
    g.fillRect(x(start) - 1, 0, 9, 10); g.fillRect(x(end) - 8, HEIGHT - 10, 9, 10);   // the grips of the handles
    if (playhead !== null) { g.fillStyle = '#fff'; g.fillRect(x(playhead), 0, 1, HEIGHT); }
    info.textContent = `${start.toFixed(2)} s to ${end.toFixed(2)} s · ${(end - start).toFixed(2)} s of ${total.toFixed(2)} s · showing ${viewStart.toFixed(2)} to ${viewEnd.toFixed(2)} s`;
  };
  const sync = () => { startInput.value = start.toFixed(2); endInput.value = end.toFixed(2); draw(); };
  const stopPreview = () => { clearInterval(timer); audio.pause(); playButton.textContent = '▶ Preview part'; draw(); };
  const play = () => {
    if (!audio.paused) { stopPreview(); return; }
    audio.currentTime = start; audio.play();
    playButton.textContent = '■ Stop preview';
    timer = setInterval(() => {
      if (audio.currentTime >= end - 0.01) { if (repeat.checked) audio.currentTime = start; else { stopPreview(); return; } }
      draw(audio.currentTime);
    }, 30);
  };

  const scale = () => WIDTH / canvas.getBoundingClientRect().width;
  const localX = (event) => (event.clientX - canvas.getBoundingClientRect().left) * scale();
  const handleAt = (px) => (Math.abs(px - x(start)) <= GRAB ? 'start' : Math.abs(px - x(end)) <= GRAB ? 'end' : null);

  canvas.onpointermove = (event) => { if (!canvas.hasPointerCapture(event.pointerId)) canvas.style.cursor = handleAt(localX(event)) ? 'ew-resize' : 'crosshair'; };
  canvas.onpointerdown = (event) => {
    canvas.setPointerCapture(event.pointerId);
    const px = localX(event), grabbed = handleAt(px), anchor = at(px);
    if (!grabbed) { start = clamp(anchor, 0, total); end = Math.min(total, start + MIN_PART); sync(); }
    canvas.onpointermove = (move) => {
      const here = clamp(at(localX(move)), 0, total);
      if (grabbed === 'start') start = Math.min(here, end - MIN_PART);
      else if (grabbed === 'end') end = Math.max(here, start + MIN_PART);
      else { start = Math.min(anchor, here); end = Math.max(anchor, here, start + MIN_PART); }
      sync();
    };
    canvas.onpointerup = () => { canvas.onpointermove = null; canvas.onpointerup = null; canvas.onpointermove = (m) => { if (!canvas.hasPointerCapture(m.pointerId)) canvas.style.cursor = handleAt(localX(m)) ? 'ew-resize' : 'crosshair'; }; };
  };
  // the wheel zooms around the pointer; with Shift it moves along the file
  canvas.onwheel = (event) => {
    event.preventDefault();
    const span = viewEnd - viewStart;
    if (event.shiftKey) {
      const move = (event.deltaY || event.deltaX) / 500 * span;
      viewStart = clamp(viewStart + move, 0, total - span); viewEnd = viewStart + span;
    } else {
      const focus = at(localX(event)), factor = event.deltaY < 0 ? 0.8 : 1.25, next = clamp(span * factor, 0.05, total);
      viewStart = clamp(focus - (focus - viewStart) * (next / span), 0, total - next); viewEnd = viewStart + next;
    }
    draw();
  };
  startInput.onchange = () => { start = clamp(seconds(startInput.value), 0, end - MIN_PART); sync(); };
  endInput.onchange = () => { end = clamp(seconds(endInput.value), start + MIN_PART, total); sync(); };
  playButton.onclick = play;

  const whole = el('button', { type: 'button', textContent: 'Whole file', onclick: () => { start = 0; end = total; sync(); } });
  const zoomPart = el('button', { type: 'button', textContent: 'Zoom to part', title: 'Show just the chosen part', onclick: () => { const pad = (end - start) * 0.1; viewStart = clamp(start - pad, 0, total); viewEnd = clamp(end + pad, viewStart + 0.05, total); draw(); } });
  const zoomAll = el('button', { type: 'button', textContent: 'Show all', onclick: () => { viewStart = 0; viewEnd = total; draw(); } });
  const apply = el('button', { type: 'button', className: 'primary', textContent: 'Use this part' });
  const cancel = el('button', { type: 'button', textContent: 'Cancel' });
  dialog.append(
    el('h2', { id: 'clipper-title', textContent: 'Choose the part to use' }),
    el('p', { className: 'note', textContent: path }),
    canvas,
    el('div', { className: 'row' }, [el('label', {}, ['Start ', startInput, ' s']), el('label', {}, ['End ', endInput, ' s']), whole, zoomPart, zoomAll]),
    el('div', { className: 'row' }, [info]),
    el('div', { className: 'row' }, [playButton, el('label', {}, [repeat, ' repeat']), el('span', { className: 'sp' }), cancel, apply]),
  );
  document.body.append(dialog);
  sync();
  return new Promise((resolve) => {
    const close = (value) => { clearInterval(timer); audio.pause(); dialog.close(); dialog.remove(); resolve(value); };
    apply.onclick = () => close({ total, start: Number(start.toFixed(2)), end: Number(end.toFixed(2)), whole: start <= 0.005 && end >= total - 0.005 });
    cancel.onclick = () => close(null);
    dialog.oncancel = (event) => { event.preventDefault(); close(null); };
    dialog.showModal();
  });
}
