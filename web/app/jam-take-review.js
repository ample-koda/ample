// The review of a long ("endless") take in the Jam: what was played, as notes on a strip of bars; drag over it to choose the bars to keep, listen to the choice, and keep it (the clip gets longer when
// the choice is longer than the clip) or throw it away. The Jam gives the playing and the writing; this only shows the take and takes the choice. @ample:virtual-keyboard
import { el } from './ui.js';
import { barAt, selectRows, takeBars } from '../lib/virtual-keys.mjs';
import { barBeats } from '../lib/meter.mjs';
import { editRange } from '../lib/take-range.mjs';

/**
 * @param {{ ppq: number, clipBars: () => number, preview: (rows: object[], bars: number, done: () => void) => (() => void), keep: (choice: { rows: object[], bars: number, extend: boolean }) => void }} options
 */
export function createTakeReview({ ppq, clipBars, preview, keep, keepLabel = 'Keep', keepTitle = 'Write the chosen bars into the clip', showExtend = true }) {
  const state = { notes: [], bars: 1, from: 0, to: 1, drag: null, stopPreview: null, width: 640, height: 70 };
  const info = el('output', { className: 'mls-review-info', 'aria-live': 'polite' });
  const canvas = el('canvas', { className: 'mls-review-strip', 'aria-label': 'The take: drag over the bars to keep, or click one bar. Use the bar fields for the keyboard', role: 'img', title: 'Drag over the bars to keep; click to choose one bar' });
  const play = el('button', { type: 'button', className: 'mls-btn', textContent: '▶ Preview', title: 'Listen to the bars that are chosen' });
  const extend = el('input', { type: 'checkbox', checked: true, id: 'mls-review-extend' });
  const extendLabel = el('label', { className: 'mls-review-extend', title: 'When the chosen bars are longer than the clip, make the clip longer; otherwise they are cut at the end of the clip' }, [extend, ' Make the clip longer if needed']);
  const keepButton = el('button', { type: 'button', className: 'mls-btn mls-review-keep', textContent: keepLabel, title: keepTitle });
  const whole = el('button', { type: 'button', className: 'mls-btn', textContent: 'Whole take', title: 'Choose all of it' });
  const num = (label) => el('input', { type: 'number', min: '1', max: '1', step: '1', inputMode: 'numeric', className: 'mls-review-bar-input', 'aria-label': label });
  const fromInput = num('First bar to keep'), toInput = num('Last bar to keep');
  const range = el('span', { className: 'mls-review-range', role: 'group', 'aria-label': 'Bars to keep' }, ['Bars ', fromInput, ' to ', toInput]);
  const discard = el('button', { type: 'button', className: 'mls-btn', textContent: 'Discard', title: 'Throw the take away' });
  const root = el('div', { className: 'mls-review', hidden: true, role: 'region', 'aria-label': 'Review of the take' }, [info, canvas, el('div', { className: 'mls-review-bar' }, [play, whole, range, ...(showExtend ? [extendLabel] : []), keepButton, discard])]);

  const stop = () => { state.stopPreview?.(); state.stopPreview = null; play.textContent = '▶ Preview'; };
  const hide = () => { stop(); root.hidden = true; state.notes = []; };
  function describe() {
    const chosen = selectRows(state.notes, state.from, state.to, ppq), longer = chosen.bars > clipBars();
    info.textContent = `${state.notes.length} notes over ${state.bars} bar${state.bars === 1 ? '' : 's'}. Chosen: bar${chosen.bars === 1 ? '' : 's'} ${state.from + 1}–${state.to} (${chosen.rows.length} notes)${longer ? ` · longer than the clip (${clipBars()} bar${clipBars() === 1 ? '' : 's'})` : ''}`;
    fromInput.max = toInput.max = String(state.bars); fromInput.value = String(state.from + 1); toInput.value = String(state.to);
    extendLabel.classList.toggle('needed', longer);
  }
  function draw() {
    const dpr = window.devicePixelRatio || 1, w = Math.max(240, Math.floor(root.clientWidth || state.width)), h = state.height;
    if (canvas.width !== Math.round(w * dpr)) { canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr); canvas.style.width = w + 'px'; canvas.style.height = h + 'px'; }
    state.width = w;
    const g = canvas.getContext('2d'), styles = getComputedStyle(document.documentElement), bar = barBeats() * ppq, total = state.bars * bar;
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.fillStyle = styles.getPropertyValue('--panel2').trim() || '#222'; g.fillRect(0, 0, w, h);
    g.fillStyle = 'rgba(255,255,255,.18)'; g.fillRect(state.from / state.bars * w, 0, (state.to - state.from) / state.bars * w, h);
    g.strokeStyle = 'rgba(255,255,255,.2)';
    for (let b = 0; b <= state.bars; b++) { const x = Math.round(b / state.bars * w); g.beginPath(); g.moveTo(x, 0); g.lineTo(x, h); g.stroke(); }
    const pitches = state.notes.map((n) => n.pitch), lo = Math.min(...pitches, 60) - 1, hi = Math.max(...pitches, 60) + 1;
    g.fillStyle = styles.getPropertyValue('--acc').trim() || '#4ad6ff';
    for (const n of state.notes) g.fillRect(n.at / total * w, h - ((n.pitch - lo) / Math.max(1, hi - lo)) * (h - 10) - 8, Math.max(2, n.length / total * w), 5);
    // the end of the clip as it is now
    const clipX = Math.min(w - 1, clipBars() / state.bars * w);
    if (clipBars() < state.bars) { g.fillStyle = '#ff9f4a'; g.fillRect(clipX, 0, 2, h); }
  }
  function show(notes) {
    state.notes = notes; state.bars = takeBars(notes, ppq); state.from = 0; state.to = state.bars;
    root.hidden = false; describe(); requestAnimationFrame(draw);
  }
  const barOfEvent = (event) => barAt(event.clientX - canvas.getBoundingClientRect().left, canvas.getBoundingClientRect().width, state.bars);
  canvas.onpointerdown = (event) => { stop(); canvas.setPointerCapture(event.pointerId); state.drag = { start: barOfEvent(event), moved: false }; };
  canvas.onpointermove = (event) => {
    if (!state.drag) return;
    const bar = barOfEvent(event);
    state.drag.moved ||= bar !== state.drag.start;
    state.from = Math.min(state.drag.start, bar); state.to = Math.max(state.drag.start, bar) + 1;
    describe(); draw();
  };
  canvas.onpointerup = () => {
    if (state.drag && !state.drag.moved) { state.from = state.drag.start; state.to = state.drag.start + 1; describe(); draw(); }   // a click chooses that one bar; Whole take is its own button
    state.drag = null;
  };
  const fromFields = (which) => () => {
    stop();
    const r = editRange(which, (which === 'from' ? fromInput : toInput).value, { from: state.from + 1, to: state.to }, state.bars);
    state.from = r.from - 1; state.to = r.to; describe(); draw();   // describe() also rewrites both fields, so a bad entry snaps back
  };
  fromInput.onchange = fromFields('from'); toInput.onchange = fromFields('to');
  for (const input of [fromInput, toInput]) input.onkeydown = (event) => { if (event.key === 'Escape') { describe(); event.stopPropagation(); } };   // Escape puts the chosen bar back in the field
  canvas.onpointercancel = () => { state.drag = null; };
  whole.onclick = () => { state.from = 0; state.to = state.bars; describe(); draw(); };
  play.onclick = () => {
    if (state.stopPreview) { stop(); return; }
    const chosen = selectRows(state.notes, state.from, state.to, ppq);
    play.textContent = '■ Stop';
    state.stopPreview = preview(chosen.rows, chosen.bars, () => { state.stopPreview = null; play.textContent = '▶ Preview'; });
  };
  keepButton.onclick = () => { const chosen = selectRows(state.notes, state.from, state.to, ppq); stop(); keep({ ...chosen, extend: extend.checked }); hide(); };
  discard.onclick = () => hide();
  new ResizeObserver(() => { if (!root.hidden) draw(); }).observe(root);
  return { element: root, show, hide, stop, get open() { return !root.hidden; } };
}
