// The branch manager: the whole hierarchy of lines as a tree over the canvas, with search, fold, focus, go, look only,
// drop (dangerous act) and bring back. @ample:branch-manager

import { $, activatable, el, showMenu, toast } from './ui.js';
import { api } from './api.js';
import { S, enterPeek, loadHistory } from './store.js';
import { registerView } from './views.js';
import { goToNode, goToLine, dropLine, forkFrom, compareDialog, tagNode } from './history.js';
import { histModel, lineChildren, lineParent, shortId } from './history-model.js';

const B = { q: '', folded: new Set(), archived: false, focus: null, refocus: null };

async function bringBack(name) {
  try { await api.post('/api/history/restore-line', { line: name, reason: 'brought back in the branch manager' }); await loadHistory(); toast(`${name} is back.`); } catch (x) { toast(x.message); }
}

function layout(m) {
  const pos = new Map();
  let row = 0;
  const walk = (name, depth) => {
    pos.set(name, { x: 20 + depth * 260, y: 20 + row * 130 });
    row++;
    if (B.folded.has(name)) return;
    lineChildren(m, name, B.archived).forEach((k) => walk(k, depth + 1));
  };
  m.lines.filter((l) => (B.archived || !l.archived) && lineParent(m, l.name) === null).forEach((l) => walk(l.name, 0));
  return pos;
}

export function renderBranchManager() {
  const root = $('#bm');
  const m = histModel(S.history);
  if (!m) return;
  if (!B.focus || !m.lines.some((l) => l.name === B.focus)) B.focus = m.h.current;
  root.innerHTML = '';
  const bar = el('div', { className: 'bmbar' });
  const q = el('input', { id: 'bm-search', placeholder: 'Search branches and revisions...', value: B.q, style: 'width:240px' });
  q.oninput = () => { B.q = q.value; renderBranchManager(); const i = $('#bm-search'); if (i) { i.focus(); i.setSelectionRange(B.q.length, B.q.length); } };
  const arch = Object.assign(el('input', { type: 'checkbox', checked: B.archived, style: 'width:auto', id: 'bm-archived' }), { onchange: (e) => { B.archived = e.target.checked; renderBranchManager(); } });
  bar.append(q, el('label', {}, [arch, ' show dropped']), el('button', { id: 'bm-foldall', textContent: 'Fold all', onclick: () => { m.lines.forEach((l) => { if (lineChildren(m, l.name, B.archived).length) B.folded.add(l.name); }); renderBranchManager(); } }), el('button', { id: 'bm-unfoldall', textContent: 'Unfold all', onclick: () => { B.folded.clear(); renderBranchManager(); } }), el('button', { id: 'bm-newleaf', textContent: `+ New branch under ${B.focus}`, onclick: () => forkFrom(m.headOf(B.focus), false) }), el('span', { className: 'note', style: 'margin:0', textContent: 'Click a box to focus it; double-click goes to its tip.' }));
  const scroll = el('div', { className: 'bmscroll', id: 'bmscroll' });
  const stage = el('div', { className: 'bmstage' });
  const pos = layout(m);
  const w = Math.max(...[...pos.values()].map((p) => p.x)) + 280;
  const h = Math.max(...[...pos.values()].map((p) => p.y)) + 150;
  stage.style.width = `${w}px`;
  stage.style.height = `${h}px`;
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('width', w); svg.setAttribute('height', h);
  const onPath = new Set();
  for (let l = B.focus; l; l = lineParent(m, l)) onPath.add(l);
  const needle = B.q.toLowerCase();
  const hit = (l) => needle && (l.name.toLowerCase().includes(needle) || (m.byId.get(l.head)?.message || '').toLowerCase().includes(needle));
  m.lines.filter((l) => pos.has(l.name)).forEach((l) => {
    const p = lineParent(m, l.name);
    if (!p || !pos.has(p)) return;
    const a = pos.get(p), b = pos.get(l.name);
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', `M${a.x + 220} ${a.y + 56} C ${a.x + 240} ${a.y + 56} ${b.x - 20} ${b.y + 56} ${b.x} ${b.y + 56}`);
    path.setAttribute('fill', 'none');
    path.setAttribute('stroke', onPath.has(l.name) ? 'var(--acc)' : 'var(--line)');
    path.setAttribute('stroke-width', onPath.has(l.name) ? 3 : 2);
    svg.append(path);
  });
  stage.append(svg);
  m.lines.filter((l) => pos.has(l.name)).forEach((l) => {
    const p = pos.get(l.name);
    const tip = m.byId.get(l.head);
    const box = el('div', { className: `bmbox${l.name === B.focus ? ' focus' : ''}${onPath.has(l.name) ? ' onpath' : ''}${l.name === 'studio' ? ' studio' : ''}${l.archived ? ' arch' : ''}${needle && !hit(l) ? ' dimmed' : ''}${hit(l) ? ' hit' : ''}` });
    box.dataset.line = l.name;
    box.style.left = `${p.x}px`;
    box.style.top = `${p.y}px`;
    const kids = lineChildren(m, l.name, B.archived);
    const title = el('div', { className: 't' }, [kids.length ? activatable(el('span', { className: 'bm-fold', textContent: B.folded.has(l.name) ? '▸' : '▾', style: 'cursor:pointer;color:var(--acc)', title: 'Fold or unfold', 'aria-expanded': String(!B.folded.has(l.name)), onclick: (e) => { e.stopPropagation(); B.refocus = `fold:${l.name}`; if (B.folded.has(l.name)) B.folded.delete(l.name); else B.folded.add(l.name); renderBranchManager(); } }), `${B.folded.has(l.name) ? 'Unfold' : 'Fold'} ${l.name}`) : '', el('b', { textContent: `${l.name === 'studio' ? '★ ' : ''}${l.name === m.h.current ? '● ' : ''}${l.name}` }), l.archived ? el('span', { className: 'tag', textContent: 'dropped' }) : '']);
    const dots = el('div', { className: 'dots' });
    m.h.nodes.filter((n) => m.lineOf.get(n.nodeId) === l.name).slice(-8).forEach((n) => dots.append(el('i', { title: `${shortId(n.nodeId)} ${n.message}`, className: n.nodeId === m.hereId ? 'here' : '' })));
    const act = el('div', { className: 'row' });
    act.append(el('button', { textContent: 'Go', title: 'Go to the tip of this branch', onclick: (e) => { e.stopPropagation(); goToLine(l.name); } }), el('button', { textContent: 'Look only', title: 'Look at the tip of this branch without changing anything', onclick: (e) => { e.stopPropagation(); enterPeek(l.head); } }));
    if (l.archived) act.append(el('button', { textContent: 'Bring back', onclick: (e) => { e.stopPropagation(); bringBack(l.name); } }));
    else if (l.name !== 'studio') act.append(el('button', { textContent: 'Drop...', onclick: (e) => { e.stopPropagation(); dropLine(l.name); } }));
    box.append(title, el('div', { className: 'note', style: 'margin:0', textContent: tip ? tip.message : '' }), dots, act);
    box.onclick = () => { B.focus = l.name; B.refocus = `box:${l.name}`; renderBranchManager(); };
    box.ondblclick = () => goToLine(l.name);
    activatable(box, `Branch ${l.name}: ${tip ? tip.message : ''}`);
    box.addEventListener('keydown', (e) => {
      if (e.target !== box || !(e.key === 'ContextMenu' || (e.key === 'F10' && e.shiftKey))) return;
      e.preventDefault();
      const r = box.getBoundingClientRect();
      box.oncontextmenu({ preventDefault() {}, stopPropagation() {}, clientX: r.left + 20, clientY: r.top + 20 });
    });
    box.oncontextmenu = (e) => {
      e.preventDefault();
      e.stopPropagation();
      B.focus = l.name;
      showMenu(e.clientX, e.clientY, [
        { head: l.name },
        { label: 'Go to tip', fn: () => goToLine(l.name) },
        { label: 'Look only (read-only)', fn: () => enterPeek(l.head) },
        { label: 'Compare with current...', fn: () => compareDialog(l.head, m.h.current) },
        '-',
        { label: 'New branch from tip...', fn: () => forkFrom(l.head, false) },
        { label: 'Tag tip...', fn: () => tagNode(l.head) },
        ...(kids.length ? [{ label: B.folded.has(l.name) ? 'Unfold children' : 'Fold children', fn: () => { if (B.folded.has(l.name)) B.folded.delete(l.name); else B.folded.add(l.name); renderBranchManager(); } }] : []),
        ...(l.archived ? [{ label: 'Bring back', fn: () => bringBack(l.name) }] : l.name !== 'studio' ? [{ label: 'Drop...', danger: true, fn: () => dropLine(l.name) }] : []),
      ]);
    };
    stage.append(box);
  });
  scroll.append(stage);
  root.append(bar, scroll);
  const f = document.querySelector('.bmbox.focus');
  if (f && f.scrollIntoView) f.scrollIntoView({ block: 'nearest', inline: 'center' });
  if (B.refocus) {
    const [kind, name] = [B.refocus.slice(0, B.refocus.indexOf(':')), B.refocus.slice(B.refocus.indexOf(':') + 1)];
    B.refocus = null;
    const box = [...root.querySelectorAll('.bmbox')].find((x) => x.dataset.line === name);
    (kind === 'fold' ? box?.querySelector('.bm-fold') : box)?.focus();
  }
}

export function initBranchManager() { registerView('bm', async () => { await loadHistory(); renderBranchManager(); }); }
