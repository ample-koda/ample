// The arrangement: lanes generated from the blocks, bars from their parts, a transport with the waveform of the
// built song, and preview of a bar range. @ample:arrangement @ample:preview-playback

import { $, activatable, ask, el, showMenu, toast } from './ui.js';
import { api } from './api.js';
import { S, build, edit, flat, notify, readOnly, select } from './store.js';
import { drawSections, sectionMenu } from './view-sections.js';
import { sectionAt } from '../lib/view-sections.mjs';
import { sectionsOf } from './store.js';
import { seamSpans, seamText } from '../lib/seam-spans.mjs';
import { drawControl } from './control-lane.js';
import { download, drawPeaks, onPlayer, peaksOf, play, playerState, seekFraction, setLoop, stop, toggle } from './player.js';
import { focusNode } from './canvas.js';
import { gotoSource } from './panels.js';
import { openRecorder } from './recorder.js';
import { openSoundCheck } from './soundcheck.js';
import { openTranscription, renderTranscriptionPanel } from './transcription.js';
import { barBeats } from '../lib/meter.mjs';

let bw = 32;
let partDrag = null;
const openTracks = new Set();
let labelsObserver = null;

const songBars = (nodes) => Math.max(16, ...nodes.flatMap((n) => n.parts.map((p) => p.at + p.length - 1)));
const masterTake = () => {
  const t = S.build.takes || {};
  if (t.master) return t.master;
  const last = flat().nodes.filter((n) => t[n.id]).pop();
  return last ? t[last.id] : null;
};

export const writeParts = (node, parts, trimBars) => edit([
  { type: 'setParts', block: node.id, parts: parts.map((p) => ({ at: p.at, length: p.length, name: p.name || '' })) },
  ...(trimBars ? [{ type: 'setParameter', block: node.id, name: 'bars', value: String(Math.max(...parts.map((p) => p.length))) }] : []),
]);

export async function commitParts(node, parts, originalLength, nextLength) {
  const configuredBars = Number(node.params.bars);
  let trimBars = false;
  if (nextLength < originalLength && Number.isFinite(configuredBars) && configuredBars > nextLength) {
    const choice = await ask('Trim configured block scope?', `The clip is now ${nextLength} bars while ${node.title} is configured for ${configuredBars} bars.`, [
      { label: 'Cancel resize', value: 'cancel' }, { label: 'Keep block scope', value: 'keep' }, { label: 'Trim block scope', value: 'trim', primary: true },
    ]);
    if (choice === 'cancel' || choice === undefined) return;
    trimBars = choice === 'trim';
  }
  await writeParts(node, parts, trimBars);
}

async function editPartTiming(node, parts, index) {
  const part = parts[index];
  const start = el('input', { type: 'number', min: 1, max: 256, value: part.at, 'aria-label': 'Start bar' });
  const length = el('input', { type: 'number', min: 1, max: 256, value: part.length, 'aria-label': 'Length in bars' });
  const result = await ask('Clip timing', `${node.title} · enter the exact bar position and length.`, [
    { label: 'Cancel', value: false }, { label: 'Apply timing', value: true, primary: true },
  ], [el('label', {}, [document.createTextNode('Start bar'), start]), el('label', {}, [document.createTextNode('Length in bars'), length])]);
  if (!result) return;
  const at = Number(start.value), bars = Number(length.value);
  if (!Number.isInteger(at) || !Number.isInteger(bars) || at < 1 || bars < 1 || at + bars > 257) { toast('Enter a valid range ending by bar 256.'); return; }
  const next = parts.map((p, i) => i === index ? { ...p, at, length: bars } : p);
  await commitParts(node, next, part.length, bars);
}

export async function previewSong(range) {
  const r = await build(null, range || null, range ? 'preview' : undefined);
  if (!r) return;
  const take = r.masterTakeId ?? r.MasterTakeId;
  if (!take) { toast('There is nothing to play yet.'); return; }
  try { await play(take); } catch (e) { if (!e.toasted) toast(e.message); }
}

export function renderArrangement() {
  const box = $('#b-arr');
  if (!box) return;
  const scrollBars = ($('#arr')?.scrollLeft || 0) / Number($('#arr')?.dataset.barWidth || bw);
  const scrollTop = $('#arr')?.scrollTop || 0;
  labelsObserver?.disconnect();
  box.innerHTML = '';
  const nodes = flat().nodes;
  const tempo = S.doc && S.doc.project ? S.doc.project.tempo : 120;
  const ctl = el('div', { className: 'row', id: 'arr-ctl' });
  ctl.append(el('button', { type: 'button', className: 'arr-info', textContent: 'i', title: 'The arrangement shows the same project as bars on a timeline, with seconds beneath. It is generated from the blocks; the source text remains authoritative. Drag over the ruler to select bars, then preview that range.', 'aria-label': 'About the arrangement' }));
  ctl.append(el('button', { textContent: '−', title: 'Narrower bars', 'aria-label': 'Narrower bars', onclick: () => { bw = Math.max(16, bw - 8); renderArrangement(); } }), el('span', { id: 'arr-bw', textContent: `${bw} px/bar` }), el('button', { textContent: '+', title: 'Wider bars', 'aria-label': 'Wider bars', onclick: () => { bw = Math.min(96, bw + 8); renderArrangement(); } }));
  if (S.range) ctl.append(el('button', { id: 'arr-preview', className: 'primary', textContent: `▶ preview bars ${S.range.firstBar}-${S.range.firstBar + S.range.bars - 1}`, onclick: () => previewSong(S.range) }), el('button', { textContent: 'clear', onclick: () => { S.range = null; renderArrangement(); } }));
  // the master slice: the part of the song that comes out of the master (the last master block of the song)
  const master = nodes.filter((n) => n.tpl === 'master').at(-1);
  const sliceOf = () => { const p = master?.params || {}, from = Number(p.slice_from) || 0, to = Number(p.slice_to) || 0; return from || to ? { from: from || 1, to: to || null } : null; };
  const writeSlice = (from, to) => edit([from === null ? { type: 'removeParameter', block: master.id, name: 'slice_from' } : { type: 'setParameter', block: master.id, name: 'slice_from', value: String(from) }, to === null ? { type: 'removeParameter', block: master.id, name: 'slice_to' } : { type: 'setParameter', block: master.id, name: 'slice_to', value: String(to) }]).then(() => notify());
  if (master && !readOnly()) {
    if (S.range) ctl.append(el('button', { id: 'arr-slice-set', 'data-icon': '✂', 'aria-label': 'Master slice = these bars', textContent: '✂ Master slice = these bars', title: 'Only these bars come out of the master: the song is cut to this part (drag over the ruler to choose bars first)', onclick: () => writeSlice(S.range.firstBar, S.range.firstBar + S.range.bars - 1) }));
    if (sliceOf()) ctl.append(el('button', { id: 'arr-slice-clear', 'data-icon': '✕', 'aria-label': 'Clear slice', textContent: '✕ Clear slice', title: 'The whole song comes out of the master again', onclick: () => writeSlice(null, null) }));
  }
  ctl.append(el('button', { 'data-icon': '✓', 'aria-label': 'Sound check', textContent: 'Sound check', title: 'What is wrong with the sound of the built song: silent, very quiet, clipping or unheard blocks, with a fix', onclick: () => openSoundCheck().catch((e) => toast(e.message)) }));
  ctl.append(el('button', { 'data-icon': '●', 'aria-label': 'Record', textContent: '● Record...', title: 'Record from the microphone, listen, then add the take to the project', onclick: () => openRecorder().catch((e) => toast(e.message)) }));
  ctl.append(el('button', { 'data-icon': '⇅', 'aria-label': 'Separate and transcribe audio', title: 'Separate and transcribe audio', textContent: 'Separate and transcribe audio...', onclick: () => openTranscription().catch((e) => toast(e.message)) }));
  box.append(ctl);
  const transcription = renderTranscriptionPanel();
  if (transcription) box.append(transcription);

  // transport
  const take = masterTake();
  const tr = el('div', { className: 'row', id: 'transport', style: 'flex-wrap:nowrap' });
  const playBtn = el('button', { id: 'tp-play', textContent: '▶', title: take ? 'Play the built song' : 'Build the song first', 'aria-label': 'Play the song', disabled: !take });
  playBtn.onclick = () => take && toggle(take).catch((e) => toast(e.message));
  const loop = el('input', { type: 'checkbox', id: 'tp-loop', style: 'width:auto', checked: playerState().loop });
  loop.onchange = () => setLoop(loop.checked);
  const wave = el('div', { className: 'arr-wave', id: 'tp-wave' });
  wave.dataset.secondsPerBar = String(barBeats() * 60 / tempo);
  wave.dataset.firstBar = '1';
  const cvs = el('canvas', { width: 800, height: 44, style: 'position:absolute;height:100%;display:block' });
  const head = el('div', { id: 'tp-head', style: 'position:absolute;top:0;bottom:0;width:2px;background:var(--acc);left:0;pointer-events:none' });
  wave.append(cvs, head);
  const scrub = (e) => {
    if (!take) return;
    const seconds = (e.clientX - wave.getBoundingClientRect().left) / bw * Number(wave.dataset.secondsPerBar) - (Number(wave.dataset.firstBar) - 1) * Number(wave.dataset.secondsPerBar);
    const seek = () => { const duration = playerState().duration; if (duration) seekFraction(seconds / duration); };
    if (playerState().take === take) seek(); else play(take).then(seek).catch((x) => { if (!x.toasted) toast(x.message); });
  };
  wave.onpointerdown = (e) => { wave.setPointerCapture(e.pointerId); scrub(e); wave.onpointermove = scrub; wave.onpointerup = () => { wave.onpointermove = null; wave.onpointerup = null; }; };
  const more = el('button', { textContent: '▾', title: take ? 'Stop or download this built audio' : 'Build first', disabled: !take });
  more.onclick = (e) => { const r = more.getBoundingClientRect(); showMenu(r.left, r.bottom, [{ label: 'Stop', fn: stop }, { label: 'Download WAV', fn: () => download(take, 'wav') }, { label: 'Download MP3', fn: () => download(take, 'mp3') }]); e.stopPropagation(); };
  tr.append(playBtn, more, el('span', { id: 'tp-time', className: 'note', style: 'min-width:90px;margin:0', textContent: '0:00 / 0:00' }), el('label', {}, [loop, ' loop']));
  ctl.append(tr);
  if (take) Promise.all([peaksOf(take, 4000), api.get(`/api/takes/${encodeURIComponent(take)}`)]).then(([p, record]) => {
    if (!wave.isConnected) return;
    const firstBar = Number(record.plan?.settings?.firstBar) || 1;
    wave.dataset.firstBar = String(firstBar);
    cvs.style.left = `${(firstBar - 1) * bw}px`;
    cvs.width = Math.max(1, Math.ceil(p.seconds / Number(wave.dataset.secondsPerBar) * bw));
    cvs.style.width = `${cvs.width}px`;
    wave.style.width = `${Math.max(W, (firstBar - 1) * bw + cvs.width)}px`;
    drawPeaks(cvs, p.peaks, getComputedStyle(document.documentElement).getPropertyValue('--acc').trim());
    updateTransport(playerState());
  }).catch(() => {});
  else wave.title = 'Build the song to see its waveform';
  updateTransport(playerState());

  const shell = el('div', { className: 'arr-shell' });
  const labels = el('div', { className: 'arr-labels' });
  const labelContent = el('div', { className: 'arr-label-content' });
  const arr = el('div', { className: 'arr', id: 'arr' });
  const trackLabels = [];
  labels.append(labelContent);
  shell.append(labels, arr);
  arr.dataset.barWidth = String(bw);
  wave.addEventListener('wheel', (event) => {
    if (!event.deltaY) return;
    event.preventDefault();
    event.stopPropagation();
    const cursor = event.clientX - arr.getBoundingClientRect().left;
    const bar = (arr.scrollLeft + cursor) / bw;
    const next = Math.max(16, Math.min(96, bw + (event.deltaY < 0 ? 8 : -8)));
    if (next === bw) return;
    bw = next;
    renderArrangement();
    $('#arr').scrollLeft = Math.max(0, bar * bw - cursor);
  }, { passive: false });
  wave.title = `${wave.title ? `${wave.title}. ` : ''}Scroll wheel to zoom; drag to scrub`;
  const bars = Math.min(256, Math.ceil((songBars(nodes) + 2) / 2) * 2);
  const W = bars * bw;
  wave.style.width = `${W}px`;
  wave.style.backgroundSize = `${bw}px 100%`;
  arr.append(wave);
  const ruler = el('div', { className: 'ruler', style: `width:${W}px;cursor:col-resize;background-size:${bw}px 100%` });
  for (let i = 0; i < bars; i += bw < 28 ? 4 : 2) ruler.append(Object.assign(el('span', { textContent: `bar ${i + 1} | ${Math.round((i * barBeats() * 60) / tempo)}s` }), { style: `left:${i * bw}px;width:${2 * bw}px` }));
  const strip = el('div', { className: 'arr-sections', 'aria-label': 'Sections of the song' });
  drawSections(strip, bw);
  ruler.append(strip);
  ruler.oncontextmenu = (e) => { e.preventDefault(); const bar = Math.floor((e.clientX - ruler.getBoundingClientRect().left) / bw) + 1; sectionMenu(e, bar, sectionAt(sectionsOf(), bar)); };
  const sel = el('div', { id: 'arr-range', style: `position:absolute;top:0;bottom:0;background:color-mix(in srgb,var(--acc) 20%,transparent);border-left:2px solid var(--acc);border-right:2px solid var(--acc);pointer-events:none;display:${S.range ? 'block' : 'none'}` });
  if (S.range) { sel.style.left = `${(S.range.firstBar - 1) * bw}px`; sel.style.width = `${S.range.bars * bw}px`; }
  ruler.append(sel);
  const slice = sliceOf();
  if (master && slice) {
    const endBar = slice.to || bars;
    const band = el('div', { id: 'arr-slice', className: 'arr-slice', title: `Master slice: bars ${slice.from} to ${slice.to || 'the end'}. Drag an edge to change it.` });
    band.style.left = `${(slice.from - 1) * bw}px`; band.style.width = `${(endBar - slice.from + 1) * bw}px`;
    const edge = (side) => {
      const handle = el('span', { className: `arr-slice-edge ${side}`, title: side === 'left' ? 'First bar of the slice' : 'Last bar of the slice' });
      handle.onpointerdown = (event) => {
        if (event.button !== 0) return;
        event.stopPropagation(); event.preventDefault();
        const rect = ruler.getBoundingClientRect();
        let from = slice.from, to = endBar;
        const barAt = (ev) => Math.max(1, Math.min(bars, Math.round((ev.clientX - rect.left) / bw) + (side === 'left' ? 1 : 0)));
        const move = (ev) => { const bar = barAt(ev); if (side === 'left') from = Math.min(bar, to); else to = Math.max(bar, from); band.style.left = `${(from - 1) * bw}px`; band.style.width = `${(to - from + 1) * bw}px`; };
        const up = () => { removeEventListener('pointermove', move); removeEventListener('pointerup', up); writeSlice(from, to >= bars && !slice.to ? null : to); };
        addEventListener('pointermove', move); addEventListener('pointerup', up);
      };
      return handle;
    };
    band.append(edge('left'), edge('right'));
    ruler.append(band);
  }
  ruler.onpointerdown = (e) => {
    const r = ruler.getBoundingClientRect();
    const at = (ev) => Math.max(0, Math.min(bars - 1, Math.floor((ev.clientX - r.left) / bw)));
    const first = at(e);
    ruler.setPointerCapture(e.pointerId);
    ruler.onpointermove = (ev) => { const a = Math.min(first, at(ev)), b = Math.max(first, at(ev)); sel.style.display = 'block'; sel.style.left = `${a * bw}px`; sel.style.width = `${(b - a + 1) * bw}px`; };
    ruler.onpointerup = (ev) => { ruler.onpointermove = null; ruler.onpointerup = null; const a = Math.min(first, at(ev)), b = Math.max(first, at(ev)); S.range = { firstBar: a + 1, bars: b - a + 1 }; renderArrangement(); notify(); };
  };
  arr.append(ruler);
  nodes.filter((n) => n.outs.some((o) => ['audio', 'midi', 'text'].includes(o[1])) && !['mixer', 'bus', 'master'].includes(n.tpl)).forEach((n) => {
    const parts = n.parts.length ? n.parts : [{ at: 1, length: Number(n.params.bars) > 0 ? Number(n.params.bars) : songBars(nodes), name: '' }];
    const isMidi = n.outs.some((o) => o[1] === 'midi'), isText = n.outs.some((o) => o[1] === 'text');
    const t = el('div', { className: 'track', style: `width:${W}px;background-size:${bw}px 100%` });
    const lab = el('div', { className: 'tlabel', textContent: n.title, title: 'Click: select the block', style: 'cursor:pointer' });
    lab.onclick = () => { select(n.id); notify(); focusNode(n); };
    activatable(lab, `Select block ${n.title}`);
    labelContent.append(lab);
    trackLabels.push([lab, t]);
    parts.forEach((p) => {
      const b = el('div', { className: `bar${n.dyn ? ' dyn' : ''}${isMidi ? ' midi' : ''}${isText ? ' txt' : ''}`, title: `${n.title}: bar ${p.at} for ${p.length}${p.name ? ` (${p.name})` : ''}`, tabIndex: 0, role: 'group', 'aria-label': `${n.title}, bar ${p.at}, ${p.length} bars. Drag to move, drag edges to resize, Enter to set timing.` });
      b.dataset.block = n.id;
      b.dataset.part = String(parts.indexOf(p));
      b.style.left = `${(p.at - 1) * bw}px`;
      b.style.width = `${p.length * bw - 2}px`;
      b.append(el('span', { textContent: p.name || n.params.chords || n.title, style: 'pointer-events:none' }));
      b.append(el('span', { className: 'h l', ariaHidden: 'true' }), el('span', { className: 'h r', ariaHidden: 'true' }));
      b.onclick = () => { select(n.id); notify(); focusNode(n); };
      b.ondblclick = () => editPartTiming(n, parts, Number(b.dataset.part));
      b.oncontextmenu = (event) => {
        event.preventDefault();
        showMenu(event.clientX, event.clientY, [
          { label: `Edit timing · bar ${p.at}, ${p.length} bars`, fn: () => editPartTiming(n, parts, Number(b.dataset.part)) },
          { label: 'Show in the source editor', fn: () => gotoSource(n) },
        ]);
      };
      b.onkeydown = async (event) => {
        if (readOnly() || !['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
        event.preventDefault();
        const i = Number(b.dataset.part), next = parts.map((part, j) => j !== i ? part : event.shiftKey
          ? { ...part, length: Math.max(1, part.length + (event.key === 'ArrowRight' ? 1 : -1)) }
          : { ...part, at: Math.max(1, part.at + (event.key === 'ArrowRight' ? 1 : -1)) });
        await commitParts(n, next, p.length, next[i].length);
      };
      b.onpointerdown = (event) => {
        if (readOnly() || event.button !== 0) return;
        const handle = event.target.closest('.h'), edge = handle?.classList.contains('l') ? 'left' : handle ? 'right' : 'move';
        partDrag = { node: n, parts, index: Number(b.dataset.part), part: p, edge, x: event.clientX, at: p.at, length: p.length, bar: b, changed: false };
        b.setPointerCapture(event.pointerId);
        event.preventDefault();
      };
      b.onpointermove = (event) => {
        if (!partDrag || partDrag.bar !== b) return;
        const drag = partDrag, delta = Math.round((event.clientX - drag.x) / bw);
        if (!delta) return;
        drag.changed = true;
        let next = { ...drag.part };
        if (drag.edge === 'move') next.at = Math.max(1, drag.at + delta);
        else if (drag.edge === 'right') next.length = Math.max(1, drag.length + delta);
        else { const shift = Math.min(drag.length - 1, delta); next.at = Math.max(1, drag.at + shift); next.length = drag.length - (next.at - drag.at); }
        drag.next = next;
        b.style.left = `${(next.at - 1) * bw}px`;
        b.style.width = `${next.length * bw - 2}px`;
      };
      b.onpointerup = async (event) => {
        if (!partDrag || partDrag.bar !== b) return;
        const drag = partDrag; partDrag = null;
        if (!drag.changed || !drag.next) return;
        const next = drag.parts.map((part, i) => i === drag.index ? drag.next : part);
        await commitParts(drag.node, next, drag.part.length, drag.next.length);
      };
      b.onpointercancel = () => { if (partDrag?.bar === b) { partDrag = null; renderArrangement(); } };
      attachExpand(b, t, n);
      t.append(b);
    });
    arr.append(t);
  });
  // the controls (a follow, curve or map block): a lane with the curve of the latest take, on the same bars
  nodes.filter((n) => n.outs.some((o) => String(o[1]).startsWith('control'))).forEach((n) => {
    const t = el('div', { className: 'track control-lane', style: `width:${W}px;background-size:${bw}px 100%` });
    const lab = el('div', { className: 'tlabel', textContent: `\u2307 ${n.title}`, title: 'A control: click to select the block', style: 'cursor:pointer' });
    lab.onclick = () => { select(n.id); notify(); focusNode(n); };
    activatable(lab, `Select block ${n.title}`);
    labelContent.append(lab);
    trackLabels.push([lab, t]);
    const cvs = el('canvas', { width: W, height: 26, className: 'control-curve', 'aria-label': `Control ${n.title}` });
    cvs.dataset.tempo = String(tempo);
    t.append(cvs);
    const take = (S.build.takes || {})[n.id];
    if (take) drawControl(cvs, take, bw, getComputedStyle(document.documentElement).getPropertyValue('--control').trim() || '#2ee6d6');
    arr.append(t);
  });
  box.append(shell);
  const alignLabels = () => {
    labels.style.height = `${arr.clientHeight}px`;
    labelContent.style.height = `${arr.scrollHeight}px`;
    trackLabels.forEach(([lab, track]) => { lab.style.top = `${track.offsetTop}px`; });
    labels.scrollTop = arr.scrollTop;
  };
  labelsObserver = new ResizeObserver(alignLabels);
  labelsObserver.observe(arr);
  trackLabels.forEach(([, track]) => labelsObserver.observe(track));
  arr.addEventListener('scroll', () => { labels.scrollTop = arr.scrollTop; });
  arr.scrollLeft = scrollBars * bw;
  arr.scrollTop = scrollTop;
  alignLabels();
  updateTransport(playerState());
}

// Ctrl+click on a bar opens or closes it: a play/stop button and the waveform of the block's own take, which can be scrubbed.
function attachExpand(bar, track, node) {
  const ex = el('div', { className: 'bar-ex' });
  const btn = el('button', { textContent: '▶', title: 'Play this block', 'aria-label': 'Play this block' });
  const wv = el('div', { className: 'bar-wv' });
  const cvs = el('canvas', { width: 400, height: 60 });
  const head = el('div', { className: 'bar-head' });
  wv.append(cvs, head);
  ex.append(btn, wv);
  for (const type of ['pointerdown', 'click', 'dblclick']) ex.addEventListener(type, (event) => event.stopPropagation());
  const takeId = () => (S.build.takes || {})[node.id];
  let drawn = null;
  const ensure = () => {
    const take = takeId();
    if (!take || drawn === take) return;
    drawn = take;
    peaksOf(take, 400).then(async (p) => {
      drawPeaks(cvs, p.peaks, getComputedStyle(document.documentElement).getPropertyValue('--acc').trim());
      // the joins of a loop: drawn over the waveform in the colour of the seam port, with their seconds on hover
      if (node.tpl !== 'import-audio') return;
      try {
        const seam = await peaksOf(take, 400, 'loop-region');
        drawPeaks(cvs, seam.peaks, getComputedStyle(document.documentElement).getPropertyValue('--warn').trim(), true);
        const text = seamText(seamSpans(seam.peaks, seam.seconds ?? p.seconds ?? 0));
        if (text) cvs.title = text;
      } catch { /* no seam output in this take */ }
    }).catch(() => { drawn = null; });
  };
  bar.addEventListener('click', (event) => {
    if (!event.ctrlKey && !event.metaKey) return;
    event.stopImmediatePropagation();
    openTracks.has(node.id) ? openTracks.delete(node.id) : openTracks.add(node.id);
    track.classList.toggle('open', openTracks.has(node.id));
    ensure();
  }, true);
  if (openTracks.has(node.id)) { track.classList.add('open'); ensure(); }
  btn.onclick = () => { const take = takeId(); if (take) toggle(take).catch((e) => toast(e.message)); else toast('Build this block first.'); };
  wv.onpointerdown = (event) => {
    const take = takeId();
    if (!take) return;
    wv.setPointerCapture(event.pointerId);
    const scrub = (e) => { const r = wv.getBoundingClientRect(), f = (e.clientX - r.left) / r.width; if (playerState().take === take) seekFraction(f); else play(take).then(() => seekFraction(f)).catch((x) => { if (!x.toasted) toast(x.message); }); };
    scrub(event);
    wv.onpointermove = scrub;
    wv.onpointerup = () => { wv.onpointermove = null; wv.onpointerup = null; };
  };
  ex.dataset.block = node.id;
  bar.append(ex);
}

const fmt = (s) => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`;

/** Moves the playhead and the node players without rebuilding anything. */
export function updateTransport(st) {
  const head = $('#tp-head');
  const take = masterTake();
  const wave = $('#tp-wave');
  if (head) head.style.left = take && st.take === take && wave ? `${((Number(wave.dataset.firstBar) - 1) + st.time / Number(wave.dataset.secondsPerBar)) * bw}px` : '0';
  const time = $('#tp-time');
  if (time) time.textContent = take && st.take === take ? `${fmt(st.time)} / ${fmt(st.duration)}` : '0:00 / 0:00';
  const pb = $('#tp-play');
  if (pb) { const on = take && st.take === take && st.playing; pb.textContent = on ? '⏸' : '▶'; if (take) { pb.title = on ? 'Pause the song' : 'Play the built song'; pb.setAttribute('aria-label', on ? 'Pause the song' : 'Play the song'); } }
  document.querySelectorAll('.bar-ex').forEach((ex) => {
    const take = (S.build.takes || {})[ex.dataset.block];
    const mine = take && st.take === take;
    const exb = ex.querySelector('button'); exb.textContent = mine && st.playing ? '⏸' : '▶'; exb.title = mine && st.playing ? 'Pause this block' : 'Play this block'; exb.setAttribute('aria-label', exb.title);
    ex.querySelector('.bar-head').style.left = mine && st.duration ? `${st.time / st.duration * 100}%` : '0';
  });
  document.querySelectorAll('.node .player').forEach((pl) => {
    const mine = pl.dataset.take === st.take;
    const percent = mine && st.duration ? Math.round(st.time / st.duration * 100) : 0;
    const slider = pl.querySelector('.plbg');
    if (slider) { slider.setAttribute('aria-valuenow', String(percent)); slider.setAttribute('aria-valuetext', `${percent}%`); }
    const played = pl.querySelector('.pl-played'), nodeHead = pl.querySelector('.plhead');
    if (played) played.style.width = `${percent}%`;
    if (nodeHead) nodeHead.style.left = `${percent}%`;
    const b = pl.querySelector('button');
    if (b) {
      const loading = st.loading === pl.dataset.take;
      b.textContent = loading ? '◌' : mine && st.playing ? '⏸' : '▶';
      b.title = loading ? 'Loading audio... Click to cancel' : mine && st.playing ? 'Pause this block' : 'Play this block';
      b.setAttribute('aria-label', b.title);
      b.setAttribute('aria-busy', String(loading));
    }
  });
}

export function initArrangement() {
  onPlayer(updateTransport);
}
