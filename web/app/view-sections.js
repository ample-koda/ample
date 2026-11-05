// The sections of the song as bands above the ruler of the Arrangement (and later the Playlist): a click selects the bars, a right click renames, moves
// to the chosen bars, or removes. They live in the view block of the song (`section "Drop" at 33 for 32`), so they change no sound. @ample:view-sections
import { el, showMenu, ask, toast } from './ui.js';
import { S, readOnly, saveSections, sectionsOf, notify } from './store.js';
import { changedSection, freshName, sectionAt, withSection, withoutSection } from '../lib/view-sections.mjs';

const colours = ['#5b8def', '#e0833a', '#4fae8b', '#b565d9', '#d9a441', '#d95f7a'];

async function apply(result) {
  if (result.problem) { toast(result.problem); return; }
  await saveSections(result.sections);
}

async function askName(title, value) {
  const input = el('input', { type: 'text', value, 'aria-label': 'Name of the section', maxLength: 60 });
  const ok = await ask(title, 'The name shows above the ruler.', [{ label: 'Cancel', value: false }, { label: 'OK', value: true, primary: true }], [el('label', {}, [document.createTextNode('Name'), input])]);
  return ok ? input.value.trim() : null;
}

/** The menu for a section (index >= 0) or for the empty ruler (index -1); `bar` is the bar that was clicked. */
export function sectionMenu(event, bar, index) {
  const list = sectionsOf();
  const range = S.range;   // the bars chosen on the ruler, { firstBar, bars }
  const editable = !readOnly();
  const items = [];
  if (index >= 0) {
    items.push({ label: 'Rename section...', fn: async () => { const name = await askName('Rename section', list[index].name); if (name) await apply(changedSection(list, index, { name })); } });
    if (range) items.push({ label: `Set to bars ${range.firstBar} to ${range.firstBar + range.bars - 1}`, fn: () => apply(changedSection(list, index, { at: range.firstBar, length: range.bars })) });
    items.push({ label: 'Remove section', danger: true, fn: () => saveSections(withoutSection(list, index)) });
  } else {
    const at = range ? range.firstBar : bar, length = range ? range.bars : 8;
    items.push({ label: `New section at bars ${at} to ${at + length - 1}...`, fn: async () => { const name = await askName('New section', freshName(list)); if (name) await apply(withSection(list, { name, at, length })); } });
  }

  if (!editable) { toast('The song has errors: sections can be edited when they are fixed.'); return; }
  showMenu(event.clientX, event.clientY, items);
}

/** Draws the bands into a strip that lies over the ruler; `bw` is the width of a bar in pixels. */
export function drawSections(strip, bw) {
  strip.textContent = '';
  const list = sectionsOf();
  list.forEach((s, i) => {
    const band = el('div', { className: 'arr-section', title: `${s.name}: bars ${s.at} to ${s.at + s.length - 1}. Right click to rename, move or remove.`, role: 'button', tabIndex: 0, 'aria-label': `Section ${s.name}, bars ${s.at} to ${s.at + s.length - 1}`, textContent: s.name });
    band.style.left = `${(s.at - 1) * bw}px`;
    band.style.width = `${s.length * bw}px`;
    band.style.setProperty('--section', colours[i % colours.length]);
    band.onpointerdown = (e) => e.stopPropagation();   // the ruler would start choosing bars (and capture the pointer) under it
    band.onclick = (e) => { e.stopPropagation(); S.range = { firstBar: s.at, bars: s.length }; notify(); };
    band.oncontextmenu = (e) => { e.preventDefault(); e.stopPropagation(); sectionMenu(e, s.at, i); };
    band.onkeydown = (e) => { if (e.key === 'Enter' || e.key === ' ' || e.key === 'ContextMenu') { e.preventDefault(); const r = band.getBoundingClientRect(); sectionMenu({ clientX: r.left, clientY: r.bottom }, s.at, i); } };
    strip.append(band);
  });
}

export { sectionAt };
