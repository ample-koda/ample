import { registerPlaybackAudio } from './playback-volume.js';
// A preview window for a sound file of the project: it floats over the page (nothing behind it is blocked), shows the waveform, plays and stops, and the file in it can be
// dragged to the places that take a file, like from the project panel. It stays open until it is closed, so one sound can be tried and then put in several places.
// @ample:float-preview

import { beginFileDrag } from './dragfile.js';
import { stop as stopPlayer } from './player.js';
import { el, showMenu, toast } from './ui.js';
import { copyMenuItem, copyText } from './copy-path.js';

const WIDTH = 460, HEIGHT = 96;
const open = new Map();   // path -> its window
let playing = null;       // the window that plays now: one sound at a time

const clock = (seconds) => { const total = Math.max(0, seconds), minutes = Math.floor(total / 60); return `${minutes}:${(total - minutes * 60).toFixed(1).padStart(4, '0')}`; };

/** Opens (or brings forward) the preview window of a sound file. @param {{ path: string, name?: string }} item */
export async function openFilePreview(item) {
  const existing = open.get(item.path);
  if (existing) { existing.element.style.zIndex = String(nextLayer()); existing.play(); return; }
  stopPlayer();
  const name = item.name || item.path.split('/').pop();
  const audio = registerPlaybackAudio(new Audio(`/api/project/audio?path=${encodeURIComponent(item.path)}`));
  audio.preload = 'auto';
  const canvas = el('canvas', { width: WIDTH, height: HEIGHT, className: 'fp-wave', title: 'Click to play from there' });
  const playButton = el('button', { type: 'button', className: 'primary fp-play', textContent: '▶ Play', title: 'Play (and stop)' });
  const time = el('span', { className: 'fp-time', textContent: '0:00.0' });
  const loop = el('input', { type: 'checkbox', 'aria-label': 'Repeat' });
  const chip = el('div', { className: 'fp-chip', draggable: true, title: 'Drag this onto a field, the canvas or the sequencer, like a file of the project panel', textContent: `⠿ ${name}` });
  const close = el('button', { type: 'button', className: 'fp-close', textContent: '✕', title: 'Close', 'aria-label': `Close the preview of ${name}` });
  // the path: a click copies it, a right-click offers the name, the path in the project and the full path
  const pathText = el('button', { type: 'button', className: 'note fp-path', textContent: item.path, title: 'Click to copy the path; right-click to copy the name or the full path' });
  pathText.onclick = () => copyText(item.path, 'path');
  pathText.oncontextmenu = (event) => { event.preventDefault(); showMenu(event.clientX, event.clientY, copyMenuItem(item.path).children); };
  const box = el('div', { className: 'float-preview', role: 'dialog', 'aria-label': `Preview of ${name}` }, [
    el('div', { className: 'fp-head' }, [el('b', { textContent: name, title: item.path }), el('span', { className: 'sp' }), close]),
    canvas,
    el('div', { className: 'fp-row' }, [playButton, time, el('label', {}, [loop, ' repeat'])]),
    el('div', { className: 'fp-row' }, [chip, pathText]),
  ]);
  box.style.left = `${Math.max(8, (innerWidth - WIDTH - 30) / 2 + open.size * 28)}px`;
  box.style.top = `${Math.max(8, innerHeight / 2 - 150 + open.size * 28)}px`;
  box.style.zIndex = String(nextLayer());
  document.body.append(box);

  let peaks = null, frame = 0;
  const draw = () => {
    const g = canvas.getContext('2d');
    g.clearRect(0, 0, WIDTH, HEIGHT);
    const mid = HEIGHT / 2;
    g.fillStyle = getComputedStyle(document.documentElement).getPropertyValue('--acc').trim() || '#56a0f0';
    if (peaks) peaks.forEach(([lo, hi], x) => g.fillRect(x, mid + lo * mid, 1, Math.max(1, (hi - lo) * mid)));
    if (audio.duration) { const x = (audio.currentTime / audio.duration) * WIDTH; g.fillStyle = 'rgba(255,255,255,.85)'; g.fillRect(x, 0, 1.5, HEIGHT); g.fillStyle = 'rgba(86,160,240,.18)'; g.fillRect(0, 0, x, HEIGHT); }
    time.textContent = `${clock(audio.currentTime)} / ${clock(Number.isFinite(audio.duration) ? audio.duration : 0)}`;
    playButton.textContent = audio.paused ? '▶ Play' : '■ Stop';
    if (!audio.paused) frame = requestAnimationFrame(draw);
  };
  const play = () => {
    if (!audio.paused) { audio.pause(); audio.currentTime = 0; draw(); return; }
    if (playing && playing !== api) playing.pause();
    playing = api;
    stopPlayer();
    audio.play().catch((error) => toast(error.message || String(error)));
    frame = requestAnimationFrame(draw);
  };
  const api = { element: box, play, pause: () => { audio.pause(); draw(); } };
  open.set(item.path, api);
  playButton.onclick = play;
  audio.onended = () => { if (loop.checked) { audio.currentTime = 0; audio.play(); } else { audio.currentTime = 0; draw(); } };
  canvas.onpointerdown = (event) => { const r = canvas.getBoundingClientRect(); if (audio.duration) audio.currentTime = ((event.clientX - r.left) / r.width) * audio.duration; if (audio.paused) play(); else draw(); };
  chip.ondragstart = (event) => beginFileDrag(event, { path: item.path, name, isDir: false });
  const dispose = () => { cancelAnimationFrame(frame); audio.pause(); audio.removeAttribute('src'); open.delete(item.path); if (playing === api) playing = null; box.remove(); };
  close.onclick = dispose;
  box.onkeydown = (event) => { if (event.key === 'Escape') dispose(); };

  // the window is moved by dragging almost any part of it
  box.onpointerdown = (event) => {
    box.style.zIndex = String(nextLayer());
    if (event.button !== 0 || event.target.closest('button, input, label, canvas, .fp-chip')) return;
    const x0 = event.clientX, y0 = event.clientY, left = box.offsetLeft, top = box.offsetTop;
    const move = (ev) => { box.style.left = `${Math.max(0, Math.min(innerWidth - 80, left + ev.clientX - x0))}px`; box.style.top = `${Math.max(0, Math.min(innerHeight - 40, top + ev.clientY - y0))}px`; };
    const up = () => { removeEventListener('pointermove', move); removeEventListener('pointerup', up); };
    addEventListener('pointermove', move); addEventListener('pointerup', up);
  };

  // the waveform
  try {
    const buffer = await new OfflineAudioContext(1, 1, 44100).decodeAudioData(await (await fetch(`/api/project/audio?path=${encodeURIComponent(item.path)}`)).arrayBuffer());
    const data = buffer.getChannelData(0), step = Math.max(1, Math.floor(data.length / WIDTH));
    peaks = Array.from({ length: WIDTH }, (_, x) => { let lo = 0, hi = 0; for (let i = x * step, end = Math.min(data.length, i + step); i < end; i += Math.max(1, step >> 6)) { if (data[i] < lo) lo = data[i]; if (data[i] > hi) hi = data[i]; } return [lo, hi]; });
  } catch { /* the file cannot be decoded: the window still plays it when the browser can */ }
  draw();
  play();
}

let layer = 4000;
const nextLayer = () => ++layer;
