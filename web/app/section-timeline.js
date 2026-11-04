// The sections of a drums block (its `song`) as a small timeline (plan patterns P07): a coloured block per section, as wide as its bars, a bar ruler above it. A click chooses the
// pattern, the right edge changes the bars, dragging the block moves it, a right click (or the keyboard) opens the rest. It only calls back with the new list of sections; the text is
// written by the caller, as before. @ample:patterns
import { ask, el, showMenu } from './ui.js';
import { addSection, moveSection, removeSection, sectionRanges, setBars } from '../lib/pattern-model.mjs';
import { barsFromDrag, colourOf, dropIndex, dropLineX, duplicateSection, pxPerBar, rects, rulerMarks, tipOf } from '../lib/section-timeline-model.mjs';

const DRAG_START = 4;   // pixels the pointer must move before a press on a block is a drag and not a click

/**
 * entries: [{ name, bars }]; current: the pattern the grid shows; names: the pattern names a section can be added for; onSelect(name); onChange(entries); locked: no editing.
 * Returns the element; `element.setHead(fraction | null)` puts a playhead through it (0 to 1 over the whole song).
 */
export function sectionTimeline({ entries, current, names, locked, onSelect, onChange, mainName = 'pattern', blockId = '' }) {
  const ranges = sectionRanges(entries), total = entries.reduce((sum, e) => sum + e.bars, 0);
  const root = el('div', { className: 'sectl', id: 'seq-song', role: 'group', 'aria-label': 'Sections of the drum part, in the order they play' });
  root.dataset.block = blockId; root.dataset.total = String(total);
  if (!entries.length) {
    root.append(el('span', { className: 'note', textContent: 'No sections: the main pattern plays on every bar. Add a section to put patterns in order.' }), addButton());
    root.setHead = () => {};
    return root;
  }
  const scroll = el('div', { className: 'sectl-scroll' });
  const ruler = el('div', { className: 'sectl-ruler', 'aria-hidden': 'true' });
  const track = el('div', { className: 'sectl-track' });
  const head = el('i', { className: 'sectl-head', hidden: true, 'aria-hidden': 'true' });
  const drop = el('i', { className: 'sectl-drop', hidden: true, 'aria-hidden': 'true' });
  track.append(drop, head);
  scroll.append(ruler, track);
  root.append(scroll, addButton());
  let px = 24, blocks = [];

  function addButton() {
    return el('button', { type: 'button', className: 'sectl-add', textContent: '+', disabled: locked, 'aria-label': 'Add a section', title: 'Add a section with a pattern', onclick: (event) => {
      event.stopPropagation();   // the click that opens the menu must not reach the page, which closes menus
      const r = event.currentTarget.getBoundingClientRect();
      showMenu(r.left, r.bottom + 2, [{ head: 'Add a section' }, ...names.map((name) => ({ label: name === mainName ? 'main' : name, fn: () => onChange(addSection(entries, name, 1)) }))]);
    } });
  }

  const nameOf = (range) => (range.name === mainName ? 'main' : range.name);

  function layout() {
    const width = Math.max(120, (root.clientWidth || 320) - 38);
    px = pxPerBar(total, width);
    scroll.style.setProperty('--sectl-px', `${px}px`);
    track.style.width = `${total * px}px`; ruler.style.width = `${total * px}px`;
    ruler.replaceChildren(...rulerMarks(total, px).map((m) => el('span', { textContent: String(m.bar), style: `left:${m.x}px` })));
    rects(entries, px).forEach((rect, i) => { const block = blocks[i]; block.style.left = `${rect.x}px`; block.style.width = `${rect.width - 2}px`; block.classList.toggle('narrow', rect.width < 30); });
  }

  const menuFor = (range, x, y) => {
    if (locked) return;
    showMenu(x, y, [
      { head: `${nameOf(range)} · ${range.bars} bar${range.bars === 1 ? '' : 's'}` },
      { label: 'Edit this pattern', fn: () => onSelect(range.name) },
      { label: 'Duplicate', fn: () => onChange(duplicateSection(entries, range.index)) },
      { label: 'Set the bars...', fn: async () => {
        const input = el('input', { type: 'number', min: 1, max: 999, value: range.bars, 'aria-label': 'Bars' });
        const ok = await ask('Bars of this section', `How many bars does ${nameOf(range)} last?`, [{ label: 'OK', value: true, primary: true }, { label: 'Cancel', value: false }], [input]);
        if (ok && Number(input.value) >= 1) onChange(setBars(entries, range.index, Number(input.value)));
      } },
      '-',
      { label: 'Remove', fn: () => onChange(removeSection(entries, range.index)) },
    ]);
  };

  blocks = ranges.map((range) => {
    const block = el('div', { className: `sectl-block c${colourOf(range.name)}${range.name === current ? ' on' : ''}`, role: 'button', tabIndex: 0, title: tipOf(range), 'aria-label': `${tipOf(range)}. Enter edits the pattern.${locked ? '' : ' Alt+Left and Alt+Right move it, plus and minus change the bars, Delete removes it.'}` }, [
      el('span', { className: 'sectl-name', textContent: nameOf(range) }),
      el('span', { className: 'sectl-bars', textContent: String(range.bars) }),
      ...(locked ? [] : [el('i', { className: 'sectl-edge', title: 'Drag to change the bars', 'aria-hidden': 'true' })]),
    ]);
    block.dataset.section = String(range.index);
    block.onkeydown = (event) => {
      const key = event.key;
      if (key === 'Enter' || key === ' ') { event.preventDefault(); onSelect(range.name); return; }
      if (key === 'ContextMenu' || (key === 'F10' && event.shiftKey)) { event.preventDefault(); const r = block.getBoundingClientRect(); menuFor(range, r.left, r.bottom); return; }
      if (locked) return;
      if (event.altKey && key === 'ArrowLeft' && range.index > 0) { event.preventDefault(); onChange(moveSection(entries, range.index, range.index - 1)); }
      else if (event.altKey && key === 'ArrowRight' && range.index < entries.length - 1) { event.preventDefault(); onChange(moveSection(entries, range.index, range.index + 1)); }
      else if (key === '+' || key === '=') { event.preventDefault(); onChange(setBars(entries, range.index, range.bars + 1)); }
      else if (key === '-' && range.bars > 1) { event.preventDefault(); onChange(setBars(entries, range.index, range.bars - 1)); }
      else if (key === 'Delete' || key === 'Backspace') { event.preventDefault(); onChange(removeSection(entries, range.index)); }
    };
    block.oncontextmenu = (event) => { event.preventDefault(); menuFor(range, event.clientX, event.clientY); };
    block.onpointerdown = (event) => {
      if (event.button !== 0) return;
      const onEdge = event.target.classList?.contains('sectl-edge');
      if (locked && !onEdge) return;
      const startX = event.clientX, rect = rects(entries, px)[range.index];
      let mode = onEdge ? 'resize' : 'press', changed = null;
      block.setPointerCapture(event.pointerId);
      const move = (e) => {
        const dx = e.clientX - startX;
        if (mode === 'press' && Math.abs(dx) >= DRAG_START) { mode = 'move'; block.classList.add('dragging'); drop.hidden = false; }
        if (mode === 'resize') {
          const bars = barsFromDrag(range.bars, dx, px);
          if (bars !== changed) { changed = bars; block.style.width = `${bars * px - 2}px`; block.querySelector('.sectl-bars').textContent = String(bars); }
        } else if (mode === 'move') {
          block.style.transform = `translateX(${dx}px)`;
          const to = dropIndex(entries, range.index, rect.x + dx + rect.width / 2, px);
          changed = to; drop.style.left = `${dropLineX(entries, range.index, to, px)}px`;
        }
      };
      const up = () => {
        block.removeEventListener('pointermove', move); block.removeEventListener('pointerup', up); block.removeEventListener('pointercancel', cancel);
        block.classList.remove('dragging'); block.style.transform = ''; drop.hidden = true;
        if (mode === 'press') { onSelect(range.name); return; }
        if (mode === 'resize' && changed !== null && changed !== range.bars) onChange(setBars(entries, range.index, changed));
        else if (mode === 'move' && changed !== null && changed !== range.index) onChange(moveSection(entries, range.index, changed));
        else layout();
      };
      const cancel = () => { block.removeEventListener('pointermove', move); block.removeEventListener('pointerup', up); block.removeEventListener('pointercancel', cancel); block.classList.remove('dragging'); block.style.transform = ''; drop.hidden = true; layout(); };
      block.addEventListener('pointermove', move); block.addEventListener('pointerup', up); block.addEventListener('pointercancel', cancel);
    };
    track.append(block);
    return block;
  });

  root.setHead = (fraction) => {
    if (fraction === null || fraction === undefined) { head.hidden = true; return; }
    head.hidden = false; head.style.left = `${Math.max(0, Math.min(1, fraction)) * total * px}px`;
  };
  layout();
  if (typeof ResizeObserver === 'function') new ResizeObserver(() => layout()).observe(root);
  return root;
}
