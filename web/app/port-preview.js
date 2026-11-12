// Listening to one port of a block: a small player that hangs by the port like a tooltip. For a region (the joins of a loop) it loops one join, alone or with some of the sound around it.
import { getContext } from '../lib/audio-engine.mjs';
import { getToken } from './api.js';
import { S, build, edit } from './store.js';
import { el, toast } from './ui.js';
import { seamWindows, sumChannels } from '../lib/seam-windows.mjs';
import { regionChain } from '../lib/region-chain.mjs';

let context = null, node = null, panel = null, onKey = null, onDown = null, openFor = '', followFrame = 0;
const audioContext = () => (context ||= getContext());

async function takeOf(block) {
  let id = S.build.takes[block];
  if (!id) {
    const report = await build(block, null, 'preview');
    const item = (report?.blocks || []).find((b) => (b.block ?? b.Block) === block);
    id = item?.takeId ?? item?.TakeId ?? S.build.takes[block];
  }
  if (!id) throw new Error(`There is no audio for “${block}” yet: build it first.`);
  return id;
}

/** One output of a block as channels of floats; the main output of a take has no role of its own. */
const outputs = new Map();   // the last few outputs that were read, by take and role: opening a player again does not download the sound again
async function fetchOutput(block, role) {
  const take = await takeOf(block);
  const key = `${take}:${role || ''}`;
  if (!outputs.has(key)) {
    outputs.set(key, readOutput(take, block, role).catch((error) => { outputs.delete(key); throw error; }));
    while (outputs.size > 3) outputs.delete(outputs.keys().next().value);
  }
  return outputs.get(key);
}

async function readOutput(take, block, role) {
  const named = role && !['audio', 'region'].includes(role) ? `&role=${encodeURIComponent(role)}` : '';
  const response = await fetch(`/api/takes/${encodeURIComponent(take)}/download?format=wav${named}`, { headers: { 'X-Ample-Token': getToken() } });
  if (!response.ok) throw new Error(`Could not read ${role || 'the audio'} of “${block}” (${response.status}).`);
  const decoded = await audioContext().decodeAudioData(await response.arrayBuffer());
  return { rate: decoded.sampleRate, channels: Array.from({ length: decoded.numberOfChannels }, (_, c) => decoded.getChannelData(c).slice()) };
}

export function closePortPreview() {
  try { node?.stop(); } catch { /* already stopped */ }
  node = null;
  cancelAnimationFrame(followFrame);
  panel?.remove();
  panel = null;
  if (onKey) removeEventListener('keydown', onKey, true);
  onKey = null;
  if (onDown) document.removeEventListener('pointerdown', onDown, true);
  onDown = null;
  openFor = '';
}

/** anchor: the button by the port; what: { block, role, type, title, dir }. */
export async function openPortPreview(anchor, what) {
  const key = `${what.block}:${what.role}`;
  const again = panel && openFor === key;   // the same port once more: it closes
  closePortPreview();
  if (again) return;
  openFor = key;
  const status = el('div', { className: 'pp-status', textContent: 'Loading…' });
  const choices = el('div', { className: 'pp-choices', hidden: true });
  const nav = el('div', { className: 'pp-nav', hidden: true });
  const play = el('button', { type: 'button', className: 'go', textContent: '▶ Play', disabled: true });
  const canvas = el('canvas', { className: 'pp-wave', width: 460, height: 80 });
  const head = el('i', { className: 'pp-playhead', hidden: true });
  const wavebox = el('div', { className: 'pp-wavebox' }, [canvas, head]);
  panel = el('div', { className: 'port-preview', role: 'dialog', 'aria-label': `Listen to ${what.title}` }, [
    el('div', { className: 'pp-head' }, [el('b', { textContent: what.title }), el('span', { className: 'pp-sp' }), el('button', { type: 'button', className: 'pp-x', textContent: '×', 'aria-label': 'Close', onclick: closePortPreview })]),
    choices, nav, wavebox, el('div', { className: 'pp-row' }, [play, status]),
  ]);
  document.body.append(panel);
  // the player hangs by its port like a block: it moves with the canvas when that is dragged or zoomed, hides when the port is scrolled out of sight and comes back with it
  const mine = panel;
  let anchorEl = anchor;
  const place = () => {
    if (panel !== mine) return;
    if (!anchorEl.isConnected && what.selector) anchorEl = document.querySelector(what.selector) || anchorEl;
    const r = anchorEl.getBoundingClientRect(), v = document.getElementById('cv')?.getBoundingClientRect() ?? { left: 0, top: 0, right: innerWidth, bottom: innerHeight };
    const inside = anchorEl.isConnected && r.right > v.left && r.left < v.right && r.bottom > v.top && r.top < v.bottom;
    panel.style.visibility = inside ? '' : 'hidden';
    if (inside) {
      // it grows with the canvas when that is zoomed in (up to 2.5 times) and never gets smaller than it is
      const zoom = anchorEl.offsetWidth ? r.width / anchorEl.offsetWidth : 1;
      const k = Math.max(1, Math.min(2.5, zoom));
      const w = panel.offsetWidth * k, h = panel.offsetHeight * k;
      panel.style.transformOrigin = '0 0';
      panel.style.transform = k === 1 ? '' : `scale(${k})`;
      panel.style.top = `${Math.max(8, Math.min(r.top - 10 * k, innerHeight - h - 8))}px`;
      panel.style.left = `${r.right + 10 + w > innerWidth ? Math.max(8, r.left - w - 10) : r.right + 10}px`;
    }
    followFrame = requestAnimationFrame(place);
  };
  place();
  onKey = (e) => { if (e.key === 'Escape') { e.stopPropagation(); closePortPreview(); } };
  addEventListener('keydown', onKey, true);
  // a press beside the player closes it (a press on a playable port is that port's own click: it opens or closes its player)
  onDown = (e) => { if (panel && !panel.contains(e.target) && !e.target.closest?.('.port.playable')) closePortPreview(); };
  document.addEventListener('pointerdown', onDown, true);
  try {
    const isRegion = what.type === 'region';
    // the sound around a join: the body of the loop that gives the region, plus this region; both are read at the same time
    const chain = isRegion ? regionChain(S.doc?.blocks, what.block, what.dir === 'out' && !!(S.doc?.blocks || []).find((b) => b.name === what.block)?.froms?.some((f) => f.input === 'region_in')) : { source: null };
    const bodyRead = chain.source ? fetchOutput(chain.source, 'body').catch(() => null) : Promise.resolve(null);
    const own = await fetchOutput(what.block, what.role);
    if (panel !== mine) return;
    let windows = [], around = null;
    if (isRegion) {
      windows = seamWindows(own.channels, own.rate);
      const body = await bodyRead;
      if (body) around = { rate: body.rate, channels: sumChannels(body.channels, own.channels) };
      if (panel !== mine) return;
    }
    let mode = around ? 'around' : 'only', at = 0;
    const slice = () => {
      if (!isRegion) return { src: own, from: 0, to: Math.min(own.channels[0].length, Math.round(own.rate * 20)) };
      const w = windows[at] || { from: 0, to: own.channels[0].length };
      if (mode === 'only' || !around) return { src: own, from: w.from, to: w.to };
      const pad = Math.round(around.rate * 1.5);
      return { src: around, from: Math.max(0, w.from - pad), to: Math.min(around.channels[0].length, w.to + pad) };
    };
    // the waveform of what is looped (the join lit when it sits in the sound around it) and a playhead that runs along it
    const draw = () => {
      const { src, from, to } = slice();
      const g = canvas.getContext('2d');
      g.clearRect(0, 0, canvas.width, canvas.height);
      const n = to - from, w = canvas.width, h = canvas.height, style = getComputedStyle(document.documentElement);
      if (n <= 0) return;
      const lit = isRegion && mode === 'around' && around && windows[at] ? [(windows[at].from - from) / n * w, (windows[at].to - from) / n * w] : null;
      for (let x = 0; x < w; x++) {
        let lo = 0, hi = 0;
        const a = from + Math.floor(x / w * n), b = Math.max(a + 1, from + Math.floor((x + 1) / w * n));
        for (const c of src.channels) for (let i = a; i < b; i += Math.max(1, (b - a) >> 4)) { const v = c[i] || 0; if (v < lo) lo = v; if (v > hi) hi = v; }
        g.fillStyle = (lit && x >= lit[0] && x < lit[1] ? style.getPropertyValue('--warn') : style.getPropertyValue('--acc')).trim() || '#4fd';
        g.fillRect(x, h / 2 - hi * (h / 2), 1, Math.max(1, (hi - lo) * (h / 2)));
      }
    };
    let startedAt = 0, length = 1, frame = 0;
    const tick = () => { if (!node) return; head.style.left = `${(((audioContext().currentTime - startedAt) % length) / length) * 100}%`; frame = requestAnimationFrame(tick); };
    const stop = () => { try { node?.stop(); } catch { /* already stopped */ } node = null; cancelAnimationFrame(frame); head.hidden = true; play.textContent = '▶ Play'; };
    const start = () => {
      stop();
      const { src, from, to } = slice();
      if (to <= from) { status.textContent = 'There is nothing to hear here.'; return; }
      const ctx = audioContext();
      const buffer = ctx.createBuffer(src.channels.length, to - from, src.rate);
      src.channels.forEach((c, i) => buffer.copyToChannel(c.subarray(from, to), i));
      node = ctx.createBufferSource();
      node.buffer = buffer; node.loop = true; node.connect(ctx.destination); node.start();
      startedAt = ctx.currentTime; length = buffer.duration; head.hidden = false; frame = requestAnimationFrame(tick);
      play.textContent = '■ Stop';
    };
    play.onclick = () => (node ? stop() : start());
    play.disabled = false;
    if (isRegion) {
      if (!windows.length) {
        play.disabled = true;
        const block = (S.doc?.blocks || []).find((x) => x.name === what.block);
        const looping = block?.params?.loop === 'on' || (block?.parameters || []).some((x) => x.name === 'loop' && x.value === 'on');
        if (looping || !block) {
          // a loop without a crossfade joins with a hard cut: there is nothing to blend, so the seam is empty
          status.textContent = 'This loop has no crossfade, so the join is a hard cut and the seam is empty.';
          panel.append(el('button', { type: 'button', className: 'go', textContent: 'Add a 3 s crossfade', onclick: async () => { try { await edit([{ type: 'setParameter', block: what.block, name: 'crossfade', value: '3' }]); status.textContent = 'Crossfade set: build the block again to hear the seam.'; } catch (error) { toast(error.message || String(error)); } } }));
        } else status.textContent = 'This block does not loop, so it has no seam.';
        return;
      }
      choices.hidden = false;
      // two choices side by side: only the join itself, or the join with the sound around it
      choices.className = 'pp-choices pp-seg';
      const buttons = [];
      const seg = (value, text, off) => { const b = el('button', { type: 'button', textContent: text, disabled: off, title: off ? 'The body of this loop could not be read' : '', onclick: () => { mode = value; paint(); draw(); if (node) start(); } }); b.dataset.mode = value; buttons.push(b); return b; };
      const paint = () => buttons.forEach((b) => { b.classList.toggle('on', b.dataset.mode === mode); b.setAttribute('aria-pressed', String(b.dataset.mode === mode)); });
      choices.append(seg('only', 'Only the loop', false), seg('around', 'With the sound around it', !around));
      paint();
      nav.hidden = windows.length < 2;
      const label = el('span', { textContent: '' });
      const go = (d) => { at = (at + d + windows.length) % windows.length; label.textContent = `Join ${at + 1} of ${windows.length}`; draw(); if (node) start(); };
      nav.append(el('button', { type: 'button', textContent: '‹', 'aria-label': 'Previous join', onclick: () => go(-1) }), label, el('button', { type: 'button', textContent: '›', 'aria-label': 'Next join', onclick: () => go(1) }));
      go(0);
      status.textContent = `${windows.length} join${windows.length === 1 ? '' : 's'}, ${((windows[0].to - windows[0].from) / own.rate).toFixed(1)} s each`;
    } else { status.textContent = 'The first 20 seconds, repeated.'; draw(); }
  } catch (error) {
    if (panel === mine) { status.textContent = error.message || String(error); toast(status.textContent); }
  }
}
