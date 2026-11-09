// The bubbles of Koda: menu, thought cloud, choice, answer, draft, short note, warning and error. Every bubble has a close button; the text of the project (block names, answers) is always set as text, never as HTML.
// The words of Koda itself come from koda-lang (English or Dutch). A builder returns the element; the layer places it. @ample:koda
import { el } from './ui.js';
import { kodaAttachField } from './attach.js';
import { tx } from './koda-lang.js';
import { groupChanges } from '../lib/koda-diff.mjs';

/** Text with `code` and **bold** in it as DOM (never HTML): the parts between backticks become code elements, the parts between double stars bold ones. */
const bold = (text) => text.split('**').map((part, i) => (i % 2 && part ? el('b', { textContent: part }) : part));
const rich = (text) => String(text ?? '').split('`').flatMap((part, i) => (i % 2 ? [el('code', { textContent: part })] : bold(part)));
const close = (onClose) => el('button', { type: 'button', className: 'x', textContent: '×', title: tx('closeTitle'), 'aria-label': tx('close'), onclick: (e) => { e.stopPropagation(); onClose?.(); } });
const shell = (kind, children, onClose, label = '') => el('div', { className: `bub k-${kind} pop`, role: kind === 'error' || kind === 'warn' ? 'alert' : 'status', 'aria-label': label || undefined }, [...children, close(onClose)]);
const tag = (text) => el('span', { className: 'tag', textContent: text });
const buttons = (items, onPick) => el('div', { className: 'row' }, items.map((b) => el('button', { type: 'button', className: b.primary ? 'go' : b.quiet ? 'no' : '', textContent: b.label, ...(b.title ? { title: b.title } : {}), onclick: (e) => { e.stopPropagation(); onPick(b.value ?? b.label); } })));

/** A small book next to the tag of an answer that AMPLE gave from its own documentation: hover, focus or a press shows where it comes from, with a link to the documentation. */
function kbMark({ text, link, href }) {
  const pop = el('div', { className: 'kb-pop', role: 'tooltip', hidden: true }, [el('span', { textContent: text }), el('a', { href, target: '_blank', rel: 'noopener', textContent: `${link} ↗`, onclick: (e) => e.stopPropagation() })]);
  const button = el('button', { type: 'button', className: 'kb', title: text, 'aria-label': text, 'aria-expanded': 'false' });
  button.innerHTML = '<svg viewBox="0 0 24 24" width="17" height="17" aria-hidden="true"><path d="M12 7C10.3 5.4 7.6 4.8 3.5 4.8v12.4c4.1 0 6.8.6 8.5 2.2 1.7-1.6 4.4-2.2 8.5-2.2V4.8C16.4 4.8 13.7 5.4 12 7zM12 7v12.4" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round" stroke-linecap="round"/><path d="M17.6 1.6l.6 1.5 1.5.6-1.5.6-.6 1.5-.6-1.5-1.5-.6 1.5-.6z" fill="currentColor"/></svg>';
  const wrap = el('span', { className: 'kbwrap' }, [button, pop]);
  const set = (on) => { pop.hidden = !on; button.setAttribute('aria-expanded', String(on)); };
  let pinned = false;
  button.addEventListener('click', (e) => { e.stopPropagation(); pinned = !pinned; set(pinned); });   // a press (touch) keeps it open
  wrap.addEventListener('pointerenter', (e) => { if (e.pointerType === 'mouse') set(true); });
  wrap.addEventListener('pointerleave', (e) => { if (e.pointerType === 'mouse' && !pinned) set(false); });
  button.addEventListener('focus', () => set(true));
  button.addEventListener('blur', () => { if (!pinned) set(false); });
  return wrap;
}

/** The menu: what Koda looks at, the two things you can ask, hide and mute (the language is set in the project settings). */
export function menuBubble({ subject, onExplain, onDo, onHide, onMute, muted = false }, onClose) {
  const has = !!subject;
  return shell('menu', [
    tag(tx('menu.tag')),
    el('p', { className: 'dim' }, has ? [`${tx('menu.looking')} `, el('b', { textContent: subject }), '.'] : [tx('menu.none')]),
    buttons([{ label: tx('menu.explain'), value: 'explain', primary: true }, { label: tx('menu.do'), value: 'do' }], (v) => (v === 'explain' ? onExplain() : onDo())),
    el('div', { className: 'row small' }, [
      el('button', { type: 'button', className: 'no', textContent: tx('menu.hide'), onclick: (e) => { e.stopPropagation(); onHide?.(); } }),
      el('button', { type: 'button', className: 'no', textContent: muted ? tx('menu.unmute') : tx('menu.mute'), onclick: (e) => { e.stopPropagation(); onMute?.(); } }),
    ]),
  ], onClose);
}

/** Puts the caret in a field at once on a computer (on a phone the keyboard would jump up: there the field is tapped). */
const focusSoon = (input) => { try { if (!window.matchMedia('(pointer: coarse)').matches) setTimeout(() => input.focus({ preventScroll: true }), 30); } catch { /* no matchMedia */ } };

/** The thought cloud: Koda is working. controls: { steer(text), listen(), stop() } adds a field to give extra input, "stop and listen" and a hard stop. */
export function thoughtBubble(text, onClose, work = null, controls = null) {
  const b = shell('thought', [el('span', { className: 'dots' }, [el('span', { className: 'tline', textContent: text }), el('span'), el('span'), el('span')]), ...(work && work.full === work.short ? [el('p', { className: 'twork flat', textContent: tx('thought.work', { x: work.short }) })] : []), ...(work && work.full !== work.short ? [el('details', { className: 'twork' }, [el('summary', {}, [el('span', { className: 'wk', textContent: tx('thought.work', { x: work.short }) }), el('b', { className: 'chev', 'aria-hidden': 'true', textContent: '▾' })]), el('p', { textContent: work.full })])] : []), ...(controls ? [steerControls(controls)] : []), el('i', { className: 't1' }), el('i', { className: 't2' })], onClose);
  b.setAttribute('role', 'status');
  return b;
}

function steerControls({ steer, listen, stop }) {
  const input = el('input', { placeholder: tx('thought.steer.placeholder'), 'aria-label': tx('thought.steer.placeholder') });
  const send = () => { const typed = input.value.trim(); if (!typed || !steer) return; input.value = ''; steer(typed); };
  input.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); send(); } e.stopPropagation(); };
  const quiet = (label, fn, extra = '') => el('button', { type: 'button', className: 'no' + extra, textContent: label, onclick: (e) => { e.stopPropagation(); fn(); } });
  return el('div', { className: 'tctl' }, [
    ...(steer ? [el('div', { className: 'other' }, [input, ...kodaAttachField(input), el('button', { type: 'button', className: 'go', textContent: tx('thought.steer.send'), onclick: (e) => { e.stopPropagation(); send(); } })])] : []),
    el('div', { className: 'trow' }, [quiet(tx('thought.listen'), listen), quiet(tx('thought.stop'), stop)]),
  ]);
}

/** The choice bubble: numbered options, a free field, and a button for it. onPick(value) with the option value or the typed text. */
export function choiceBubble({ title, text, options = [], placeholder, other = true, submit }, onPick, onClose) {
  const hint = placeholder ?? tx('choice.other');
  const list = el('div', { className: 'opts' }, options.map((o, i) => el('button', { type: 'button', onclick: (e) => { e.stopPropagation(); onPick(o.value ?? o.label); } }, [el('kbd', { textContent: String(i + 1) }), o.label])));
  const input = el('input', { placeholder: hint, 'aria-label': hint });
  const send = () => { const typed = input.value.trim(); if (typed) onPick(typed); };
  input.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); send(); } };
  focusSoon(input);   // on a computer the person types at once
  return shell('choice', [tag(title || tx('choice.title')), ...(text ? [el('p', { textContent: text })] : []), ...(options.length ? [list] : []), ...(other ? [el('div', { className: 'other' }, [input, ...kodaAttachField(input), el('button', { type: 'button', className: 'go', textContent: submit ?? tx('choice.ok'), onclick: (e) => { e.stopPropagation(); send(); } })])] : [])], onClose);
}

/** An answer: a title, a short text, an optional longer one to unfold, and buttons (the values are 'thanks' and 'more' for the usual two). */
/**
 * The unfolded part of an answer as structure: paragraphs, headings (# ... or a line that is only **bold**), bullet and numbered lists, and markdown tables
 * (a header row, a --- row, rows with | between the cells) as real tables. Never HTML: everything is built as elements.
 */
export function blocks(text) {
  const out = [];
  const cells = (l) => l.trim().slice(1, -1).split('|').map((c) => c.trim());
  const isRow = (l) => { const t = l.trim(); return t.startsWith('|') && t.endsWith('|') && t.length > 2; };
  const divider = (l) => cells(l).every((c) => /^:?-{2,}:?$/.test(c));
  const lines = String(text ?? '').replace(/\r/g, '').split('\n');
  let para = [];
  const flush = () => { if (para.length) out.push(el('p', {}, rich(para.join(' ').trim()))); para = []; };
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i], t = line.trim();
    if (!t) { flush(); continue; }
    const heading = /^#{1,4}\s+(.+)$/.exec(t) ?? /^\*\*([^*]+)\*\*:?$/.exec(t);
    if (heading) { flush(); out.push(el('h4', {}, rich(heading[1].replace(/\*\*/g, '')))); continue; }
    if (isRow(t)) {
      flush();
      const rows = [];
      while (i < lines.length && isRow(lines[i])) { rows.push(lines[i]); i++; }
      i--;
      const body = rows.filter((r, k) => !(k === 1 && divider(r))).map(cells);
      const [head, ...rest] = body;
      out.push(el('div', { className: 'tbl' }, [el('table', {}, [el('thead', {}, [el('tr', {}, head.map((c) => el('th', {}, rich(c))))]), el('tbody', {}, rest.map((r) => el('tr', {}, r.map((c) => el('td', {}, rich(c))))))])]));
      continue;
    }
    const bullet = /^[-*\u2022]\s+(.+)$/.exec(t), numbered = /^\d+[.)]\s+(.+)$/.exec(t);
    if (bullet || numbered) {
      flush();
      const items = [];
      const re = bullet ? /^[-*\u2022]\s+(.+)$/ : /^\d+[.)]\s+(.+)$/;
      while (i < lines.length && re.test(lines[i].trim())) { items.push(re.exec(lines[i].trim())[1]); i++; }
      i--;
      out.push(el(bullet ? 'ul' : 'ol', {}, items.map((x) => el('li', {}, rich(x)))));
      continue;
    }
    para.push(t);
  }
  flush();
  return out;
}

export function answerBubble({ title, text, more = '', source = null, label = '', buttons: items, onAsk = null, askOpen = false }, onPick, onClose) {
  const list = items ?? [{ label: tx('answer.thanks'), value: 'thanks', primary: true }, { label: tx('answer.more'), value: 'more' }, ...(onAsk ? [{ label: tx('answer.ask'), value: 'ask' }] : [])];
  // "Ask a question": a field opens under the buttons, so what the answer made you think of can be asked at once (onAsk(typed))
  const hint = tx('answer.askHint');
  const input = el('input', { placeholder: hint, 'aria-label': hint });
  const send = () => { const typed = input.value.trim(); if (typed) onAsk(typed); };
  input.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); send(); } };
  const row = el('div', { className: 'other', hidden: true }, [input, ...kodaAttachField(input), el('button', { type: 'button', className: 'go', textContent: tx('do.submit'), onclick: (e) => { e.stopPropagation(); send(); } })]);
  const pick = (v) => { if (v === 'ask' && onAsk) { row.hidden = !row.hidden; if (!row.hidden) focusSoon(input); } else onPick(v); };
  if (askOpen && onAsk) { row.hidden = false; focusSoon(input); }
  // title and answer, and the details that unfold, sit in a scroll area of a limited height; the buttons stay in sight; the grip resizes (the bubble keeps hanging on Koda)
  const node = shell('answer', [
    el('div', { className: 'taghead' }, [tag(label || tx('answer.tag')), ...(source ? [kbMark(source)] : [])]),
    el('div', { className: 'scroll' }, [...(title ? [el('h3', {}, rich(title))] : []), el('p', {}, rich(text)),
      ...(more ? [el('details', {}, [el('summary', { textContent: tx('answer.unfold') }), ...blocks(more)])] : [])]),
    buttons(list, pick), row,
  ], onClose);
  addGrip(node);
  return node;
}

/* ---------- the size of an answer bubble: the user's own, kept ---------- */
const SIZE_KEY = 'ample.koda.bubble-size';
let size = (() => { try { const v = JSON.parse(localStorage.getItem(SIZE_KEY) || 'null'); return v && v.w > 0 && v.h > 0 ? v : null; } catch { return null; } })();
const applySize = (node) => { if (size) { node.style.setProperty('--bw', `${size.w}px`); node.style.setProperty('--bmax', `${size.h}px`); } };
function addGrip(node) {
  applySize(node);
  const grip = el('div', { className: 'grip', title: tx('answer.resize'), 'aria-label': tx('answer.resize'), role: 'separator' });
  grip.style.touchAction = 'none';
  grip.addEventListener('pointerdown', (e) => {
    e.preventDefault(); e.stopPropagation();
    grip.setPointerCapture(e.pointerId);
    const scroll = node.querySelector('.scroll');
    const start = { y: e.clientY, h: scroll ? scroll.offsetHeight : 160 };
    node.dataset.gripLock = '1';
    const left = node.classList.contains('grip-left');   // the grip sits at the bottom right (left when the bubble is against the right edge); the bubble stays hanging on Koda, so its width is followed until the grip is under the pointer
    let target = e, frame = 0, frac = 0.3, prev = null;   // frac: how much of a width change the grip moves (the bubble hangs on Koda: it grows mostly to the other side); learned while dragging
    const step = () => {
      frame = 0;
      const g = grip.getBoundingClientRect();
      const cx = g.left + g.width / 2;
      const error = left ? cx - target.clientX : target.clientX - cx;
      if (prev && Math.abs(node.offsetWidth - prev.w) > 0.5) frac = Math.max(0.15, Math.min(1, ((cx - prev.cx) / (node.offsetWidth - prev.w)) * (left ? -1 : 1)));
      prev = { w: node.offsetWidth, cx };
      const w = Math.max(260, Math.min(node.offsetWidth + error / frac, 760, innerWidth - 16));
      const h = Math.max(80, Math.min(start.h + (target.clientY - start.y), innerHeight * 0.8));
      size = { w: Math.round(w), h: Math.round(h) };
      applySize(node);
      if (Math.abs(error) > 1.5) frame = requestAnimationFrame(step);
    };
    const move = (ev) => { target = ev; if (!frame) frame = requestAnimationFrame(step); };
    const end = () => { delete node.dataset.gripLock; cancelAnimationFrame(frame); grip.removeEventListener('pointermove', move); grip.removeEventListener('pointerup', end); grip.removeEventListener('pointercancel', end); try { localStorage.setItem(SIZE_KEY, JSON.stringify(size)); } catch { /* storage may be blocked */ } };
    grip.addEventListener('pointermove', move); grip.addEventListener('pointerup', end); grip.addEventListener('pointercancel', end);
  });
  node.append(grip);
}

/** A switch (on/off box) with a label for assistive technology; onChange(on). */
const toggle = (label, on, onChange) => {
  const box = el('input', { type: 'checkbox', role: 'switch', checked: on, 'aria-label': label, onchange: () => onChange(box.checked) });
  return el('label', { className: 'sw2', title: label }, [box, el('span', { className: 'track' }, [el('i')])]);
};

/** The badge "optional": hover, focus or a press opens the explanation under the row (a popover over the bubble). */
function optionalBadge() {
  const pop = el('span', { className: 'opt-pop', role: 'tooltip', hidden: true, textContent: tx('draft.optionalNote') });
  const badge = el('button', { type: 'button', className: 'opt', textContent: tx('draft.optional'), 'aria-expanded': 'false' });
  let pinned = false;
  const set = (on) => {
    if (on) { const bub = badge.closest('.bub'); if (bub) pop.style.top = Math.round(badge.getBoundingClientRect().bottom - bub.getBoundingClientRect().top + 6) + 'px'; }
    pop.hidden = !on; badge.setAttribute('aria-expanded', String(on));
  };
  badge.addEventListener('click', (e) => { e.stopPropagation(); pinned = !pinned; set(pinned); });
  badge.addEventListener('pointerenter', (e) => { if (e.pointerType === 'mouse') set(true); });
  badge.addEventListener('pointerleave', (e) => { if (e.pointerType === 'mouse' && !pinned) set(false); });
  badge.addEventListener('focus', () => set(true));
  badge.addEventListener('blur', () => { if (!pinned) set(false); });
  return [badge, pop];
}

/**
 * The neon button that starts a patch of several changes. It has its own look on purpose and takes nothing from the theme: dark, a rim that turns through the colours of the logo, the teal note of the logo, an
 * equalizer and notes that rise on hover. onHover(on) is for the audio that may play on hover.
 */
function letsGo(summary, groups, onClick, onHover) {
  const note = (x, d, c) => el('span', { className: 'pt', style: `--x:${x}px;--d:${d}s`, 'aria-hidden': 'true', textContent: c });
  const button = el('button', { type: 'button', className: 'letsgo', onclick: (e) => { e.stopPropagation(); button.classList.add('busy'); button.setAttribute('aria-busy', 'true'); onClick(); } }, [
    el('span', { className: 'n', 'aria-hidden': 'true', textContent: '♪' }),
    el('span', { className: 'l' }, [tx('draft.go'), el('small', { textContent: tx('draft.goSub', { n: summary.length, m: groups.length }) })]),
    el('span', { className: 'eq', 'aria-hidden': 'true' }, [1, 2, 3, 4, 5].map(() => el('i'))),
    el('span', { className: 'sh', 'aria-hidden': 'true' }), note(-30, 0, '♪'), note(10, 0.25, '♫'), note(60, 0.5, '♪'), note(110, 0.15, '✦'), note(150, 0.4, '♫'),
  ]);
  button.addEventListener('pointerenter', (e) => { if (e.pointerType === 'mouse') onHover?.(true); });
  button.addEventListener('pointerleave', () => onHover?.(false));
  return button;
}

/**
 * A draft: what Koda wants to change, grouped per block (a switch per change and one per block, what is optional marked), and what you can do with it (the values are keep, preview, again and no).
 * Up to three blocks are shown together; with more the groups are pages (an overview first) to go through with the arrows, the dots, a drag or the arrow keys. onFocus(block|null) says which block is
 * being looked at (a page, or the pointer over a group), and onHear(on) that the pointer is on something that may be heard.
 */
export function draftBubble({ summary, note, checked = false, canPreview = false, onToggle, onFocus = null, onHear = null, more = '', diff = '', applyNow = false }, onPick, onClose) {
  const mark = { add: '+', link: '→', change: '~', remove: '−' };
  const switches = summary.some((c) => c.hunk !== undefined) && onToggle;
  const groups = groupChanges(summary);
  const root = el('div', { className: 'chgs' });
  const sync = (hunk, on) => {
    root.querySelectorAll('li[data-hunk="' + hunk + '"]').forEach((other) => { other.classList.toggle('off', !on); const box = other.querySelector('input'); if (box) box.checked = on; });
    root.querySelectorAll('.grp').forEach((g) => { const all = g.querySelector('.gh input'); if (all) all.checked = [...g.querySelectorAll('li input')].some((b) => b.checked); });
  };
  const row = (c) => {
    const li = el('li', { className: c.type === 'link' ? 'link' : 'add', 'data-hunk': c.hunk ?? '' }, [el('b', { textContent: mark[c.type] ?? '+' }), el('span', { className: 'what' }, [c.text, ...(c.optional ? optionalBadge() : [])])]);
    if (switches && c.hunk !== undefined) li.append(toggle(tx('draft.switch', { x: c.text }), true, (on) => { sync(c.hunk, on); onToggle(c.hunk, on); }));
    return li;
  };
  const grouped = groups.length > 1 || (groups[0] && groups[0].block);
  const groupEl = (g) => {
    const list = el('ul', { className: 'chg' }, g.items.map(row));
    if (!grouped) return list;
    const name = g.block || tx('draft.song');
    const head = el('div', { className: 'gh' }, [el('i', { className: 'dot' }), el('span', { className: 't', textContent: name }), el('span', { className: 'kind', textContent: tx('draft.kind.' + g.kind) })]);
    const hunks = [...new Set(g.items.map((c) => c.hunk).filter((h) => h !== undefined))];
    if (switches && hunks.length > 1) head.append(toggle(tx('draft.switchAll', { x: name }), true, (on) => hunks.forEach((h) => { sync(h, on); onToggle(h, on); })));
    const box = el('section', { className: 'grp ' + g.kind, 'data-block': g.block }, [head, list]);
    box.addEventListener('pointerenter', (e) => { if (e.pointerType === 'mouse') { onFocus?.(g.block || null); onHear?.(true); } });
    box.addEventListener('pointerleave', () => { onFocus?.(null); onHear?.(false); });
    return box;
  };
  let body;
  if (groups.length > 3) {
    // pages: an overview of the blocks, then one page per block
    let at = 0;
    const pages = [el('div', { className: 'pg overview' }, groups.map((g, k) => el('button', { type: 'button', className: 'card ' + g.kind, onclick: (e) => { e.stopPropagation(); go(k + 1); } }, [el('i', { className: 'dot' }), el('span', { className: 't', textContent: g.block || tx('draft.song') }), el('span', { className: 'kind', textContent: g.items.length + ' · ' + tx('draft.kind.' + g.kind) })]))), ...groups.map((g) => el('div', { className: 'pg' }, [groupEl(g)]))];
    const track = el('div', { className: 'track' }, pages);
    const dots = el('div', { className: 'dots2', role: 'tablist' }, pages.map((_, k) => el('button', { type: 'button', role: 'tab', className: 'pd ' + (k ? groups[k - 1].kind : 'overview'), 'aria-label': k ? groups[k - 1].block || tx('draft.song') : tx('draft.overview'), onclick: (e) => { e.stopPropagation(); go(k); } })));
    const prev = el('button', { type: 'button', className: 'arr', 'aria-label': tx('draft.prev'), textContent: '‹', onclick: (e) => { e.stopPropagation(); go(at - 1); } });
    const next = el('button', { type: 'button', className: 'arr', 'aria-label': tx('draft.next'), textContent: '›', onclick: (e) => { e.stopPropagation(); go(at + 1); } });
    const count = el('span', { className: 'count' });
    const view = el('div', { className: 'viewport', tabIndex: 0, role: 'group', 'aria-roledescription': 'carousel' }, [track]);
    function go(k) {
      at = Math.max(0, Math.min(pages.length - 1, k));
      track.style.transform = 'translateX(' + -100 * at + '%)';
      pages.forEach((p, n) => { p.inert = n !== at; });
      [...dots.children].forEach((d, n) => d.setAttribute('aria-selected', String(n === at)));
      prev.disabled = at === 0; next.disabled = at === pages.length - 1;
      count.textContent = at ? at + ' / ' + groups.length + ' · ' + (groups[at - 1].block || tx('draft.song')) : tx('draft.overview');
      onFocus?.(at ? groups[at - 1].block || null : null);
    }
    view.addEventListener('keydown', (e) => { if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') { e.preventDefault(); e.stopPropagation(); go(at + (e.key === 'ArrowRight' ? 1 : -1)); } });
    // a drag along the page (touch, pen or mouse) turns it; a short one springs back
    let x0 = null, moved = 0;
    view.addEventListener('pointerdown', (e) => { if (e.target.closest('button, input, label, a, .opt-pop')) return; x0 = e.clientX; moved = 0; });
    view.addEventListener('pointermove', (e) => { if (x0 === null) return; moved = e.clientX - x0; if (Math.abs(moved) > 6) { view.setPointerCapture?.(e.pointerId); track.style.transition = 'none'; track.style.transform = 'translateX(calc(' + -100 * at + '% + ' + moved + 'px))'; } });
    const release = () => { if (x0 === null) return; const d = moved; x0 = null; track.style.transition = ''; go(Math.abs(d) > 50 ? at + (d < 0 ? 1 : -1) : at); };
    view.addEventListener('pointerup', release); view.addEventListener('pointercancel', release);
    body = el('div', { className: 'pager' }, [view, el('div', { className: 'pbar' }, [prev, el('div', { className: 'mid' }, [count, dots]), next])]);
    go(0);
  } else body = el('div', { className: 'stack' }, groups.map(groupEl));
  root.append(body);
  const patch = summary.length > 1;
  // details (what the model explained and the change itself) open in a near fullscreen sheet, where the change can also be applied at once
  const sheet = () => {
    const done = (v) => { overlay.remove(); if (v) onPick(v); };
    const overlay = el('div', { className: 'koda-details', role: 'dialog', 'aria-label': tx('draft.details') }, [
      el('div', { className: 'kd-bar' }, [el('b', { textContent: tx('draft.details') }), el('span', { className: 'kd-fill' }), el('button', { type: 'button', className: 'go', textContent: tx('draft.applyNow'), onclick: () => done('keep') }), el('button', { type: 'button', textContent: tx('draft.close'), onclick: () => done('') })]),
      el('div', { className: 'kd-body' }, [...blocks(more), ...(diff ? [el('h4', { textContent: tx('draft.diff') }), el('pre', { textContent: diff })] : [])]),
    ]);
    overlay.addEventListener('keydown', (e) => { if (e.key === 'Escape') { e.stopPropagation(); done(''); } });
    document.body.append(overlay);
    overlay.querySelector('.go').focus();
  };
  const rest = [...(more || diff ? [{ label: tx('draft.details'), value: 'details', quiet: true }] : []), ...(canPreview ? [{ label: tx('draft.preview'), value: 'preview', quiet: true }] : []), { label: tx('draft.again'), value: 'again' }, { label: tx('draft.no'), value: 'no', quiet: true }];
  return shell('draft', [
    tag(tx('draft.tag')), el('h3', { textContent: grouped ? tx('draft.titleBlocks', { n: groups.length }) : tx('draft.title') }),
    ...(grouped ? [el('p', { className: 'sum', textContent: tx('draft.sum', { n: summary.length, m: groups.length }) })] : []), root,
    el('p', { className: 'dim', style: 'margin-top:6px' }, rich(note ?? tx('draft.note'))),
    ...(checked ? [el('p', { className: 'chk' }, [el('b', { textContent: '✓' }), tx('draft.checked')])] : []),
    el('div', { className: 'row' }, [patch ? letsGo(summary, groups, () => onPick('keep'), onHear) : el('button', { type: 'button', className: 'go', textContent: applyNow ? tx('draft.applyNow') : tx('draft.keep'), onclick: (e) => { e.stopPropagation(); onPick('keep'); } }), ...buttons(rest, (v) => (v === 'details' ? sheet() : onPick(v))).children]),
  ], onClose);
}

/** A short note, a warning or an error with buttons. */
export function noteBubble({ kind = 'toast', label = '', text, buttons: items = [] }, onPick, onClose) {
  return shell(kind, [...(label ? [tag(label)] : []), el('p', {}, rich(text)), ...(items.length ? [buttons(items, onPick)] : [])], onClose);
}

/**
 * Variants of a block: tabs A, B, C (and the original to compare with), each with what it changes and a listen button.
 * items: [{ note, lines: [text] }]; onHear(index | 'original' | null) plays that one (null stops); onPick('use', index) | ('again') | ('no').
 */
export function variantsBubble({ items, canOriginal = false, onHear, onAsk = null, said = '' }, onPick, onClose) {
  let at = 0;
  const body = el('div', { className: 'vbody', role: 'tabpanel' });
  const tabs = el('div', { className: 'vtabs', role: 'tablist' }, items.map((_, i) => el('button', { type: 'button', role: 'tab', className: 'vt', title: items[i].name || '', textContent: String.fromCharCode(65 + i) + (items[i].name ? ' · ' + items[i].name : ''), onclick: (e) => { e.stopPropagation(); show(i); } })));
  const listen = el('button', { type: 'button', className: 'vlisten', onclick: (e) => { e.stopPropagation(); onHear(at); } });
  const orig = canOriginal ? el('button', { type: 'button', className: 'vlisten orig', textContent: '▶ ' + tx('variants.original'), onclick: (e) => { e.stopPropagation(); onHear('original'); } }) : null;
  function show(i) {
    at = i;
    [...tabs.children].forEach((t, k) => t.setAttribute('aria-selected', String(k === i)));
    const it = items[i];
    body.replaceChildren(...(it.note ? [el('p', {}, rich(it.note))] : []), el('ul', { className: 'chg' }, it.lines.map((l) => el('li', {}, rich(l)))));
    listen.textContent = '▶ ' + tx('variants.listen', { x: String.fromCharCode(65 + i) });
  }
  show(0);
  // a question about the variants is answered in the bubble itself, so the variants stay to choose from
  const answer = el('p', { className: 'vans', hidden: !said }, said ? rich(said) : []);
  const input = el('input', { placeholder: tx('variants.askHint'), 'aria-label': tx('variants.askHint') });
  const send = async () => {
    const typed = input.value.trim();
    if (!typed || !onAsk) return;
    input.value = ''; answer.hidden = false; answer.textContent = '…';
    try { answer.replaceChildren(...rich(await onAsk(typed, at, (line) => { answer.textContent = '… ' + line; }))); } catch (error) { answer.textContent = error?.message || tx('failed.text'); }
  };
  input.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); send(); } };
  const row = el('div', { className: 'other', hidden: true }, [input, ...kodaAttachField(input), el('button', { type: 'button', className: 'go', textContent: tx('do.submit'), onclick: (e) => { e.stopPropagation(); send(); } })]);
  const choices = [{ label: tx('variants.use'), value: 'use', primary: true }, { label: tx('variants.again'), value: 'again' }, ...(onAsk ? [{ label: tx('answer.ask'), value: 'ask' }] : []), { label: tx('draft.no'), value: 'no', quiet: true }];
  return shell('draft', [tag(tx('variants.tag', { n: items.length })), el('div', { className: 'chgs vars' }, [tabs, body, el('div', { className: 'vplay' }, [listen, ...(orig ? [orig] : [])])]), answer,
    buttons(choices, (v) => { if (v === 'ask') { row.hidden = !row.hidden; if (!row.hidden) input.focus(); return; } onHear(null); onPick(v, at); }), row], onClose);
}
