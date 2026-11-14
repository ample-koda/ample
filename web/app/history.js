// History: the graph of revisions, lines, detail pane, branch view, header badges and the actions
// (checkpoint, go, look only, fork, tag, promote, drop, compare, cleanup). @ample:history @ample:branches @ample:compare

import { api } from './api.js';
import { $, activatable, ask, el, showMenu, toast } from './ui.js';
import { S, editHistory, enterEditPeek, enterPeek, goLine, leavePeek, loadHistory, notify, reloadDoc, restoreEditPeek, save, saveForCheckpoint, CheckpointDraftConflict, firstCheckpointDraftConflict, resolveCheckpointDraft } from './store.js';
import { dangerousAct } from './dangerous.js';
import { diffHunks, diffStats, lineDiff } from './diff.js';
import { listRecoveryDrafts, removeRecoveryDraft } from '../lib/recovery-drafts.mjs';
import { stillUnsaved, unsavedMessage, unsavedWork } from '../lib/unsaved-work.mjs';
import { compareRefs, fmtTime, histModel, lineChain, lineChildren, lineParent, shortId, visibleNodes } from './history-model.js';

const NS = 'http://www.w3.org/2000/svg';
const mk = (t, a) => { const e = document.createElementNS(NS, t); for (const k in a) e.setAttribute(k, a[k]); return e; };
let showArchived = false;
let branchSel = null;
let gitLayout = localStorage.getItem('ample.history.layout') === 'git';
let historySplit = Number(localStorage.getItem('ample.history.split')) || 50;
let historyMode = 'project', editHistorySeq = 0, selectedEditStep = -1, reduxEditHashes = new Map(), reduxEditSignature = '', reduxEditError = '';
let editPreviewTimer = 0, editRenderSeq = 0, focusAfter = null;

const model = () => histModel(S.history);
const peeking = () => !!S.peek;

/* ---------- header ---------- */
export function renderHistoryHeader() {
  import('./ai.js').then((m) => m.syncPeekBar()).catch(() => {});
  const m = model();
  const lb = $('#b-line');
  const ban = $('#banner');
  if (!m) { lb.textContent = S.project ? S.project.name : ''; return; }
  const line = S.peek ? S.peek.ref : m.h.current;
  lb.textContent = S.peek?.ai ? 'peek: AI proposal' : `${S.peek ? 'peek' : 'on'}: ${S.peek ? shortId(line) : line}`;
  lb.className = `badge ${S.peek ? 'peek' : line === 'studio' ? 'studio' : 'garage'}`;
  $('#b-studio').textContent = `★ studio = ${shortId(m.studioId)}`;
  ban.classList.toggle('show', peeking());
  if (peeking()) {
    if (S.peek.ai) {
      const task = S.peek.ai;
      ban.textContent = 'Read-only preview of the AI proposal, in the real views. Right-click a block: Build and listen. Nothing is saved or changed. (click to go back to the proposal)';
      ban.onclick = () => leavePeek().then(() => import('./ai.js').then((m) => m.openAiTask(task)));
    } else {
      ban.textContent = `Read-only: you are looking at ${shortId(S.peek.ref)}. Nothing can change here; nothing is built. (click to leave)`;
      ban.onclick = () => leavePeek().then(() => toast('Back where you were.'));
    }
  }
  ['btn-save', 'btn-cp', 'btn-promote', 'btn-fork', 'btn-build'].forEach((id) => { if (peeking() && !(S.peek.ai && id === 'btn-build')) $(`#${id}`).disabled = true; });
}

/* ---------- actions ---------- */
async function afterChange() { await Promise.all([loadHistory(), reloadDoc()]); }

/**
 * Everything that is not saved: every open tab (not only the song that has focus), the module that is open,
 * and the text recovery buffers of this browser.
 */
async function pendingWork() {
  const dirtyTabs = await (await import('./tabs.js')).dirtyTabPaths();
  if (S.doc?.dirty && S.path && !dirtyTabs.includes(S.path)) dirtyTabs.push(S.path);   // the open song, also when it has no tab
  if (S.moduleStack.length && S.moduleDoc?.dirty && S.modulePath && !dirtyTabs.includes(S.modulePath)) dirtyTabs.push(S.modulePath);
  let drafts = [];
  try { drafts = listRecoveryDrafts(localStorage, S.project?.id || ''); } catch { /* storage may be blocked */ }
  return unsavedWork({ dirtyTabs, drafts });
}

/**
 * Saves what is pending. True only when nothing is left unsaved afterwards;
 * otherwise it says what failed and the caller must not go on.
 */
async function saveEverything() {
  const failed = [];
  try {
    await (await import('./source.js')).saveRecoveryDraftsForCheckpoint();
    await saveForCheckpoint();
    for (const item of await pendingWork()) {
      // a tab that is changed on the server only (no browser copy): saved one by one, a failure is kept
      const route = /\.amplet$/i.test(item.path) ? '/api/doc/save' : '/api/wc/save';
      try { await api.post(route, { path: item.path, suppressCheckpoint: true }); } catch { failed.push(item.path); }
    }
    const left = stillUnsaved(await pendingWork(), failed);
    if (left.length) { toast(unsavedMessage(left, 'Nothing was switched.')); return false; }
    return true;
  } catch (e) {
    toast(e instanceof CheckpointDraftConflict ? e.message : `The save failed, so nothing was switched: ${e.message || e}`);
    return false;
  }
}

async function unsavedChoice() {
  if (S.peek) return true;
  const items = await pendingWork();
  if (!items.length) return true;
  const choices = [
    { label: 'Cancel', value: 'cancel' },
    { label: 'Discard them', value: 'discard' },
    { label: 'Save first', value: 'save', primary: true },
  ];
  const a = await ask('Unsaved changes', unsavedMessage(items), choices);
  if (!a || a === 'cancel') return false;
  if (a === 'save') return saveEverything();
  for (const item of items) {
    try { removeRecoveryDraft(localStorage, S.project?.id || '', item.path); } catch { /* storage may be blocked */ }
  }
  return true;
}

export async function checkpoint() {
  const name = el('input', { placeholder: 'name (empty = "checkpoint")' });
  const ok = await ask('Checkpoint', 'All recoverable source drafts and the current song or module are saved first. AMPLE then makes one checkpoint containing the saved work.', [{ label: 'Cancel', value: false }, { label: 'Save all work and checkpoint', value: true, primary: true }], [name]);
  if (!ok) return;
  // a recovery copy in this browser that differs from the file stops the checkpoint: the person chooses what happens to it, then the checkpoint goes on
  for (let round = 0; round < 20; round++) {
    try {
      // every conflict is found and decided before a single file is written: a cancel then changes nothing
      const found = await firstCheckpointDraftConflict();
      if (found) throw found;
      await (await import('./source.js')).saveRecoveryDraftsForCheckpoint();
      await saveForCheckpoint();
      const r = await api.post('/api/history/checkpoint', { message: name.value.trim() || 'checkpoint' });
      toast(r.created ? 'Checkpoint taken.' : 'Nothing changed since the last checkpoint.');
      return;
    } catch (e) {
      if (!(e instanceof CheckpointDraftConflict)) { toast(e.message); return; }
      const options = [{ label: 'Cancel', value: 'cancel' }, { label: 'Open the file', value: 'open' }, { label: 'Use the saved file', value: 'discard' }];
      if (!e.isModule) options.push({ label: 'Keep my browser copy', value: 'keep', primary: true });
      const choice = await ask('Checkpoint stopped', `${e.path} in this browser is not the same as the file on the server. Open it to look first, keep the copy of this browser (it is saved as the file), or use the saved file (the copy of this browser is thrown away).`, options);
      if (choice === 'open') { document.dispatchEvent(new CustomEvent(/\.amplet$/i.test(e.path) ? 'ample:open' : 'ample:open-file', { detail: e.path })); return; }
      if (choice !== 'keep' && choice !== 'discard') return;
      try { await resolveCheckpointDraft(e.path, choice); } catch (error) { toast(error.message); return; }
    }
  }
}

export async function goToNode(id) {
  const m = model();
  const line = m.lines.find((l) => l.head === id);
  if (!line) {
    if (await forkFrom(id, true)) toast('A new branch was made here and you are on it.');
    return;
  }
  await goToLine(line.name);
}

export async function goToLine(name) {
  if (!(await unsavedChoice())) return;
  try { await goLine(name); toast(`On ${name}.`); } catch (e) { toast(e.message); }
}

export async function forkFrom(id, andGo) {
  const name = el('input', { placeholder: 'branch name', value: `garage-${S.history.lines.length + 1}` });
  const ok = await ask('Fork a new branch', 'A branch is a path of revisions. The new one starts at this revision; nothing else changes.', [{ label: 'Cancel', value: false }, { label: 'Fork', value: true, primary: true }], [name]);
  if (!ok || !name.value.trim()) return false;
  if (!(await unsavedChoice())) return false;
  try {
    await api.post('/api/history/fork', { name: name.value.trim(), from: id });
    if (andGo) await goLine(name.value.trim()); else await loadHistory();
    return true;
  } catch (e) { toast(e.message); return false; }
}

export async function tagNode(id) {
  const name = el('input', { placeholder: 'tag name, for example v1.0' });
  const why = el('input', { placeholder: 'why (optional)' });
  const ok = await ask('Tag this revision', 'Tags are locked: moving or removing one is a dangerous act.', [{ label: 'Cancel', value: false }, { label: 'Tag', value: true, primary: true }], [name, why]);
  if (!ok || !name.value.trim()) return;
  try { await api.post('/api/history/tag', { name: name.value.trim(), ref: id, reason: why.value.trim() }); await loadHistory(); toast(`Tagged ${name.value.trim()}.`); } catch (e) { toast(e.message); }
}

export async function promote() {
  let changes = [];
  try { changes = await api.get('/api/history/promote-preview'); } catch (e) { toast(e.message); return; }
  const body = changes.length ? changes.map((c) => `• ${c.path}: ${c.change}`).join('\n') : 'garage and studio hold the same files.';
  const text = 'studio becomes what garage is now. The old studio stays in the history (and can be tagged), '
    + 'and work on studio that is not saved yet is put there first. No merge.';
  const choices = [{ label: 'Cancel', value: false }, { label: 'Make main', value: true, primary: true }];
  const ok = await ask('Make garage the main branch', `${text}\n\n${body}`, choices);
  if (!ok) return;
  // studio is replaced: when it is the open branch, its unsaved work is dealt with first, as before a branch switch
  if (model().h.current === 'studio' && !(await unsavedChoice())) return;
  try {
    await api.post('/api/history/promote');
    await afterChange();
    toast('studio is now what garage was.');
  } catch (e) { toast(e.message); }
}

export async function dropLine(name) {
  if (await dangerousAct('/api/acts/drop-line', { line: name })) { await loadHistory(); toast(`${name} branch was dropped (archived; the revisions stay).`); }
}

export async function cleanup() {
  if (await dangerousAct('/api/acts/cleanup', {})) { await loadHistory(); toast('History cleaned up.'); }
}

/* ---------- compare ---------- */
export async function compareDialog(refA, refB) {
  const m = model();
  const refs = compareRefs(m, refA, refB);
  const opt = (sel) => (v) => el('option', { value: v[0], textContent: v[1], selected: v[0] === sel });
  const a = el('select', { id: 'compare-a' }), b = el('select', { id: 'compare-b' });
  refs.forEach((v) => { a.append(opt(refA || 'studio')(v)); b.append(opt(refB || m.h.current)(v)); });
  const out = el('div', { className: 'compare-results' });
  let latest = 0;
  const run = async () => {
    const mine = ++latest;
    out.textContent = 'Reading...';
    try {
      const [fa, fb, changes] = await Promise.all([api.get(`/api/history/peek?ref=${encodeURIComponent(a.value)}`), api.get(`/api/history/peek?ref=${encodeURIComponent(b.value)}`), api.get(`/api/history/diff?from=${encodeURIComponent(a.value)}&to=${encodeURIComponent(b.value)}`)]);
      if (mine !== latest) return; // a newer choice replaced this one
      out.replaceChildren(el('div', { className: 'compare-count', textContent: `${changes.length} file${changes.length === 1 ? '' : 's'} changed` }));
      changes.forEach((change, index) => {
        const before = fa[change.path] || '', after = fb[change.path] || '';
        const binary = before.includes('\0') || after.includes('\0');
        const diff = binary ? [] : lineDiff(before, after);
        const stats = diffStats(diff);
        const file = el('details', { className: 'compare-file', open: index === 0, 'data-path': change.path });
        file.append(el('summary', {}, [el('span', { className: 'compare-path', textContent: change.path }), el('span', { className: 'compare-change', textContent: change.change }), el('span', { className: 'compare-stats', textContent: binary ? 'binary file' : `+${stats.added} −${stats.removed}` })]));
        const code = el('div', { className: 'compare-code' });
        const renderLines = () => {
          if (code.childNodes.length) return;
          if (binary) { code.append(el('div', { className: 'compare-note', textContent: 'Binary file: line diff unavailable.' })); return; }
          const hunks = diffHunks(diff, 3);
          if (!hunks.length) { code.append(el('div', { className: 'compare-note', textContent: 'No textual difference.' })); return; }
          hunks.forEach((hunk) => {
            code.append(el('div', { className: 'compare-hunk', textContent: `@@ -${hunk.oldStart},${hunk.oldCount} +${hunk.newStart},${hunk.newCount} @@` }));
            hunk.lines.forEach((line) => {
              code.append(el('div', { className: `compare-line ${line.op === '+' ? 'added' : line.op === '-' ? 'removed' : 'context'}` }, [el('span', { className: 'compare-number', textContent: line.oldNumber ?? '' }), el('span', { className: 'compare-number', textContent: line.newNumber ?? '' }), el('span', { className: 'compare-sign', textContent: line.op }), el('span', { className: 'compare-text', textContent: line.text })]));
              if (line.noNewline && line.op !== ' ') code.append(el('div', { className: 'compare-newline', textContent: '\\ No newline at end of file' }));
            });
          });
        };
        file.append(code);
        file.addEventListener('toggle', () => { if (file.open) renderLines(); });
        if (file.open) renderLines();
        out.append(file);
      });
    } catch (e) { if (mine === latest) out.textContent = e.message; }
  };
  a.onchange = run;
  b.onchange = run;
  const row = el('div', { className: 'compare-ref-row' }, [el('label', { htmlFor: 'compare-a', textContent: 'A (before)' }), a, el('label', { htmlFor: 'compare-b', textContent: 'B (after)' }), b]);
  run();
  const dialog = $('#dlg');
  dialog.classList.add('compare-dialog');
  try { await ask('Compare', 'Changes from A to B, grouped by file.', [{ label: 'Close', value: true, primary: true }], [row, out]); } finally { dialog.classList.remove('compare-dialog'); }
}

/* ---------- the graph ---------- */
// Keyboard on a revision: Enter/Space is the click, the menu key or Shift+F10 opens the same menu as a right-click.
function keyOnRevision(e, node, n, activate) {
  if (e.target !== node) return;
  if (activate && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); activate(); return; }
  if (e.key === 'ContextMenu' || (e.key === 'F10' && e.shiftKey)) {
    e.preventDefault();
    const r = node.getBoundingClientRect();
    S.selRev = n.nodeId; renderDetail();
    histMenu({ clientX: r.left + r.width / 2, clientY: r.top + r.height / 2 }, n);
  }
}

function histMenu(e, n) {
  const m = model();
  const line = m.lines.find((l) => l.head === n.nodeId);
  showMenu(e.clientX, e.clientY, [
    { head: `${shortId(n.nodeId)} ${n.message}` },
    { label: line ? `Go to ${line.name}` : 'Fork from here and go', fn: () => goToNode(n.nodeId) },
    { label: 'Look only (read-only)', fn: () => enterPeek(n.nodeId) },
    { label: 'Compare with here...', fn: () => compareDialog(n.nodeId, m.h.current) },
    { label: 'Fork a branch from here...', fn: () => forkFrom(n.nodeId, false) },
    { label: 'Tag...', fn: () => tagNode(n.nodeId) },
  ]);
}

export function renderHistory() {
  const m = model();
  const svg = $('#hist');
  if (!svg) return;
  const graphView = $('#history-graph-view');
  const gitView = $('#history-git-view');
  const layoutButton = $('#h-layout');
  graphView.hidden = gitLayout;
  gitView.hidden = !gitLayout;
  layoutButton.textContent = gitLayout ? 'Graph view' : 'List view';
  layoutButton.setAttribute('aria-pressed', String(gitLayout));
  svg.innerHTML = '';
  $('#h-adv').textContent = 'Advanced view';
  $('#h-adv').setAttribute('aria-pressed', String(!!S.adv));
  $('#h-hint').textContent = gitLayout ? 'commit list · newest first · click to select, double-click or Enter to go, right-click or the menu key for actions' : S.adv ? 'all revisions, archived branches, ids' : 'recent revisions and what matters (tips, studio, here, tags); "advanced" shows everything';
  if (!m) { gitView.replaceChildren(); return; }
  const laneH = 62, top = 56, x0 = 70, dx = 56;   // room above the dots too: the labels of every other revision sit above, so each one can be twice as wide
  const vis = visibleNodes(m, S.adv);
  const lanes = m.lines.filter((l) => (S.adv || !l.archived) && vis.some((n) => m.lineOf.get(n.nodeId) === l.name)).map((l) => l.name);
  const pos = new Map();
  vis.forEach((n, i) => { const li = lanes.indexOf(m.lineOf.get(n.nodeId)); if (li >= 0) pos.set(n.nodeId, { x: x0 + i * dx, y: top + li * laneH }); });
  const vw = x0 + vis.length * dx + 40, vh = top + 30 + lanes.length * laneH;
  svg.setAttribute('viewBox', `0 0 ${vw} ${vh}`);
  svg.style.height = `${vh}px`;
  svg.style.width = `${vw}px`;
  lanes.forEach((l, li) => { const t = mk('text', { x: 6, y: top + 4 + li * laneH, fill: l === 'studio' ? 'var(--studio)' : 'var(--dim)', 'font-size': 12 }); t.textContent = l; svg.append(t); });
  const visIds = new Set(vis.map((n) => n.nodeId));
  vis.forEach((n) => {
    const p = pos.get(n.nodeId);
    if (!p) return;
    let par = n.parentId ? m.byId.get(n.parentId) : null;
    let hidden = 0;
    while (par && !visIds.has(par.nodeId)) { par = par.parentId ? m.byId.get(par.parentId) : null; hidden++; }
    const pp = par && pos.get(par.nodeId);
    if (pp) svg.append(mk('path', { d: `M${pp.x} ${pp.y} C ${pp.x + 20} ${pp.y} ${p.x - 20} ${p.y} ${p.x} ${p.y}`, stroke: 'var(--line)', fill: 'none', 'stroke-width': 2, 'stroke-dasharray': hidden ? '4 4' : '' }));
  });
  const sel = S.selRev || m.hereId;
  vis.forEach((n, vi) => {
    const p = pos.get(n.nodeId);
    if (!p) return;
    const lineName = m.lineOf.get(n.nodeId);
    const arch = m.archived.has(lineName);
    const col = lineName === 'studio' ? 'var(--studio)' : 'var(--garage)';
    const g = mk('g', { style: 'cursor:pointer', 'data-node': n.nodeId });
    g.append(mk('circle', { cx: p.x, cy: p.y, r: 9, fill: arch ? 'var(--line)' : col, opacity: arch ? 0.6 : 1 }));
    if (n.nodeId === sel) g.append(mk('circle', { cx: p.x, cy: p.y, r: 12, fill: 'none', stroke: 'var(--acc)', 'stroke-width': 2, 'stroke-dasharray': '3 2' }));
    if (n.nodeId === m.hereId) g.append(mk('circle', { cx: p.x, cy: p.y, r: 15, fill: 'none', stroke: peeking() ? 'var(--warn)' : 'var(--text)', 'stroke-width': 2 }));
    if (n.nodeId === m.studioId) { const s = mk('text', { x: p.x - 6, y: p.y - 18, fill: 'var(--studio)', 'font-size': 14 }); s.textContent = '★'; g.append(s); }
    if (n.reduxRef) { const r = mk('text', { x: p.x + 10, y: p.y - 10, fill: 'var(--acc)', 'font-size': 9 }); r.textContent = n.reduxRef; g.append(r); }
    if (S.adv) { const s = mk('text', { x: p.x - 8, y: p.y - 14, fill: 'var(--dim)', 'font-size': 9 }); s.textContent = shortId(n.nodeId); g.append(s); }
    const labels = (m.tagsAt.get(n.nodeId) || []).map((t) => `tag ${t}`);
    // the labels alternate: one below the dot, the next above it, so a label has two columns of room instead of one
    const up = vi % 2 === 1;
    if (!labels.length) labels.push(n.message.length > 21 ? `${n.message.slice(0, 20)}…` : n.message);
    const upBase = p.y - (n.nodeId === m.studioId || S.adv ? 36 : 26);
    labels.forEach((t, i) => { const tx = mk('text', { x: p.x - 8, y: up ? upBase - i * 12 : p.y + 26 + i * 12, fill: 'var(--dim)', 'font-size': 10 }); tx.textContent = t; g.append(tx); });
    const tt = mk('title', {});
    tt.textContent = `${shortId(n.nodeId)} ${n.message} [${lineName}] ${fmtTime(n.at)}${n.reduxRef ? ` · ${n.reduxRef}` : ''}`;
    g.append(tt);
    g.onclick = () => { S.selRev = n.nodeId; renderHistory(); renderDetail(); };
    g.ondblclick = () => goToNode(n.nodeId);
    g.oncontextmenu = (e) => { e.preventDefault(); S.selRev = n.nodeId; renderDetail(); histMenu(e, n); };
    g.setAttribute('tabindex', '0'); g.setAttribute('role', 'button');
    g.setAttribute('aria-label', `Revision ${shortId(n.nodeId)}: ${n.message} (${lineName})`);
    g.onkeydown = (e) => keyOnRevision(e, g, n, () => { focusAfter = { box: svg, id: n.nodeId }; g.onclick(); });
    svg.append(g);
  });
  if (gitLayout) renderGitHistory(gitView, m, vis);
  if (focusAfter) {
    const { box, id } = focusAfter; focusAfter = null;
    const root = gitLayout ? gitView : svg;
    if (box) [...root.querySelectorAll('[data-node]')].find((x) => x.dataset.node === id)?.focus();
  }
  const lv = $('#leaves');
  lv.innerHTML = '';
  if (S.adv) lv.append(el('label', {}, [Object.assign(el('input', { type: 'checkbox', checked: showArchived, style: 'width:auto' }), { onchange: (e) => { showArchived = e.target.checked; renderHistory(); } }), ' show archived in this list']));
  let i = 0;
  m.lines.forEach((l) => {
    if (l.archived && !(S.adv && showArchived)) return;
    const tip = m.byId.get(l.head);
    const row = el('div', { className: `leaf${l.archived ? ' arch' : ''}` });
    row.dataset.line = l.name;
    row.append(el('span', { textContent: `[${++i}] ${l.name === m.h.current ? '● here ' : ''}${l.name === 'studio' ? '★ ' : ''}${l.name}`, style: 'min-width:170px' }), el('span', { textContent: `${S.adv ? `${shortId(l.head)} ` : ''}"${tip ? tip.message : ''}"`, className: 'note' }), el('button', { textContent: 'Go', title: 'Go to the tip of this branch', onclick: () => goToLine(l.name) }));
    if (l.name !== 'studio' && !l.archived) row.append(el('button', { textContent: 'Drop...', onclick: () => dropLine(l.name) }));
    lv.append(row);
  });
}

async function loadEditHistoryRedux(path, hashes, signature) {
  const seq = ++editHistorySeq;
  reduxEditSignature = signature;
  reduxEditError = '';
  reduxEditHashes = new Map();
  if (!path || !hashes.length) return;
  try {
    for (let offset = 0; offset < hashes.length; offset += 75) {
      const query = new URLSearchParams({ path });
      hashes.slice(offset, offset + 75).forEach((hash) => query.append('hash', hash));
      const result = await api.get(`/api/redux/edit-history?${query}`);
      if (seq !== editHistorySeq) break;
      if (seq === editHistorySeq) for (const record of result.records || []) reduxEditHashes.set(record.hash, [{ ar: record.ar, at: record.at }]);
    }
  } catch (error) { if (seq === editHistorySeq) reduxEditError = `AMPLE Redux matches unavailable: ${error.message}`; }
  if (seq === editHistorySeq) renderEditHistory();
}

export async function renderEditHistory() {
  const seq = ++editRenderSeq;   // a run that finds a newer one after an await returns without touching the page
  const list = $('#edit-history-list');
  if (!list) return;
  const { states, current } = editHistory();
  const inPeek = !!S.peek?.edit;
  const selected = inPeek ? selectedEditStep : current;
  $('#hist-project-view').hidden = historyMode !== 'project';
  $('#hist-edit-view').hidden = historyMode !== 'edit';
  $('#hist-project-tab').classList.toggle('on', historyMode === 'project');
  $('#hist-edit-tab').classList.toggle('on', historyMode === 'edit');
  $('#hist-project-tab').setAttribute('aria-pressed', String(historyMode === 'project'));
  $('#hist-edit-tab').setAttribute('aria-pressed', String(historyMode === 'edit'));
  if (historyMode !== 'edit' && !S.peek?.edit) return;
  $('#edit-history-summary').textContent = reduxEditError || `${states.length - 1} undo/redo step(s) · preview is read-only · AR badges mark exact states found in saved Redux records.`;
  $('#edit-history-return').hidden = !inPeek;
  $('#edit-history-restore').hidden = !inPeek;
  list.replaceChildren();
  if (S.peek && !S.peek.edit) { $('#edit-history-summary').textContent = 'Return from the project-history preview before browsing edit history.'; return; }
  if (!states.length) { list.append(el('p', { className: 'note', textContent: 'Open a song to see its edit history.' })); return; }
  const hashed = await Promise.all(states.map(async (state) => state.text == null ? '' : Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(state.text))), (byte) => byte.toString(16).padStart(2, '0')).join('')));
  if (seq !== editRenderSeq) return;
  const path = S.moduleStack.length && S.modulePath ? S.modulePath : S.path;
  const uniqueHashes = [...new Set(hashed.filter(Boolean))].sort();
  const signature = `${path || ''}:${uniqueHashes.join(',')}`;
  if (historyMode === 'edit' && signature !== reduxEditSignature) loadEditHistoryRedux(path, uniqueHashes, signature);
  const rows = states.map((state, index) => {
    const matches = reduxEditHashes.get(hashed[index]) || [];
    const row = el('button', { type: 'button', className: `edit-history-step${index === current ? ' current' : ''}${index === selected ? ' selected' : ''}${matches.length ? ' redux-match' : ''}`, 'aria-current': index === current ? 'step' : 'false' });
    row.append(el('span', { className: 'edit-history-dot', textContent: index === current ? '●' : index < current ? '↶' : '↷' }), el('span', { className: 'edit-history-label', textContent: state.label }));
    if (matches.length) row.append(el('span', { className: 'edit-history-ars', textContent: matches.map((match) => match.ar).join(' · '), title: matches.map((match) => `${match.ar} · ${new Date(match.at).toLocaleString()}`).join('\n') }));
    row.onclick = async () => {
      selectedEditStep = index;
      if (index === current) { if (S.peek?.edit) await leavePeek(); renderEditHistory(); return; }
      clearTimeout(editPreviewTimer);
      editPreviewTimer = window.setTimeout(async () => {
        if (S.peek?.edit && S.editPeek?.label === state.label) return;
        try { await enterEditPeek(state.text, state.label); renderEditHistory(); } catch (error) { toast(error.message); }
      }, 220);
    };
    row.dataset.step = String(index);
    return row;
  });
  list.replaceChildren(...rows);
  list.ondblclick = async (event) => {
    clearTimeout(editPreviewTimer);
    const row = event.target.closest('.edit-history-step'), index = Number(row?.dataset.step), state = states[index];
    if (!row || !state || index === current) return;
    event.preventDefault();
    try {
      selectedEditStep = index;
      if (!S.peek?.edit || S.editPeek?.label !== state.label) await enterEditPeek(state.text, state.label);
      if (!(event.ctrlKey || event.metaKey) && !await ask('Restore this edit-history step?', `Restore “${state.label}” as one undoable edit?`, [{ label: 'Keep preview', value: false }, { label: 'Restore this step', value: true, primary: true }])) { renderEditHistory(); return; }
      await restoreEditPeek();
      selectedEditStep = editHistory().current;
      renderEditHistory();
    } catch (error) { toast(error.message); }
  };
}

function renderGitHistory(box, m, visible) {
  box.replaceChildren();
  const ordered = [...visible].reverse();
  const lanes = [...new Set(ordered.map((n) => m.lineOf.get(n.nodeId)))];
  const laneX = new Map(lanes.map((name, i) => [name, 18 + i * 18]));
  const rowH = 48, graphW = Math.max(46, lanes.length * 18 + 20);
  const graph = mk('svg', { class: 'git-history-graph', viewBox: `0 0 ${graphW} ${ordered.length * rowH}`, 'aria-hidden': 'true' });
  graph.style.width = `${graphW}px`;
  graph.style.height = `${ordered.length * rowH}px`;
  const rowIndex = new Map(ordered.map((n, i) => [n.nodeId, i]));
  ordered.forEach((n, i) => {
    let parentNode = n.parentId ? m.byId.get(n.parentId) : null;
    while (parentNode && !rowIndex.has(parentNode.nodeId)) parentNode = parentNode.parentId ? m.byId.get(parentNode.parentId) : null;
    const parent = parentNode ? rowIndex.get(parentNode.nodeId) : undefined;
    if (parent === undefined) return;
    const x1 = laneX.get(m.lineOf.get(n.nodeId)), x2 = laneX.get(m.lineOf.get(parentNode.nodeId));
    const y1 = i * rowH + rowH / 2, y2 = parent * rowH + rowH / 2;
    const color = m.lineOf.get(n.nodeId) === 'studio' ? 'var(--studio)' : 'var(--garage)';
    graph.append(mk('path', { d: `M${x1} ${y1} C${x1} ${(y1 + y2) / 2} ${x2} ${(y1 + y2) / 2} ${x2} ${y2}`, stroke: color, fill: 'none', 'stroke-width': 2 }));
  });
  ordered.forEach((n, i) => {
    const line = m.lineOf.get(n.nodeId), x = laneX.get(line), y = i * rowH + rowH / 2;
    const color = line === 'studio' ? 'var(--studio)' : 'var(--garage)';
    graph.append(mk('circle', { cx: x, cy: y, r: 5, fill: color, stroke: n.nodeId === m.hereId ? 'var(--text)' : n.nodeId === (S.selRev || m.hereId) ? 'var(--acc)' : 'var(--panel2)', 'stroke-width': n.nodeId === m.hereId || n.nodeId === (S.selRev || m.hereId) ? 3 : 2 }));
  });
  const rows = el('div', { className: 'git-history-rows' });
  rows.style.height = `${ordered.length * rowH}px`;
  ordered.forEach((n, i) => {
    const line = m.lineOf.get(n.nodeId), tags = m.tagsAt.get(n.nodeId) || [];
    const row = el('button', { type: 'button', className: `git-history-row${n.nodeId === (S.selRev || m.hereId) ? ' selected' : ''}`, title: `${n.message} · ${line} · ${fmtTime(n.at)}` });
    row.style.paddingLeft = `${graphW + 10}px`;
    row.style.top = `${i * rowH}px`;
    const labels = [line, n.nodeId === m.hereId ? 'here' : '', n.nodeId === m.studioId ? '★ studio' : '', n.reduxRef, ...tags.map((t) => `tag: ${t}`)].filter(Boolean);
    row.append(el('span', { className: 'git-history-message', textContent: n.message }), el('span', { className: 'git-history-meta', textContent: `${shortId(n.nodeId)}  ${labels.join(' · ')}  ·  ${fmtTime(n.at)}` }));
    row.onclick = () => { S.selRev = n.nodeId; focusAfter = { box, id: n.nodeId }; renderHistory(); renderDetail(); };
    row.dataset.node = n.nodeId;
    row.ondblclick = () => goToNode(n.nodeId);
    row.oncontextmenu = (e) => { e.preventDefault(); S.selRev = n.nodeId; renderDetail(); histMenu(e, n); };
    row.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); goToNode(n.nodeId); } else keyOnRevision(e, row, n, null); };
    rows.append(row);
  });
  box.append(graph, rows);
}

export function renderDetail() {
  const box = $('#hdetail');
  if (!box) return;
  box.innerHTML = '';
  const m = model();
  if (!m) return;
  const n = m.byId.get(S.selRev || m.hereId);
  if (!n) return;
  box.append(el('h4', { textContent: `Revision ${shortId(n.nodeId)}` }));
  const kv = el('div', { className: 'kv' });
  const add = (k, v) => kv.append(el('b', { textContent: k }), el('span', { textContent: v }));
  const lineName = m.lineOf.get(n.nodeId);
  add('message', n.message);
  add('branch', lineName + (m.lines.find((l) => l.name === lineName && l.head === n.nodeId) ? ' (tip)' : ''));
  add('kind', n.kind);
  add('time', fmtTime(n.at));
  add('files', String(n.files));
  add('AMPLE Redux', n.reduxRef || '-');
  add('is', [n.nodeId === m.hereId ? 'here' : '', n.nodeId === m.studioId ? 'studio' : ''].filter(Boolean).join(', ') || '-');
  add('tags', (m.tagsAt.get(n.nodeId) || []).join(', ') || '-');
  if (S.adv) { add('id', n.nodeId); add('parent', n.parentId || '-'); }
  box.append(kv);
  const r = el('div', { className: 'row' });
  r.append(el('button', { textContent: 'Go here', onclick: () => goToNode(n.nodeId) }), el('button', { textContent: 'Look only', onclick: () => enterPeek(n.nodeId) }), el('button', { textContent: 'Compare...', onclick: () => compareDialog(n.nodeId, m.h.current) }));
  const r2 = el('div', { className: 'row' });
  r2.append(el('button', { textContent: 'Fork...', onclick: () => forkFrom(n.nodeId, false) }), el('button', { textContent: 'Tag...', onclick: () => tagNode(n.nodeId) }));
  box.append(r, r2, el('p', { className: 'note', textContent: 'Right-click a revision for more.' }));
}

/* ---------- branches: one line laid out, and the structure list ---------- */
export function openBranch(name) {
  branchSel = name;
  renderBranches();
  document.querySelectorAll('#btabs button[data-t]').forEach((b) => b.classList.toggle('on', b.dataset.t === 'branch'));
  document.querySelectorAll('.bsec').forEach((s) => s.classList.toggle('on', s.id === 'b-branch'));
}

export function renderBranches() {
  const box = $('#b-branch');
  if (!box) return;
  const m = model();
  box.innerHTML = '';
  box.append(el('p', { className: 'note', textContent: 'One branch laid out: the root on the left, the path of branches that led to the focused one, and its direct sub-branches on the right. Dots are revisions (ringed = you are here). Click a branch box to focus it.' }));
  if (!m) return;
  const focus = branchSel && m.lines.some((l) => l.name === branchSel) ? branchSel : m.h.current;
  const chain = lineChain(m, focus);
  const scroll = el('div', { className: 'brscroll' });
  const row = el('div', { className: 'brrow' });
  const boxFor = (name) => {
    const l = m.lines.find((x) => x.name === name);
    const tip = m.byId.get(l.head);
    const b = el('div', { className: `brbox${name === focus ? ' this' : ''}${name === 'studio' ? ' studio' : ''}${l.archived ? ' rej' : ''}` });
    b.dataset.line = name;
    b.append(el('div', {}, [el('b', { textContent: `${name === 'studio' ? '★ ' : ''}${name === m.h.current ? '● ' : ''}${name}` })]), el('div', { className: 'note', textContent: tip ? tip.message : '' }));
    const dots = el('div', { className: 'dots' });
    const own = m.h.nodes.filter((n) => m.lineOf.get(n.nodeId) === name);
    (own.length ? own : [m.byId.get(l.head)]).filter(Boolean).slice(-12).forEach((n) => {
      const d = el('i', { title: `${shortId(n.nodeId)} ${n.message}` });
      if (n.nodeId === m.hereId) d.classList.add('here');
      d.onclick = (e) => { e.stopPropagation(); S.selRev = n.nodeId; renderHistory(); renderDetail(); };
      dots.append(d);
    });
    b.append(dots);
    const r = el('div', { className: 'row' });
    r.append(el('button', { textContent: 'Go', onclick: (e) => { e.stopPropagation(); goToLine(l.name); } }), el('button', { textContent: 'Look only', onclick: (e) => { e.stopPropagation(); enterPeek(l.head); } }));
    b.append(r);
    b.onclick = () => { branchSel = name; renderBranches(); };
    b.ondblclick = () => goToLine(l.name);
    return b;
  };
  chain.forEach((name, i) => { row.append(boxFor(name)); if (i < chain.length - 1) row.append(el('div', { className: 'brarrow' })); });
  const kids = lineChildren(m, focus, false);
  if (kids.length) { row.append(el('div', { className: 'brarrow' })); const col = el('div', { className: 'brcol' }); kids.forEach((k) => col.append(boxFor(k))); row.append(col); }
  scroll.append(row);
  box.append(scroll);
}

export function appendHistoryStructure(box) {
  const m = model();
  if (!m) return;
  box.append(el('h5', { textContent: S.adv ? 'Branches (advanced: the whole tree)' : 'Branches (path to here and direct sub-branches)' }));
  const lrow = (name, depth) => {
    const l = m.lines.find((x) => x.name === name);
    const tip = m.byId.get(l.head);
    const r = el('div', { className: `rev${name === m.h.current ? ' sel' : ''}${l.archived ? ' dim' : ''}`, title: 'Click: show this branch laid out. Double-click: go to its tip.' });
    r.dataset.line = name;
    r.style.paddingLeft = `${8 + depth * 14}px`;
    r.append(el('span', { textContent: `${depth ? '└ ' : ''}${name === 'studio' ? '★ ' : ''}${name === m.h.current ? '● ' : ''}${name}`, style: `font-weight:${name === m.h.current ? 600 : 400}` }), el('span', { className: 'dim', textContent: `  ${tip ? tip.message : ''}${l.archived ? ' (archived)' : ''}` }));
    r.onclick = () => openBranch(name);
    activatable(r, `Show branch ${name}`);
    r.ondblclick = () => goToLine(name);
    r.oncontextmenu = (e) => { e.preventDefault(); showMenu(e.clientX, e.clientY, [{ head: name }, { label: 'Show this branch laid out', fn: () => openBranch(name) }, { label: 'Go to its tip', fn: () => goToLine(name) }, { label: 'Look only', fn: () => enterPeek(l.head) }, ...(name !== 'studio' && !l.archived ? [{ label: 'Drop...', fn: () => dropLine(name) }] : [])]); };
    box.append(r);
  };
  if (S.adv) {
    const walk = (name, depth) => { lrow(name, depth); lineChildren(m, name, true).forEach((k) => walk(k, depth + 1)); };
    m.lines.filter((l) => lineParent(m, l.name) === null).forEach((l) => walk(l.name, 0));
  } else {
    const chain = lineChain(m, m.h.current);
    chain.forEach((l, i) => lrow(l, i));
    lineChildren(m, m.h.current, false).forEach((k) => lrow(k, chain.length));
  }
  box.append(el('h5', { textContent: 'Tags (locked)' }));
  m.h.tags.forEach((t) => {
    const r = el('div', { className: 'rev', textContent: `🏷 ${t.name}  → ${shortId(t.nodeId)}` });
    r.onclick = () => { S.selRev = t.nodeId; renderHistory(); renderDetail(); };
    r.ondblclick = () => goToNode(t.nodeId);
    box.append(r);
  });
  if (!m.h.tags.length) box.append(el('div', { className: 'dim', textContent: 'none', style: 'padding:2px 6px' }));
}

export function initHistory() {
  $('#hist-project-tab').onclick = () => { historyMode = 'project'; renderEditHistory(); };
  $('#hist-edit-tab').onclick = () => { historyMode = 'edit'; renderEditHistory(); };
  $('#edit-history-return').onclick = () => leavePeek().then(renderEditHistory);
  $('#edit-history-restore').onclick = () => restoreEditPeek().then(() => { selectedEditStep = editHistory().current; renderEditHistory(); });
  $('#btn-cp').onclick = checkpoint;
  $('#btn-promote').onclick = promote;
  $('#btn-fork').onclick = () => { const m = model(); if (m) forkFrom(m.headOf('studio'), true); };
  $('#h-adv').onclick = () => { S.adv = !S.adv; renderHistory(); notify(); };
  $('#h-layout').onclick = () => { gitLayout = !gitLayout; localStorage.setItem('ample.history.layout', gitLayout ? 'git' : 'graph'); renderHistory(); };
  const grid = $('.hgrid'), splitter = $('#history-splitter');
  const setSplit = (value, persist) => {
    historySplit = Math.max(25, Math.min(75, value));
    grid.style.gridTemplateColumns = `minmax(0, ${historySplit}fr) 6px minmax(0, ${100 - historySplit}fr)`;
    splitter.setAttribute('aria-valuenow', String(Math.round(historySplit)));
    if (persist) localStorage.setItem('ample.history.split', String(historySplit));
  };
  setSplit(historySplit, false);
  window.addEventListener('resize', () => setSplit(historySplit, false));
  splitter.onpointerdown = (event) => {
    if (event.button !== 0) return;
    event.preventDefault();
    splitter.setPointerCapture(event.pointerId);
    const bounds = grid.getBoundingClientRect();
    const move = (e) => setSplit((e.clientX - bounds.left) / bounds.width * 100, false);
    move(event);
    splitter.onpointermove = move;
    splitter.onpointerup = splitter.onpointercancel = () => {
      splitter.onpointermove = splitter.onpointerup = splitter.onpointercancel = null;
      setSplit(historySplit, true);
    };
  };
  splitter.onkeydown = (event) => {
    if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
      event.preventDefault();
      setSplit(historySplit + (event.key === 'ArrowLeft' ? -2 : 2), true);
    }
  };
  $('#h-compare').onclick = () => compareDialog();
  $('#h-cleanup').onclick = cleanup;
}
