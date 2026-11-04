import { el, toast } from './ui.js';
import { onPlayer, playProjectFile, playerState, seekFraction, setLoop, stop } from './player.js';
import { addProjectPreview } from './filelist.js';

let active = null;
const tiles = new Set();
const rows = new Set();

export function clearTilePreviews() {
  if (active && playerState().source === active.path) stop();
  active = null;
  tiles.clear();
  rows.clear();
}

onPlayer((state) => {
  for (const entry of rows) {
    if (!entry.row.isConnected) { rows.delete(entry); continue; }
    const own = state.source === entry.path && state.playing;
    entry.row.classList.toggle('project-row-playing', own);
    entry.host.hidden = !own;
    if (own && !entry.loaded) { entry.loaded = true; addProjectPreview(entry.host, entry.item); }
    entry.scrub.value = state.duration && own ? String(state.time / state.duration * 1000) : '0';
    entry.host.style.setProperty('--preview-position', `${state.duration && own ? state.time / state.duration * 100 : 0}%`);
  }
  for (const tile of tiles) {
    const own = state.source === tile.path;
    tile.play.textContent = own && state.playing ? '■' : '▶';
    tile.play.title = own && state.playing ? 'Stop preview' : 'Play preview';
    tile.play.setAttribute('aria-label', tile.play.title);
    if (!tile.scrubbing) tile.scrub.value = own && state.duration ? String(state.time / state.duration * 1000) : '0';
    tile.host.style.setProperty('--preview-position', `${own && state.duration ? state.time / state.duration * 100 : 0}%`);
  }
});

export function addTileControls(host, item) {
  if (item.isDir || item.kind !== 'audio') return;
  const play = el('button', { type: 'button', textContent: '▶', title: 'Play preview', 'aria-label': 'Play preview' });
  const loop = el('button', { type: 'button', textContent: '↻', title: 'Loop preview', 'aria-label': 'Loop preview', 'aria-pressed': 'false' });
  const scrub = el('input', { type: 'range', min: '0', max: '1000', value: '0', 'aria-label': `Scrub ${item.name}` });
  const controls = el('div', { className: 'project-tile-controls' }, [play, scrub, loop]);
  const tile = { path: item.path, host, play, scrub, scrubbing: false, looping: false };
  tiles.add(tile);
  controls.onclick = controls.ondblclick = controls.onpointerdown = (event) => event.stopPropagation();
  controls.draggable = false;
  controls.ondragstart = (event) => { event.preventDefault(); event.stopPropagation(); };
  play.onclick = async (event) => {
    event.stopPropagation();
    if (playerState().source === tile.path && playerState().playing) { stop(); return; }
    active = tile;
    play.disabled = true;
    host.classList.add('is-loading');   // the sound is loaded in the browser first: a shimmer over the waveform says so
    play.setAttribute('aria-busy', 'true');
    try { await playProjectFile(tile.path); if (active === tile && host.isConnected && playerState().source === tile.path) setLoop(tile.looping); }
    catch (error) { if (active === tile && !error.toasted) toast(error.message); }
    finally { play.disabled = false; host.classList.remove('is-loading'); play.removeAttribute('aria-busy'); }
  };
  loop.onclick = (event) => {
    event.stopPropagation(); tile.looping = !tile.looping;
    loop.setAttribute('aria-pressed', String(tile.looping));
    if (active === tile && playerState().source === tile.path) setLoop(tile.looping);
  };
  scrub.onpointerdown = () => { tile.scrubbing = true; };
  scrub.oninput = () => { if (active === tile && playerState().source === tile.path) seekFraction(Number(scrub.value) / 1000); };
  scrub.onpointerup = scrub.onpointercancel = () => { tile.scrubbing = false; if (!host.matches(':hover')) leave(); };
  const leave = () => {
    if (active !== tile) return;
    active = null;
    if (playerState().source === tile.path) stop();
  };
  host.addEventListener('pointerleave', () => { if (!tile.scrubbing) leave(); });
  host.addEventListener('focusout', (event) => { if (!host.contains(event.relatedTarget) && !host.matches(':hover')) leave(); });
  host.addEventListener('dragstart', leave);
  controls.replaceChildren(play, loop);
  const waveform = host.querySelector('.file-preview');
  if (waveform) { const seek = el('div', { className: 'project-wave-seek' }); waveform.replaceWith(seek); seek.append(waveform, scrub); seek.onclick = seek.ondblclick = (event) => event.stopPropagation(); seek.onpointerdown = (event) => event.stopPropagation(); seek.ondragstart = (event) => { event.preventDefault(); event.stopPropagation(); }; }
  host.append(controls);
}

export function addDetailPreview(row, cell, item) {
  if (item.isDir || item.kind !== 'audio') return;
  const scrub = el('input', { type: 'range', min: '0', max: '1000', value: '0', 'aria-label': `Scrub ${item.name}` });
  const host = el('div', { className: 'project-wave-seek project-row-wave', hidden: true }, [scrub]);
  host.onclick = host.ondblclick = host.onpointerdown = (event) => event.stopPropagation();
  host.ondragstart = (event) => { event.preventDefault(); event.stopPropagation(); };
  scrub.oninput = () => { if (playerState().source === item.path) seekFraction(Number(scrub.value) / 1000); };
  rows.add({ row, host, scrub, path: item.path, item, loaded: false });
  cell.append(host);
}

const stopActive = () => { if (active && playerState().source === active.path) stop(); active = null; };
document.addEventListener('visibilitychange', () => { if (document.hidden) stopActive(); });
document.addEventListener('click', (event) => { if (event.target.closest('#btabs button:not([data-t="project"])')) stopActive(); });
window.addEventListener('blur', () => { if (active && playerState().source === active.path) stop(); active = null; });
