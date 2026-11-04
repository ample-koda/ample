import { filterProjectFiles } from '../lib/filepicker-model.mjs';
import { api } from './api.js';
import { el, toast } from './ui.js';
import { onPlayer, playProjectFile, playerState, stop } from './player.js';
import { beginFileDrag } from './dragfile.js';
import { projectItemIcon } from './fileicons.js';

const previewCache = new Map();
const previewTargets = new WeakMap();
const listState = new WeakMap();
let previewObserver = null;
const virtualThreshold = 2000;
const virtualRowHeight = 104;
const virtualOverscan = 8;

function previewData(item) {
  const key = `${item.path}:${item.modified}:${item.sizeBytes}`;
  if (previewCache.has(key)) return previewCache.get(key);
  const request = item.kind === 'audio' ? api.projectFilePeaks(item.path).then((value) => ({ ...value, kind: 'audio' })) : api.projectFilePreview(item.path);
  previewCache.set(key, request);
  request.catch(() => previewCache.delete(key));
  return request;
}

function drawPreview(host, item, value) {
  host.replaceChildren();
  host.classList.remove('file-preview-placeholder');
  host.setAttribute('aria-label', `${item.kind} preview for ${item.name}`);
  if (value.kind === 'audio') {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', `0 0 ${value.peaks.length / 2} 40`);
    svg.setAttribute('preserveAspectRatio', 'none');   // the waveform fills the box whatever its shape: a wide thin one in a row, a taller one in a tile
    const points = [];
    for (let index = 0; index < value.peaks.length; index += 2) {
      const x = index / 2;
      points.push(`${x},${20 - value.peaks[index] * 18}`, `${x},${20 - value.peaks[index + 1] * 18}`);
    }
    const line = document.createElementNS('http://www.w3.org/2000/svg', 'polyline');
    line.setAttribute('points', points.join(' '));
    line.setAttribute('class', 'file-waveform-line');
    line.setAttribute('vector-effect', 'non-scaling-stroke');
    svg.append(line); host.append(svg, el('small', { textContent: `${value.seconds.toFixed(1)}s · ${value.rate} Hz` }));
  } else if (value.kind === 'midi') {
    const roll = el('span', { className: 'file-midi-roll' });
    const notes = value.notes || [];
    const pitches = notes.map((note) => note.pitch);
    const minPitch = Math.min(...pitches, 0), pitchRange = Math.max(1, Math.max(...pitches, 0) - minPitch + 1), end = Math.max(1, ...notes.map((note) => note.end));
    notes.forEach((note) => {
      const block = el('i', { title: `Pitch ${note.pitch}, tick ${note.start} to ${note.end}` });
      block.style.left = `${note.start / end * 100}%`; block.style.width = `${Math.max(0.7, (note.end - note.start) / end * 100)}%`; block.style.bottom = `${(note.pitch - minPitch) / pitchRange * 86}%`;
      roll.append(block);
    });
    host.append(roll, el('small', { textContent: `${value.bars} bars · ${value.noteCount} notes${value.truncated ? '+' : ''}` }));
  } else if (value.kind === 'song') host.textContent = `${value.tempo} BPM · ${value.blocks} blocks`;
  else if (value.kind === 'lyrics') host.textContent = value.lines.join(' · ') || 'No lyric lines';
  else host.textContent = item.kind.toUpperCase();
}

export function addProjectPreview(host, item) {
  if (item.isDir || !['audio', 'midi', 'song', 'lyrics'].includes(item.kind)) return;
  const preview = el('span', { className: `file-preview file-preview-${item.kind} file-preview-placeholder`, 'aria-label': `${item.kind} preview loading`, textContent: item.kind === 'audio' ? 'Audio · loading waveform...' : 'Preview loading...' });
  host.append(preview);
  previewTargets.set(preview, { item, host: preview });
  if (!('IntersectionObserver' in window)) {
    previewData(item).then((value) => drawPreview(preview, item, value)).catch(() => { preview.textContent = 'Preview unavailable'; });
    return;
  }
  previewObserver ||= new IntersectionObserver((entries) => entries.forEach((entry) => {
    if (!entry.isIntersecting) return;
    previewObserver.unobserve(entry.target);
    const target = previewTargets.get(entry.target);
    if (target) previewData(target.item).then((value) => drawPreview(target.host, target.item, value)).catch(() => { target.host.textContent = 'Preview unavailable'; });
  }), { rootMargin: '100px' });
  previewObserver.observe(preview);
}

export function renderProjectFileList(host, items, query, accept, selected, onSelect, onContext = null) {
  host.setAttribute('role', 'listbox');
  const rowH = () => Number(host.dataset.rowHeight) || virtualRowHeight;
  let state = listState.get(host);
  if (!state) {
    state = { items: [], render: null };
    host.tabIndex = 0;
    host.addEventListener('keydown', (event) => {
      const current = state.items.findIndex((item) => item.path === state.selected);
      const next = event.key === 'ArrowDown' ? Math.min(state.items.length - 1, current + 1) : event.key === 'ArrowUp' ? Math.max(0, current < 0 ? 0 : current - 1) : event.key === 'Home' ? 0 : event.key === 'End' ? state.items.length - 1 : -1;
      if (next < 0 || !state.items.length) return;
      event.preventDefault();
      if (state.items.length > virtualThreshold) host.scrollTop = next * rowH();
      state.onSelect(state.items[next]);
    });
    state.render = () => {
      const filtered = state.items;
      const scrolled = host.scrollTop;   // emptying the host clamps its scroll position to 0, so read it first and put it back
      host.replaceChildren();
      if (filtered.length <= virtualThreshold) { appendRows(filtered, 0, filtered.length); return; }
      const start = Math.max(0, Math.floor(scrolled / rowH()) - virtualOverscan);
      const count = Math.ceil(host.clientHeight / rowH()) + virtualOverscan * 2;
      const end = Math.min(filtered.length, start + count);
      const top = el('div', { className: 'file-list-spacer', 'aria-hidden': 'true' }); top.style.height = `${start * rowH()}px`;
      const bottom = el('div', { className: 'file-list-spacer', 'aria-hidden': 'true' }); bottom.style.height = `${(filtered.length - end) * rowH()}px`;
      host.append(top); appendRows(filtered, start, end); host.append(bottom);
      host.scrollTop = scrolled;
    };
    host.addEventListener('scroll', () => { if (host.scrollRaf) cancelAnimationFrame(host.scrollRaf); host.scrollRaf = requestAnimationFrame(state.render); }, { passive: true });
    listState.set(host, state);
  }
  function appendRows(filtered, start, end) {
  filtered.slice(start, end).forEach((item, offset) => {
    const row = el('button', { type: 'button', role: 'option', 'data-path': item.path, draggable: true, className: `file-list-row${item.isDir ? ' file-list-folder' : ''}${state.selected === item.path ? ' sel' : ''}`, 'aria-selected': String(state.selected === item.path) });
    row.tabIndex = state.selected === item.path ? 0 : -1;
    if (filtered.length > virtualThreshold) row.setAttribute('aria-posinset', String(start + offset + 1));
    if (filtered.length > virtualThreshold) row.setAttribute('aria-setsize', String(filtered.length));
    row.append(el('span', { className: 'file-list-name' }, [projectItemIcon(item), el('span', { textContent: item.name })]));
    // the row itself is a <button> (role="option"), so the listen cell must not be one too (no interactive descendants).
    if (!item.isDir) row.append(filePlayCell(item, 'span'));
    if (!item.isDir) addProjectPreview(row, item);
    row.ondragstart = (event) => beginFileDrag(event, item);
    row.onclick = () => state.onSelect(item);
    row.oncontextmenu = (event) => { if (state.onContext) { event.preventDefault(); event.stopPropagation(); state.onContext(event, item); } };
    host.append(row);
  });
  }
  state.items = filterProjectFiles(items, query, accept, true);
  state.selected = selected;
  state.onSelect = onSelect;
  state.onContext = onContext;
  host.oncontextmenu = (event) => { if (state.onContext && event.target === host) { event.preventDefault(); state.onContext(event, null); } };
  if (state.items.length > virtualThreshold && selected) {
    const selectedIndex = state.items.findIndex((item) => item.path === selected);
    if (selectedIndex >= 0 && (selectedIndex * rowH() < host.scrollTop || selectedIndex * rowH() > host.scrollTop + host.clientHeight)) host.scrollTop = selectedIndex * rowH();
  } else if (state.items.length <= virtualThreshold) host.scrollTop = 0;
  state.render();
}

/* ---------- a play and stop button for audio files, to listen quickly ---------- */
const AUDIO_FILE = /\.(wav|mp3|flac|ogg|aif|aiff)$/i;
let playButtonsWired = false;
function refreshPlayButtons() {
  const state = playerState();
  document.querySelectorAll('.file-play[data-play-path]').forEach((button) => {
    const on = state.playing && state.source === button.dataset.playPath;
    button.textContent = on ? '■' : '▶';
    button.classList.toggle('playing', on);
    button.setAttribute('aria-label', `${on ? 'Stop' : 'Play'} ${button.dataset.playPath}`);
    button.title = on ? 'Stop' : 'Play this file';
  });
}

/** The play and stop button of an audio file; for any other item an empty space of the same width, so the names
 * stay in line. `tag` defaults to a real `<button>`; pass `'span'` (with `role="button"` and a tab stop) when the
 * cell sits inside an already-interactive host (for example a project-file-picker row, itself a `<button>`) - a
 * `<button>` has no interactive descendants in its content model, so nesting one inside another is invalid. */
export function filePlayCell(item, tag = 'button') {
  if (!playButtonsWired) { playButtonsWired = true; onPlayer(refreshPlayButtons); }
  if (item.isDir || !AUDIO_FILE.test(item.name || item.path || '')) return el('span', { className: 'file-play-space', 'aria-hidden': 'true' });
  const isButton = tag === 'button';
  const button = el(tag, isButton ? { type: 'button', className: 'file-play', textContent: '▶' } : { role: 'button', tabIndex: 0, className: 'file-play', textContent: '▶' });
  button.dataset.playPath = item.path;
  button.title = 'Play this file';
  button.setAttribute('aria-label', `Play ${item.path}`);
  button.onpointerdown = (event) => event.stopPropagation();
  button.ondblclick = (event) => event.stopPropagation();
  const activate = (event) => {
    event.stopPropagation();
    const state = playerState();
    if (state.playing && state.source === item.path) stop();
    else playProjectFile(item.path).catch((error) => { if (!error.toasted) toast(error.message || String(error)); });
  };
  button.onclick = activate;
  if (!isButton) button.onkeydown = (event) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); activate(event); } };
  button.draggable = false;
  queueMicrotask(refreshPlayButtons);
  return button;
}
