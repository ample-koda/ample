// The canvas: pan, zoom, selection, blocks, ports and links, minimap, breadcrumb. Looks and behaves like the sketch.
// Every change of the song is an edit call to the server; this file keeps no authoritative state.
// @ample:canvas @ample:ports-links @ample:groups

import { llamaAvatarButton } from './llama-avatar.js';
import { derivedChips, derivedMenuItems } from './package.js';
import { aiItems } from './ai.js';
import { $, el, showMenu, toast, ask } from './ui.js';
import { HEAD, ROW, W, baseWidth, sizes, compatible, fitView, fitPhoneView, fitReadableView, linkPath, nodeHeight, nodeWidth, nodesInRect, portPos, snap, toWorld, zoomAt, align, distribute, bounds } from './geometry.js';
import { containerView, groupBlocks, ungroup } from './graph.js';
import { S, bakeDynamicHelpers, blockReadsClock, quiet, recordGroups, saveViewSoon, setPinned, subscribeProgress, build, container, pinUnplaced, current, edit, flat, notify, notifySelection, readOnly, graphBroken, blockProblems, saveGroups, select, undo, redo, enterModule, setModuleLevel, applyText, openDoc, enterMidiVariant } from './store.js';
import { gotoSource, showBlockStatusError } from './panels.js';
import { componentMenuItems } from './components.js';
import { registerPlaybackAudio } from './playback-volume.js';
import { download, drawPeaks, peaksOf, playerState, seekTake, stop, toggle, playLivePreview, updateLivePreview, setAuditionBlock, onPlayer } from './player.js';
import { liveEffectGraph } from './studio/preview/audio.js';
import { renderComponentUi } from './component-ui.js';
import { liveOptions } from './component-live-ui.js';
import { openVariants } from './variants.js';
import { openLyrics } from './lyrics.js';
import { api } from './api.js';
import { isExpression, seedSites, stableNameOf } from '../lib/note-pipeline.mjs';
import { refKey } from '../lib/block-activity.mjs';
import { openMidiCompare } from './midi-compare-view.js';
import { setView } from './views.js';
import { openStudioPanel } from './studio/modal.js';
import { hasStudioPanel } from './studio/panels/registry.js';
import { volumeControls } from './studio/panels/volume.js';
import { portHint } from '../lib/port-names.mjs';
import { chainText, regionChain } from '../lib/region-chain.mjs';
import { openPortPreview } from './port-preview.js';
import { regionReveal } from './graph.js';
import { seamSpans, seamText } from '../lib/seam-spans.mjs';
import { tapConnect } from '../lib/touch-gestures.mjs';
import { meterLine, parseMeterReport } from '../lib/meter-report.mjs';
import { effectChainEdit } from '../lib/effect-chain-edit.mjs';
import { autoArrange } from '../lib/auto-arrange.mjs';
import { parseView } from '../lib/view-lines.mjs';
import { blocksFor, freeBlockName, kindOf, paramAccepts } from '../lib/file-kinds.mjs';
import { UPLOAD_FOLDER, draggedFile } from './dragfile.js';
import { uploadProjectFiles } from './filepicker.js';
import { renderBlockToFile } from './renderfile.js';
import { TINTS, tintOf } from '../lib/tint.mjs';
import { barBeats } from '../lib/meter.mjs';

const cv = $('#cv');
const world = $('#world');
const linksSvg = $('#links');
const SVG = 'http://www.w3.org/2000/svg';
let tmp = null;
let mini = { x0: 0, y0: 0, s: 1 };

const linkId = (l) => `${l[0]}|${l[1]}|${l[2]}|${l[3]}`;
const nodeById = (id) => current().nodes.find((n) => n.id === id);
const canvasRect = () => cv.getBoundingClientRect();
const point = (cx, cy) => toWorld(S.view, canvasRect(), cx, cy);

/** Marks the selected blocks without rebuilding the canvas. */
export function paintSelection() {
  world.querySelectorAll('.node').forEach((d) => d.classList.toggle('sel', S.selSet.has(d.dataset.id)));
  drawLinks();
  drawMini();
}

export function applyView() {
  world.style.transform = `translate(${S.view.x}px,${S.view.y}px) scale(${S.view.k})`;
  const g = 40 * S.view.k;
  cv.style.backgroundSize = `${g}px ${g}px`;
  cv.style.backgroundPosition = `${S.view.x}px ${S.view.y}px`;
  $('#z-pct').textContent = `${Math.round(S.view.k * 100)}%`;
  drawMini();
}

// the view switcher (Blocks / Source / ...) floats at the top of the canvas: a fitted view keeps that band free so no block title ends up under it
const VIEW_SWITCH_BAND = 44;

/** all: the whole graph in view, however small; otherwise a phone starts at a readable zoom at the top left (the Fit button and the F key ask for all) */
export function fit(all = false) {
  const r = canvasRect();
  S.view = (all === true ? fitView : fitPhoneView)(current().nodes, { width: r.width, height: r.height }, 60, VIEW_SWITCH_BAND);
  applyView();
}

export function fitInitialView() {
  const r = canvasRect();
  S.view = fitReadableView(current().nodes, { width: r.width, height: r.height }, VIEW_SWITCH_BAND);
  applyView();
}

function zoomBy(f) {
  const r = canvasRect();
  S.view = zoomAt(S.view, r.width / 2, r.height / 2, f);
  applyView();
}

export function focusNode(n) {
  const r = canvasRect();
  const k = Math.max(S.view.k, 0.9);
  world.style.transition = 'transform .35s';
  S.view = { k, x: r.width / 2 - (n.x + nodeWidth(n) / 2) * k, y: r.height / 2 - (n.y + 40) * k };
  applyView();
  setTimeout(() => { world.style.transition = ''; }, 400);
}

/** Select, focus and open the context menu for a node reached from the source editor. */
export function showConnectionContextMenu(to, source, output, input) {
  const real = flat().links.find((link) => link.to === to && link.from === source && link.outName === output && link.inName === input);
  if (!real) { toast('This source connection could not be found in the node editor.'); return; }
  const l = current().links.find((link) => realLinksOf(link).includes(real));
  if (!l) { toast('Open the group containing this connection to select it.'); return; }
  S.selLink = linkId(l); S.sel = null; S.selSet.clear(); notify();
  const v = current(), a = v.nodes.find((n) => n.id === l[0]), b = v.nodes.find((n) => n.id === l[2]);
  if (!a || !b) return;
  const from = portPos(a, 'out', l[1]), toPos = portPos(b, 'in', l[3]), r = canvasRect(), mid = { x: (from.x + toPos.x) / 2, y: (from.y + toPos.y) / 2 };
  S.view.x = r.width / 2 - mid.x * S.view.k; S.view.y = r.height / 2 - mid.y * S.view.k; applyView();
  showMenu(r.left + r.width / 2, r.top + r.height / 2, [{ head: `${real.type} connection` }, { label: real.disabled ? 'Enable connection' : 'Disable connection', fn: () => edit([{ type: 'setConnectionDisabled', to: real.to, input: real.inName, disabled: !real.disabled }]) }, { label: 'Delete link', danger: true, fn: () => edit([...pinUnplaced(), { type: 'disconnect', to: real.to, input: real.inName }]) }]);
}

/* ---------- drawing ---------- */
export function renderGraph() {
  const v = current();
  // only blocks were moved and they already are where they were dropped: nothing is drawn again, so the waveforms stay
  if (quiet.layout && world.querySelectorAll('.node').length === v.nodes.length && v.nodes.every((n) => world.querySelector(`.node[data-id="${CSS.escape(n.id)}"]`))) {
    for (const n of v.nodes) { const de = world.querySelector(`.node[data-id="${CSS.escape(n.id)}"]`); de.style.left = `${n.x}px`; de.style.top = `${n.y}px`; }
    drawLinks();
    return;
  }
  const focused = document.activeElement && document.activeElement.classList && document.activeElement.classList.contains('node') ? document.activeElement.dataset.id : null;
  world.querySelectorAll('.node').forEach((n) => { n.querySelector('.volume-controls')?.dispose?.(); n.remove(); });
  v.nodes.forEach((n) => world.append(nodeEl(n)));
  if (focused) {
    const again = world.querySelector(`.node[data-id="${CSS.escape(focused)}"]`);
    if (again) again.focus({ preventScroll: true });
  }
  drawLinks();
  renderCrumbs();
  applyView();
  $('#canvaswrap').classList.toggle('romode', readOnly());
  $('#canvaswrap').classList.toggle('broken', graphBroken());
  if (linkMark) linkMark();   // a link is being dragged: the new ports get their marks again
}

/** The sound lines of a group (the audio ones), each with the block behind it. */
function groupAudioLines(n) {
  return (n.outKeys || []).map((key, index) => ({ key, index, label: n.outs[index][0], type: n.outs[index][1], block: key.slice(0, key.lastIndexOf(':')) })).filter((line) => line.type === 'audio');
}
const chosenLine = (n) => { const lines = groupAudioLines(n); return lines.find((line) => line.key === S.groups[n.id]?.playOut) || lines[0]; };

/** The status of a group: what its blocks say together (built when all of them are, building while one is, failed when one failed). */
function groupStatOf(n) {
  const members = S.groups[n.id]?.members || [];
  const states = members.map((id) => S.build.status[id]);
  if (states.some((b) => b?.status === 'failed')) return { cls: 'idle', txt: 'failed', st: '', bad: true, tip: states.find((b) => b?.status === 'failed')?.message };
  if (S.build.running && states.some((b) => !b)) return { cls: 'run', txt: 'building', st: 'running' };
  const done = states.filter((b) => b && ['rendered', 'cached', 'frozen', 'deferred'].includes(b.status)).length;
  if (members.length && done === members.length) return states.every((b) => b.status === 'cached' || b.status === 'frozen') ? { cls: 'hit', txt: 'cache hit', st: 'done' } : { cls: 'ok', txt: 'built', st: 'done' };
  return done ? { cls: 'idle', txt: `${done} of ${members.length} built`, st: '' } : { cls: 'idle', txt: 'not built', st: '' };
}

export function statOf(n) {
  if (n.isGroup) return groupStatOf(n);
  const end = flat().nodes.filter((node) => node.line > n.line).reduce((next, node) => Math.min(next, node.line), Infinity);
  const diagnostic = (S.songDiagnostics || []).find((item) => (item.severity === 'error' || item.severity === 1) && (item.range?.start.line ?? item.line - 1) >= n.line && (item.range?.start.line ?? item.line - 1) < end);
  if (diagnostic) return { cls: 'invalid', txt: 'invalid block', st: '', bad: true, tip: `${diagnostic.code}: ${diagnostic.message}`, diagnostic };
  if (n.disabledBy || n.flags.includes('disabled')) return { cls: 'idle', txt: n.flags.includes('disabled') ? 'disabled' : 'upstream disabled', st: '', tip: `Disabled by ${n.disabledBy || n.id}` };
  const b = S.build.status[n.id];
  const live = S.build.live && S.build.live[n.id];
  if (S.build.running && live && ['running', 'queued'].includes(live.state)) return live.state === 'running' ? { cls: 'run', txt: 'building', st: 'running' } : { cls: 'idle', txt: 'waiting', st: '' };
  if (S.build.running && !b && S.build.only && !live) return { cls: 'idle', txt: 'not built', st: '' };   // a one-block build only touches that block and what it needs
  if (S.build.running && !b) return { cls: 'run', txt: 'building', st: 'running' };
  if (!b) return { cls: 'idle', txt: 'not built', st: '' };
  switch (b.status) {
        case 'rendered': return { cls: 'ok', txt: 'newly rendered in the latest build of this block', st: 'done', tip: [b.message, b.at ? new Date(b.at).toLocaleString() : ''].filter(Boolean).join(' — ') };
    case 'deferred': return { cls: 'hit', txt: 'recipe only', st: 'done', tip: 'This source recipe is rendered when an audio output requests it.' };
    case 'cached': case 'frozen': return { cls: 'hit', txt: 'existing take reused (cache hit)', st: 'done', tip: [b.message, b.at ? new Date(b.at).toLocaleString() : ''].filter(Boolean).join(' — ') };
    case 'failed': return { cls: 'invalid', txt: 'build failed', st: '', bad: true, tip: b.message };
    case 'skipped': return { cls: 'blocked', txt: 'blocked by another error', st: '', bad: true, tip: b.message };
    default: return { cls: 'idle', txt: b.status, st: '' };
  }
}

/**
 * The progress of a block, without changing its size: the status tag names the step that is busy (for example "synthesis 13%"), a thin bar along the
 * bottom edge shows the whole block, and while the pointer is on the block a panel opens below it with every sub-step (each 0 to 100%; a step that is
 * busy without a known end is striped). The panel floats over what is below, so nothing moves.
 */
function paintOne(d) {
  const live = S.build.running && S.build.live ? S.build.live[d.dataset.id] : null;
  let bar = d.querySelector('.nbar');
  let more = d.querySelector('.nmore');
  let tag = d.querySelector('.tag.stat');
  const statusIcon = d.querySelector('.node-status');
  if (live?.state === 'running' && statusIcon) { statusIcon.className = 'node-status run'; statusIcon.title = `Building — ${live.steps.at(-1)?.name || 'starting'}`; }
  if (!live || live.state !== 'running') { bar?.remove(); more?.remove(); d.classList.remove('progress-above'); delete d.dataset.progressStep; delete d.dataset.progressStepAt; return; }
  if (!tag) { tag = el('span', { className: 'tag stat run' }); d.querySelector('.nfoot')?.prepend(tag); }
  const wholeKnown = live.fraction > 0 || !live.steps.length;
  const whole = Math.round(live.fraction * 100);
  const pct = (s) => (s.fraction === null || s.fraction === undefined ? null : Math.round(s.fraction * 100));
  const busy = live.steps.at(-1);
  if (busy && d.dataset.progressStep !== busy.name) { d.dataset.progressStep = busy.name; d.dataset.progressStepAt = String(Date.now()); }
  const elapsed = Math.max(0, Math.floor((Date.now() - Number(d.dataset.progressStepAt || Date.now())) / 1000));
  const elapsedLabel = elapsed >= 60 ? `${Math.floor(elapsed / 60)}m ${elapsed % 60}s` : `${elapsed}s`;
  if (tag) tag.textContent = busy ? `${busy.name}${pct(busy) === null ? '' : ` ${pct(busy)}%`}` : `building ${whole}%`;
  if (!bar) { bar = el('div', { className: 'nbar' }, [el('i')]); d.append(bar); }
  bar.title = wholeKnown ? `${whole}%` : 'Overall progress is not reported';
  bar.classList.toggle('busy', !wholeKnown);
  bar.firstChild.style.width = `${wholeKnown ? whole : 100}%`;
  if (!live.steps.length) { more?.remove(); return; }
  if (!more) { more = el('div', { className: 'nmore' }); d.append(more); }
  const followProgress = more.scrollHeight - more.scrollTop - more.clientHeight < 12, previousScroll = more.scrollTop;
  more.replaceChildren(el('div', { className: 'pstep head' }, [el('span', { textContent: 'whole block' }), el('div', { className: `pbar small${wholeKnown ? '' : ' busy'}` }, [el('i', { style: `width:${wholeKnown ? whole : 100}%` })]), el('b', { textContent: wholeKnown ? `${whole}%` : '…' })]), ...live.steps.map((s) => {
    const p = pct(s);
    return el('div', { className: 'pstep' }, [el('span', { textContent: s === busy ? `${s.name} · ${elapsedLabel}` : s.name }), el('div', { className: `pbar small${p === null ? ' busy' : ''}` }, [el('i', { style: `width:${p === null ? 100 : p}%` })]), el('b', { textContent: p === null ? '…' : `${p}%` })]);
  }));
  more.scrollTop = followProgress ? more.scrollHeight : previousScroll;
  const rect = d.getBoundingClientRect(), canvas = canvasRect(), below = canvas.bottom - rect.bottom, above = rect.top - canvas.top;
  const placeAbove = below < more.scrollHeight * S.view.k + 16 && above > below;
  d.classList.toggle('progress-above', placeAbove);
  more.style.maxHeight = `${Math.max(32, Math.min(200, (placeAbove ? above : below) / S.view.k - 16))}px`;
}

// At most once per frame, and only the blocks that have progress to show or still carry a bar.
let progressQueued = false;
function paintProgress() {
  if (progressQueued) return;
  progressQueued = true;
  requestAnimationFrame(() => {
    progressQueued = false;
    world.querySelectorAll('.node').forEach((d) => { if ((S.build.live && S.build.live[d.dataset.id]) || d.querySelector('.nbar')) paintOne(d); });
  });
}
subscribeProgress(paintProgress);

/** A block that failed because the song lacks a required plugin setting (AMP5370): the key and the project default the message names. */
function missingSetting(n) {
  const m = /^AMP5370:.*Use: (\S+) (.+?)\s+\(the project default\)/.exec((S.build.status[n.id] && S.build.status[n.id].message) || '');
  return m ? { key: m[1], value: m[2] } : null;
}

/** The quick fix: copy the project default into the song's settings line, then build the block again. */
async function useProjectDefault(n) {
  const m = missingSetting(n);
  if (m && await edit([{ type: 'setSetting', key: m.key, value: m.value }])) build(n.id);
}

/**
 * How long the sound of a voice or music block really is, next to how long its place is ("voice 3.8 s of 7.8 s"), so a block whose sound is much shorter
 * than its place (a blank stretch) or longer than it is easy to see. The sound is measured on the take's waveform: from the first to the last point
 * above a small part of the loudest one.
 */
function soundLabel(n, take) {
  const tag = el('span', { className: 'tag len', textContent: '…', title: 'Length of the sound of this block against its place in the song' });
  peaksOf(take).then((p) => {
    const pts = p.peaks;
    const count = pts.length / 2;
    const peak = Math.max(...Array.from({ length: count }, (_, i) => Math.max(Math.abs(pts[i * 2]), Math.abs(pts[i * 2 + 1]))));
    if (!peak) { tag.textContent = 'silent'; tag.classList.add('bad'); return; }
    const level = (i) => Math.max(Math.abs(pts[i * 2]), Math.abs(pts[i * 2 + 1]));
    let a = 0;
    let b = count - 1;
    while (a < count && level(a) < peak * 0.04) a++;
    while (b > a && level(b) < peak * 0.04) b--;
    const per = p.seconds / count;
    const sound = (b - a + 1) * per;
    const tempo = (S.doc && S.doc.project && (S.doc.project.tempo ?? S.doc.project.Tempo)) || 120;
    const place = (n.parts && n.parts[0] ? n.parts[0].length : 0) * ((barBeats() * 60) / tempo);
    tag.textContent = place ? `${sound.toFixed(1)} s of ${place.toFixed(1)} s` : `${sound.toFixed(1)} s`;
    tag.title = `The sound is ${sound.toFixed(1)} s long${place ? ` and the block has ${place.toFixed(1)} s` : ''}. Orange: the sound is much shorter than its place (a blank stretch); red: longer.`;
    tag.classList.toggle('warn', !!place && sound < place * 0.7);
    tag.classList.toggle('bad', !!place && sound > place * 1.03);
  }).catch(() => { tag.textContent = ''; });
  return tag;
}

function fitBlockTitle(element, text, width) {
  const measure = document.createElement('canvas').getContext('2d');
  let size = 14;
  while (size > 6) { measure.font = `650 ${size}px monospace`; if (measure.measureText(text).width + text.length * .3 <= width) break; size -= .25; }
  element.style.fontSize = `${size}px`;
}

function inlineRename(n, title, event) {
  if (n.proxy || n.isGroup || n.isModule || readOnly() || n.flags.includes('lock')) return;
  event.preventDefault(); event.stopPropagation();
  const input = el('input', { type: 'text', value: n.id, className: 'node-title-editor', 'aria-label': 'Rename block' });
  title.replaceWith(input);
  const fitTitle = () => fitBlockTitle(input, input.value, baseWidth(n) - 40);
  input.oninput = fitTitle; fitTitle();
  input.onpointerdown = (pointer) => pointer.stopPropagation();
  input.onkeydown = (key) => {
    key.stopPropagation();
    if (key.key === 'Escape') { key.preventDefault(); renderGraph(); }
    if (key.key === 'Enter') { key.preventDefault(); input.blur(); }
  };
  input.onblur = async () => {
    const requested = input.value.trim();
    if (!requested || requested === n.id) { renderGraph(); return; }
    const unique = freeName(requested);
    if (unique !== requested) toast(`That name is already used; renamed this block to ${unique}.`);
    if (!await edit([{ type: 'rename', block: n.id, newName: unique }])) renderGraph();
  };
  input.focus(); input.select();
}

/** A mixer grows lanes when its grip is dragged down and loses empty lanes when it is dragged up; lanes that are in use stay (never fewer than 4). */
function growMixer(n, event, grip) {
  const y0 = event.clientY, pointerId = event.pointerId, lanes0 = n.ins.length;
  const used = Math.max(0, ...flat().links.filter((l) => l.to === n.id).map((l) => l.inp + 1));
  let lanes = lanes0;
  const box = grip.closest('.node');
  box.classList.add('resizing'); resizing.add(n.id);
  const ghosts = [];
  const clearGhosts = () => { ghosts.splice(0).forEach((ghost) => ghost.remove()); box.style.paddingBottom = ''; };
  const updateLanes = (move) => {
    if (move.pointerId !== pointerId) return;
    lanes = Math.max(2, used, Math.min(16, lanes0 + Math.round((move.clientY - y0) / S.view.k / ROW)));
    grip.title = `${lanes} input lanes${lanes === used && lanes > 4 ? ' (the lanes in use cannot be taken away)' : ''}`;
    // show the new lanes while dragging: the block grows and the lanes to come appear as dimmed ports
    clearGhosts();
    if (lanes > lanes0) {
      box.style.paddingBottom = `${(lanes - lanes0) * ROW}px`;
      for (let k = lanes0; k < lanes; k++) {
        const ghost = el('div', { className: 'ghost-lane' }, [el('i'), el('span', { textContent: 'audio' })]);
        ghost.style.top = `${HEAD + k * ROW}px`; ghost.style.height = `${ROW}px`;
        box.append(ghost); ghosts.push(ghost);
      }
    }
    drawLinks();
  };
  const finish = async (up) => {
    if (up.pointerId !== pointerId) return;
    if (up.type === 'pointerup') updateLanes(up);   // pointerup carries the final position even when the last move event was coalesced under load
    removeEventListener('pointermove', updateLanes, true); removeEventListener('pointerup', finish, true); removeEventListener('pointercancel', finish, true);
    clearGhosts(); box.classList.remove('resizing'); resizing.delete(n.id);
    if (up.type === 'pointerup' && lanes !== lanes0) await edit([{ type: 'setParameter', block: n.id, name: 'inputs', value: String(lanes) }]);
  };
  addEventListener('pointermove', updateLanes, true); addEventListener('pointerup', finish, true); addEventListener('pointercancel', finish, true);
}

/** The blocks whose grip is being dragged: a redraw of the canvas in the middle of the drag keeps them marked. */
const resizing = new Set();

/** The volume blocks whose faders are open: a click anywhere else, or Escape, closes them again. */
const volumePopOpen = new Set();
// a click elsewhere closes them (a press with the left button that is let go without moving); panning and zooming the canvas leave them open
let popPress = null;
addEventListener('pointerdown', (event) => { popPress = volumePopOpen.size && event.button === 0 && !event.target.closest?.('.volume-pop, .volume-expand') ? { x: event.clientX, y: event.clientY } : null; }, true);
addEventListener('pointerup', (event) => { const press = popPress; popPress = null; if (press && volumePopOpen.size && Math.hypot(event.clientX - press.x, event.clientY - press.y) < 4) { volumePopOpen.clear(); notify(); } }, true);
addEventListener('keydown', (event) => { if (event.key === 'Escape' && volumePopOpen.size) { volumePopOpen.clear(); notify(); } });
// when the drag ends, whatever happened to the block under it (it can be redrawn and lose the pointer), nothing stays marked
addEventListener('pointerup', () => setTimeout(() => { if (resizing.size) { resizing.clear(); document.querySelectorAll('.node.resizing').forEach((e) => e.classList.remove('resizing')); } }, 0), true);

const sizesKey = () => `ample.sizes:${S.project ? S.project.id : ''}:${S.path}`;
let sizesLoadedFor = null;
/** The sizes the owner gave blocks on this canvas live in the browser (like groups), not in the song. */
function loadSizes() {
  if (sizesLoadedFor === sizesKey()) return;
  sizesLoadedFor = sizesKey();
  if (parseView(S.doc?.text).found) return;   // the song file has them
  for (const key of Object.keys(sizes)) delete sizes[key];
  try { Object.assign(sizes, JSON.parse(localStorage.getItem(sizesKey()) || '{}')); } catch { /* storage may be blocked */ }
}
const saveSizes = () => { try { localStorage.setItem(sizesKey(), JSON.stringify(sizes)); } catch { /* storage may be blocked */ } saveViewSoon(); };

function nodeEl(n) {
  loadSizes();
  const resizeMark = resizing.has(n.id) ? ' resizing' : '';
  const rows = Math.max(n.ins.length, n.outs.length, 1);
  const d = el('div', { className: `node${resizeMark}${S.pins.has(n.id) ? ' pinned' : ''}${S.selSet.has(n.id) ? ' sel' : ''}${n.proxy ? ' proxy' : ''}${n.isGroup ? ' grp' : ''}${n.disabledBy || n.flags.includes('disabled') ? ' is-disabled' : ''}` });
  d.style.left = `${n.x}px`;
  d.style.top = `${n.y}px`;
  d.style.width = `${baseWidth(n)}px`;
  if (sizes[n.id] && sizes[n.id] !== 1) { d.style.transformOrigin = '0 0'; d.style.transform = `scale(${sizes[n.id]})`; }
  if (!n.proxy && !n.isGroup) {
    // the grip at the bottom right makes a block smaller or bigger; a double click gives the normal size back
    const grip = el('div', { className: 'size-grip', title: 'Drag to make this block smaller or bigger (double click: normal size)' });
    grip.onpointerdown = (event) => {
      event.stopPropagation(); event.preventDefault();
      if (n.tpl === 'mixer' || n.tpl === 'bus') { growMixer(n, event, grip); return; }
      grip.setPointerCapture(event.pointerId);
      const x0 = event.clientX, s0 = sizes[n.id] || 1, w = baseWidth(n);
      d.classList.add('resizing'); resizing.add(n.id);
      grip.onpointermove = (move) => {
        const next = Math.round(Math.max(0.5, Math.min(1.6, s0 + (move.clientX - x0) / S.view.k / w)) * 20) / 20;
        if (next === 1) delete sizes[n.id]; else sizes[n.id] = next;
        d.style.transformOrigin = '0 0'; d.style.transform = next === 1 ? '' : `scale(${next})`;
        drawLinks(); drawMini();
      };
      grip.onpointerup = () => { grip.onpointermove = null; grip.onpointerup = null; d.classList.remove('resizing'); resizing.delete(n.id); saveSizes(); };
    };
    grip.ondblclick = (event) => { event.stopPropagation(); delete sizes[n.id]; d.style.transform = ''; saveSizes(); drawLinks(); drawMini(); };
    d.append(grip);
  }
  d.dataset.id = n.id;
  {
    const problems = n.proxy || n.isGroup || n.isModule ? [] : blockProblems(n.id), errs = problems.filter((p) => p.severity === 'error');
    if (errs.length) {
      d.classList.add('err'); d.dataset.err = String(errs.length);
      d.append(el('span', { className: 'node-errmark', textContent: '!', title: errs.map((p) => `${p.code} ${p.message}${p.hint ? '\n' + p.hint : ''}`).join('\n\n'), 'aria-label': `${errs.length} error(s) in this block` }));
    } else if (problems.some((p) => p.severity === 'warning')) d.classList.add('warn');
  }
  d.tabIndex = n.proxy ? -1 : 0;
  d.setAttribute('role', 'group');
  { const tint = n.proxy ? '' : tintOf(n.params); if (tint) { d.classList.add('tinted'); d.style.setProperty('--tint', TINTS[tint]); d.dataset.tint = tint; } }
  d.setAttribute('aria-label', n.proxy ? n.title : `${n.title}, ${S.tpl[n.tpl]?.component ? `${S.tpl[n.tpl].title} (${n.tpl})` : n.tpl}, ${n.cls}${n.flags.includes('disabled') ? ', disabled' : ''}`);
  d.setAttribute('aria-disabled', String(!n.proxy && !!(n.disabledBy || n.flags.includes('disabled'))));
  d.onfocus = () => { if (!n.proxy && !S.selSet.has(n.id)) { select(n.id); notifySelection(); } };
  const head = el('div', { className: 'nh' });
  const title = el('span', { className: 't', textContent: n.title, title: n.title });
  fitBlockTitle(title, n.title, baseWidth(n) - 50);
  title.ondblclick = (event) => { event.preventDefault(); event.stopPropagation(); inlineRename(n, title, event); };
  const status = statOf(n);
  const statusButton = el('button', { type: 'button', className: `node-status ${status.bad ? status.cls === 'blocked' ? 'blocked' : 'invalid' : status.cls}`, textContent: status.bad ? '!' : n.disabledBy || n.flags.includes('disabled') ? '−' : '', title: `${status.txt}${status.tip ? ` — ${status.tip}` : ''}`, 'aria-label': `${n.title}: ${status.txt}${status.tip ? ` — ${status.tip}` : ''}` });
  statusButton.onpointerdown = (event) => event.stopPropagation();
  statusButton.ondblclick = (event) => event.stopPropagation();
  statusButton.onclick = (event) => { event.stopPropagation(); if (status.bad) showBlockStatusError(n.id, status.diagnostic); };
  const pinned = S.pins.has(n.id);
  const pin = !n.proxy && !n.isGroup ? el('button', { type: 'button', className: 'node-pin', textContent: '📌', title: pinned ? 'Unpin this block' : 'Pin this block in place', 'aria-label': pinned ? `Unpin ${n.title}` : `Pin ${n.title} in place`, 'aria-pressed': String(pinned), disabled: readOnly() }) : null;
  if (pin) { pin.onpointerdown = (event) => event.stopPropagation(); pin.onclick = (event) => { event.stopPropagation(); setPinned([n.id], !pinned); }; }
  const nameplate = el('div', { className: 'node-nameplate' }, [el('i', { className: 'node-screw', 'aria-hidden': 'true' }), title, ...(pin ? [pin] : []), ...(n.proxy ? [el('i', { className: 'node-screw', 'aria-hidden': 'true' })] : [statusButton])]);
  head.append(nameplate);
  if (!n.proxy && !n.isGroup) head.append(...derivedChips(n.id));
  if (n.isGroup) {
    const tg = el('span', { textContent: n.expanded ? '▾' : '▸', title: 'Show or hide the contents', style: 'cursor:pointer;color:var(--acc)' });
    tg.onpointerdown = (ev) => ev.stopPropagation();
    tg.onclick = (ev) => { ev.stopPropagation(); S.groups[n.id].expanded = !S.groups[n.id].expanded; saveGroups(); notify(); };
    head.append(tg);
  }
  if (!n.proxy) head.append(el('span', { className: `cls ${n.cls}`, textContent: n.ver ? `v${n.ver}` : n.cls, title: `${n.cls}${n.ver ? ` v${n.ver}` : ''}` }));
  if (!n.proxy) {
    const selectedInputs = new Map();
    const inputLoads = new Map();
    const effectPanelOptions = (effect) => {
      const template = S.tpl[effect.tpl];
      const getSource = () => flat().links.find((link) => !link.disabled && link.to === effect.id && (link.type === 'audio' || link.type === 'render'));
      const inputKey = () => `${S.path}:${getSource()?.from || ''}`;
      const getTakeId = () => S.build.takes?.[getSource()?.from] || selectedInputs.get(inputKey()) || null;
      const loadInput = async () => { const source = getSource(); if (!source) return null; const key = inputKey(), request = {}; inputLoads.set(key, request); const control = await api.get(`/api/control/block?path=${encodeURIComponent(S.path)}&block=${encodeURIComponent(source.from)}`); if (inputLoads.get(key) === request) selectedInputs.set(key, control.selected || null); return getTakeId(); };
      const getInputTitle = () => { const source = getSource(); return flat().nodes.find((node) => node.id === source?.from)?.title || source?.from || 'audio input'; };
      const buildAndSelectInput = async () => { const source = getSource(); if (!source) return null; await build(source.from); const takeId = S.build.takes?.[source.from] || null; if (!takeId) return null; await api.post('/api/control/select', { path: S.path, block: source.from, take: takeId }); inputLoads.delete(inputKey()); selectedInputs.set(inputKey(), takeId); return takeId; };
      const renderInput = getSource()?.type === 'render';
      return { type: effect.tpl, title: effect.title, block: effect.id, inputTitle: getInputTitle(), definitions: template?.parameters || [], getParams: () => current().nodes.find((item) => item.id === effect.id)?.params || effect.params, readOnly: readOnly() || effect.flags.includes('lock'), takeId: getTakeId(), getTakeId, loadInput, buildLabel: renderInput ? 'Build and use source variant' : 'Build and use input', playLabel: 'Play input', buildInput: getSource() ? buildAndSelectInput : null, realPreview: (params, inputTakeId) => { const source = getSource(); const takeId = inputTakeId || getTakeId(); if (!source || !takeId) throw new Error('Build and select the upstream input before previewing this effect.'); return api.effectPreview({ path: S.path, block: effect.id, takeId, fromMs: 0, toMs: 30_000, params }); }, loadImpulse: (params) => api.effectImpulse({ path: S.path, block: effect.id, params }), preview: () => {}, commit: (name, value) => edit([{ type: 'setParameter', block: effect.id, name, value: String(value) }]), commitMany: (values) => edit(values.map(({ name, value }) => ({ type: 'setParameter', block: effect.id, name, value: String(value) }))) };
    };
    const openAdvanced = (event) => {
      event.stopPropagation();
      const template = S.tpl[n.tpl];
      const source = flat().links.find((link) => !link.disabled && link.to === n.id && (link.type === 'audio' || link.type === 'render'));
      const audioSource = n.outs.some((port) => port[1] === 'audio');
      const takeId = source ? S.build.takes?.[source.from] : audioSource ? S.build.takes?.[n.id] : null;
      const buildInput = source ? async () => { await build(source.from); const take = S.build.takes?.[source.from] || null; if (take) await api.post('/api/control/select', { path: S.path, block: source.from, take }); return take; } : audioSource ? async () => { await build(n.id); return S.build.takes?.[n.id] || null; } : null;
      const options = { type: n.tpl, title: n.title, block: n.id, definitions: template?.parameters || [], getParams: () => current().nodes.find((item) => item.id === n.id)?.params || n.params, readOnly: readOnly() || n.flags.includes('lock'), takeId, buildInput, buildLabel: audioSource && !source ? 'Build block' : 'Build and use input', playLabel: audioSource && !source ? 'Play block' : 'Play input', realPreview: null, preview: () => {}, commit: (name, value) => edit([{ type: 'setParameter', block: n.id, name, value: String(value) }]), commitMany: (values) => edit(values.map(({ name, value }) => ({ type: 'setParameter', block: n.id, name, value: String(value) }))) };
      openStudioPanel({ ...options, ...(['eq', 'filter', 'compressor', 'duck', 'distortion', 'delay', 'reverb', 'volume', 'denoise', 'chorus', 'normalize', 'tremolo', 'granular', 'reverse', 'rewind', 'autotune', 'convolution', 'master'].includes(n.tpl) ? effectPanelOptions(n) : {}), ...(n.tpl === 'master' ? { realPreview: null, tempo: S.doc?.project?.tempo || 120 } : {}), ...(n.tpl === 'autotune' ? { melody: flat().links.some((link) => !link.disabled && link.to === n.id && link.inName === 'target') } : {}) });
    };
    if (!n.proxy && n.outs.some((port) => port[1] === 'audio')) {
      const effects = [...(n.renderChain || []), ...(n.effectChain || [])].filter((effect, index, all) => all.findIndex((entry) => entry.id === effect.id) === index);
      // import-audio: choose the part of the file that the block uses, with a preview of just that part
      if (n.tpl === 'import-audio') {
        const part = el('button', { type: 'button', className: 'studio-part-button', textContent: '✂', 'aria-label': `Choose the part of the file that ${n.title} uses`, title: 'Choose the part of the audio file to use (start and end), with a preview', disabled: readOnly() || n.flags.includes('lock') || !n.params.file });
        part.onpointerdown = (event) => event.stopPropagation();
        part.onclick = async (event) => {
          event.stopPropagation();
          try {
            const { chooseAudioPart } = await import('./clipper.js');
            const chosen = await chooseAudioPart(String(n.params.file || '').trim().replace(/^"|"$/g, ''), { start: n.params.start, end: n.params.end });
            if (!chosen) return;
            await edit([{ type: 'setParameter', block: n.id, name: 'start', value: chosen.whole ? '0' : String(chosen.start) }, { type: 'setParameter', block: n.id, name: 'end', value: chosen.whole ? '0' : String(chosen.end) }]);
            if (n.params.loop !== 'on') await fitLengthToAudio(n, chosen.whole ? chosen.total : chosen.end - chosen.start);   // a looping block keeps the length it has
          } catch (error) { toast(error.message || String(error)); }
        };
        head.append(part);
      }
      // cut: choose the part of what comes in that the block keeps, on the waveform of its input
      if (n.tpl === 'cut') {
        const part = el('button', { type: 'button', className: 'studio-part-button', textContent: '✂', 'aria-label': `Choose the part that ${n.title} keeps`, title: 'Choose the part of the input to keep (start and end), with a preview', disabled: readOnly() || n.flags.includes('lock') });
        part.onpointerdown = (event) => event.stopPropagation();
        part.onclick = async (event) => {
          event.stopPropagation();
          try {
            const source = flat().links.find((link) => !link.disabled && link.to === n.id && link.type === 'audio');
            if (!source) { toast('Connect an audio input first: the part is chosen on its waveform.'); return; }
            if (!S.build.takes?.[source.from]) await build(source.from);
            const take = S.build.takes?.[source.from];
            if (!take) { toast('The input could not be built. See the build log.'); return; }
            const info = await api.get(`/api/takes/${encodeURIComponent(take)}`);
            if (!info.audioDataId) { toast('The input has no audio to show.'); return; }
            const { chooseAudioPart } = await import('./clipper.js');
            const chosen = await chooseAudioPart(`input: ${source.from}`, { start: n.params.start, end: n.params.end }, { url: `/api/data/${info.audioDataId}` });
            if (!chosen) return;
            const clear = { first: '0', last: '0' };   // first and last win over start and end, so the chosen part must switch them off
            await edit([{ type: 'setParameter', block: n.id, name: 'start', value: chosen.whole ? '0' : String(chosen.start) }, { type: 'setParameter', block: n.id, name: 'end', value: chosen.whole ? '0' : String(chosen.end) }, ...['first', 'last'].filter((name) => n.params[name] && Number(n.params[name]) > 0).map((name) => ({ type: 'setParameter', block: n.id, name, value: clear[name] }))]);
          } catch (error) { toast(error.message || String(error)); }
        };
        head.append(part);
      }
      // a block that already has an EQ linked to it does not offer + EQ again; it shows an EQ button that goes to the linked one
      if (effects.length) {
        const go = (effect) => { select(effect.id); notifySelection(); notify(); focusNode(effect); };
        const arrow = el('button', { type: 'button', className: 'studio-effect-arrow', textContent: `Effects ${effects.length} ▾`, 'aria-label': `Go to effects of ${n.title}`, title: 'Choose an effect (type to search)', 'aria-haspopup': 'listbox', 'aria-expanded': 'false' });
        arrow.onpointerdown = (event) => event.stopPropagation();
        arrow.onclick = (event) => {
          event.stopPropagation();
          const existing = d.querySelector('.studio-effect-picker');
          if (existing) { existing.remove(); arrow.setAttribute('aria-expanded', 'false'); return; }
          const picker = el('div', { className: 'studio-effect-picker' }), results = el('div', { role: 'listbox', id: `effect-options-${n.id}`, 'aria-label': 'Connected effects' });
          const search = el('input', { type: 'search', placeholder: 'Search effects...', role: 'combobox', 'aria-label': 'Search connected effects', 'aria-autocomplete': 'list', 'aria-expanded': 'true', 'aria-controls': results.id });
          let matches = effects, index = 0;
          const draw = () => {
            results.replaceChildren();
            matches.forEach((effect, position) => {
              const disabled = effect.disabledBy || effect.flags?.includes('disabled');
              const option = el('button', { type: 'button', role: 'option', id: `effect-option-${n.id}-${position}`, className: disabled ? 'effect-off' : '', 'aria-selected': String(position === index), 'aria-label': `${effect.title} · ${effect.tpl}${disabled ? ' · disabled' : ''}`, textContent: `${effect.title} · ${effect.tpl}${disabled ? ' · disabled' : ''}`, onclick: () => go(effect) });
              results.append(option);
            });
            if (!matches.length) results.append(el('span', { className: 'note', textContent: 'No matching effects.' }));
            if (matches.length) search.setAttribute('aria-activedescendant', results.children[index].id); else search.removeAttribute('aria-activedescendant');
          };
          search.oninput = () => { const query = search.value.trim().toLowerCase(); matches = effects.filter((effect) => `${effect.title} ${effect.id} ${effect.tpl}`.toLowerCase().includes(query)); index = 0; draw(); };
          picker.onpointerdown = (pointer) => pointer.stopPropagation(); picker.onclick = (click) => click.stopPropagation();
          results.onpointerdown = (pointer) => pointer.preventDefault();
          picker.onkeydown = (key) => {
            key.stopPropagation();
            if (key.key === 'Escape') { picker.remove(); arrow.setAttribute('aria-expanded', 'false'); arrow.focus(); }
            if (key.key === 'Enter' && matches.length) { key.preventDefault(); go(matches[index]); }
            if ((key.key === 'ArrowDown' || key.key === 'ArrowUp') && matches.length) { key.preventDefault(); index = (index + (key.key === 'ArrowDown' ? 1 : matches.length - 1)) % matches.length; draw(); }
          };
          picker.onfocusout = (focus) => { if (!picker.contains(focus.relatedTarget)) { picker.remove(); arrow.setAttribute('aria-expanded', 'false'); } };
          picker.append(search, results); d.append(picker); arrow.setAttribute('aria-expanded', 'true'); draw(); search.focus();
        };
        head.append(arrow);
      }
    }
    const sourceButton = el('button', { type: 'button', className: 'studio-source-node', textContent: '</>', title: 'Edit this block in source', 'aria-label': 'Edit in source' });
    sourceButton.onpointerdown = (event) => event.stopPropagation();
    sourceButton.onclick = (event) => { event.stopPropagation(); gotoSource(n); };
    const hasGui = hasStudioPanel(n.tpl) || n.tpl === 'convolution';
    const advanced = el('button', { type: 'button', className: 'studio-advanced-node', textContent: '↗', title: hasGui ? 'Open this block’s studio controls' : 'Show this block’s properties', 'aria-label': 'Advanced' });
    advanced.onpointerdown = (event) => event.stopPropagation();
    advanced.onclick = hasGui ? openAdvanced : (event) => { event.stopPropagation(); select(n.id); notifySelection(); notify(); const panel = $('#right'); panel.scrollTop = 0; panel.scrollLeft = 0; };
    head.append(advanced, sourceButton);
    if (n.tpl === 'delay-llama') head.append(llamaAvatarButton(n.id));   // only the Delay Llama blocks: the linked llama avatar
  }
  const toolbar = el('div', { className: 'node-toolbar' });
  // a component shows its friendly name (#[DisplayName]); the full name, the version and the id are on hover
  const friendly = S.tpl[n.tpl]?.component ? S.tpl[n.tpl] : null;
  toolbar.append(el('span', { className: 'node-type', textContent: friendly ? friendly.title.toUpperCase() : n.tpl === 'volume' ? 'VOLUME' : (n.tpl || '').toUpperCase(), ...(friendly ? { title: `${friendly.title}\nfull name: ${n.tpl}\nid: ${friendly.componentId}\nversion: ${n.ver || friendly.version}` } : {}) }));
  for (const selector of ['.cls', '.studio-advanced-node']) { const action = [...head.children].find((child) => child.matches(selector)); if (action) toolbar.append(action); }
  [...head.children].filter((child) => child !== nameplate).forEach((child) => toolbar.append(child));
  head.append(toolbar);
  d.append(head);
  for (let i = 0; i < rows; i++) {
    const r = el('div', { className: 'nrow' });
    for (const pt of [n.ins[i], n.outs[i]]) r.append(el('span', { textContent: (pt && portSpec(n, pt, pt === n.ins[i] ? 'inputs' : 'outputs')?.title) || (pt ? ['splitter', 'volume'].includes(n.tpl) && ['left', 'right', 'mixed'].includes(pt[0]) ? `audio ${pt[0] === 'left' ? 'L' : pt[0] === 'right' ? 'R' : 'mixed'}` : ['audio', 'render'].includes(pt[1]) && n.tpl !== 'sequencer' && !n.isGroup && !n.proxy ? pt[1] : pt[0] : ''), title: (pt && portSpec(n, pt, pt === n.ins[i] ? 'inputs' : 'outputs')?.description) || (pt ? `${pt[0]} (${pt[1]})` : '') }));
    d.append(r);
  }
  const port = (pt, i, dir) => {
    const e = el('span', { className: `port ${dir} ${pt[1]}` });
    e.style.top = `${HEAD + i * ROW + ROW / 2 - 6}px`;
    Object.assign(e.dataset, { node: n.id, idx: i, dir, type: pt[1] });
    e.title = portHint(pt, dir === 'in' ? n.ins : n.outs, dir === 'out');
    const spec = portSpec(n, pt, dir === 'in' ? 'inputs' : 'outputs');
    if (spec?.description) {
      const self = dir === 'out' && !!(S.doc?.blocks || []).find((b) => b.name === n.id)?.froms?.some((f) => f.input === 'region_in');   // an effect's own region output counts its own effect
      const chain = pt[1] === 'region' ? chainText(regionChain(S.doc?.blocks, n.id, self)) : '';
      e.title = `${spec.title || pt[0]}: ${spec.description}${chain ? ` (${chain})` : ''}`;
    }
    if (dir === 'out' && spec?.description) {
      // a play triangle in the dot itself: a click on the dot (not a drag) opens a small player for what this output carries (a region: one join, looped)
      e.append(el('i', { className: 'pp', textContent: '▶', 'aria-hidden': 'true' }));
      e.classList.add('playable');
      e.addEventListener('click', (ev) => { if (ev.pointerType === 'touch') return; ev.stopPropagation(); openPortPreview(e, { block: n.id, role: pt[0], type: pt[1], title: spec.title || pt[0], dir, selector: `.port[data-node="${CSS.escape(n.id)}"][data-dir="${dir}"][data-idx="${i}"]` }); });
    }
    d.append(e);
  };
  n.ins.forEach((pt, i) => port(pt, i, 'in'));
  n.outs.forEach((pt, i) => port(pt, i, 'out'));
  if (!n.proxy) {
    if (n.tpl === 'volume') {
      d.classList.add('volume-node');
      const params = { ...n.params };
      // the faders are made when they are shown: in the window that opens from the block (the block itself shows the levels in one line)
      const makeControls = () => {
      const controls = volumeControls({ block: n.id, params, readOnly: readOnly() || n.flags.includes('lock'), preview: (name, value) => { params[name] = value; if (playerState().auditionBlock === n.id) updateLivePreview(params); }, commit: (name, value) => edit([{ type: 'setParameter', block: n.id, name, value }]), commitMany: (values) => edit(values.map(({ name, value }) => ({ type: 'setParameter', block: n.id, name, value }))) }, () => S.build.takes?.[n.id]);
      const preview = el('button', { type: 'button', className: 'volume-live', textContent: '▶ Live preview', title: 'Play the built upstream audio with these live volume controls', disabled: !!(n.disabledBy || n.flags.includes('disabled')) });
      const syncPreview = (state) => { const playing = state.auditionBlock === n.id && state.playing; preview.textContent = playing ? '■ Stop preview' : '▶ Live preview'; preview.setAttribute('aria-pressed', String(playing)); };
      const unsubscribePreview = onPlayer(syncPreview), disposeMeters = controls.dispose;
      controls.dispose = () => { disposeMeters(); unsubscribePreview(); };
      syncPreview(playerState());
      preview.onclick = async (event) => {
        event.stopPropagation();
        if (playerState().auditionBlock === n.id && playerState().playing) { stop(); return; }
        preview.disabled = true;
        try {
          Object.assign(params, current().nodes.find((node) => node.id === n.id)?.params || {});
          const source = flat().links.find((link) => !link.disabled && link.to === n.id && (link.type === 'audio' || link.type === 'render'));
          if (!source) throw new Error('Connect an audio or render input first.');
          const control = await api.get(`/api/control/block?path=${encodeURIComponent(S.path)}&block=${encodeURIComponent(source.from)}`);
          const takeId = control.selected || S.build.takes?.[source.from];
          if (!takeId) throw new Error('Build the upstream audio first.');
          const blob = await api.effectPreview({ path: S.path, block: n.id, takeId, fromMs: 0, toMs: 30_000, params: { ...params, enabled: 'off' } });
          if (!preview.isConnected) return;
          await playLivePreview(blob, (context, input, output) => liveEffectGraph(context, input, output, 'volume', params, S.doc?.project?.tempo));
          setAuditionBlock(n.id);
        } catch (error) { toast(error.message); }
        finally { preview.disabled = false; }
      };
      controls.append(preview);
      return controls;
      };
      const summary = () => {
        const p = current().nodes.find((x) => x.id === n.id)?.params || n.params, level = (key) => p[key] || '0 dB';
        return p.mono === 'on' ? `mono · ${level('gain')}` : `L ${level('left_gain')} · R ${level('right_gain')}${p.left === 'off' ? ' · L muted' : ''}${p.right === 'off' ? ' · R muted' : ''}`;
      };
      const expand = el('button', { type: 'button', className: 'volume-expand', textContent: '⤢ Faders', title: 'Show the faders, the meters and the live preview below the block; a click anywhere else closes them', 'aria-label': `Open the faders of ${n.title}`, 'aria-expanded': String(volumePopOpen.has(n.id)) });
      expand.onpointerdown = (event) => event.stopPropagation();
      expand.onclick = (event) => {
        event.stopPropagation();
        if (volumePopOpen.has(n.id)) volumePopOpen.delete(n.id); else { volumePopOpen.clear(); volumePopOpen.add(n.id); }
        notify();
      };
      d.append(el('div', { className: 'volume-compact' }, [el('span', { className: 'volume-summary', textContent: summary() }), expand]));
      // open: the faders hang below the block over what is there, and stay while they are used (a change redraws the block)
      if (volumePopOpen.has(n.id)) { const pop = el('div', { className: 'volume-pop' }, [makeControls()]); pop.onpointerdown = (event) => event.stopPropagation(); d.append(pop); }
    }
    if (n.isGroup && n.expanded) d.append(innerPreview(n));
    // the controls a component puts on its card (the card block of its layout), and the button that opens all of them in a window
    const cardTemplate = S.tpl[n.tpl];
    if (cardTemplate?.component && cardTemplate.ui?.card?.length && !n.isGroup && !n.proxy) {
      const values = Object.fromEntries((cardTemplate.parameters || []).map((p) => [p.name, n.params[p.name] ?? p.default]));
      const laid = renderComponentUi(cardTemplate, values, cardTemplate.ui.card, 'card', {
        disabled: readOnly(),
        save: (name, text) => edit([{ type: 'setParameter', block: n.id, name, value: text }]),
        saveMany: (items) => edit(items.map((i) => ({ type: 'setParameter', block: n.id, name: i.name, value: i.text }))),
        ...liveOptions(cardTemplate, n.id),
      });
      const open = el('button', { type: 'button', className: 'cui-open', textContent: '⤢', title: 'Show all the controls in a window', 'aria-label': `Show the controls of ${n.title}` });
      open.onclick = (event) => { event.stopPropagation(); import('./component-popup.js').then((m) => m.openComponentPopup(n.id)); };
      const holder = el('div', { className: 'cui-card-holder' }, [laid.element, open]);
      // a knob dragged on the card must never drag or select the block
      for (const type of ['pointerdown', 'click', 'dblclick', 'contextmenu']) holder.addEventListener(type, (event) => event.stopPropagation());
      d.append(holder);
    }
    const f = el('div', { className: 'nfoot' });
    const st = statOf(n);
    if (st.st === 'running') f.append(el('span', { className: `tag stat ${st.cls}`, textContent: st.txt, title: st.tip || '' }));
    if (n.dyn) f.append(el('span', { className: 'tag dyn', textContent: 'dynamic' }));
    if (n.flags.includes('bake')) f.append(el('span', { className: 'tag bake', textContent: 'baked' }));
    if (n.flags.includes('lock')) f.append(el('span', { className: 'tag lock', textContent: 'locked' }));
    if (n.freeze) f.append(el('span', { className: 'tag lock', textContent: `freeze ${n.freeze}` }));
    if (n.plugin && !S.plugins.some((p) => p.id === n.plugin)) f.append(el('span', { className: 'tag missing', textContent: 'plugin missing' }));
    if (!n.isGroup && !n.known) f.append(el('span', { className: 'tag missing', textContent: 'unknown type' }));
    if (n.isGroup) {
      const audioLines = (n.outKeys || []).map((key, index) => ({ key, index, label: n.outs[index][0], type: n.outs[index][1] })).filter((line) => line.type === 'audio');
      if (audioLines.length) {
        const chosen = () => audioLines.find((line) => line.key === S.groups[n.id]?.playOut) || audioLines[0];
        const playLine = async (line) => {
          const block = line.key.slice(0, line.key.lastIndexOf(':'));
          if (!S.build.takes?.[block]) await build(block);
          const take = S.build.takes?.[block];
          if (!take) { toast('That line could not be built. See the build log.'); return; }
          await toggle(take);
        };
        const builtLine = S.build.takes?.[chosen().key.slice(0, chosen().key.lastIndexOf(':'))];
        const groupPlay = el('button', { type: 'button', textContent: '▶', title: audioLines.length > 1 ? `Play the line "${chosen().label}" of this group (the arrow chooses another)` : `Play the line "${chosen().label}" of this group`, 'aria-label': `Play ${n.title}` });
        groupPlay.onpointerdown = (ev) => ev.stopPropagation();
        groupPlay.onclick = (ev) => { ev.stopPropagation(); playLine(chosen()).catch((e) => toast(e.message || String(e))); };
        if (!builtLine) f.append(groupPlay);   // what is built has the player of every block, with the waveform to scrub
        if (audioLines.length > 1) {
          const choose = el('button', { type: 'button', textContent: '▾', title: 'Choose which line to play', 'aria-label': `Choose the line of ${n.title} to play` });
          choose.onpointerdown = (ev) => ev.stopPropagation();
          choose.onclick = (ev) => { ev.stopPropagation(); const r = choose.getBoundingClientRect(); showMenu(r.left, r.bottom, audioLines.map((line) => ({ label: `${line.label === chosen().label && line.key === chosen().key ? '✓ ' : ''}${line.label}  (${line.key.replace(':', ' · ')})`, fn: () => { S.groups[n.id].playOut = line.key; saveGroups(); notify(); playLine(line).catch((e) => toast(e.message || String(e))); } }))); };
          f.append(choose);
        }
      }
      f.append(el('span', { className: 'enter', textContent: '⤢ open' }));
    }
    const take = S.build.takes && S.build.takes[n.id];
    if (take && (n.tpl === 'ai-voice' || n.tpl === 'ace-music')) f.append(soundLabel(n, take));
    const lineTake = n.isGroup && chosenLine(n) ? S.build.takes?.[chosenLine(n).block] : null;
    if ((n.isGroup ? lineTake : take) && (n.isGroup || n.outs.some((o) => o[1] === 'audio'))) {
      const take = n.isGroup ? lineTake : S.build.takes && S.build.takes[n.id];
      const pl = el('div', { className: 'player' });
      pl.dataset.take = take;
      pl.style.display = 'flex';
      const pb = el('button', { textContent: '▶', title: 'Play this block (the take of the last build)', 'aria-label': 'Play this block' });
      pb.onpointerdown = (ev) => ev.stopPropagation();
      pb.onclick = (ev) => { ev.stopPropagation(); toggle(take).catch((e) => toast(e.message)); };
      const more = el('button', { textContent: '▾', title: 'Stop or download', onclick: (ev) => { ev.stopPropagation(); const r = ev.currentTarget.getBoundingClientRect(); showMenu(r.left, r.bottom, [{ label: 'Stop', fn: stop }, { label: 'Download WAV', fn: () => download(take, 'wav') }, { label: 'Download MP3', fn: () => download(take, 'mp3') }]); } });
      const bg = el('span', { className: 'plbg', tabIndex: 0 });
      bg.setAttribute('role', 'slider'); bg.setAttribute('aria-label', `Seek ${n.title} playback`); bg.setAttribute('aria-valuemin', '0'); bg.setAttribute('aria-valuemax', '100');
      const wave = el('canvas', { width: 320, height: 36 }); wave.setAttribute('aria-hidden', 'true');
      const played = el('i', { className: 'pl-played' }), head = el('b', { className: 'plhead' });
      bg.append(wave, played, head);
      const seek = (ev) => { const r = bg.getBoundingClientRect(); seekTake(take, (ev.clientX - r.left) / r.width).catch((e) => toast(e.message)); };
      bg.onpointerdown = (ev) => { ev.stopPropagation(); bg.setPointerCapture(ev.pointerId); seek(ev); bg.onpointermove = (move) => { if (move.buttons & 1) seek(move); }; bg.onpointerup = () => { bg.onpointermove = null; bg.onpointerup = null; }; };
      bg.onkeydown = (ev) => { if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(ev.key)) return; ev.preventDefault(); const state = playerState(), fraction = state.take === take && state.duration ? state.time / state.duration : 0; seekTake(take, ev.key === 'Home' ? 0 : ev.key === 'End' ? 1 : fraction + (ev.key === 'ArrowRight' ? 0.02 : -0.02)).catch((e) => toast(e.message)); };
      pl.append(pb, more, bg);
      f.append(pl);
      // the waveform is asked for when the block comes into view: a song with 40 blocks does not ask for 40 waveforms at once
      whenVisible(wave, () => peaksOf(take, 320).then(async (p) => {
        if (!wave.isConnected) return;
        drawPeaks(wave, p.peaks, getComputedStyle(document.documentElement).getPropertyValue('--acc').trim());
        // the joins of a loop over the waveform, in the colour of the seam port, with their seconds on hover
        if (n.tpl !== 'import-audio') return;
        try {
          const seam = await peaksOf(take, 320, 'loop-region');
          if (!wave.isConnected) return;
          drawPeaks(wave, seam.peaks, getComputedStyle(document.documentElement).getPropertyValue('--warn').trim(), true);
          const text = seamText(seamSpans(seam.peaks, seam.seconds ?? p.seconds ?? 0));
          if (text) bg.title = text;
        } catch { /* this take has no seam output */ }
      }).catch(() => {}));
    }
    if (S.peek?.ai && !n.isGroup && !n.proxy && S.peek.audio?.has(n.id)) {
      // a block of the AI proposal that was built: it plays (and can be scrubbed and repeated) from the temporary project of the proposal, without building again
      const au = el('audio', { controls: true, preload: 'none', className: 'ai-peek-audio', src: `/api/ai/tasks/${S.peek.ai}/audio?block=${encodeURIComponent(n.id)}`, 'aria-label': `${n.title} of the AI proposal` });
      registerPlaybackAudio(au);
      au.onpointerdown = (ev) => ev.stopPropagation();
      au.onclick = (ev) => ev.stopPropagation();
      au.onplay = () => { import('./ai.js').then((m) => m.stopPreview()); document.querySelectorAll('.ai-peek-audio').forEach((a) => { if (a !== au) a.pause(); }); };   // one sound at a time
      f.append(au);
    } else if (S.peek?.ai && !n.isGroup && !n.proxy && n.outs.some((o) => o[1] === 'audio') && !take) {
      // in the read-only preview of an AI proposal a block has no take of its own: this builds it in the proposal and plays it
      const pp = el('button', { type: 'button', className: 'ai-peek-play', textContent: '▶', title: 'Build this block in the AI proposal and listen (nothing in your project is built or changed)', 'aria-label': `Build ${n.title} of the AI proposal and listen` });
      pp.onpointerdown = (ev) => ev.stopPropagation();
      pp.onclick = (ev) => { ev.stopPropagation(); build(n.id); };
      f.append(pp);
    }
    d.append(f);
    if (n.tpl === 'meter') {
      // what the meter measured in the latest build, next to the block (the report is the message of its result)
      const report = parseMeterReport(S.build.status[n.id]?.message);
      if (report) { const line = meterLine(report); d.append(el('div', { className: `meter-report${line.warn ? ' warn' : ''}`, textContent: line.text, title: S.build.status[n.id].message })); }
    }
    paintOne(d);
    if (st.st) d.classList.add(`st-${st.st}`);
    if (st.bad) d.classList.add('st-failed');
  }
  d.oncontextmenu = (e) => { e.preventDefault(); e.stopPropagation(); if (!S.selSet.has(n.id)) select(n.id); notify(); nodeMenu(e, n); };
  d.ondblclick = (e) => {
    e.stopPropagation();
    if (n.isGroup) enter(n);
    else if (n.isModule) enter(n);
    else if (n.tpl === 'lyrics') openLyrics(n);
    else if (!n.proxy && !e.target.closest('button,input,.port')) gotoSource(n);   // a double tap on connector dots (tap-tap connecting) is not a double click on the block
  };
  head.onpointerdown = (e) => startDrag(e, n, head);
  d.onpointerdown = (e) => {
    if (e.button === 0 && !e.target.classList.contains('port')) pressSelect(e, n);
    e.stopPropagation();
  };
  d.querySelectorAll('.port.out').forEach((pe) => { pe.onpointerdown = (ev) => { ev.stopPropagation(); ev.preventDefault(); startLink(n, +pe.dataset.idx, ev); }; });
  return d;
}

function innerPreview(n) {
  const c = containerView(flat(), S.groups, n.id);
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('width', 164);
  svg.setAttribute('height', 84);
  svg.style.cssText = 'margin:4px 8px 0;background:var(--bg);border:1px solid var(--line);border-radius:6px;display:block;pointer-events:none';
  const all = c.nodes;
  const x0 = Math.min(...all.map((b) => b.x)), y0 = Math.min(...all.map((b) => b.y)), x1 = Math.max(...all.map((b) => b.x + nodeWidth(b))), y1 = Math.max(...all.map((b) => b.y + 70));
  const s = Math.min(150 / (x1 - x0), 72 / (y1 - y0));
  const pt = (b, dx, dy) => [8 + (b.x - x0 + dx) * s, 6 + (b.y - y0 + dy) * s];
  c.links.forEach((l) => {
    const A = all.find((b) => b.id === l[0]), B = all.find((b) => b.id === l[2]);
    if (!A || !B) return;
    const a = pt(A, nodeWidth(A), 30), b = pt(B, 0, 30);
    const p = document.createElementNS(SVG, 'path');
    p.setAttribute('d', `M${a[0]} ${a[1]} L${b[0]} ${b[1]}`);
    p.setAttribute('stroke', 'var(--dim)');
    p.setAttribute('fill', 'none');
    svg.append(p);
  });
  all.forEach((b) => {
    const p = pt(b, 0, 0);
    const r = document.createElementNS(SVG, 'rect');
    r.setAttribute('x', p[0]); r.setAttribute('y', p[1]); r.setAttribute('width', nodeWidth(b) * s); r.setAttribute('height', 56 * s); r.setAttribute('rx', 2);
    r.setAttribute('fill', b.proxy ? 'none' : 'var(--panel2)');
    r.setAttribute('stroke', b.proxy ? 'var(--dim)' : 'var(--line)');
    svg.append(r);
    const size = Math.max(3, 56 * s * 0.3), room = Math.floor((nodeWidth(b) * s - 4) / (size * 0.62));
    if (!b.proxy && room >= 3) {
      const t = document.createElementNS(SVG, 'text');
      t.setAttribute('x', p[0] + 2); t.setAttribute('y', p[1] + size * 1.3); t.setAttribute('font-size', size); t.setAttribute('fill', 'var(--text)');
      t.textContent = b.title.length > room ? `${b.title.slice(0, room - 1)}…` : b.title;
      svg.append(t);
    }
  });
  return svg;
}

/** Callbacks run after the nodes and the lines have been (re)drawn, so something that decorates them (the play lights) can put its marks back. */
const paintedListeners = new Set();
export const onGraphPainted = (f) => { paintedListeners.add(f); return () => paintedListeners.delete(f); };

export function drawLinks() {
  linksSvg.innerHTML = '';
  const v = current();
  const graphBounds = bounds(v.nodes);
  if (graphBounds) {
    const pad = 100, left = graphBounds.x0 - pad, top = graphBounds.y0 - pad, width = graphBounds.x1 - graphBounds.x0 + pad * 2, height = graphBounds.y1 - graphBounds.y0 + pad * 2;
    linksSvg.style.left = `${left}px`; linksSvg.style.top = `${top}px`; linksSvg.style.width = `${width}px`; linksSvg.style.height = `${height}px`;
    linksSvg.setAttribute('viewBox', `${left} ${top} ${width} ${height}`); linksSvg.setAttribute('preserveAspectRatio', 'none');
    // one neon gradient over the whole picture (cyan on the left, magenta on the right, the way the signal flows); the play lights stroke their lines with it
    linksSvg.insertAdjacentHTML('afterbegin', `<defs><linearGradient id="fx-neon" gradientUnits="userSpaceOnUse" x1="${left}" y1="0" x2="${left + width}" y2="0"><stop offset="0" stop-color="#2de2ff"/><stop offset="1" stop-color="#ff4fd8"/></linearGradient></defs>`);
  }
  v.links.forEach((l) => {
    const A = v.nodes.find((n) => n.id === l[0]), B = v.nodes.find((n) => n.id === l[2]);
    if (!A || !B || !A.outs[l[1]] || !B.ins[l[3]]) return;
    const type = A.outs[l[1]][1];
    const d = linkPath(portPos(A, 'out', l[1]), portPos(B, 'in', l[3]));
    const vis = document.createElementNS(SVG, 'path');
    vis.setAttribute('d', d);
    vis.setAttribute('class', 'vis');
    vis.dataset.link = linkId(l); vis.dataset.from = l[0]; vis.dataset.to = l[2]; vis.dataset.type = type;
    if (l[4]) vis.dataset.real = `${l[4].from}:${l[4].out}>${l[4].to}:${l[4].inp}`;   // the line of the real graph behind it (a group shows one line for several)
    vis.setAttribute('stroke', `var(--${type === 'text' ? 'text-c' : String(type).startsWith('control') ? 'control' : type})`);
    vis.setAttribute('stroke-width', S.selLink === linkId(l) ? 5 : 2.5);
    if (l[4]?.disabled) { vis.setAttribute('stroke-dasharray', '8 6'); vis.setAttribute('opacity', '0.35'); }
    const label = document.createElementNS(SVG, 'title'); label.textContent = l[4]?.disabled ? `${type} connection · disabled` : `${type} connection`; vis.append(label);
    const hit = document.createElementNS(SVG, 'path');
    hit.setAttribute('d', d);
    hit.setAttribute('class', 'hit');
    hit.onclick = (e) => { e.stopPropagation(); S.selLink = linkId(l); S.sel = null; S.selSet.clear(); notify(); };
    hit.oncontextmenu = (e) => {
      e.preventDefault(); e.stopPropagation();
      S.selLink = linkId(l);
      showMenu(e.clientX, e.clientY, [{ head: `${type} link${l[4]?.disabled ? ' · disabled' : ''}` }, { label: l[4]?.disabled ? 'Enable connection' : 'Disable connection', fn: () => l[4] && edit([{ type: 'setConnectionDisabled', to: l[4].to, input: l[4].inName, disabled: !l[4].disabled }]) }, { label: 'Delete link', danger: true, fn: () => deleteLink(l) }]);
    };
    linksSvg.append(vis, hit);
  });
  // references in a note pipeline (notes a.notes |> shift(8bars) |> merge(b.notes ...)) are not cables, but they are data flow: a thin dotted line from each clip used to the clip that uses it, drawn by itself from the text
  const byName = new Map(v.nodes.map((n) => [n.name || n.id, n]));
  v.nodes.forEach((B) => {
    if (B.tpl !== 'midi-clip' || !isExpression(B.params.notes ?? '')) return;
    const used = new Set([...String(B.params.notes).matchAll(/(?:^|[^\w.-])([A-Za-z_][\w-]*)\.notes\b/g)].map((m) => m[1]));
    used.forEach((name) => {
      const A = byName.get(name);
      if (!A || A === B || !A.outs?.length) return;
      const from = portPos(A, 'out', 0), to = { x: B.x, y: B.y + (HEAD + ROW / 2) * (sizes[B.id] || 1) };
      const ref = document.createElementNS(SVG, 'path');
      ref.setAttribute('d', linkPath(from, to));
      ref.setAttribute('class', 'vis ref'); ref.dataset.from = A.id; ref.dataset.to = B.id; ref.dataset.ref = refKey(A.id, B.id);
      ref.setAttribute('fill', 'none'); ref.setAttribute('stroke', 'var(--midi, #8fa6c9)'); ref.setAttribute('stroke-width', 1); ref.setAttribute('stroke-dasharray', '2 5'); ref.setAttribute('opacity', '0.55'); ref.style.pointerEvents = 'none';
      const label = document.createElementNS(SVG, 'title'); label.textContent = `${name} is used in the notes of ${B.name || B.id}`; ref.append(label);
      linksSvg.append(ref);
    });
  });
  if (tmp) {
    const p = document.createElementNS(SVG, 'path');
    p.setAttribute('d', linkPath(tmp.from, tmp.to));
    p.setAttribute('class', 'vis');
    p.setAttribute('stroke', 'var(--acc)');
    p.setAttribute('stroke-dasharray', '6 4');
    linksSvg.append(p);
  }
  for (const f of paintedListeners) f();
}

/** Runs the callback once, the first time the element is on screen (at once where IntersectionObserver does not exist). */
function whenVisible(element, run) {
  if (typeof IntersectionObserver !== 'function') { run(); return; }
  const observer = new IntersectionObserver((entries) => { if (entries.some((entry) => entry.isIntersecting)) { observer.disconnect(); run(); } }, { root: cv });
  observer.observe(element);
}

/** The template's own description of a port (a friendly title and a tooltip text), when it has one. */
const portSpec = (n, pt, side) => (S.tpl[n.tpl]?.[side] || []).find((p) => p.name === pt[0]);

function renderCrumbs() {
  const c = $('#crumbs');
  c.innerHTML = '';
  const name = S.doc && S.doc.project ? S.doc.project.name : S.project ? S.project.name : 'song';
  if (S.moduleStack.length) {
    const root = el('span', { className: 'c', textContent: name, title: S.path || name, 'data-koda-song': '' });
    root.onclick = () => { setModuleLevel(0).then(() => { setView('blocks', true); fit(); }); };
    c.append(root);
    S.moduleStack.forEach((entry, i) => {
      c.append(el('span', { textContent: '›' }));
      const crumb = el('span', { className: 'c', textContent: entry.instance });
      crumb.onclick = () => { if (i < S.moduleStack.length - 1) setModuleLevel(i + 1).then(fit); };
      c.append(crumb);
    });
    return;
  }
  S.stack.forEach((id, i) => {
    const label = id === 'root' ? name : S.groups[id] ? S.groups[id].title : id;
    const s = el('span', { className: 'c', textContent: label, title: i === 0 && S.path ? S.path : label });
    if (i === 0) s.setAttribute('data-koda-song', '');
    s.onclick = () => { if (i < S.stack.length - 1) { S.stack = S.stack.slice(0, i + 1); select(null); notify(); fit(); } };
    c.append(s);
    if (i < S.stack.length - 1) c.append(el('span', { textContent: '›' }));
  });
}

function drawMini() {
  const m = $('#mini');
  if (!m || m.style.display === 'none') return;
  const ctx = m.getContext('2d'), w = m.width, h = m.height;
  ctx.clearRect(0, 0, w, h);
  const v = current();
  const ns = v.nodes;
  if (!ns.length) return;
  const r = canvasRect();
  const M = 300;
  const b = bounds(ns);
  const x0 = b.x0 - M, y0 = b.y0 - M, x1 = b.x1 + M, y1 = b.y1 + M;
  const s = Math.min(w / (x1 - x0), h / (y1 - y0));
  mini = { x0, y0, s };
  const css = getComputedStyle(document.body);
  const col = (c) => css.getPropertyValue(c).trim() || css.getPropertyValue('--dim').trim();
  v.links.forEach((l) => {
    const A = ns.find((n) => n.id === l[0]), B = ns.find((n) => n.id === l[2]);
    if (!A || !B) return;
    ctx.strokeStyle = col('--dim');
    ctx.beginPath();
    ctx.moveTo((A.x + nodeWidth(A) - x0) * s, (A.y + 30 - y0) * s);
    ctx.lineTo((B.x - x0) * s, (B.y + 30 - y0) * s);
    ctx.stroke();
  });
  ns.forEach((n) => {
    ctx.fillStyle = S.selSet.has(n.id) ? col('--acc') : n.isGroup ? col('--warn') : col('--dim');
    ctx.fillRect((n.x - x0) * s, (n.y - y0) * s, nodeWidth(n) * s, 60 * s);
  });
  ctx.strokeStyle = col('--text');
  ctx.lineWidth = 1.5;
  ctx.strokeRect((-S.view.x / S.view.k - x0) * s, (-S.view.y / S.view.k - y0) * s, (r.width / S.view.k) * s, (r.height / S.view.k) * s);
}

/* ---------- moving blocks ---------- */
/** A press on a block selects it; a press on a block of a group that is selected keeps the group (so the whole group can be dragged) and only becomes a selection of that one block when it was a click. Returns whether the group was kept. */
function pressSelect(e, n) {
  const additive = e.shiftKey || e.ctrlKey;
  const keep = !additive && S.selSet.has(n.id) && S.selSet.size > 1;
  if (!keep) { select(n.id, additive); notifySelection(); return false; }
  const sx = e.clientX, sy = e.clientY;
  let far = false;
  const move = (ev) => { if (Math.hypot(ev.clientX - sx, ev.clientY - sy) > 4) far = true; };
  const up = () => {
    removeEventListener('pointermove', move, true);
    removeEventListener('pointerup', up, true);
    if (!far) { select(n.id); notifySelection(); }
  };
  addEventListener('pointermove', move, true);
  addEventListener('pointerup', up, true);
  return true;
}

function startDrag(e, n, head) {
  if (e.button !== 0) return;
  e.stopPropagation();
  pressSelect(e, n);
  if (n.isMidiEventNode) return;
  if (readOnly()) return;
  const v = current();
  const moving = (S.selSet.has(n.id) ? v.nodes.filter((x) => S.selSet.has(x.id)) : [n]).filter((x) => !x.isMidiEventNode && !S.pins.has(x.id));   // the inputs and outputs nodes of a group move too; a pinned block stays
  const start = moving.map((x) => [x, x.x, x.y]);
  const sx = e.clientX, sy = e.clientY;
  let moved = false;
  const captureTarget = e.target.closest('.t') || head;
  captureTarget.setPointerCapture(e.pointerId);
  const onMove = (ev) => {
    moved = true;
    start.forEach(([x, ox, oy]) => {
      x.x = snap(ox + (ev.clientX - sx) / S.view.k);
      x.y = snap(oy + (ev.clientY - sy) / S.view.k);
      const de = world.querySelector(`.node[data-id="${CSS.escape(x.id)}"]`);
      if (de) { de.style.left = `${x.x}px`; de.style.top = `${x.y}px`; }
    });
    drawLinks();
    drawMini();
  };
  const onUp = async () => {
    removeEventListener('pointermove', onMove);
    removeEventListener('pointerup', onUp);
    removeEventListener('pointercancel', onUp);
    if (!moved) return;
    const ops = [];
    for (const [x] of start) {
      if (x.proxy) { const group = S.groups[x.groupId]; if (group) { group.proxyPos = { ...(group.proxyPos || {}), [x.id]: { x: x.x, y: x.y } }; saveGroups(); } } else if (x.isGroup) { S.groups[x.id].x = x.x; S.groups[x.id].y = x.y; saveGroups(); } else ops.push({ type: 'setLayout', block: x.id, x: x.x, y: x.y });
    }
    if (ops.length) {
      quiet.layout = ops.every((op) => op.type === 'setLayout');
      try { await edit(ops); } finally { quiet.layout = false; }
    } else notify();
  };
  addEventListener('pointermove', onMove);
  addEventListener('pointerup', onUp);
  addEventListener('pointercancel', onUp);
}

/* ---------- links ---------- */
/** While a link is dragged: marks the inputs that can take it again, because a redraw of the blocks (a build status that arrives, an edit) throws the marks away. */
let linkMark = null;
function startLink(n, idx, e) {
  if (readOnly()) { toast('The song has errors: it is read-only until they are fixed.'); return; }
  const type = n.outs[idx][1];
  tmp = { from: portPos(n, 'out', idx), to: point(e.clientX, e.clientY) };
  cv.classList.add('linking');
  const mark = () => world.querySelectorAll('.port.in').forEach((p) => p.classList.add(p.dataset.node !== n.id && compatible(type, p.dataset.type) ? 'can' : 'cannot'));
  mark();
  linkMark = mark;
  const move = (ev) => {
    tmp.to = point(ev.clientX, ev.clientY);
    // once the line is really dragged (a plain click on the dot is something else), the effects show their hidden loop seam input as a place to drop it
    if (type === 'region' && !regionReveal.on && Math.hypot(ev.clientX - e.clientX, ev.clientY - e.clientY) > 5) { regionReveal.on = true; notify(); mark(); }
    // a control: every parameter that can be automated shows its control input as a place to drop it
    if (String(type).startsWith('control') && !regionReveal.control && Math.hypot(ev.clientX - e.clientX, ev.clientY - e.clientY) > 5) { regionReveal.control = true; notify(); mark(); }
    drawLinks();
  };
  const up = async (ev) => {
    removeEventListener('pointermove', move);
    removeEventListener('pointerup', up);
    cv.classList.remove('linking');
    linkMark = null;
    world.querySelectorAll('.port.in').forEach((p) => p.classList.remove('can', 'cannot'));
    const t = document.elementFromPoint(ev.clientX, ev.clientY);
    tmp = null;
    drawLinks();
    const reveal = regionReveal.on || regionReveal.control;
    if (t && t.classList && t.classList.contains('port') && t.dataset.dir === 'in') await completeLink(n, idx, t);
    if (reveal) { regionReveal.on = false; regionReveal.control = false; notify(); }   // unused loop seam and control ports go away again
  };
  addEventListener('pointermove', move);
  addEventListener('pointerup', up);
  drawLinks();
}

/** Tap-tap connecting on touch: the first tap on an output dot chooses it, the tap on an input dot connects. State in lib/touch-gestures.mjs. */
let tapState = null;
function clearTapChoice() {
  tapState = null;
  cv.classList.remove('connecting');
  world.querySelectorAll('.port.out.chosen').forEach((p) => p.classList.remove('chosen'));
  world.querySelectorAll('.port.in').forEach((p) => p.classList.remove('can', 'cannot'));
}
function tapPort(port) {
  const dir = port?.dataset.dir === 'in' || port?.dataset.dir === 'out' ? port.dataset.dir : null;
  const r = tapConnect(tapState, { dir, node: port?.dataset.node, idx: port ? +port.dataset.idx : 0 });
  if (r.action === 'none') return;
  const previous = tapState;
  clearTapChoice();
  if (r.action === 'need-output') { toast('Tap an output dot first, then the input dot to connect to.'); return; }
  if (r.action === 'choose') {
    if (readOnly()) { toast('The song has errors: it is read-only until they are fixed.'); return; }
    const n = nodeById(r.state.node);
    if (!n?.outs[r.state.idx]) return;
    tapState = r.state;
    cv.classList.add('connecting');
    const type = n.outs[r.state.idx][1];
    // a region output: the effects show their hidden loop seam input as a place to tap (and hide it again after the connection)
    if (regionReveal.on !== (type === 'region') || regionReveal.control !== String(type).startsWith('control')) { regionReveal.on = type === 'region'; regionReveal.control = String(type).startsWith('control'); notify(); }
    (world.querySelector(`.port.out[data-node="${CSS.escape(n.id)}"][data-idx="${r.state.idx}"]`) || port).classList.add('chosen');
    world.querySelectorAll('.port.in').forEach((p) => p.classList.add(p.dataset.node !== n.id && compatible(type, p.dataset.type) ? 'can' : 'cannot'));
    toast(`Tap an input dot to connect ${n.title}.`);
    return;
  }
  if (r.action === 'connect') {
    const n = nodeById(r.from.node);
    if (n) completeLink(n, r.from.idx, port).catch((error) => toast(error.message || String(error))).finally(() => { if (regionReveal.on || regionReveal.control) { regionReveal.on = false; regionReveal.control = false; notify(); } });
  }
  void previous;
}

async function completeLink(n, idx, t) {
  const type = n.outs[idx][1];
  {
    const target = nodeById(t.dataset.node), ti = +t.dataset.idx;
    if (!target || target.id === n.id) { toast('A block cannot connect to itself.'); return; }
    if (!compatible(type, target.ins[ti][1])) { toast(`Types differ: ${type} to ${target.ins[ti][1]}. Not connected.`); return; }
    const bound = target.isGroup ? target.inBound?.[ti] : null;
    if (bound && !n.isGroup && !n.proxy && S.groups[target.id]?.members.includes(bound.block) && !S.groups[target.id].members.includes(n.id)) {
      const bops = pinUnplaced();
      const had = flat().links.find((l) => l.to === bound.block && l.inName === bound.input);
      if (had) bops.push({ type: 'disconnect', to: bound.block, input: bound.input });
      bops.push({ type: 'connect', from: n.id, output: n.outs[idx][0], to: bound.block, input: bound.input });
      if (await edit(bops)) toast(`Connected ${n.title} → ${target.title}`);
      return;
    }
    // from an output line of a group to a block outside it: the connection starts at the block inside that the line stands for
    if (n.isGroup && !target.isGroup && !target.proxy && n.outKeys?.[idx]) {
      const key = n.outKeys[idx], cut = key.lastIndexOf(':');
      const inner = flat().nodes.find((node) => node.id === key.slice(0, cut));
      const output = inner?.outs[Number(key.slice(cut + 1))];
      if (inner && output && compatible(output[1], target.ins[ti][1]) && !S.groups[n.id]?.members.includes(target.id)) {
        const gops = pinUnplaced();
        if (flat().links.some((l) => l.to === target.id && l.inp === ti)) gops.push({ type: 'disconnect', to: target.id, input: target.ins[ti][0] });
        gops.push({ type: 'connect', from: inner.id, output: output[0], to: target.id, input: target.ins[ti][0] });
        if (await edit(gops)) toast(`Connected ${n.title} → ${target.title}`);
        return;
      }
    }
    if (n.isGroup || target.isGroup || n.proxy || target.proxy) { toast('Group ports follow the real connections: connect the blocks themselves.'); return; }
    const ops = pinUnplaced();
    const existing = flat().links.find((l) => l.to === target.id && l.inp === ti);
    if (existing) ops.push({ type: 'disconnect', to: target.id, input: target.ins[ti][0] });
    ops.push({ type: 'connect', from: n.id, output: n.outs[idx][0], to: target.id, input: target.ins[ti][0] });
    if (await edit(ops)) toast(`Connected ${n.title} → ${target.title}`);
  }
}

/** The real connections of the song behind a link of the canvas: one for a link between two blocks, and for a link at the port of a group the connections that go through that port. */
function realLinksOf(l) {
  if (l[4]) return [l[4]];
  const from = nodeById(l[0]), to = nodeById(l[2]);
  if (!from || !to) return [];
  const sourceMatches = (real) => (from.isGroup ? `${real.from}:${real.out}` === from.outKeys?.[l[1]] : real.from === from.id && real.out === l[1]);
  const bound = to.isGroup ? to.inBound?.[l[3]] : null;
  const targetMatches = (real) => (to.isGroup ? (bound ? real.to === bound.block && real.inName === bound.input : `${real.from}:${real.out}` === to.inKeys?.[l[3]] && S.groups[to.id]?.members.includes(real.to)) : real.to === to.id && real.inp === l[3]);
  return flat().links.filter((real) => sourceMatches(real) && targetMatches(real));
}

export async function deleteLink(l) {
  const real = realLinksOf(l);
  if (!real.length) { toast('This connection could not be found in the song.'); return; }
  S.selLink = null;
  await edit([...pinUnplaced(), ...real.map((ref) => ({ type: 'disconnect', to: ref.to, input: ref.inName }))]);
}

/* ---------- blocks ---------- */
function freeName(base) {
  const names = new Set(flat().nodes.map((n) => n.id));
  if (!names.has(base)) return base;
  let i = 2;
  while (names.has(`${base}-${i}`)) i++;
  return `${base}-${i}`;
}

export async function addBlock(tpl, x, y, version) {
  try { const last = JSON.parse(localStorage.getItem('ample.lib.recent') || '[]'); localStorage.setItem('ample.lib.recent', JSON.stringify([tpl, ...(Array.isArray(last) ? last : []).filter((x) => x !== tpl)].slice(0, 6))); } catch { /* storage may be blocked */ }   // the library lists the blocks that were added last
  const name = freeName(S.tpl[tpl]?.component ? tpl.slice(tpl.lastIndexOf('.') + 1) : tpl);
  const ok = await edit([{ type: 'addBlock', blockType: tpl, name, ...(version ? { version } : {}) }, { type: 'setLayout', block: name, x: snap(x), y: snap(y) }]);
  if (ok) {
    select(name);
    notify();
    const t = S.tpl[tpl];
    toast(`Added ${name} (${t.class} template v${version || t.version})`);
  }
}

async function editMidiVariant(n) {
  try {
    let moduleName = n.params['variant-module'];
    if (!moduleName) {
      const sourceFile = n.params['source-file'] || n.params.file;
      const existing = new Set((await api.get('/api/project/files')).map(file => file.path));
      const base = `${n.id.replace(/[^A-Za-z0-9_-]/g, '-').slice(0, 48)}-midi`;
      moduleName = base;
      for (let suffix = 2; existing.has(`modules/${moduleName}.module`); suffix++) moduleName = `${base.slice(0, 64 - String(suffix).length - 1)}-${suffix}`;
      await api.post('/api/midi/module', { file: sourceFile, track: n.params.track || 'all', name: moduleName });
      if (!await edit([
        { type: 'setParameter', block: n.id, name: 'source-file', value: sourceFile },
        { type: 'setParameter', block: n.id, name: 'variant-module', value: moduleName },
      ])) return;
    } else if (!(await api.get('/api/project/files')).some(file => file.path === `modules/${moduleName}.module`)) {
      toast('The MIDI variant module is missing; the original file is still active.');
      return;
    }
    const updated = flat().nodes.find(node => node.id === n.id) || n;
    await enterMidiVariant(updated, moduleName);
    fit();
  } catch (error) { toast(error.message || String(error)); }
}

async function switchMidiVariant(n, edited) {
  const file = edited ? n.params['variant-file'] : n.params['source-file'] || n.params.file;
  if (!file) { toast('Save the MIDI variant first.'); return; }
  if (file === n.params.file) { toast(edited ? 'The edited MIDI is already active.' : 'The original MIDI is already active.'); return; }
  await edit([{ type: 'setParameter', block: n.id, name: 'file', value: file }]);
}
async function convertMidiToSong(n) {
  const moduleNameField = el('input', { value: n.id.replace(/[^A-Za-z0-9_-]/g, '-'), ariaLabel: 'Generated module name' });
  const songNameField = el('input', { placeholder: 'Song name (when creating a new song)', ariaLabel: 'New song name' });
  const choice = await ask('Convert MIDI to song', 'Create the generated module and add it to the current song or a new song.', [{ label: 'Cancel', value: 'cancel' }, { label: 'Current song', value: 'current' }, { label: 'New song', value: 'new', primary: true }], [moduleNameField, songNameField]);
  if (choice === 'cancel' || choice === undefined) return;
  const moduleName = moduleNameField.value.trim();
  const songName = songNameField.value.trim();
  if (!/^[A-Za-z0-9_-]{1,64}$/.test(moduleName)) { toast('Module names need 1 to 64 letters, digits, underscores or hyphens.'); return; }
  if (choice === 'new' && !/^[A-Za-z0-9_-]{1,64}$/.test(songName)) { toast('Enter a song name using letters, digits, underscores or hyphens.'); return; }
  try {
    const projectPaths = new Set((await api.get('/api/project/files')).map(file => file.path));
    if (projectPaths.has(`modules/${moduleName}.module`)) { toast(`Module ${moduleName} already exists; choose another module name.`); return; }
    if (choice === 'new' && projectPaths.has(`${songName}.amplet`)) { toast(`Song ${songName} already exists; choose another song name.`); return; }
    const result = await api.post('/api/midi/module', { file: n.params.file, track: n.params.track || 'all', name: moduleName });
    let path = S.path;
    if (choice === 'new') {
      path = (await api.post('/api/songs', { name: songName })).path;
      const doc = await api.get(`/api/doc?path=${encodeURIComponent(path)}`);
      const alias = 'midi_import';
      const songSource = doc.text.replace(/\btempo\s+\d+\b/, `tempo ${result.tempo}`);
      const text = `${songSource.trimEnd()}\n\nuse ${moduleName}@1.0 as ${alias} {\n}\nmixer midi_mix {\n  from ${alias}.audio -> audio1\n}\nmaster master {\n  from midi_mix.audio -> audio\n}\n`;
      await api.put('/api/doc', { path, text, baseVersion: doc.version });
      await openDoc(path);
    } else {
      const removed = await edit([{ type: 'removeBlock', block: n.id }]);
      if (!removed) return;
      const alias = freeName('midi_import');
      const master = S.doc.blocks.find(b => b.type === 'master');
      let text = `${S.doc.text.trimEnd()}\n\nuse ${moduleName}@1.0 as ${alias} {\n}\n`;
      if (master?.froms?.length) {
        const original = master.froms[0];
        const merge = freeName('midi_merge');
        text += `mixer ${merge} {\n  from ${original.block}.${original.output} -> audio1\n  from ${alias}.audio -> audio2\n}\n`;
        text = text.replace(`from ${original.block}.${original.output} -> ${original.input}`, `from ${merge}.audio -> ${original.input}`);
      } else {
        const mix = freeName('midi_mix'), masterName = freeName('master');
        text += `mixer ${mix} {\n  from ${alias}.audio -> audio1\n}\nmaster ${masterName} {\n  from ${mix}.audio -> audio\n}\n`;
      }
      await applyText(text);
    }
    toast(`Converted ${n.params.file} to ${path}; generated ${result.path}. Save the song to record it in history.${result.warnings?.length ? ` Warnings: ${result.warnings.join('; ')}` : ''}`);
  } catch (error) { toast(error.message || String(error)); }
}

async function importMidiBlock(x, y) {
  try {
    const files = (await api.get('/api/project/files')).map(f => f.path).filter(p => /\.midi?$/i.test(p));
    if (!files.length) { toast('Add a .mid or .midi file to this project first.'); return; }
    const file = el('select', { ariaLabel: 'Project MIDI file', style: 'width:min(560px,80vw)' });
    files.forEach(path => file.append(el('option', { value: path, textContent: path })));
    const summary = el('p', { textContent: 'Reading MIDI summary...' });
    const track = el('select', { ariaLabel: 'MIDI track selection' }, [el('option', { value: 'all', textContent: 'All tracks' }), el('option', { value: 'single', textContent: 'One track by index' })]);
    const trackIndex = el('input', { type: 'number', min: '0', value: '0', disabled: true, ariaLabel: 'Zero-based MIDI track index' });
    track.onchange = () => { trackIndex.disabled = track.value !== 'single'; };
    let info = null, request = 0;
    const loadInfo = async () => {
      const currentRequest = ++request;
      info = null;
      summary.textContent = 'Reading MIDI summary...';
      try {
        const result = await api.get(`/api/midi/info?file=${encodeURIComponent(file.value)}`);
        if (currentRequest !== request) return;
        info = result;
        trackIndex.max = String(Math.max(0, result.tracks - 1));
        summary.textContent = `${result.tracks} ${result.tracks === 1 ? 'track' : 'tracks'} · ${Math.round(60_000_000 / result.microsecondsPerQuarter)} BPM initial tempo · ${result.bars} ${result.bars === 1 ? 'bar' : 'bars'} · ${result.lengthTicks} ticks`;
      } catch (error) { if (currentRequest === request) summary.textContent = `Could not read this MIDI file: ${error.message || error}`; }
    };
    file.onchange = () => { loadInfo().catch(error => toast(error.message || String(error))); };
    await loadInfo();
    const choice = await ask('Add MIDI block', 'Choose a project MIDI file and the tracks to expose in this song.', [{ label: 'Cancel', value: 'cancel' }, { label: 'Add block', value: 'add', primary: true }], [file, summary, track, trackIndex]);
    if (choice !== 'add') return;
    if (!info) { toast('Choose a readable project MIDI file first.'); return; }
    const selectedTrack = track.value === 'all' ? 'all' : trackIndex.value;
    if (selectedTrack !== 'all' && (!/^\d+$/.test(selectedTrack) || Number(selectedTrack) >= info.tracks)) { toast(`Choose a track from 0 to ${info.tracks - 1}.`); return; }
    const previous = S.sel;
    await addBlock('midi', x, y);
    if (S.sel && S.sel !== previous) {
      const id = S.sel;
      if (!await edit([{ type: 'setParameter', block: id, name: 'file', value: file.value }, { type: 'setParameter', block: id, name: 'track', value: selectedTrack }, { type: 'setParameter', block: id, name: 'bars', value: String(info.bars) }])) await edit([{ type: 'removeBlock', block: id }]);
    }
  } catch (error) { toast(error.message || String(error)); }
}

async function dropMidiFile(file, x, y) {
  if (S.moduleStack.length || container() !== 'root') { toast('Import MIDI at the song root level.'); return; }
  if (readOnly()) { toast('This song is read-only.'); return; }
  if (!/\.midi?$/i.test(file.name)) { toast('Choose a .mid or .midi file.'); return; }
  const track = el('select', { ariaLabel: 'MIDI track selection' });
  track.append(el('option', { value: 'all', textContent: 'All tracks' }), el('option', { value: 'single', textContent: 'One track by index' }));
  const trackIndex = el('input', { type: 'number', min: '0', max: '255', value: '0', ariaLabel: 'Zero-based MIDI track index' });
  const choice = await ask('Import MIDI', `Import ${file.name}? Choose all tracks or enter one zero-based track index.`, [{ label: 'Cancel', value: false }, { label: 'Import', value: true, primary: true }], [track, trackIndex]);
  if (!choice) return;
  const selectedTrack = track.value === 'all' ? 'all' : trackIndex.value;
  if (selectedTrack !== 'all' && (!/^\d+$/.test(selectedTrack) || Number(selectedTrack) > 255)) { toast('Enter a MIDI track index from 0 to 255.'); return; }
  const ghost = el('div', { className: 'node midi-pending', textContent: 'Importing MIDI...' });
  ghost.style.left = `${snap(x)}px`; ghost.style.top = `${snap(y)}px`; world.append(ghost);
  const progress = $('#dlg'); $('#dlg-t').textContent = 'Importing MIDI'; $('#dlg-b').textContent = 'Validating and saving the MIDI file...'; $('#dlg-extra').replaceChildren(); $('#dlg-btns').replaceChildren(); progress.oncancel = (event) => event.preventDefault(); progress.showModal();
  try {
    const extension = /\.midi$/i.test(file.name) ? '.midi' : '.mid';
    const result = await api.upload('/api/midi/upload', file, extension, selectedTrack);
    const name = freeName(file.name.replace(/\.midi?$/i, '').replace(/[^A-Za-z0-9_-]/g, '-') || 'midi');
    if (await edit([{ type: 'addBlock', blockType: 'midi', name }, { type: 'setParameter', block: name, name: 'file', value: result.path }, { type: 'setParameter', block: name, name: 'track', value: selectedTrack }, { type: 'setParameter', block: name, name: 'bars', value: String(result.bars) }, { type: 'setLayout', block: name, x: snap(x), y: snap(y) }])) {
      select(name); notify(); toast(`Imported ${result.path}`);
    }
  } catch (error) { toast(error.message || String(error)); }
  finally { progress.close(); ghost.remove(); }
}

/** Blocks play WAV files: an MP3, Ogg or FLAC file is decoded to a WAV next to it first (an existing one is reused). */
async function asWav(path) {
  if (!/.(mp3|ogg|flac)$/i.test(path)) return path;
  const r = await api.post('/api/project/convert-audio', { path });
  if (r.created) toast(`${path} was converted to ${r.path} (blocks play WAV files).`);
  return r.path;
}

/**
 * Adds a block that reads a project file (a drop on the canvas, "Use in song" in the project panel). `block` and `param` choose the block type when
 * the file could go to several; without them the preferred one for the kind is used. x and y place it; without them it goes to a free spot.
 */
export async function useFileInSong(path, { block = null, param = null, isDir = false, x = null, y = null } = {}) {
  if (readOnly()) { toast('This song is read-only.'); return false; }
  try { path = await asWav(path); } catch (error) { toast(error.message || String(error)); return false; }
  const kind = kindOf(path, isDir);
  if (!kind) { toast('This file type cannot create a block here.'); return false; }
  if (kind.id === 'module') { toast('A module is used with a use line: drag it into the code editor, or use Add in the source.'); return false; }
  const choice = block ? { block, param } : blocksFor(kind, S.templates)[0];
  if (!choice || !S.tpl[choice.block]) { toast('This file type cannot create a block here.'); return false; }
  const name = freeBlockName(path, flat().nodes.map((n) => n.id));
  const spot = x === null ? { x: 40 + (flat().nodes.length % 6) * 40, y: 40 + (flat().nodes.length % 6) * 30 } : { x, y };
  const operations = [{ type: 'addBlock', blockType: choice.block, name }, { type: 'setLayout', block: name, x: snap(spot.x), y: snap(spot.y) }, { type: 'setParameter', block: name, name: choice.param, value: path }];
  if (choice.block === 'midi') {
    const info = await api.get(`/api/midi/info?file=${encodeURIComponent(path)}`);
    operations.push({ type: 'setParameter', block: name, name: 'bars', value: String(info.bars) });
  }
  if (await edit(operations)) {
    select(name); notify();
    if (choice.block === 'import-audio') await placeAudioBlock(name, path);
    else toast(`Added ${name} from ${path}`);
    return true;
  }
  return false;
}

/** How long an audio file of the project lasts, in seconds. */
async function fileSeconds(path) {
  const buffer = await new OfflineAudioContext(1, 1, 44100).decodeAudioData(await (await fetch(`/api/project/audio?path=${encodeURIComponent(path)}`)).arrayBuffer());
  return buffer.duration;
}

/** The bars that a stretch of seconds takes at the tempo of the song, rounded up so that none of it is cut off. */
const barsFor = (seconds) => Math.max(1, Math.ceil(seconds / ((barBeats() * 60) / (Number(S.doc?.project?.tempo) || 120)) - 1e-6));

/** Makes the block as long as the sound it plays (or the part of it that was chosen), so the end is not cut off. */
async function fitLengthToAudio(n, seconds) {
  const first = n.parts?.[0];
  await edit([{ type: 'setParts', block: n.id, parts: [{ at: first?.at || 1, length: barsFor(seconds), name: first?.name || '' }] }]);
}

/** An audio file only plays when its block has a place on the timeline and a way to the output: bar 1 for as many bars as the file lasts, into a free mixer input (or the master). */
async function placeAudioBlock(name, path) {
  try {
    const bars = barsFor(await fileSeconds(path));
    const ops = [{ type: 'setParts', block: name, parts: [{ at: 1, length: bars, name: '' }] }];
    const nodes = flat().nodes, links = flat().links;
    let target = null;
    for (const node of nodes.filter((n) => n.tpl === 'mixer' || n.tpl === 'bus' || n.tpl === 'master')) {
      const free = node.ins.findIndex((input, index) => input[1] === 'audio' && !links.some((l) => l.to === node.id && l.inp === index));
      if (free >= 0) { target = { node, input: node.ins[free][0] }; break; }
    }
    if (target) ops.push({ type: 'connect', from: name, output: 'audio', to: target.node.id, input: target.input });
    await edit(ops);
    toast(target ? `Added ${name} from ${path}: bars 1 to ${bars}, connected to ${target.node.id}.` : `Added ${name} from ${path}: bars 1 to ${bars}. Connect its output to a mixer or the master to hear it.`);
  } catch (error) { toast(`Added ${name} from ${path}, but it could not be placed: ${error.message || error}`); }
}

/** Points a parameter of an existing block at a project file. */
export async function pointBlockAtFile(blockName, param, path) {
  if (readOnly()) { toast('This song is read-only.'); return false; }
  if (param === 'file') { try { path = await asWav(path); } catch (error) { toast(error.message || String(error)); return false; } }
  if (await edit([{ type: 'setParameter', block: blockName, name: param, value: path }])) { select(blockName); notify(); toast(`${blockName}: ${param} is now ${path}`); return true; }
  return false;
}

async function dropOutsideFile(file, x, y) {
  const kind = kindOf(file.name);
  if (!kind || kind.id === 'midi') { await dropMidiFile(file, x, y); return; }
  const done = await uploadProjectFiles([file], UPLOAD_FOLDER[kind.id] || 'assets', () => {});
  if (done[0]?.path) await useFileInSong(done[0].path, { x, y });
}

/** Delete removes the selected blocks (a block that another block still reads from is refused); with `force` (Ctrl+Delete) the links that leave the blocks are taken away first, so they can go. */
export async function deleteSelected(force = false) {
  const ids = [...S.selSet].filter((id) => { const n = nodeById(id); return n && !n.proxy && !n.isGroup && !n.isMidiEventNode; });
  const groups = [...S.selSet].filter((id) => S.groups[id]);
  groups.forEach((id) => { S.groups = ungroup(S.groups, id); });
  if (groups.length) saveGroups();
  if (!ids.length) { if (groups.length) notify(); return true; }
  // Remove blocks that nothing reads first, so a chain can be removed in one go.
  const order = [...ids].sort((a, b) => (flat().links.some((l) => l.from === a && ids.includes(l.to)) ? -1 : 0) - (flat().links.some((l) => l.from === b && ids.includes(l.to)) ? -1 : 0));
  const cut = force ? flat().links.filter((l) => ids.includes(l.from) && !ids.includes(l.to)).map((l) => ({ type: 'disconnect', to: l.to, input: l.inName })) : [];
  return edit([...pinUnplaced(order), ...cut, ...order.map((id) => ({ type: 'removeBlock', block: id }))]);   // false when a block was refused: its message must stay
}

/** Copies a block (the menu item Duplicate); a clip with stable random names asks what the copy should do with them. */
export const duplicateBlock = (n) => duplicate(n);

async function duplicate(n) {
  const name = freeName(`${n.id}-copy`);
  // a clip with a note pipeline or a random-stream is copied as text by the server, so that its stable random names can be reused, forked or left
  if (n.tpl === 'midi-clip' && (isExpression(n.params.notes ?? '') || Object.keys(n.params).some((k) => k.startsWith('random-stream:')))) {
    const used = new Set(seedSites(n.params.notes ?? '').map((s) => s.stable).filter(Boolean));
    for (const [k, v] of Object.entries(n.params)) if (k.startsWith('random-stream:')) { const stable = stableNameOf(String(v)); if (stable) used.add(stable); }
    let mode = 'reuse';
    if (used.size) {
      const names = [...used].map((x) => `"${x}"`).join(', ');
      const picked = await ask('Copy a clip with stable random names',
        `${n.title} draws its random values from the song-global stable name${used.size === 1 ? '' : 's'} ${names}. What should the copy do?\n\nReuse: the copy keeps the same names, so it makes exactly the same random choices as the original.\nFork: the copy gets its own branch of the same names (.fork("copy-${name}")): still related, but its own deterministic variation.\nIndependent: the copy gets new song-global names (${[...used][0]}_2 ...): a completely separate random family.\n\nExplicit seeds (seed=123456) have no name and are copied as they are.`,
        [{ label: 'Cancel', value: null }, { label: 'Reuse', value: 'reuse' }, { label: 'Fork', value: 'fork', primary: true }, { label: 'Independent', value: 'independent' }]);
      if (!picked) return;
      mode = picked;
    }
    try {
      const result = await api.post('/api/notes/duplicate', { path: S.path, block: n.id, name, mode });
      if (await applyText(result.text)) { select(name); notify(); }
    } catch (error) { toast(error.message); }
    return;
  }
  const ops = [{ type: 'addBlock', blockType: n.tpl, name }, ...Object.entries(n.params).map(([k, v]) => ({ type: 'setParameter', block: name, name: k, value: v })), { type: 'setLayout', block: name, x: snap(n.x + 30), y: snap(n.y + 30) }];
  if (await edit(ops)) { select(name); notify(); }
}

export async function toggleFlag(n, flag) {
  await edit([{ type: n.flags.includes(flag) ? 'removeFlag' : 'addFlag', block: n.id, flag }]);
}

export function groupSelected() {
  if (readOnly()) { toast('The song has errors: it is read-only until they are fixed.'); return; }
  const r = groupBlocks(flat(), S.groups, [...S.selSet].filter((id) => !nodeById(id)?.isMidiEventNode));
  if (r.error) { toast(r.error); return; }
  recordGroups(JSON.stringify(S.groups));
  S.groups = r.groups;
  saveGroups();
  select(r.id);
  notify();
  toast('Grouped. Double-click opens the group; the arrow on its title hides or shows the contents. Groups live in this view; the source is unchanged.');
}

export function ungroupNode(n) {
  recordGroups(JSON.stringify(S.groups));
  S.groups = ungroup(S.groups, n.id);
  saveGroups();
  select(null);
  notify();
  toast('Ungrouped: the blocks are back on this level.');
}

export function enter(n) {
  if (n.isModule) { enterModule(n).then(fit); return; }
  S.stack.push(n.id);
  select(null);
  notify();
  fit();
  toast(`Inside ${n.title}. Alt+Up or the breadcrumb goes back up.`);
}

export function up() {
  if (S.moduleStack.length) { setModuleLevel(S.moduleStack.length - 1).then(fit); return; }
  if (S.stack.length < 2) return;
  S.stack.pop();
  select(null);
  notify();
  fit();
}

export function selectAll() {
  S.selSet = new Set(current().nodes.filter((n) => !n.proxy).map((n) => n.id));
  S.sel = [...S.selSet].pop() || null;
  notify();
}

export async function alignSelected(axis) {
  const m = align(current().nodes.filter((n) => S.selSet.has(n.id) && !n.proxy && !n.isGroup && !S.pins.has(n.id)), axis);
  await edit([...m].map(([id, p]) => ({ type: 'setLayout', block: id, x: p.x, y: p.y })));
}

const EFFECT_MENU_GROUPS = [
  ['EQ & filters', ['eq', 'peq', 'filter', 'denoise', 'deesser']],
  ['Dynamics', ['compressor', 'duck', 'gate', 'limiter']],
  ['Space', ['reverb', 'convolution', 'delay']],
  ['Modulation', ['chorus', 'flanger', 'phaser', 'tremolo']],
  ['Levels & stereo', ['volume', 'pan', 'width', 'meter', 'normalize']],
  ['Timing & transforms', ['chop', 'cut', 'fade', 'granular', 'join', 'pitch', 'reverse', 'rewind', 'scratch', 'shift', 'stretch']],
  ['Voice', ['autotune', 'vocoder']],
  ['Other', ['bitcrusher', 'distortion']],
];

function effectMenuGroups(effects, item, byName) {
  const assigned = new Set(EFFECT_MENU_GROUPS.flatMap(([, ids]) => ids));
  const groups = EFFECT_MENU_GROUPS.map(([label, ids]) => ({ label, children: effects.filter((t) => ids.includes(t.id)).sort((a, b) => byName(a.title, b.title)).map(item) })).filter((group) => group.children.length);
  const remaining = effects.filter((t) => !assigned.has(t.id)).sort((a, b) => byName(a.title, b.title)).map(item);
  if (remaining.length) {
    const other = groups.find((group) => group.label === 'Other');
    if (other) other.children.push(...remaining);
    else groups.push({ label: 'Other', children: remaining });
  }
  groups.forEach((group) => group.children.sort((a, b) => byName(a.label, b.label)));
  return groups;
}

/** The blocks that can be hung on an output, grouped so the effect list stays short. */
function childMenuItems(n, place = null) {
  const outputType = (n.outs.find((port) => port[1] === 'audio') || n.outs[0])?.[1];
  if (!outputType) return [];
  const byName = (a, b) => a.localeCompare(b, 'en', { sensitivity: 'base' });
  const fits = S.templates.filter((t) => (S.tpl[t.id]?.inputs || []).some((port) => compatible(outputType, port.type)));
  const item = (t) => ({ label: t.title, title: t.description || '', fn: () => addChild(n, t.id, place).catch((error) => toast(error.message || String(error))) });
  const routingIds = new Set(['crossfade', 'splitter']);
  const routing = fits.filter((t) => t.category === 'Mix' || routingIds.has(t.id));
  const effects = fits.filter((t) => t.category === 'Effects' && !routingIds.has(t.id));
  const effectItems = effectMenuGroups(effects, item, byName);
  const categories = [...new Set(fits.filter((t) => t.category !== 'Effects' && t.category !== 'Mix').map((t) => t.category))].sort(byName);
  return [
    ...categories.map((category) => ({ label: category, children: fits.filter((t) => t.category === category).sort((a, b) => byName(a.title, b.title)).map(item) })),
    ...(effectItems.length ? [{ label: 'Effects', children: effectItems }] : []),
    ...(routing.length ? [{ label: 'Routing', children: routing.sort((a, b) => byName(a.title, b.title)).map(item) }] : []),
  ];
}

/** For a group: the blocks that can hang on its lines. With one line they are listed at once; with several the entries are the lines, and a small menu with the blocks comes up for the one that is chosen. */
function groupChildItems(n, event) {
  const lines = (n.outKeys || []).map((key, index) => {
    const cut = key.lastIndexOf(':');
    const inner = flat().nodes.find((node) => node.id === key.slice(0, cut));
    const port = inner?.outs[Number(key.slice(cut + 1))];
    return inner && port ? { label: n.outs[index][0], source: { ...inner, outs: [port] } } : null;
  }).filter(Boolean);
  const place = { x: snap(n.x + nodeWidth(n) + 80), y: snap(n.y) };
  if (!lines.length) return [];
  if (lines.length === 1) return childMenuItems(lines[0].source, place);
  const x = event.clientX, y = event.clientY;
  return lines.map((line) => ({ label: `From the line "${line.label}" ›`, fn: () => showMenu(x, y, [{ head: `Add child to "${line.label}"` }, ...childMenuItems(line.source, place)]) }));
}

/** Hangs a new block on the output of a block: an effect goes into the chain after it, anything else is connected beside it. */
async function addChild(n, templateId, place = null) {
  if (readOnly()) { toast('This song is read-only.'); return; }
  // an effect (audio in, audio out) goes into the chain after the block: what the block fed before is fed by the effect now
  const passesAudio = (S.tpl[templateId]?.inputs || []).some((port) => port.type === 'audio') && (S.tpl[templateId]?.outputs || []).some((port) => port.type === 'audio');
  if (passesAudio && n.outs.some((port) => port[1] === 'audio')) {
    const result = effectChainEdit(n, flat().nodes.find((node) => node.id === n.id)?.effectChain || [], flat().links, flat().nodes, S.tpl, { kind: 'add', type: templateId });
    if (!result.error && await edit(result.ops)) {
      const added = result.ops.find((op) => op.type === 'addBlock')?.name;
      if (added) { select(added); notify(); toast(`Added ${added} after ${n.id}.`); }
      return;
    }
  }
  const template = S.tpl[templateId];
  const output = n.outs.find((port) => port[1] === 'audio') || n.outs[0];
  const input = (template?.inputs || []).find((port) => compatible(output[1], port.type));
  if (!template || !output || !input) { toast('That block does not fit here.'); return; }
  const name = freeName(`${n.id}-${templateId}`);
  const at = place || { x: snap(n.x + nodeWidth(n) + 80), y: snap(n.y) };
  if (await edit([{ type: 'addBlock', blockType: templateId, name }, { type: 'setLayout', block: name, x: at.x, y: at.y }, { type: 'connect', from: n.id, output: output[0], to: name, input: input.name }])) { select(name); notify(); toast(`Added ${name} after ${n.id}.`); }
}

/** Auto formatting: puts the selected blocks in order by the way the signal flows (all blocks when only one or none is selected is not offered). */
const ARRANGE_MODES = ['ltr', 'ttb', 'wrap'];
const ARRANGE_NAMES = { ltr: 'left to right', ttb: 'top to bottom', wrap: 'in rows' };
let lastArrange = null;   // { key, mode, sig }: what the last Auto format did, so that pressing it again on the same blocks (when nothing would move) tries the next layout
const placeSig = (nodes, ids) => ids.map((id) => { const n = nodes.find((x) => x.id === id); return n ? `${id}@${n.x},${n.y}` : id; }).sort().join('|');
export async function autoArrangeSelected() {
  const chosen = current().nodes.filter((n) => S.selSet.has(n.id) && !n.proxy && !n.isGroup);
  const nodes = flat().nodes.map((n) => ({ id: n.id, x: n.x, y: n.y, w: nodeWidth(n), h: nodeHeight(n), last: n.tpl === 'master' }));
  const ids = chosen.map((n) => n.id), key = [...ids].sort().join('|');
  // the layout after the one this selection got last, as long as the blocks are still where that layout put them; otherwise the first one
  const again = lastArrange && lastArrange.key === key && lastArrange.sig === placeSig(nodes, ids);
  let at = new Map(), mode = 'ltr';
  for (let k = 0; k < ARRANGE_MODES.length; k++) {
    mode = ARRANGE_MODES[((again ? ARRANGE_MODES.indexOf(lastArrange.mode) + 1 : 0) + k) % ARRANGE_MODES.length];
    at = autoArrange(nodes, flat().links, ids, [...S.pins], { mode });
    if (!at.size) break;
    if ([...at].some(([id, p]) => { const n = nodes.find((x) => x.id === id); return n.x !== p.x || n.y !== p.y; })) break;   // this one moves something; if not, the next layout is tried
  }
  if (!at.size) { toast('Select two or more blocks to arrange them.'); return; }
  await edit([...at].map(([id, p]) => ({ type: 'setLayout', block: id, x: p.x, y: p.y })));
  lastArrange = { key, mode, sig: placeSig(nodes.map((n) => ({ ...n, x: at.get(n.id)?.x ?? n.x, y: at.get(n.id)?.y ?? n.y })), ids) };
  toast(`Arranged ${at.size} blocks ${ARRANGE_NAMES[mode]}. Press Auto format again for another layout.`);
}

export async function distributeSelected(axis) {
  const m = distribute(current().nodes.filter((n) => S.selSet.has(n.id) && !n.proxy && !n.isGroup && !S.pins.has(n.id)), axis);
  if (!m.size) { toast('Select three or more blocks to distribute them.'); return; }
  await edit([...m].map(([id, p]) => ({ type: 'setLayout', block: id, x: p.x, y: p.y })));
}

/** Inside a group: add a line to the group by hand for an input or an output of a block (typed like that input or output), and take such a line away again. */
function exposeItems(n) {
  const groupId = container(), g = S.groups[groupId];
  if (!g || groupId === 'root' || n.proxy || n.isGroup || !g.members.includes(n.id)) return [];
  const change = (apply) => { recordGroups(JSON.stringify(S.groups)); apply(S.groups[groupId]); saveGroups(); notify(); };
  const insFree = n.ins.filter((port) => !(g.exposedIns || []).some((e) => e.block === n.id && e.input === port[0]));
  const outFree = n.outs.map((port, index) => ({ port, index })).filter(({ index }) => !(g.exposedOuts || []).some((e) => e.block === n.id && e.out === index));
  return [
    ...(outFree.length ? [{ label: 'Add an output line to the group', children: outFree.map(({ port, index }) => ({ label: `${port[0]} (${port[1]})`, fn: () => change((group) => { group.exposedOuts = [...(group.exposedOuts || []), { block: n.id, out: index, name: port[0], type: port[1] }]; }) })) }] : []),
    ...(insFree.length ? [{ label: 'Add an input line to the group', children: insFree.map((port) => ({ label: `${port[0]} (${port[1]})`, fn: () => change((group) => { group.exposedIns = [...(group.exposedIns || []), { block: n.id, input: port[0], type: port[1] }]; }) })) }] : []),
  ];
}

/** Menu entries that give the lines of a group a name (a hint of what comes out of it), for the group and for the outputs and inputs nodes inside it. */
function lineNameItems(n) {
  const groupId = n.isGroup ? n.id : n.groupId;
  const keys = n.isGroup ? [...(n.outKeys || []).map((key, i) => ({ key, label: n.outs[i][0], kind: 'output' })), ...(n.inKeys || []).map((key, i) => ({ key, label: n.ins[i][0], kind: 'input' }))]
    : n.id === 'out' ? (n.outKeys || []).map((key, i) => ({ key, label: n.ins[i][0], kind: 'output' })) : (n.inKeys || []).map((key, i) => ({ key, label: n.outs[i][0], kind: 'input' }));
  if (!groupId || !S.groups[groupId] || !keys.length) return [];
  return keys.map((line) => ({ label: `Name the ${line.kind} "${line.label}"...`, fn: async () => {
    const input = el('input', { value: S.groups[groupId].labels?.[line.key] || '', placeholder: line.key.replace(':', ' · '), 'aria-label': 'Name of the line', maxLength: 40 });
    if (!(await ask('Name this line', 'A short name that shows on the group and when you choose what to play. Empty goes back to the usual name.', [{ label: 'Save', value: true, primary: true }, { label: 'Cancel', value: false }], [input]))) return;
    recordGroups(JSON.stringify(S.groups));
    const labels = { ...(S.groups[groupId].labels || {}) };
    if (input.value.trim()) labels[line.key] = input.value.trim(); else delete labels[line.key];
    S.groups[groupId].labels = labels;
    saveGroups(); notify();
  } }));
}

function nodeMenu(e, n) {
  if (n.proxy) {
    const groupId = n.groupId;
    const own = (n.bound || []).map((b, i) => (b ? { b, i } : null)).filter(Boolean);
    const away = own.map(({ b, i }) => ({ label: `Remove the line "${(n.id === 'out' ? n.ins[i] : n.outs[i])[0]}"`, fn: () => { const group = S.groups[groupId]; if (!group) return; recordGroups(JSON.stringify(S.groups)); if (n.id === 'out') group.exposedOuts = (group.exposedOuts || []).filter((e) => !(e.block === b.block && e.out === b.out)); else group.exposedIns = (group.exposedIns || []).filter((e) => !(e.block === b.block && e.input === b.input)); saveGroups(); notify(); } }));
    showMenu(e.clientX, e.clientY, [{ head: n.title }, ...lineNameItems(n), ...away, { label: 'Up one level', fn: up }]);
    return;
  }
  if (n.isMidiEventNode) { showMenu(e.clientX, e.clientY, [{ head: n.title }, { label: 'Edit track events', fn: () => { select(n.id); notifySelection(); } }, { label: 'Show in the source editor', fn: () => gotoSource(n) }]); return; }
  if (n.isModule) { showMenu(e.clientX, e.clientY, [{ head: `${n.title}  (module)` }, ...(n.isMidiModule ? [{ label: 'Convert and download MIDI', fn: () => api.downloadMidiModule(n.moduleName).then(warnings => { if (warnings) toast(warnings); }).catch(error => toast(error.message)) }] : []), { label: 'Open module (double-click)', fn: () => enter(n) }, { label: 'Show in the source editor', fn: () => gotoSource(n) }]); return; }
  const ro = readOnly();
  if (ro) { showMenu(e.clientX, e.clientY, [{ head: `${n.title} (read-only)` }, { label: 'Show in the source editor', fn: () => gotoSource(n) }, ...(S.peek?.ai && !n.isGroup && !n.proxy ? [{ label: 'Build and listen to this block (AI proposal)', fn: () => import('./ai.js').then((m) => m.buildAndListen(S.peek.ai, n.id)) }] : [])]); return; }
  const many = S.selSet.size > 1 && S.selSet.has(n.id);
  const aiBlocks = [];
  const collectAiBlock = (id) => {
    const group = S.groups[id];
    if (group) { for (const member of group.members || []) collectAiBlock(member); return; }
    const block = nodeById(id);
    if (block && !block.proxy && !block.isMidiEventNode && !aiBlocks.includes(id)) aiBlocks.push(id);
  };
  for (const id of many ? S.selSet : [n.id]) collectAiBlock(id);
  const aiLabel = n.isGroup ? `group “${n.title}”` : many ? `${aiBlocks.length} selected blocks` : `block “${n.title}”`;
  const tools = [
    ...(n.tpl === 'midi' && n.params.file ? [{ label: 'Edit MIDI variant...', fn: () => editMidiVariant(n) }] : []),
    ...(n.tpl === 'midi' && n.params['source-file'] ? [{ label: 'Use original MIDI', fn: () => switchMidiVariant(n, false) }] : []),
    ...(n.tpl === 'midi' && n.params['variant-file'] ? [{ label: 'Use edited MIDI variant', fn: () => switchMidiVariant(n, true) }] : []),
    ...(n.tpl === 'midi' && n.params.file ? [{ label: 'MIDI file details', fn: async () => { try { const m = await api.get(`/api/midi/info?file=${encodeURIComponent(n.params.file)}`); toast(`${m.tracks} tracks · ${Math.round(60_000_000 / m.microsecondsPerQuarter)} BPM · ${m.bars} bars · ${m.lengthTicks} ticks`); } catch (error) { toast(error.message); } } }] : []),
    ...(n.tpl === 'midi' && n.params.file ? [{ label: 'Convert and download MIDI', fn: () => api.downloadMidi(n.params.file).catch(error => toast(error.message)) }] : []),
    ...(n.tpl === 'sequencer' ? [{ label: 'Open the sequencer', fn: () => import('./sequencer.js').then((m) => m.openSequencer(n.id)) }] : []),
    ...(n.tpl === 'drums' ? [{ label: 'Patterns view', fn: () => import('./patternview.js').then((m) => m.openPatternView(n.id)) }] : []),
    ...(n.tpl === 'midi' && n.params.file ? [{ label: 'Compare A/B...', fn: () => openMidiCompare(n.params.file, S.build.takes[n.id], S.build.takes[n.id], S.build.takes) }] : []),
    ...(n.tpl === 'midi' && n.params.file ? [{ label: 'Convert to song...', fn: () => convertMidiToSong(n) }] : []),
    ...(n.tpl === 'import-audio' && n.params.file ? [{ label: 'Make the block as long as the audio', fn: async () => { try { const part = Number(n.params.end) > 0 ? Number(n.params.end) - (Number(n.params.start) || 0) : (await fileSeconds(String(n.params.file).trim().replace(/^"|"$/g, ''))) - (Number(n.params.start) || 0); await fitLengthToAudio(n, part); toast('The block now lasts as long as the audio it plays.'); } catch (error) { toast(error.message || String(error)); } } }] : []),
    ...(n.isGroup || n.proxy || many ? [] : derivedMenuItems(n)),
    ...(n.isGroup ? lineNameItems(n) : []),
    ...(n.isGroup || n.proxy || many ? [] : componentMenuItems(n)),
    ...exposeItems(n),
    ...(n.isGroup ? [{ label: 'Open (double-click)', fn: () => enter(n) }, { label: 'Ungroup (Ctrl+Shift+G)', fn: () => ungroupNode(n) }, { label: n.expanded ? 'Hide contents' : 'Show contents', fn: () => { S.groups[n.id].expanded = !n.expanded; saveGroups(); notify(); } }] : []),
    ...(!n.isGroup && n.dyn ? [{ label: 'Make variants...', fn: () => openVariants(n.id) }] : []),
    ...(n.tpl === 'lyrics' ? [{ label: 'Edit lyrics...  (double-click)', fn: () => openLyrics(n) }] : []),
  ];
  const layoutItems = [
    ...(!(n.isGroup || n.proxy) ? [(() => { const ids = (many ? [...S.selSet] : [n.id]).filter((id) => { const x = nodeById(id); return x && !x.proxy && !x.isGroup; }); const allPinned = ids.every((id) => S.pins.has(id)); return { label: allPinned ? (ids.length > 1 ? `Unpin ${ids.length} blocks` : 'Unpin') : (ids.length > 1 ? `Pin ${ids.length} blocks in place` : 'Pin in place'), title: 'A pinned block keeps its place: it is not moved by dragging or by Auto format, and Auto format arranges the others around it', fn: () => setPinned(ids, !allPinned) }; })()] : []),
    ...(many ? [{ label: `Auto format ${S.selSet.size} blocks`, fn: autoArrangeSelected }] : []),
  ];
  const editItems = [
    ...(!n.isGroup ? [{ label: 'Show in the source editor', fn: () => gotoSource(n) }] : []),
    ...(many ? [{ label: `Group selected (${S.selSet.size} blocks) (Ctrl+G)`, fn: groupSelected }] : []),
    ...(missingSetting(n) ? [{ label: `Use the project default (${missingSetting(n).key} ${missingSetting(n).value})`, fn: () => useProjectDefault(n) }] : []),
    ...(!n.isGroup ? [
      ...(['eq', 'filter', 'compressor', 'duck', 'distortion', 'delay', 'reverb'].includes(n.tpl) ? [{ label: n.params.enabled === 'off' ? 'Enable effect' : 'Bypass effect', fn: () => edit([{ type: 'setParameter', block: n.id, name: 'enabled', value: n.params.enabled === 'off' ? 'on' : 'off' }]) }] : []),
      ...(blockReadsClock(n.id) && !readOnly() ? [{ label: 'Bake clock values of this block…', fn: () => bakeDynamicHelpers(n.id) }] : []),
      ...(n.dyn ? [{ label: n.flags.includes('bake') ? 'Unbake (back to recipe only)' : 'Bake (freeze the result into the project)', fn: () => toggleFlag(n, 'bake') }] : []),
      { label: n.flags.includes('disabled') ? 'Enable block' : 'Disable block', fn: () => toggleFlag(n, 'disabled') },
      { label: 'Save selection as template…', fn: () => { if (!S.selSet.has(n.id)) select(n.id); import('./project-templates.js').then((m) => m.openSaveTemplate()); } },
      { label: 'Ask Koda about this block', fn: () => import('./koda.js').then((m) => m.ask(m.subjectOfBlock(n.id))) },
      { label: n.flags.includes('lock') ? 'Unlock' : 'Lock (freeze parameters)', fn: () => toggleFlag(n, 'lock') },
      { label: 'Duplicate', fn: () => duplicate(n) },
    ] : []),
  ];
  const buildItems = [
    ...(n.isGroup ? [{ label: 'Build the group', fn: async () => { try { const outputs = [...new Set((n.outKeys || []).map((key) => key.slice(0, key.lastIndexOf(':'))))]; for (const id of outputs.length ? outputs : S.groups[n.id].members) await build(id); toast(`Built ${n.title}.`); } catch (error) { toast(error.message || String(error)); } } }] : [{ label: 'Build only this block', fn: () => build(n.id) }]),
    { label: n.isGroup ? 'Render the chosen line to file...' : 'Render to file...', fn: () => { const id = n.isGroup ? chosenLine(n)?.block : n.id; if (!id) { toast('This group has no sound line to render.'); return; } renderBlockToFile(id).catch((error) => toast(error.message || String(error))); } },
    { label: S.selSet.size > 1 ? `Save ${S.selSet.size} blocks as a bundle...` : 'Save as bundle...', fn: () => import('./bundles.js').then((m) => m.saveAsBundle(S.selSet.size > 1 ? [...S.selSet] : [n.id])) },
  ];
  const addItems = n.outs.length ? n.isGroup ? groupChildItems(n, e) : childMenuItems(n) : [];
  showMenu(e.clientX, e.clientY, [
    { head: `${n.title}  (${n.cls} v${n.ver})` },
    { label: 'Properties', fn: () => { select(n.id); notifySelection(); notify(); } },
    { label: 'Koda', children: [
      { label: 'Look at this', fn: () => import('./koda.js').then((m) => m.lookAtBlock(n.id)) },
      { label: 'Ask Koda about this', fn: () => import('./koda.js').then((m) => { m.lookAtBlock(n.id); setTimeout(() => m.ask(m.subjectOfBlock(n.id)), 1300); }) },
      { label: 'Look at something else...', fn: () => import('./koda.js').then((m) => m.pickLook()) },
      { label: 'Show or hide Koda', fn: () => import('./koda.js').then((m) => m.toggleKoda()) },
    ] },
    ...(addItems.length ? [{ label: 'Add', children: addItems }] : []),
    { label: 'Build & export', children: buildItems },
    ...(layoutItems.length ? [{ label: 'Layout', children: layoutItems }] : []),
    ...(editItems.length ? [{ label: 'Edit', children: editItems }] : []),
    ...(tools.length ? [{ label: 'Block tools', children: tools }] : []),
    ...(aiBlocks.length ? [{ label: 'AI', children: aiItems({ context: 'blocks', blocks: aiBlocks, contextLabel: aiLabel, path: S.path, song: S.path }) }] : []),
    { label: 'Remove', children: [
      { label: n.isGroup ? 'Ungroup and remove the group' : 'Delete', danger: true, fn: n.isGroup ? () => ungroupNode(n) : () => { select(n.id); notify(); deleteSelected(); } },
      ...(!n.isGroup ? [{ label: 'Delete and disconnect (Ctrl+Delete)', danger: true, fn: () => { if (!S.selSet.has(n.id)) select(n.id); notify(); deleteSelected(true); } }] : []),
    ] },
  ]);
}

/* ---------- canvas events ---------- */
export function initCanvas() {
  let fitAfterLoad = false;
  const fitLoadedSong = () => {
    const r = canvasRect();
    if (!fitAfterLoad || r.width <= 0 || r.height <= 0) return;
    fitAfterLoad = false;
    fit();
  };
  document.addEventListener('ample:song-opened', () => { fitAfterLoad = true; requestAnimationFrame(fitLoadedSong); });
  new ResizeObserver(() => { if (fitAfterLoad) requestAnimationFrame(fitLoadedSong); }).observe(cv);
  let pinch = null, touchingCanvas = false;
  const touchPair = (touches) => {
    const [a, b] = touches, rect = canvasRect();
    return { x: (a.clientX + b.clientX) / 2 - rect.left, y: (a.clientY + b.clientY) / 2 - rect.top, distance: Math.hypot(b.clientX - a.clientX, b.clientY - a.clientY) };
  };
  cv.addEventListener('touchstart', (event) => {
    if (event.touches.length !== 2) return;
    event.preventDefault();
    touchingCanvas = true;
    pinch = touchPair(event.touches);
    cv.onpointermove = null; cv.onpointerup = null;
    cv.querySelectorAll('.nh').forEach((head) => { head.onpointermove = null; head.onpointerup = null; });
  }, { passive: false, capture: true });
  cv.addEventListener('touchmove', (event) => {
    if (!touchingCanvas) return;
    event.preventDefault();
    if (event.touches.length !== 2 || !pinch) return;
    const next = touchPair(event.touches);
    S.view = zoomAt(S.view, pinch.x, pinch.y, next.distance / Math.max(1, pinch.distance));
    S.view.x += next.x - pinch.x; S.view.y += next.y - pinch.y;
    pinch = next; applyView();
  }, { passive: false, capture: true });
  const finishPinch = (event) => {
    if (!touchingCanvas) return;
    if (event.cancelable) event.preventDefault();
    pinch = null;
    if (!event.touches.length) { touchingCanvas = false; cv.style.cursor = 'default'; }
  };
  cv.addEventListener('touchend', finishPinch, { passive: false, capture: true });
  cv.addEventListener('touchcancel', finishPinch, { passive: false, capture: true });
  ['pointerdown', 'pointermove', 'pointerup'].forEach((type) => cv.addEventListener(type, (event) => {
    if (touchingCanvas && event.pointerType === 'touch') { event.preventDefault(); event.stopImmediatePropagation(); }
  }, { capture: true }));
  // a tap or click on the empty canvas, then at once a second press that is held and dragged, draws the selection rectangle too (touch and mouse, without Shift)
  let lastTap = null;
  cv.onpointerdown = (e) => {
    if (e.button !== 0 && e.button !== 1) return;
    $('#menu').style.display = 'none';
    const sx = e.clientX, sy = e.clientY, ox = S.view.x, oy = S.view.y;
    let moved = false;
    const doubled = e.button === 0 && !!lastTap && performance.now() - lastTap.at < 450 && Math.hypot(sx - lastTap.x, sy - lastTap.y) < 30;
    lastTap = null;
    cv.setPointerCapture(e.pointerId);
    if (doubled || (e.shiftKey && !e.ctrlKey && !e.metaKey)) {   // Ctrl+Shift is Koda's (adding a thing to look at), not the rectangle
      const r0 = canvasRect();
      const band = $('#band');
      band.style.display = 'block';
      const upd = (ev) => {
        band.style.left = `${Math.min(sx, ev.clientX) - r0.left}px`;
        band.style.top = `${Math.min(sy, ev.clientY) - r0.top}px`;
        band.style.width = `${Math.abs(ev.clientX - sx)}px`;
        band.style.height = `${Math.abs(ev.clientY - sy)}px`;
      };
      upd(e);
      cv.onpointermove = upd;
      cv.onpointerup = (ev) => {
        cv.onpointermove = null;
        cv.onpointerup = null;
        band.style.display = 'none';
        const hit = nodesInRect(current().nodes, point(sx, sy), point(ev.clientX, ev.clientY));
        hit.forEach((n) => S.selSet.add(n.id));
        S.sel = [...S.selSet].pop() || null;
        S.selLink = null;
        notify();
        if (S.selSet.size) toast(`${S.selSet.size} selected. Ctrl+G groups them.`);
      };
      return;
    }
    cv.style.cursor = 'grabbing';
    cv.onpointermove = (ev) => { moved = true; S.view.x = ox + ev.clientX - sx; S.view.y = oy + ev.clientY - sy; applyView(); };
    cv.onpointerup = () => {
      cv.onpointermove = null;
      cv.onpointerup = null;
      cv.style.cursor = 'default';
      if (!moved) { select(null); notify(); lastTap = { at: performance.now(), x: sx, y: sy }; }
    };
  };
  cv.addEventListener('wheel', (e) => {
    e.preventDefault();
    const r = canvasRect();
    S.view = zoomAt(S.view, e.clientX - r.left, e.clientY - r.top, Math.exp(-e.deltaY * 0.0015));
    applyView();
  }, { passive: false });
  // touch: tap an output dot, then an input dot (a tap on the chosen dot again cancels); a dragged link keeps working
  cv.addEventListener('click', (e) => { if (e.pointerType === 'touch' && e.target.closest?.('.port')) tapPort(e.target.closest('.port')); });
  $('#z-in').onclick = () => zoomBy(1.25);
  $('#z-out').onclick = () => zoomBy(0.8);
  $('#z-fit').onclick = () => fit(true);
  $('#z-100').onclick = () => { S.view.k = 1; applyView(); };
  // a project file dragged over the canvas: a node that reads that kind of file lights up, empty canvas makes a new block (the drag state is kept by dragfile.js)
  const dropNode = (e) => {
    const drag = draggedFile();
    const node = e.target.closest?.('.node');
    if (!drag?.kind || !node) return null;
    const n = nodeById(node.dataset.id);
    const param = n && (S.tpl[n.tpl]?.parameters || []).find((p) => paramAccepts(p, drag.kind));
    return param ? { id: n.id, param: param.name } : null;
  };
  cv.ondragover = (e) => {
    e.preventDefault();
    const drag = draggedFile();
    document.querySelectorAll('.node.drop-ok').forEach((node) => node.classList.remove('drop-ok'));
    if (!drag) return;
    const onNode = dropNode(e);
    if (onNode) { e.target.closest('.node')?.classList.add('drop-ok'); cv.classList.remove('drop-invalid'); return; }
    cv.classList.toggle('drop-invalid', !drag.kind || blocksFor(drag.kind, S.templates).length === 0);
  };
  cv.ondragleave = () => { cv.classList.remove('drop-invalid'); document.querySelectorAll('.node.drop-ok').forEach((node) => node.classList.remove('drop-ok')); };
  cv.ondrop = (e) => {
    e.preventDefault();
    const file = e.dataTransfer.files?.[0];
    if (file) { const p = point(e.clientX, e.clientY); dropOutsideFile(file, p.x - W / 2, p.y - 14).catch((error) => toast(error.message || String(error))); return; }
    const projectPath = draggedFile()?.path || e.dataTransfer.getData('application/x-ample-path');
    const onNode = dropNode(e);
    cv.classList.remove('drop-invalid');
    document.querySelectorAll('.node.drop-ok').forEach((node) => node.classList.remove('drop-ok'));
    if (projectPath && onNode) { pointBlockAtFile(onNode.id, onNode.param, projectPath).catch((error) => toast(error.message)); return; }
    if (projectPath) { const p = point(e.clientX, e.clientY); useFileInSong(projectPath, { isDir: !!draggedFile()?.isDir, x: p.x - W / 2, y: p.y - 14 }).catch((error) => toast(error.message)); return; }
    const component = e.dataTransfer.getData('application/x-ample-component');
    if (component) {
      // an entry of the component library: a block (the import is written first when the song does not have it yet)
      const p = point(e.clientX, e.clientY);
      import('./component-library.js').then((m) => m.dropComponent({ ...JSON.parse(component), imported: !!S.tpl[JSON.parse(component).name] }, p.x - W / 2, p.y - 14)).catch((error) => toast(error.message || String(error)));
      return;
    }
    const id = e.dataTransfer.getData('text/plain');
    if (S.tpl[id]) { const p = point(e.clientX, e.clientY); addBlock(id, p.x - W / 2, p.y - 14); }
  };
  cv.oncontextmenu = (e) => {
    e.preventDefault();
    const p = point(e.clientX, e.clientY);
    // the blocks by category (A to Z), each category a list that opens beside it, and the blocks in it A to Z
    const byName = (a, b) => a.localeCompare(b, 'en', { sensitivity: 'base' });
    const categories = [...new Set(S.templates.map((t) => t.category))].sort(byName);
    const items = readOnly() ? [] : categories.map((category) => {
      const templates = S.templates.filter((t) => t.category === category).sort((a, b) => byName(a.title, b.title));
      const item = (t) => ({ label: t.title, title: t.description || '', fn: () => addBlock(t.id, p.x, p.y) });
      return { label: category, children: category === 'Effects' ? effectMenuGroups(templates, item, byName) : templates.map(item) };
    });
    if (!readOnly()) items.push({ label: 'AI', children: aiItems({ context: 'canvas', path: S.path, song: S.path }) }, { label: 'Layout', children: [{ label: 'Fit', fn: fit }] });
    items.sort((a, b) => byName(a.label, b.label));
    showMenu(e.clientX, e.clientY, [{ head: 'Add block here' }, { label: 'Koda', children: [{ label: 'Look at something...', fn: () => import('./koda.js').then((m) => m.pickLook()) }, { label: 'Show or hide Koda', fn: () => import('./koda.js').then((m) => m.toggleKoda()) }] }, ...(S.tpl.midi && !readOnly() ? [{ label: 'Import MIDI file...', fn: () => importMidiBlock(p.x, p.y) }] : []), ...items, '-', { label: 'Bundle library...', fn: () => import('./bundles.js').then((m) => m.openBundleLibrary()) }, { label: 'Select all (Ctrl+A)', fn: selectAll }, ...(S.selSet.size > 1 ? [{ label: `Group selected (${S.selSet.size}) (Ctrl+G)`, fn: groupSelected }] : [])]);
  };
  const m = $('#mini');
  m.onpointerdown = (e) => {
    m.setPointerCapture(e.pointerId);
    const mv = (ev) => {
      const rc = m.getBoundingClientRect();
      const wx = mini.x0 + (ev.clientX - rc.left) / mini.s, wy = mini.y0 + (ev.clientY - rc.top) / mini.s;
      const r = canvasRect();
      S.view.x = r.width / 2 - wx * S.view.k;
      S.view.y = r.height / 2 - wy * S.view.k;
      applyView();
    };
    mv(e);
    m.onpointermove = mv;
    m.onpointerup = () => { m.onpointermove = null; };
  };
  $('#z-map').onclick = (ev) => {
    const on = m.style.display === 'none';
    m.style.display = on ? 'block' : 'none';
    ev.currentTarget.classList.toggle('on', on);
    ev.currentTarget.setAttribute('aria-pressed', String(on));
    drawMini();
  };
  addEventListener('keydown', (e) => {
    const tag = (e.target.tagName || '').toLowerCase();
    if (tag === 'input' || tag === 'textarea' || tag === 'select' || e.target.isContentEditable || e.target.closest?.('.cm-editor,[contenteditable],[role="textbox"]')) return;
    const ctrl = e.ctrlKey || e.metaKey;
    if (e.key === 'Escape') $('#menu').style.display = 'none';
    // a key an inner control (knob, curve handle) already handled, or any key while a modal dialog is open, is not the canvas's
    // (undo and redo stay available inside the studio panel: a knob turn there is an edit of the song)
    const openDialog = document.querySelector('dialog[open]');
    if (e.defaultPrevented || (openDialog && !(openDialog.id === 'studio' && ctrl && (e.key.toLowerCase() === 'z' || e.key.toLowerCase() === 'y')))) return;
    if ((e.key === 'Delete' || e.key === 'Backspace') && (e.target === document.body || e.target === document.documentElement || e.target.closest?.('#cv'))) {
      if (S.selLink) { const l = current().links.find((x) => linkId(x) === S.selLink); if (l) { deleteLink(l); toast('Link removed. Press Ctrl+Z to undo.'); } }
      else if (!readOnly() && S.selSet.size) { const count = S.selSet.size; Promise.resolve(deleteSelected(ctrl)).then((done) => done !== false && toast(`Removed ${count} item${count === 1 ? '' : 's'}. Press Ctrl+Z to undo.`)); }
    }
    if (e.key === 'f' && !ctrl) fit(true);
    if (ctrl && e.key.toLowerCase() === 'g') { e.preventDefault(); const n = S.sel && nodeById(S.sel); if (e.shiftKey && n && n.isGroup) ungroupNode(n); else groupSelected(); }
    if (ctrl && e.key.toLowerCase() === 'a') { e.preventDefault(); selectAll(); }
    if (ctrl && e.key.toLowerCase() === 'z') { e.preventDefault(); if (e.shiftKey) redo(); else undo(); }
    if (ctrl && e.key.toLowerCase() === 'y') { e.preventDefault(); redo(); }
    if (e.altKey && e.key === 'ArrowUp') up();
    if (!e.altKey && !ctrl && e.key.startsWith('Arrow') && S.selSet.size && !readOnly()) {
      e.preventDefault();
      const step = e.shiftKey ? 50 : 10;
      const dx = e.key === 'ArrowLeft' ? -step : e.key === 'ArrowRight' ? step : 0;
      const dy = e.key === 'ArrowUp' ? -step : e.key === 'ArrowDown' ? step : 0;
      const moving = current().nodes.filter((n) => S.selSet.has(n.id) && !n.proxy && !S.pins.has(n.id));
      moving.filter((n) => n.isGroup).forEach((n) => { S.groups[n.id].x += dx; S.groups[n.id].y += dy; });
      if (moving.some((n) => n.isGroup)) saveGroups();
      const ops = moving.filter((n) => !n.isGroup).map((n) => ({ type: 'setLayout', block: n.id, x: n.x + dx, y: n.y + dy }));
      if (ops.length) edit(ops); else notify();
    }
    if (e.key === 'Enter' && e.target.classList && e.target.classList.contains('node')) {
      const n = nodeById(e.target.dataset.id);
      if (n && n.isGroup) enter(n); else if (n && !n.proxy) gotoSource(n);
    }
  });
  addEventListener('resize', applyView);
}

export { container };
