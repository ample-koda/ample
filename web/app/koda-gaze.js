// What Koda looks at, drawn as neon lines from Koda to the thing: drag from Koda with Ctrl held and let go on a block or a line of code, and Koda looks there (the block or line is selected, the line stays while that
// holds and fades when it does not). While Koda thinks, thin dotted lines glance at the blocks the model reads and fade by themselves. The lines hang like cords and sway; they glow with the beat when music plays.
// Everything is a decoration over the page that never takes a click. @ample:koda
import { S, flat, notifySelection, select } from './store.js';
import { chainText, regionChain } from '../lib/region-chain.mjs';
import { arrangementSubject, barSubject, blockSubject, branchSubject, codeSubject, fieldSubject, fileSubject, groupSubject, multiSubject, partSubject, portSubject, revisionSubject, songSubject, templateSubject, typeSubject } from '../lib/koda-context.mjs';
import { newStrand, nearestOnRect, stepStrand, strandPath } from '../lib/koda-strand.mjs';

const NS = 'http://www.w3.org/2000/svg';
let svg = null, hooks = { head: () => null, beat: () => 0 }, raf = 0, prev = 0;
const strands = new Set();
const reduced = () => { try { return matchMedia('(prefers-reduced-motion: reduce)').matches; } catch { return false; } };

export function initGaze(next) {
  hooks = { ...hooks, ...next };
  if (svg) return;
  svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('class', 'koda-gaze');
  svg.setAttribute('aria-hidden', 'true');
  document.body.append(svg);
}

const make = (kind, to, life = 0, alive = null) => {
  const g = document.createElementNS(NS, 'g');
  g.setAttribute('class', `gz gz-${kind}`);
  const glow = document.createElementNS(NS, 'path'); glow.setAttribute('class', 'gz-glow');
  const line = document.createElementNS(NS, 'path'); line.setAttribute('class', 'gz-line');
  const end = document.createElementNS(NS, 'circle'); end.setAttribute('class', 'gz-end'); end.setAttribute('r', kind === 'glance' ? '3' : '5');
  g.append(glow, line, end);
  svg.append(g);
  const a = hooks.head();
  const b = a ? to(a) : null;
  const s = { kind, g, glow, line, end, to, life, age: 0, alpha: 0, dying: false, away: false, alive, long: life > 3000, cord: newStrand(a ?? { x: 0, y: 0 }, b ?? a ?? { x: 0, y: 0 }) };
  strands.add(s);
  wake();
  return s;
};
const drop = (s) => { s.g.remove(); strands.delete(s); };
const fade = (s) => { s.dying = true; wake(); };

function frame(now) {
  raf = 0;
  const dt = Math.min(64, prev ? now - prev : 16);
  prev = now;
  const pulse = hooks.beat();
  svg.style.setProperty('--gz-pulse', pulse.toFixed(3));
  for (const s of [...strands]) {
    const a = hooks.head();
    const b = a ? s.to(a) : null;
    // the end is gone for good (nothing selected any more): the line fades away; the end is only out of sight (scrolled away, Koda hidden): the line waits, invisible, and comes back with it
    if (s.alive && !s.alive()) s.dying = true;
    s.away = !a || !b;
    s.age += dt;
    if (s.kind === 'glance') { s.life -= dt; if (s.life <= 0) s.dying = true; }
    // the lines to what an answer talks about stay longer, a bit dimmer, and fade out slowly
    const top = s.long ? 0.7 : 1;
    s.alpha = s.dying || s.away ? s.alpha - dt / (s.long ? 1600 : s.kind === 'glance' ? 500 : 450) : Math.min(top, s.alpha + dt / 220);
    if (s.alpha <= 0 && s.dying) { drop(s); continue; }
    if (a && b) {
      if (reduced()) { s.cord.x = (a.x + b.x) / 2; s.cord.y = (a.y + b.y) / 2; } else stepStrand(s.cord, a, b, dt, s.kind === 'glance' ? 0.12 : 0.2);
      const d = strandPath(a, b, s.cord);
      s.glow.setAttribute('d', d); s.line.setAttribute('d', d);
      s.end.setAttribute('cx', b.x); s.end.setAttribute('cy', b.y);
    }
    s.g.style.opacity = String(Math.max(0, Math.min(1, s.alpha)));
  }
  if (strands.size) raf = requestAnimationFrame(frame); else prev = 0;
}
function wake() { if (!raf && svg && !document.hidden) raf = requestAnimationFrame(frame); }

/* ---------- where the lines end ---------- */
const nodeEl = (id) => { try { return document.querySelector(`.node[data-id="${CSS.escape(id)}"]`); } catch { return null; } };
const inView = (r) => r.width > 0 && r.height > 0 && r.right > 0 && r.bottom > 0 && r.left < innerWidth && r.top < innerHeight;
const esc = (s) => CSS.escape(String(s));
/** An element scrolled out of the panel it sits in (a row of the file list that went under the header) is not shown, although its box is still on the screen. */
function clipped(el, r) {
  for (let p = el.parentElement; p && p !== document.body && p !== document.documentElement; p = p.parentElement) {
    const o = getComputedStyle(p);
    if (o.overflowX === 'visible' && o.overflowY === 'visible') continue;
    const c = p.getBoundingClientRect();
    if (c.width > 0 && c.height > 0 && (r.bottom <= c.top || r.top >= c.bottom || r.right <= c.left || r.left >= c.right)) return true;
  }
  return false;
}

/** Where an element is, as the end of a line coming from the point `from` (null when it is not shown). */
export function elementPoint(selector, from) {
  let el = null;
  try { el = document.querySelector(selector); } catch { /* bad selector */ }
  if (!el || !el.isConnected) return null;
  const r = el.getBoundingClientRect();
  return inView(r) && !clipped(el, r) ? nearestOnRect(r, from) : null;
}
/** Where a block is, as the end of a line coming from the point `from` (null when the block is not shown). */
export const blockPoint = (id, from) => elementPoint(`.node[data-id="${esc(id)}"]`, from);

const editor = () => document.querySelector('#se-ta');
// the source editor is CodeMirror with a hidden text area that mirrors it: the picture is the box and the scroller, the text is the text area
const editorBox = () => document.querySelector('#se-cm') ?? editor();
const editorScroll = () => document.querySelector('#se-cm .cm-scroller')?.scrollTop ?? editor()?.scrollTop ?? 0;
const editorShown = () => { const b = editorBox(); return !!b && b.getClientRects().length > 0; };
const lineMetrics = (ta) => { const cs = getComputedStyle(ta); return { lh: parseFloat(cs.lineHeight) || 18, pad: parseFloat(cs.paddingTop) || 0 }; };
/** Where a line of the source editor is (the end of the line of code, null when it is scrolled out of sight). */
export function linePoint(line) {
  const ta = editor();
  if (!ta || !editorShown()) return null;
  const r = editorBox().getBoundingClientRect();
  const { lh, pad } = lineMetrics(ta);
  const y = r.top + pad + (line + 0.5) * lh - editorScroll();
  if (y < r.top + 4 || y > r.bottom - 4) return null;
  return { x: r.left + 52, y };
}

/** The windows whose parts Koda can be linked to: the sequencer and AMPLE Jam (in both of its forms). */
const AREAS = [['.mls-panel', 'AMPLE Jam'], ['.sqmain, [id^="sq-"], .sqtransport', 'Sequencer']];
const PARTS = 'button, input, select, textarea, label, [title], [aria-label], .sqchannel, .sqtrack, .sqsection, .sqmarker, .sqlane, .sqplaylist, .sqpatterns, .sqruler, .sqrack, .sqsections, .mls-tool, .mls-ruler, .mls-keys, .mls-velocity, .mls-strip, .mls-toolbar, .mls-status, .mls-corner';
let partCounter = 0;
const CONTAINERS = { sqpatterns: 'the patterns', sqsections: 'the sections', sqplaylist: 'the playlist', sqlanes: 'the lanes', sqrack: 'the channels', sqtrack: 'a track of the playlist', sqruler: 'the bar numbers', sqmarkers: 'the markers', sqdetails: 'the details' };
const shortText = (n) => String(n?.textContent ?? '').replace(/\s+/g, ' ').trim().slice(0, 60);

/** A part of the sequencer or of AMPLE Jam under the pointer, or null. The part gets a mark of its own so the neon line can find it again. */
function partAt(e) {
  const area = AREAS.find(([selector]) => e.closest?.(selector));
  if (!area) return null;
  const root = e.closest(area[0]);
  const part = e.closest(PARTS);
  if (!part || !root.contains(part)) return null;
  if (!part.dataset.kodaId) part.dataset.kodaId = `p${++partCounter}`;
  const aria = part.getAttribute('aria-label') || '';
  let own = (aria.length > 40 ? aria.split(/[.:] /)[0] : aria) || (part.matches('input, select, textarea') ? (part.labels?.[0] ? shortText(part.labels[0]) : '') : shortText(part)) || part.getAttribute('title') || part.className.toString().split(' ')[0] || part.tagName.toLowerCase();
  const channel = part.closest('.sqchannel');
  const context = channel ? (channel.querySelector('.seqname, .sqname, input')?.value || shortText(channel.querySelector('b, .sqgmname'))) : '';
  // a box with many children (the patterns, the playlist...) is named for what it is, not by all the words in it
  const boxName = [...part.classList].map((c) => CONTAINERS[c]).find(Boolean) || (part.children.length > 6 && shortText(part).length >= 50 ? 'a group of controls' : '');
  if (boxName) own = boxName;
  return { kind: 'part', area: area[1], block: root.dataset?.sequencer || root.dataset?.block || '', pattern: root.dataset?.pattern || '', label: own, hint: part.getAttribute('title') || (aria.length > 40 ? aria : ''), value: part.matches('input, select') ? (part.type === 'checkbox' ? (part.checked ? 'on' : 'off') : part.value) : '', context, selector: `[data-koda-id="${part.dataset.kodaId}"]` };
}

const FILE_ROW = '.file-list-row[data-path], .project-tile[data-path], tr[data-path]';

/** What is under a point of the screen: a block, a field of the properties, a block of the library, a template card, a file or folder, or a line of the source editor. */
export function targetAt(x, y) {
  for (const e of document.elementsFromPoint(x, y)) {
    if (e.closest?.('.koda')) continue;
    // the history, the branches and the arrangement: a revision (dot of the graph), a branch (row of the branch manager), a bar on a track, the ruler of the arrangement
    const rev = e.closest?.('g[data-node]');
    if (rev) {
      const m = /^Revision (\S+): (.*) \((.*)\)$/.exec(rev.getAttribute('aria-label') ?? '');
      return { kind: 'revision', id: rev.dataset.node, message: m?.[2] ?? '', line: m?.[3] ?? '', at: rev.querySelector('title')?.textContent?.match(/\d{4}-\d\d-\d\d[^·]*/)?.[0]?.trim() ?? '', ref: rev.querySelector('title')?.textContent?.match(/· (AR-\d+)/)?.[1] ?? '', selector: `g[data-node="${esc(rev.dataset.node)}"]` };
    }
    const branch = e.closest?.('.rev[data-line]');
    if (branch) return { kind: 'branch', name: branch.dataset.line, current: /●/.test(branch.textContent ?? ''), studio: branch.dataset.line === 'studio', selector: `.rev[data-line="${esc(branch.dataset.line)}"]` };
    const bar = e.closest?.('#arr .bar[data-block]');
    if (bar) {
      const m = /: bar (\d+) for (\d+)(?: \((.*)\))?$/.exec(bar.getAttribute('title') ?? '');
      return { kind: 'bar', block: bar.dataset.block, at: Number(m?.[1]) || 1, length: Number(m?.[2]) || 1, name: m?.[3] ?? '', selector: `#arr .bar[data-block="${esc(bar.dataset.block)}"][data-part="${esc(bar.dataset.part ?? '0')}"]` };
    }
    const plClip = e.closest?.('#b-playlist .pl-clip[data-key]');
    if (plClip) {
      const m = /^(.*?)(?: · (.*))?: bars (\d+) to (\d+)/.exec(plClip.getAttribute('title') ?? '');
      const block = plClip.dataset.key.slice(0, plClip.dataset.key.lastIndexOf(':'));
      const at = Number(m?.[3]) || 1, to = Number(m?.[4]) || at;
      return { kind: 'bar', block, at, length: to - at + 1, name: m?.[2] ?? '', selector: `#b-playlist .pl-clip[data-key="${esc(plClip.dataset.key)}"]` };
    }
    const plRuler = e.closest?.('#b-playlist .pl-ruler');
    if (plRuler) {
      const bw = parseFloat(plRuler.style.backgroundSize) || 0;
      return { kind: 'arrangement', bar: bw ? Math.floor((x - plRuler.getBoundingClientRect().left) / bw) + 1 : 0, selector: '#b-playlist .pl-ruler' };
    }
    const ruler = e.closest?.('#arr .ruler');
    if (ruler) {
      const bw = Number(document.getElementById('arr')?.dataset.barWidth) || 0;
      return { kind: 'arrangement', bar: bw ? Math.floor((x - ruler.getBoundingClientRect().left) / bw) + 1 : 0, selector: '#arr .ruler' };
    }
    if (e.closest?.('#crumbs .c[data-koda-song]')) return { kind: 'song', path: S.path || '', name: S.doc?.project?.name ?? S.project?.name ?? '', selector: '#crumbs .c[data-koda-song]' };
    if (e.closest?.('#se-title[data-koda-song]')) return { kind: 'song', path: S.path || '', name: S.doc?.project?.name ?? S.project?.name ?? '', selector: '#se-title[data-koda-song]' };
    const port = e.closest?.('.port[data-node]');
    if (port) {
      const n = flat().nodes.find((x) => x.id === port.dataset.node);
      const pt = n && (port.dataset.dir === 'in' ? n.ins : n.outs)[+port.dataset.idx];
      if (pt) {
        const spec = (S.tpl[n.tpl]?.[port.dataset.dir === 'in' ? 'inputs' : 'outputs'] || []).find((p) => p.name === pt[0]);
        const chain = pt[1] === 'region' ? chainText(regionChain(S.doc?.blocks, n.id, port.dataset.dir === 'out' && !!(S.doc?.blocks || []).find((b) => b.name === n.id)?.froms?.some((f) => f.input === 'region_in'))) : '';
        return { kind: 'port', id: `${n.id}.${port.dataset.dir}.${pt[0]}`, block: n.id, dir: port.dataset.dir, name: pt[0], type: pt[1], title: spec?.title || '', description: spec?.description || '', chain, selector: `.port[data-node="${esc(n.id)}"][data-dir="${port.dataset.dir}"][data-idx="${port.dataset.idx}"]` };
      }
    }
    const node = e.closest?.('.node[data-id]');
    if (node) return { kind: 'block', id: node.dataset.id };
    const prop = e.closest?.('.prop[data-koda-param]');
    if (prop) return { kind: 'field', block: prop.dataset.kodaBlock, param: prop.dataset.kodaParam, selector: `.prop[data-koda-block="${esc(prop.dataset.kodaBlock)}"][data-koda-param="${esc(prop.dataset.kodaParam)}"]` };
    const item = e.closest?.('.tplitem[data-tpl]');
    if (item) return { kind: 'type', id: item.dataset.tpl, selector: `.tplitem[data-tpl="${esc(item.dataset.tpl)}"]` };
    const card = e.closest?.('.tpl-card[data-id]');
    if (card) return { kind: 'template', selector: `.tpl-card[data-id="${esc(card.dataset.id)}"]`, name: card.querySelector('.tpl-head b')?.textContent ?? '', say: card.querySelector('.tpl-say')?.textContent ?? '', meta: card.querySelector('.tpl-meta')?.textContent ?? '', project: !!card.querySelector('.tpl-x') };
    const tab = e.closest?.('#tabbar .tab[data-path], #tabs-popup .tabs-row[data-path]');   // a tab of the tab row, or a row of its list
    if (tab) return { kind: 'tab', path: tab.dataset.path, selector: `${tab.classList.contains('tab') ? '#tabbar .tab' : '#tabs-popup .tabs-row'}[data-path="${esc(tab.dataset.path)}"]` };
    const file = e.closest?.(FILE_ROW);
    if (file) return { kind: 'file', path: file.dataset.path, isDir: file.classList.contains('file-list-folder'), selector: `[data-path="${esc(file.dataset.path)}"]` };
    const part = partAt(e);
    if (part) return part;
    if (e.closest?.('.sewrap')) {
      const ta = editor();
      if (!ta || !editorShown()) return null;
      const r = editorBox().getBoundingClientRect();
      const { lh, pad } = lineMetrics(ta);
      const line = Math.floor((y - r.top - pad + editorScroll()) / lh);
      const lines = ta.value.split('\n');
      if (line < 0 || line >= lines.length) return null;
      // a selection in the editor (one line or several) that the pointer is on is the thing pointed at, as a whole, and it is kept as it is
      if (ta.selectionStart !== ta.selectionEnd) {
        const v = ta.value, from = ta.selectionStart, to = ta.selectionEnd;
        const startLine = v.slice(0, from).split('\n').length - 1, endLine = v.slice(0, to - (v[to - 1] === '\n' ? 1 : 0)).split('\n').length - 1;
        if (line >= startLine && line <= endLine) return { kind: 'selection', id: from + '-' + to, from, to, startLine, endLine, line };
      }
      return { kind: 'line', line };
    }
  }
  return null;
}

/* ---------- the line while aiming, the line that stays, the glances ---------- */
let aim = null, aimAt = { x: 0, y: 0 };
/** What Koda looks at: one thing, or several (Ctrl+Shift and drag adds one). Each: { key, subject, strand }. */
let looks = [];
const MAX_LOOKS = 16;
export const targetKey = (t) => (t ? [t.kind, t.id ?? t.selector ?? t.line ?? t.path ?? t.block ?? ''].join(':') : '');

export function aimMove(x, y) {
  aimAt = { x, y };
  if (!aim || !strands.has(aim)) aim = make('aim', () => aimAt);
}
export function aimEnd() { if (aim) fade(aim); aim = null; }
export function clearLook() { for (const l of looks) fade(l.strand); looks = []; }
const liveLooks = () => { looks = looks.filter((l) => strands.has(l.strand) && !l.strand.dying); return looks; };
/** What Koda looks at now (a subject; several things together are one subject of the kind multi), or null when the lines have faded. */
export function lookSubject() {
  const on = liveLooks().filter((l) => !l.strand.away);
  if (on.length === 0) return null;
  return on.length === 1 ? on[0].subject : multiSubject(on.map((l) => l.subject));
}
export const looksCount = () => liveLooks().length;
/** Lets go of the thing Koda looked at last (its line fades). Returns how many it still looks at. */
export function letGoNewest() {
  const list = liveLooks();
  const last = list[list.length - 1];
  if (last) { fade(last.strand); looks = looks.filter((x) => x !== last); }
  return liveLooks().length;
}
/** Whether Koda already looks at this target (letting go on it again lets go of it). */
export const isLinked = (target) => liveLooks().some((l) => l.key === targetKey(target));
/** Lets go of one thing Koda looks at (its line fades). Returns whether it was linked. */
export function unlook(target) {
  const l = liveLooks().find((x) => x.key === targetKey(target));
  if (!l) return false;
  fade(l.strand);
  looks = looks.filter((x) => x !== l);
  return true;
}

/** Koda looks at a target from targetAt: the subject that goes with it is made, a block or a line is selected so the question is about it, and a neon line stays to it while it holds. Returns the subject, or null.
 *  With add, the thing is added to what Koda already looks at (nothing is selected then, and the earlier lines stay until they are let go). */
export function lookAt(target, add = false) {
  if (!add) clearLook();
  if (!target) return null;
  let subject = null, valid = () => false, point = () => null;
  if (target.kind === 'block') {
    const node = flat().nodes.find((n) => n.id === target.id);
    const group = !node && S.groups[target.id];
    if ((!node && !group) || node?.proxy) return null;
    if (!add) { select(target.id); notifySelection(); }
    subject = group ? groupSubject({ id: group.id, title: group.title, members: group.members.map((m) => ({ block: m, type: flat().nodes.find((n) => n.id === m)?.tpl ?? '' })) }) : blockSubject(node, flat(), S.tpl);
    valid = add ? () => true : () => S.sel === target.id; point = (from) => blockPoint(target.id, from);
  } else if (target.kind === 'line') {
    const ta = editor();
    if (!ta || !editorShown()) return null;
    const lines = ta.value.split('\n');
    let from = 0;
    for (let i = 0; i < target.line; i++) from += lines[i].length + 1;
    const to = from + lines[target.line].length;
    if (to <= from) return null;
    if (!add) ta.focus(), ta.setSelectionRange(from, to);
    subject = codeSubject(ta.value, from, to);
    valid = add ? () => true : () => ta.selectionStart === from && ta.selectionEnd === to; point = () => linePoint(target.line);
  } else if (target.kind === 'selection') {
    const ta = editor();
    if (!ta || !editorShown() || target.to > ta.value.length) return null;
    if (!add) { ta.focus(); ta.setSelectionRange(target.from, target.to); }
    subject = codeSubject(ta.value, target.from, target.to);
    valid = add ? () => true : () => ta.selectionStart === target.from && ta.selectionEnd === target.to;
    point = () => linePoint(Math.round((target.startLine + target.endLine) / 2));
  } else {
    if (target.kind === 'field') {
      const node = flat().nodes.find((n) => n.id === target.block);
      if (!node) return null;
      subject = fieldSubject(node, target.param, S.tpl);
    } else if (target.kind === 'type') {
      if (!S.tpl[target.id]) return null;
      subject = typeSubject({ id: target.id, ...S.tpl[target.id] });
    } else if (target.kind === 'template') subject = templateSubject(target);
    else if (target.kind === 'file') subject = fileSubject(target.path, target.isDir);
    else if (target.kind === 'tab') subject = { kind: 'tab', path: target.path };
    else if (target.kind === 'revision') subject = revisionSubject(target);
    else if (target.kind === 'branch') subject = branchSubject(target);
    else if (target.kind === 'bar') subject = barSubject(target);
    else if (target.kind === 'song') subject = songSubject(target);
    else if (target.kind === 'port') subject = portSubject(target);
    else if (target.kind === 'part') subject = partSubject(target);
    else if (target.kind === 'arrangement') subject = arrangementSubject(target);
    else return null;
    const selected = S.sel;   // choosing another block ends the look at a field, a library block, a template or a file
    valid = add ? () => true : () => S.sel === selected; point = (from) => elementPoint(target.selector, from);
  }
  if (add) {
    if (liveLooks().length >= MAX_LOOKS) return null;
    for (const l of looks) l.strand.alive = null;   // together they stay until each is let go
  }
  const strand = make('look', point, 0, valid);
  strand.subject = subject;
  looks.push({ key: targetKey(target), subject, strand });
  return subject;
}

/** Short dotted lines to the blocks the model is reading, one after the other; they fade by themselves. */
export function glance(ids, life = 1700) {
  if (!svg) return;
  ids.slice(0, 5).forEach((id, i) => setTimeout(() => { if (nodeEl(id)) make('glance', (from) => blockPoint(id, from), life); }, i * 260));
}

export function clearGaze() { aimEnd(); clearLook(); for (const s of strands) fade(s); }
