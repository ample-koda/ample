// The automation editor (plan X06): a curve over the bars of the song for one parameter. Click on an empty place to add a point, drag a point to move it,
// double-click a point to remove it. It writes the same auto(bar:value, ...) text the source holds. @ample:automation

import { el, ask, showMenu, toast } from './ui.js';
import { AUTOMATABLE, automationRange, formatAuto, parseAuto, startingPoints } from '../lib/automation-model.mjs';
import { flat } from './store.js';
import { fadeable, isFade } from '../lib/fade-model.mjs';
import { openFadeEditor } from './fade-editor.js';
import { createAutomationPreview } from './automation-preview.js';

const ns = 'http://www.w3.org/2000/svg';
const node = (tag, attrs = {}) => { const n = document.createElementNS(ns, tag); Object.entries(attrs).forEach(([k, v]) => n.setAttribute(k, String(v))); return n; };

/** Whether a block type has an automatable parameter of this name. */
export const automatable = (type, name) => !!AUTOMATABLE[type]?.[name];

/**
 * Opens the editor. Resolves to the new text (an auto(...) text or '' for "remove the automation back to a plain value"), or undefined when cancelled.
 * @param {{ type: string, name: string, title: string, text: string, bars: number }} options
 */
export async function openAutomationEditor({ type, name, title, text, bars, tempo, block }) {
  // a fade has its own editor (a start, a length and a shape instead of points)
  if (isFade(text)) return openFadeEditor({ title, text, bars, tempo });
  // the range can depend on the other parameters of the block (a pitch curve with a range is in percent)
  const range = automationRange(type, name, flat().nodes.find((n) => n.id === block)?.params) || AUTOMATABLE[type][name];
  const shownBars = Math.max(16, bars || 16);
  let points = parseAuto(text) || startingPoints(text, Math.max(range.min, Math.min(range.max, range.min)), shownBars);
  const W = 680, H = 250, left = 58, bottom = 26;
  const toX = (bar) => left + ((bar - 1) / Math.max(1, shownBars - 1)) * (W - left - 10);
  const toBar = (x) => Math.max(1, Math.min(shownBars, Math.round(1 + ((x - left) / (W - left - 10)) * (shownBars - 1))));
  const norm = (v) => (range.log ? Math.log(v / range.min) / Math.log(range.max / range.min) : (v - range.min) / (range.max - range.min));
  const denorm = (n) => (range.log ? range.min * (range.max / range.min) ** n : range.min + n * (range.max - range.min));
  const toY = (v) => 10 + (1 - norm(Math.max(range.min, Math.min(range.max, v)))) * (H - bottom - 10);
  const toValue = (y) => Math.round(Math.max(range.min, Math.min(range.max, denorm(1 - (y - 10) / (H - bottom - 10)))));

  const svg = node('svg', { viewBox: `0 0 ${W} ${H}`, class: 'automation-plot', role: 'img', 'aria-label': `${title} automation over the bars` });
  svg.setAttribute('style', 'touch-action:none');
  const defs = node('defs');
  const stroke = node('linearGradient', { id: 'auto-stroke', x1: 0, x2: 1, y1: 0, y2: 0 });
  stroke.append(node('stop', { offset: 0, 'stop-color': '#2de2ff' }), node('stop', { offset: 1, 'stop-color': '#ff4fd8' }));
  const fill = node('linearGradient', { id: 'auto-fill', x1: 0, x2: 0, y1: 0, y2: 1 });
  fill.append(node('stop', { offset: 0, 'stop-color': '#2de2ff', 'stop-opacity': 0.28 }), node('stop', { offset: 1, 'stop-color': '#ff4fd8', 'stop-opacity': 0.02 }));
  defs.append(stroke, fill);
  const grid = node('g');
  // the plot area, a faint line at every bar (a stronger one every few bars with its number), and the value at the top, the middle and the bottom
  grid.append(node('rect', { x: left, y: 10, width: W - left - 10, height: H - bottom - 10, rx: 6, class: 'auto-area' }));
  const labelStep = [1, 2, 4, 8, 16].find((s) => s * ((W - left - 10) / Math.max(1, shownBars - 1)) >= 34) ?? 32;
  for (let bar = 1; bar <= shownBars; bar++) {
    const strong = (bar - 1) % labelStep === 0;
    grid.append(node('line', { x1: toX(bar), x2: toX(bar), y1: 10, y2: H - bottom, class: strong ? 'auto-grid strong' : 'auto-grid' }));
    if (strong) { const t = node('text', { x: toX(bar), y: H - 8, class: 'auto-label', 'text-anchor': 'middle' }); t.textContent = String(bar); grid.append(t); }
  }
  [range.min, (range.min + range.max) / 2, range.max].forEach((v) => {
    grid.append(node('line', { x1: left, x2: W - 10, y1: toY(v), y2: toY(v), class: 'auto-grid value' }));
    const t = node('text', { x: left - 8, y: toY(v) + 4, class: 'auto-label', 'text-anchor': 'end' }); t.textContent = `${Math.round(v)}${range.unit ? ` ${range.unit}` : ''}`; grid.append(t);
  });
  const area = node('path', { class: 'auto-fill' });
  const line = node('path', { class: 'auto-line', fill: 'none' });
  const dots = node('g');
  const tip = node('g', { class: 'auto-tip', visibility: 'hidden' });
  const tipBox = node('rect', { rx: 5, height: 20 }), tipText = node('text', { y: 14, 'text-anchor': 'middle' });
  tip.append(tipBox, tipText);
  svg.append(defs, grid, area, line, dots, tip);
  const showTip = (p) => { const label = `bar ${p.bar} · ${p.value}${range.unit ? ` ${range.unit}` : ''}`, w = label.length * 6.6 + 14, x = Math.max(w / 2, Math.min(W - w / 2, toX(p.bar))); tipText.textContent = label; tipText.setAttribute('x', x); tipBox.setAttribute('x', x - w / 2); tipBox.setAttribute('width', w); tipBox.setAttribute('y', Math.max(2, toY(p.value) - 34)); tipText.setAttribute('y', Math.max(2, toY(p.value) - 34) + 14); tip.setAttribute('visibility', 'visible'); };
  const hideTip = () => tip.setAttribute('visibility', 'hidden');
  let lastBar = points[0]?.bar ?? 1;   // the point touched last: the preview window is centred on it

  let draw = () => {
    const focusedBar = dots.contains(document.activeElement) ? dots.querySelector(':focus')?.__point : null;
    points.sort((a, b) => a.bar - b.bar);
    const d = [`M${left} ${toY(points[0].value)}`, ...points.map((p) => `L${toX(p.bar)} ${toY(p.value)}`), `L${W - 10} ${toY(points[points.length - 1].value)}`];
    line.setAttribute('d', d.join(' '));
    area.setAttribute('d', `${d.join(' ')} L${W - 10} ${H - bottom} L${left} ${H - bottom} Z`);
    dots.replaceChildren(...points.map((p, index) => {
      const dot = node('circle', { cx: toX(p.bar), cy: toY(p.value), r: 7, class: 'auto-dot', tabindex: 0, role: 'slider', 'aria-label': `Point at bar ${p.bar}`, 'aria-valuemin': range.min, 'aria-valuemax': range.max, 'aria-valuenow': p.value, 'aria-valuetext': `Bar ${p.bar}, ${p.value}${range.unit ? ` ${range.unit}` : ''}` });
      dot.__point = p;
      dot.onkeydown = (event) => {
        const big = event.shiftKey;
        if (event.key === 'Delete' || event.key === 'Backspace') { event.preventDefault(); event.stopPropagation(); if (points.length > 1) { points.splice(points.indexOf(p), 1); draw(); focusAt(Math.min(index, points.length - 1)); } else toast('The last point stays: a line needs one.'); return; }
        let bar = p.bar, value = p.value;
        if (event.key === 'ArrowLeft') bar -= big ? 4 : 1; else if (event.key === 'ArrowRight') bar += big ? 4 : 1;
        else if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
          const n = norm(p.value) + (event.key === 'ArrowUp' ? 1 : -1) * (big ? 0.1 : 0.02);
          value = Math.round(Math.max(range.min, Math.min(range.max, denorm(Math.max(0, Math.min(1, n))))));
          if (value === p.value) value = Math.max(range.min, Math.min(range.max, p.value + (event.key === 'ArrowUp' ? 1 : -1)));
        } else return;
        event.preventDefault(); event.stopPropagation();
        bar = Math.max(1, Math.min(shownBars, bar));
        if (points.some((q) => q !== p && q.bar === bar)) return; // never merge with a neighbour by keyboard
        p.bar = bar; p.value = value; draw(); dots.querySelectorAll('.auto-dot').forEach((node) => { if (node.__point === p) node.focus(); });
      };
      // the point is moved by the window while the button is down: the dots are made again at every change, so the one under the pointer cannot hold the drag
      dot.onpointerdown = (event) => {
        if (event.button !== 0) return;
        event.stopPropagation(); event.preventDefault();
        const moving = p;
        const move = (ev) => { const box = svg.getBoundingClientRect(); const x = ((ev.clientX - box.left) / box.width) * W, y = ((ev.clientY - box.top) / box.height) * H; moving.bar = toBar(x); moving.value = toValue(y); lastBar = moving.bar; draw(); showTip(moving); };
        const up = () => { removeEventListener('pointermove', move); removeEventListener('pointerup', up); removeEventListener('pointercancel', up); points = [...new Map(points.map((q) => [q.bar, q])).values()]; draw(); hideTip(); };
        addEventListener('pointermove', move); addEventListener('pointerup', up); addEventListener('pointercancel', up);
      };
      dot.oncontextmenu = (event) => {
        event.preventDefault(); event.stopPropagation();
        showMenu(event.clientX, event.clientY, [{ head: `Point at bar ${p.bar}` }, { label: 'Delete point', fn: () => { if (points.length > 1) { points.splice(points.indexOf(p), 1); draw(); } else toast('The last point stays: a line needs one.'); } }]);
      };
      dot.onmouseenter = () => showTip(p); dot.onmouseleave = hideTip; dot.onfocus = () => showTip(p); dot.onblur = hideTip;
      dot.ondblclick = (event) => { event.stopPropagation(); if (points.length > 1) { points.splice(points.indexOf(p), 1); draw(); } };
      return dot;
    }));
    source.textContent = formatAuto(points);
    if (focusedBar) dots.querySelectorAll('.auto-dot').forEach((node) => { if (node.__point === focusedBar) node.focus(); });
  };
  const focusAt = (index) => dots.querySelectorAll('.auto-dot')[index]?.focus();
  /** Adds a point in the middle of the widest gap, on the line, so a keyboard user can start a new point. */
  const addPoint = () => {
    const sorted = [...points].sort((a, b) => a.bar - b.bar);
    let best = null;
    sorted.forEach((q, i) => { const next = i + 1 < sorted.length ? sorted[i + 1].bar : shownBars + 1; const gap = next - q.bar; if (gap >= 2 && (!best || gap > best.gap)) best = { gap, bar: q.bar + Math.floor(gap / 2), value: q.value }; });
    if (!best) { toast('Every bar already has a point.'); return; }
    const added = { bar: best.bar, value: best.value };
    lastBar = added.bar; points.push(added); draw(); dots.querySelectorAll('.auto-dot').forEach((node) => { if (node.__point === added) node.focus(); });
  };
  svg.onpointerdown = (event) => {
    if (event.button !== 0) return;
    const box = svg.getBoundingClientRect();
    const x = ((event.clientX - box.left) / box.width) * W, y = ((event.clientY - box.top) / box.height) * H;
    if (x < left || y > H - bottom) return;
    lastBar = toBar(x);
    points.push({ bar: lastBar, value: toValue(y) }); points = [...new Map(points.map((q) => [q.bar, q])).values()]; draw();
  };
  svg.setAttribute('tabindex', '0'); svg.setAttribute('role', 'group');
  svg.addEventListener('keydown', (event) => { if (event.key === 'Enter' && event.target === svg) { event.preventDefault(); addPoint(); } });
  const addButton = el('button', { type: 'button', id: 'automation-add-point', className: 'auto-add', textContent: '+ Add point', title: 'Adds a point in the widest gap (also: Enter on the curve)', onclick: addPoint });
  const source = el('code', { className: 'auto-source', title: 'The text this writes into the song' });
  const hint = el('p', { className: 'auto-hint', textContent: 'Click in the curve to add a point, drag a point to move it, double-click to remove it.' });
  const more = el('details', { className: 'auto-more' }, [el('summary', { textContent: 'Keyboard and other shapes' }), el('p', { textContent: 'Tab to a point and use the arrow keys (Shift for bigger steps) to move it, Delete to remove it, or press Enter on the curve to add one. Between points the value moves in a straight line. In the source you can also type lfo(4, low, high) or env(1, 2, 4, 2, low, high).' })]);
  // hearing the draft: Live (the browser runs the effect and follows the curve while you drag) and Preview (the song, or a window of bars, built with this curve; nothing is written)
  const status = el('span', { className: 'auto-status', role: 'status', 'aria-live': 'polite' });
  const preview = block ? createAutomationPreview({ block, type, name, tempo, totalBars: bars || undefined, getPoints: () => points, getText: () => formatAuto(points), onStatus: (message) => { status.textContent = message; } }) : null;
  const liveButton = el('button', { type: 'button', id: 'auto-live', className: 'auto-play', textContent: '\u25CF Live' });
  const draftButton = el('button', { type: 'button', id: 'auto-preview', className: 'auto-play', textContent: '\u25B6 Preview', title: 'Plays the song with this curve, built exactly like the real build. Nothing is written until Apply.' });
  const windowSelect = el('select', { id: 'auto-window', 'aria-label': 'What the preview plays', title: 'What Live and Preview play' }, [el('option', { value: 'around', textContent: '8 bars around the last point' }), el('option', { value: 'whole', textContent: 'The whole song' })]);
  let refreshTimer = 0;
  const paintPlay = () => {
    const mode = preview?.mode();
    liveButton.classList.toggle('on', mode === 'live'); draftButton.classList.toggle('on', mode === 'draft');
    liveButton.textContent = mode === 'live' ? '\u25A0 Stop live' : '\u25CF Live'; draftButton.textContent = mode === 'draft' ? '\u25A0 Stop preview' : '\u25B6 Preview';
  };
  if (preview) {
    const live = preview.liveState();
    liveButton.disabled = !live.ok; liveButton.title = live.reason;
    liveButton.onclick = async () => { if (preview.mode() === 'live') { preview.stop(); paintPlay(); return; } await preview.startLive(lastBar, windowSelect.value === 'whole'); paintPlay(); };
    draftButton.onclick = async () => { if (preview.mode() === 'draft') { preview.stop(); paintPlay(); return; } await preview.startDraft(lastBar, windowSelect.value === 'whole'); paintPlay(); };
    // a draft that is playing is rendered again by itself a moment after the curve stopped changing
    const baseDraw = draw;
    draw = () => { baseDraw(); if (preview.mode() === 'draft') { clearTimeout(refreshTimer); refreshTimer = setTimeout(() => preview.startDraft(lastBar, windowSelect.value === 'whole').then(paintPlay), 900); } };
  }
  const wrap = el('div', { className: 'auto-editor' }, [hint, svg, el('div', { className: 'auto-foot' }, [addButton, el('span', { className: 'auto-source-label', textContent: 'Source' }), source]), ...(preview ? [el('div', { className: 'auto-preview' }, [liveButton, draftButton, windowSelect, status])] : []), more]);
  draw();

  const dialog = document.getElementById('dlg');
  dialog.classList.add('automation-dialog');   // wider than a plain question: the plot needs room
  let result;
  try {
    result = await ask(`Automate ${title}`, '', [
      { label: 'Cancel', value: false }, { label: 'Back to a plain value', value: 'clear' }, ...(fadeable(type, name) ? [{ label: 'Fade...', value: 'fade' }] : []), { label: 'Apply', value: true, primary: true },
    ], [wrap]);
  } finally { dialog.classList.remove('automation-dialog'); clearTimeout(refreshTimer); preview?.stop(); }
  if (result === 'fade') return openFadeEditor({ title, text: '', bars, tempo });
  if (result === 'clear') return '';
  return result === true ? formatAuto(points) : undefined;
}
