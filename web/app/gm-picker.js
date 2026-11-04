// The General MIDI instrument picker: a dark list under the program field (instead of the browser's white datalist popup). Typing filters it by number or name,
// every row has a play/stop button: pressing it toggles, hovering a row plays it by itself (a short phrase) and leaving the row stops it, so you can run the mouse down
// the list and hear the instruments. A click on the row chooses it; Esc or a click elsewhere closes the list.
import { el } from './ui.js';
import { GM_PROGRAMS, parseGmProgram } from '../lib/gm-programs.mjs';

const HOVER_MS = 180, PHRASE_MS = 2600;

/** @param {HTMLInputElement} input  @param {{ hear:(program:number)=>unknown, stopHear:()=>void, pick:(program:number)=>void }} api */
export function attachGmPicker(input, { hear, stopHear, pick }) {
  let list = null, rows = [], active = -1, playing = -1, hoverTimer = 0, phraseTimer = 0;

  const setPlaying = (index) => {
    clearTimeout(phraseTimer);
    if (playing >= 0 && rows[playing]) { rows[playing].classList.remove('playing'); rows[playing].querySelector('.gm-play').textContent = '▶'; }
    playing = index;
    if (index >= 0 && rows[index]) {
      rows[index].classList.add('playing'); rows[index].querySelector('.gm-play').textContent = '■';
      phraseTimer = setTimeout(() => setPlaying(-1), PHRASE_MS);   // the phrase is over: the button is a play button again, press it to hear it once more
    }
  };
  const play = (index) => { clearTimeout(hoverTimer); setPlaying(index); hear(Number(rows[index].dataset.program)); };
  const stopAll = () => { clearTimeout(hoverTimer); setPlaying(-1); stopHear(); };
  const setActive = (index, andPlay) => {
    if (active >= 0 && rows[active]) rows[active].classList.remove('active');
    active = index;
    if (index >= 0 && rows[index]) { rows[index].classList.add('active'); rows[index].scrollIntoView({ block: 'nearest' }); if (andPlay) { clearTimeout(hoverTimer); hoverTimer = setTimeout(() => play(index), HOVER_MS); } }
  };
  const visible = () => rows.filter((row) => !row.hidden);

  function filter() {
    const q = input.value.trim().toLowerCase();
    rows.forEach((row) => { row.hidden = !!q && !(row.dataset.program.startsWith(q) || row.querySelector('.gm-name').textContent.toLowerCase().includes(q)); });
  }
  function place() {
    const r = input.getBoundingClientRect(), height = Math.min(320, window.innerHeight - 24);
    const below = window.innerHeight - r.bottom, up = r.top > below && below < height + 8;
    list.style.left = `${Math.max(8, Math.min(r.left, window.innerWidth - 276))}px`;
    list.style.maxHeight = `${Math.max(120, Math.min(height, (up ? r.top : below) - 12))}px`;
    if (up) { list.style.top = ''; list.style.bottom = `${window.innerHeight - r.top + 4}px`; } else { list.style.bottom = ''; list.style.top = `${r.bottom + 4}px`; }
  }
  function open() {
    if (list) return;
    list = el('div', { className: 'gm-picker', role: 'listbox', 'aria-label': 'General MIDI instruments' });
    rows = GM_PROGRAMS.map((name, program) => {
      const row = el('div', { className: 'gm-row', role: 'option', 'data-program': program }, [
        el('span', { className: 'gm-num', textContent: String(program) }), el('span', { className: 'gm-name', textContent: name }),
        el('button', { type: 'button', className: 'gm-play', textContent: '▶', title: 'Hear it (hover a row and it plays by itself)', 'aria-label': `Hear ${name}` }),
      ]);
      row.addEventListener('mouseenter', () => { setActive(rows.indexOf(row), true); });
      row.addEventListener('mouseleave', () => { if (playing === rows.indexOf(row) || hoverTimer) stopAll(); });
      row.addEventListener('mousedown', (event) => event.preventDefault());   // the field keeps its focus
      row.querySelector('.gm-play').addEventListener('click', (event) => { event.stopPropagation(); const index = rows.indexOf(row); if (playing === index) stopAll(); else play(index); });
      row.addEventListener('click', () => choose(Number(row.dataset.program)));
      return row;
    });
    list.append(...rows);
    document.body.append(list);
    place();
    const now = parseGmProgram(input.value);
    if (now !== null) { setActive(now, false); rows[now]?.scrollIntoView({ block: 'center' }); }
    addEventListener('resize', close, { once: true });
    document.addEventListener('pointerdown', outside, true);
  }
  function close() {
    if (!list) return;
    stopAll();
    document.removeEventListener('pointerdown', outside, true);
    list.remove(); list = null; rows = []; active = -1; playing = -1;
  }
  const outside = (event) => { if (list && !list.contains(event.target) && event.target !== input) close(); };
  function choose(program) { close(); pick(program); }

  input.addEventListener('focus', open);
  input.addEventListener('click', open);
  input.addEventListener('input', () => { if (!list) open(); filter(); setActive(-1); });
  input.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && list) { event.preventDefault(); event.stopPropagation(); close(); return; }
    if (!list && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) open();
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const shown = visible(); if (!shown.length) return;
      const at = shown.indexOf(rows[active]), next = shown[Math.max(0, Math.min(shown.length - 1, at + (event.key === 'ArrowDown' ? 1 : -1)))];
      setActive(rows.indexOf(next), true);
    } else if (event.key === 'Enter' && list && active >= 0) { event.preventDefault(); choose(Number(rows[active].dataset.program)); }
  });
  input.addEventListener('blur', () => setTimeout(() => { if (list && document.activeElement !== input) close(); }, 120));
  return { close };
}
