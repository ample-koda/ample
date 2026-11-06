// The linked llama avatar: only the Delay Llama blocks have it. A small llama button on the block calls a floating llama (as big as Koda) that belongs to THAT block: it sings
// when its block sings (the notes of its clip at the playhead of the song, with the vowel and the mouth of its curves), and between the notes it grooves on what plays, like Koda.
// Drag it where you want it, double-click or double-tap to put it away again. Where it stands and whether it is out is remembered per block in this browser only. @ample:delay-llama

import { auraOf, createLlama, SCHEMES } from './llama-art.js';
import { S, flat, saveViewSoon } from './store.js';
import { sizes } from './geometry.js';
import { showMenu } from './ui.js';
import { playerState } from './player.js';
import { outputLevels } from './playback-volume.js';
import { absolute, parse } from '../lib/notes-model.mjs';
import { barBeats } from '../lib/meter.mjs';

const clean = (v) => String(v ?? '').replace(/^"|"$/g, '').trim();
const SIZE = () => (innerWidth <= 700 ? { width: 110, height: 112 } : { width: 150, height: 152 });

// The size of a llama (0.5 to 1.25 in steps of 0.05, mostly meant to make it smaller): kept in the SONG, in its view block as `size "llama:<block>" <scale>`, 1 everywhere by default
const SCALE_MIN = 0.5, SCALE_MAX = 1.25;
const snapScale = (v) => Math.min(SCALE_MAX, Math.max(SCALE_MIN, Math.round(v * 20) / 20));
const sizeKey = (block) => `llama:${block}`;
const scaleOf = (block) => snapScale(Number(sizes[sizeKey(block)]) || 1);
function applyScale(host, block) { const k = scaleOf(block); host.style.transformOrigin = '0 0'; host.style.transform = k === 1 ? '' : `scale(${k})`; }

/** Four corners scale the llama in steps of 0.05; the opposite corner stays where it is. The size goes into the song (a step that can be undone). */
function startLlamaResize(block) {
  const avatar = open.get(block);
  if (!avatar || avatar.resizing) return;
  const host = avatar.host, base = SIZE();
  const box = document.createElement('div');
  box.className = 'llama-resize';
  const label = document.createElement('span'); label.className = 'llama-resize-label';
  const frame = () => { const r = host.getBoundingClientRect(); Object.assign(box.style, { left: `${r.left}px`, top: `${r.top}px`, width: `${r.width}px`, height: `${r.height}px` }); label.textContent = `${scaleOf(block).toFixed(2)}×`; };
  const change = (next, anchor, corner) => {
    if (next === 1) delete sizes[sizeKey(block)]; else sizes[sizeKey(block)] = next;
    applyScale(host, block);
    if (anchor) { const x = corner.includes('l') ? anchor.x - base.width * next : anchor.x, y = corner.includes('t') ? anchor.y - base.height * next : anchor.y; host.style.left = `${x}px`; host.style.top = `${y}px`; avatar.pos = { x, y }; write(block, { ...read(block), x, y }); }
    saveViewSoon(); frame();
  };
  const finish = () => { box.remove(); avatar.resizing = false; document.removeEventListener('keydown', onKey, true); window.removeEventListener('resize', frame); };
  const onKey = (e) => { if (e.key === 'Escape' || e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); finish(); } };
  const begin = (e, corner) => {
    e.preventDefault(); e.stopPropagation();
    const r = host.getBoundingClientRect(), from = scaleOf(block);
    const anchor = { x: corner.includes('l') ? r.right : r.left, y: corner.includes('t') ? r.bottom : r.top };
    const d0 = Math.max(8, Math.hypot(e.clientX - anchor.x, e.clientY - anchor.y)), handle = e.currentTarget;
    handle.setPointerCapture?.(e.pointerId);
    const move = (ev) => { const next = snapScale(from * Math.hypot(ev.clientX - anchor.x, ev.clientY - anchor.y) / d0); if (next !== scaleOf(block)) change(next, anchor, corner); };
    const up = () => { handle.removeEventListener('pointermove', move); handle.removeEventListener('pointerup', up); handle.removeEventListener('pointercancel', up); };
    handle.addEventListener('pointermove', move); handle.addEventListener('pointerup', up); handle.addEventListener('pointercancel', up);
  };
  for (const corner of ['tl', 'tr', 'bl', 'br']) { const h = document.createElement('div'); h.className = `llama-resize-handle ${corner}`; h.dataset.corner = corner; h.addEventListener('pointerdown', (e) => begin(e, corner)); box.append(h); }
  const reset = document.createElement('button'); reset.type = 'button'; reset.className = 'llama-resize-btn reset'; reset.textContent = 'Reset'; reset.onclick = () => change(1, null, '');
  const done = document.createElement('button'); done.type = 'button'; done.className = 'llama-resize-btn done'; done.textContent = 'Done'; done.onclick = finish;
  box.append(label, reset, done);
  document.body.append(box); avatar.resizing = true;
  document.addEventListener('keydown', onKey, true); window.addEventListener('resize', frame);
  frame();
}
const open = new Map();   // block id -> { host, llama, drag }
const key = (block) => `ample.llama.avatar:${S.project?.id || ''}:${block}`;
const read = (block) => { try { return JSON.parse(localStorage.getItem(key(block)) || '{}'); } catch { return {}; } };
const write = (block, value) => { try { localStorage.setItem(key(block), JSON.stringify(value)); } catch { /* storage can be blocked */ } };
let frame = 0, level = 0, average = 0, pulse = 0;

// the lasso: a neon rope from the llama to its block (marching dashes, a loop on the block), so it is clear which block this llama belongs to; it follows the llama and the canvas
const NS = 'http://www.w3.org/2000/svg';
let overlay = null;
// An attribute is only written when its text changed: a write of the same text still costs a style recalculation.
const put = (el, name, value) => { const text = String(value); const key = '_' + name; if (el[key] !== text) { el[key] = text; el.setAttribute(name, text); } };
function ensureOverlay() {
  if (overlay?.isConnected) return overlay;
  overlay = document.createElementNS(NS, 'svg');
  overlay.setAttribute('class', 'llama-lasso');
  overlay.setAttribute('aria-hidden', 'true');
  overlay.innerHTML = '<defs><linearGradient id="llama-lasso-gradient" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#ff4fd8"/><stop offset="1" stop-color="#2de2ff"/></linearGradient></defs>';
  document.body.append(overlay);
  return overlay;
}

// The block of an avatar sits where the canvas put it: its place is read when the page moved (a pointer, a wheel, a scroll, a resize) and now and then, never once per frame
// (a read of a place after a write of the page is a forced layout, and that was 120 of them per second for one llama).
let moved = true, readAt = 0, lastTick = 0;
const touched = () => { moved = true; };
if (typeof window !== 'undefined') for (const type of ['pointermove', 'wheel', 'scroll', 'resize', 'touchmove']) window.addEventListener(type, touched, { passive: true, capture: true });
const FRAME_MS = () => (innerWidth <= 700 ? 50 : 33);   // 20 pictures a second on a phone, 30 elsewhere; at rest a third of that
function blockBox(block) {
  const box = document.querySelector('.node[data-id="' + block.replace(/"/g, '') + '"]')?.getBoundingClientRect();
  if (!box || box.width < 4 || box.bottom < 0 || box.top > innerHeight || box.right < 0 || box.left > innerWidth) return null;
  return { left: box.left, top: box.top, right: box.right, bottom: box.bottom };
}

function drawLasso(avatar, block, singing, t) {
  const box = avatar.box;
  if (!box) { if (avatar.rope) { put(avatar.rope, 'visibility', 'hidden'); put(avatar.glow, 'visibility', 'hidden'); put(avatar.loop, 'visibility', 'hidden'); } return; }
  const s = SIZE(), a = { x: avatar.pos.x + s.width / 2, y: avatar.pos.y + s.height * 0.3 };
  // where the rope meets the block: the nearest point of its border (the middle of its top when the llama is above it)
  const inside = a.x > box.left && a.x < box.right && a.y > box.top && a.y < box.bottom;
  const b = inside ? { x: box.left + (box.right - box.left) / 2, y: box.top } : { x: Math.min(box.right, Math.max(box.left, a.x)), y: Math.min(box.bottom, Math.max(box.top, a.y)) };
  const sag = Math.min(70, 18 + Math.hypot(b.x - a.x, b.y - a.y) * 0.18) * (1 + Math.sin(t * 1.3) * 0.08);
  const d = 'M' + a.x.toFixed(1) + ' ' + a.y.toFixed(1) + ' C' + (a.x + (b.x - a.x) * 0.3).toFixed(1) + ' ' + (a.y + (b.y - a.y) * 0.1 + sag).toFixed(1) + ' ' + (a.x + (b.x - a.x) * 0.7).toFixed(1) + ' ' + (a.y + (b.y - a.y) * 0.9 + sag).toFixed(1) + ' ' + b.x.toFixed(1) + ' ' + b.y.toFixed(1);
  if (!avatar.rope) {
    const svg = ensureOverlay();
    avatar.glow = document.createElementNS(NS, 'path'); avatar.glow.setAttribute('fill', 'none'); avatar.glow.setAttribute('stroke-linecap', 'round');   // the light round the rope: a wider, fainter line (no filter: a filter is repainted with every frame of the dashes)
    avatar.rope = document.createElementNS(NS, 'path'); avatar.rope.setAttribute('fill', 'none'); avatar.rope.setAttribute('stroke-linecap', 'round'); avatar.rope.setAttribute('stroke-dasharray', '9 7');
    avatar.loop = document.createElementNS(NS, 'ellipse'); avatar.loop.setAttribute('fill', 'none');
    const colours = SCHEMES[avatar.scheme] || SCHEMES.trans;
    avatar.gradient = document.createElementNS(NS, 'linearGradient'); avatar.gradient.setAttribute('id', 'llama-lasso-' + block.replace(/[^a-z0-9]/gi, '-')); avatar.gradient.setAttribute('gradientUnits', 'userSpaceOnUse');
    avatar.gradient.innerHTML = '<stop offset="0" stop-color="' + colours.neonA + '"/><stop offset="1" stop-color="' + colours.neonB + '"/>';
    svg.querySelector('defs').append(avatar.gradient);
    avatar.colour = colours.neonB;
    svg.append(avatar.glow, avatar.rope, avatar.loop);
  }
  // the gradient follows the rope (from the llama to the block), the dashes march towards the block, brighter while it sings
  const stroke = 'url(#' + avatar.gradient.id + ')';
  put(avatar.gradient, 'x1', a.x.toFixed(1)); put(avatar.gradient, 'y1', a.y.toFixed(1)); put(avatar.gradient, 'x2', b.x.toFixed(1)); put(avatar.gradient, 'y2', b.y.toFixed(1));
  for (const part of [avatar.rope, avatar.glow, avatar.loop]) put(part, 'visibility', 'visible');
  put(avatar.rope, 'd', d); put(avatar.glow, 'd', d);
  put(avatar.rope, 'stroke', stroke); put(avatar.glow, 'stroke', stroke);
  put(avatar.rope, 'stroke-width', singing ? '3.4' : '2.4'); put(avatar.glow, 'stroke-width', singing ? '11' : '7');
  put(avatar.rope, 'stroke-dashoffset', (-(t * (singing ? 60 : 22)) % 16).toFixed(1));
  put(avatar.rope, 'opacity', singing ? '1' : '0.75'); put(avatar.glow, 'opacity', singing ? '0.28' : '0.16');
  // the loop of the lasso on the block: a small ring that pulses with the beat
  put(avatar.loop, 'cx', b.x.toFixed(1)); put(avatar.loop, 'cy', b.y.toFixed(1));
  put(avatar.loop, 'rx', (9 + pulse * 5).toFixed(1)); put(avatar.loop, 'ry', (6 + pulse * 3).toFixed(1));
  put(avatar.loop, 'stroke', avatar.colour); put(avatar.loop, 'stroke-width', '2.4'); put(avatar.loop, 'opacity', singing ? '1' : '0.8');
}

// a curve text auto(bar:percent, ...) as a function of the bar (0..1); a plain number or letter stays what it is
function curveOf(text, fallback, letters = false) {
  const t = clean(text);
  const m = /^auto\((.*)\)$/s.exec(t);
  if (m) {
    const points = m[1].split(',').map((p) => p.trim().split(':')).map(([at, value]) => ({ at: Number(at), value: Math.min(100, Math.max(0, Number.parseInt(value, 10))) / 100 })).filter((p) => Number.isFinite(p.at) && Number.isFinite(p.value)).sort((a, b) => a.at - b.at);
    if (points.length) return (bar) => {
      if (bar <= points[0].at) return points[0].value;
      for (let i = 1; i < points.length; i++) if (bar <= points[i].at) return points[i - 1].value + (points[i].value - points[i - 1].value) * (bar - points[i - 1].at) / Math.max(1e-9, points[i].at - points[i - 1].at);
      return points.at(-1).value;
    };
  }
  if (letters && /^[aeiou]$/i.test(t)) { const v = 'aeiou'.indexOf(t.toLowerCase()) / 4; return () => v; }
  const v = Number.parseFloat(t);
  return () => (Number.isFinite(v) ? Math.min(1, Math.max(0, v / 100)) : fallback);
}

// what the block sings at a bar position: from its clip (the notes that reach it) and its own curves. It is cached per version of the song, so a frame does not flatten
// the graph and compare the whole text of the clip (it did, 60 times a second per llama); when the version moved the text is compared and the notes are kept when they are the same.
const cache = new Map();
const versionOfSong = () => `${S.path}|${S.doc?.version}|${S.moduleDoc?.version ?? ''}`;
export function singerOf(block) {
  const version = versionOfSong();
  const hit = cache.get(block);
  if (hit?.version === version) return hit;
  const graph = flat(), node = graph.nodes.find((n) => n.id === block);
  if (!node) return null;
  const link = graph.links.find((l) => l.to === block && l.type === 'midi' && !l.disabled), clip = link && graph.nodes.find((n) => n.id === link.from);
  const notesText = clean(clip?.params?.notes), ppq = Number(clip?.params?.ppq) > 0 ? Number(clip.params.ppq) : 1_000_000;
  const signature = JSON.stringify([notesText, ppq, node.params.vowel, node.params.mouth, node.params.vibrato]);
  if (hit?.signature === signature) { hit.version = version; return hit; }
  const notes = (notesText ? absolute(parse(notesText, ppq)) : []).sort((x, y) => x.at - y.at);
  const made = { version, signature, notes, ppq, longest: notes.reduce((m, n) => Math.max(m, n.length), 0), vowel: curveOf(node.params.vowel, 0.5, true), mouth: curveOf(node.params.mouth, 0.6), vibrato: (Number.parseFloat(clean(node.params.vibrato)) || 30) / 100 };
  cache.set(block, made);
  return made;
}

/** The note that sounds at a tick (the latest one that started and has not ended), found by a binary search in the sorted notes instead of a walk along all of them. */
function noteAt(singer, tick) {
  const notes = singer.notes;
  let lo = 0, hi = notes.length - 1, last = -1;
  while (lo <= hi) { const mid = (lo + hi) >> 1; if (notes[mid].at <= tick) { last = mid; lo = mid + 1; } else hi = mid - 1; }
  for (let i = last; i >= 0 && tick - notes[i].at <= singer.longest; i--) if (tick < notes[i].at + notes[i].length) return notes[i];
  return null;
}

function tick(now) {
  frame = 0;
  for (const [block, avatar] of [...open]) if (avatar.song !== S.path) hideLlamaAvatar(block, true);
  if (!open.size) return;
  const st = playerState();
  // at rest (nothing plays and no llama grooves) a third of the pictures is plenty
  const rest = !st.playing && [...open.values()].every((a) => a.groove < 0.02);
  if (now - lastTick < (rest ? 2.5 : 1) * FRAME_MS() - 2) { frame = requestAnimationFrame(tick); return; }
  lastTick = now;
  const tempo = Number(S.doc?.project?.tempo) || 120;
  // all the reads first, then the writes
  if (moved || now - readAt > 500) { for (const [block, avatar] of open) avatar.box = blockBox(block); moved = false; readAt = now; }
  // the groove: the loudness of what leaves the speakers, and its jumps (a kick makes the llama bounce)
  const { left, right } = outputLevels(), now01 = Math.min(1, Math.max(left, right));
  level += (now01 - level) * 0.35;
  average += (level - average) * 0.04;
  pulse = Math.max(pulse * 0.88, Math.max(0, level - average) * 3);
  const firstBar = S.range?.firstBar || 1, bar = 1 + (st.time * tempo / (barBeats() * 60)) + (firstBar - 1);
  for (const [block, avatar] of open) {
    const singer = st.playing ? singerOf(block) : null;
    const singing = singer && !st.auditionBlock ? noteAt(singer, (bar - 1) * barBeats() * singer.ppq) : null;
    if (singing) avatar.llama.set({ pitch: singing.pitch, vowel: singer.vowel(bar), mouth: singer.mouth(bar), level: 0.35 + 0.65 * (singing.velocity / 127), vibrato: singer.vibrato });
    else avatar.llama.set({ pitch: null, level: 0 });
    // between the notes (and while it is not its turn) it grooves on the sound: a bounce on the beats and a slow sway; at rest only a very small sway
    const groove = st.playing && !singing ? 1 : 0;
    avatar.groove += (groove - avatar.groove) * 0.1;
    const t = now / 1000, bounce = avatar.groove * pulse * 9, sway = Math.sin(t * 2.4) * 2.6 * avatar.groove + Math.sin(t * 0.7) * 0.8;
    drawLasso(avatar, block, Boolean(singing), t);
    avatar.inner.style.transform = `translateY(${(-bounce).toFixed(2)}px) rotate(${sway.toFixed(2)}deg)`;
  }
  frame = requestAnimationFrame(tick);
}
const wake = () => { if (!frame && open.size) frame = requestAnimationFrame(tick); };

function clamp(x, y) { const s = SIZE(); return { x: Math.min(Math.max(8, x), Math.max(8, innerWidth - s.width - 8)), y: Math.min(Math.max(8, y), Math.max(8, innerHeight - s.height - 8)) }; }

// the nearest place to `spot` where this llama does not stand on one that is already out (a grid of steps around the wanted place, nearest first)
function freeSpot(spot) {
  const s = SIZE(), taken = [...open.values()].map((a) => ({ x: parseFloat(a.host.style.left), y: parseFloat(a.host.style.top) }));
  const hits = (c) => taken.some((t) => Math.abs(t.x - c.x) < s.width * 0.75 && Math.abs(t.y - c.y) < s.height * 0.75);
  if (!hits(spot)) return spot;
  const steps = [];
  for (let dx = -4; dx <= 4; dx++) for (let dy = -3; dy <= 3; dy++) steps.push({ dx, dy, d: Math.hypot(dx * 0.9, dy) });
  steps.sort((a, b) => a.d - b.d || a.dx - b.dx);
  for (const { dx, dy } of steps) {
    const c = clamp(spot.x + dx * s.width * 0.85, spot.y + dy * s.height * 0.85);
    if (!hits(c)) return c;
  }
  return spot;
}

/** Calls the llama of this block out (at its remembered place, or beside the middle of the screen). */
export function showLlamaAvatar(block, scheme = null) {
  if (open.has(block)) return;
  const saved = read(block), s = SIZE();
  const spot = freeSpot(clamp(saved.x ?? innerWidth - s.width - 40, saved.y ?? innerHeight - s.height - 60));
  const host = document.createElement('div');
  host.className = 'llama-avatar';
  host.dataset.block = block;
  host.title = `${block}: the Delay Llama of this block. Drag to move, double-click to put away`;
  host.style.cssText = `left:${spot.x}px;top:${spot.y}px;width:${s.width}px;height:${s.height}px`;
  applyScale(host, block);
  const inner = document.createElement('div');
  inner.className = 'llama-avatar-inner';
  const names = Object.keys(SCHEMES), chosen = scheme || (saved.open && SCHEMES[saved.scheme] ? saved.scheme : names[Math.floor(Math.random() * names.length)]);
  const llama = createLlama({ size: Math.round(s.height / 1.58), scheme: chosen, pose: (() => { try { return localStorage.getItem('ample.llama.pose') || 'sit'; } catch { return 'sit'; } })() });
  inner.append(llama.element);
  host.append(inner);
  document.body.append(host);
  host.style.background = auraOf(chosen);   // the light round the llama is a still picture behind it, not a filter on something that moves
  const avatar = { host, inner, llama, groove: 0, scheme: chosen, song: S.path, pos: { x: spot.x, y: spot.y }, box: null };
  open.set(block, avatar);
  write(block, { ...saved, open: true, x: spot.x, y: spot.y, scheme: chosen });
  // drag, and the double click or double tap that puts it away
  let drag = null, lastTap = 0;
  host.style.touchAction = 'none';
  host.addEventListener('pointerdown', (event) => {
    host.setPointerCapture(event.pointerId);
    const box = host.getBoundingClientRect();
    drag = { dx: event.clientX - box.left, dy: event.clientY - box.top, moved: false };
    const at = performance.now();
    if (at - lastTap < 380) { hideLlamaAvatar(block); return; }
    lastTap = at;
  });
  host.addEventListener('pointermove', (event) => {
    if (!drag) return;
    const p = clamp(event.clientX - drag.dx, event.clientY - drag.dy);
    drag.moved = true; host.classList.add('drag');
    host.style.left = `${p.x}px`; host.style.top = `${p.y}px`;
    avatar.pos = { x: p.x, y: p.y };
  });
  const end = () => { if (!drag) return; const moved = drag.moved; drag = null; host.classList.remove('drag'); if (moved) { write(block, { ...read(block), open: true, x: parseFloat(host.style.left), y: parseFloat(host.style.top) }); lastTap = 0; } };
  host.addEventListener('pointerup', end);
  host.addEventListener('pointercancel', end);
  host.addEventListener('contextmenu', (event) => { event.preventDefault(); event.stopPropagation(); showMenu(event.clientX, event.clientY, [{ head: `Llama of ${block}` }, { label: 'Resize…', fn: () => startLlamaResize(block) }, { label: 'Put away', fn: () => hideLlamaAvatar(block) }]); });
  document.dispatchEvent(new CustomEvent('ample:llama-avatar', { detail: { block, open: true } }));
  wake();
}

/** Puts the llama of this block away (it remembers where it stood). */
export function hideLlamaAvatar(block, keepRemembered = false) {
  const avatar = open.get(block);
  if (!avatar) return;
  avatar.llama.destroy(); avatar.host.remove(); avatar.rope?.remove(); avatar.loop?.remove(); open.delete(block);
  avatar.gradient?.remove();
  if (!keepRemembered) write(block, { ...read(block), open: false, scheme: null });
  document.dispatchEvent(new CustomEvent('ample:llama-avatar', { detail: { block, open: false } }));
}

export const llamaAvatarOpen = (block) => open.has(block);

/** The small llama button of a Delay Llama block (a toggle). */
export function llamaAvatarButton(block) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'studio-llama-node';
  button.textContent = '🦙';
  button.setAttribute('aria-label', 'Linked llama');
  const paint = () => { const on = open.has(block); button.setAttribute('aria-pressed', String(on)); button.title = on ? 'Put the linked llama away (or double-click it)' : 'Call the linked llama of this block: it sings along and grooves on the music'; };
  paint();
  button.onpointerdown = (event) => event.stopPropagation();
  button.onclick = (event) => { event.stopPropagation(); if (open.has(block)) hideLlamaAvatar(block); else showLlamaAvatar(block); paint(); };
  document.addEventListener('ample:llama-avatar', (event) => { if (event.detail.block === block && button.isConnected) paint(); });
  // a llama that was out the last time comes back with its block
  if (!open.has(block) && read(block).open) setTimeout(() => { if (!open.has(block)) { showLlamaAvatar(block); paint(); } }, 0);
  return button;
}
