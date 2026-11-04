// The lyrics editor: a full-screen editor for the file a lyrics block points at ("[bar 9] text" per line), with checks against
// the block's placement, a timing strip, import and export of .lrc and .srt text, undo and redo, and two ways back:
// "Apply and back" (writes the file, journaled) and "Undo and back". @ample:lyrics-editor

import { api } from './api.js';
import { $, ask, el, toast } from './ui.js';
import { S, edit, refreshInputs } from './store.js';
import { registerView, setView } from './views.js';
import { TAGS, isSorted, lyricChecks, lyricsText, parseLyrics, sortByTime, timedRows } from './lyrics-model.js';
import { createSaveControl } from './savecontrol.js';
import { barBeats } from '../lib/meter.mjs';

const L = { node: null, file: '', rows: [], snap: '', unit: 'bars', sel: -1, undo: [], redo: [], auto: true, bad: [] };
const tempo = () => (S.doc && S.doc.project ? S.doc.project.tempo : 120);
const barSec = (b) => ((b - 1) * barBeats() * 60) / tempo();
const secBar = (s) => Math.round(((s * tempo()) / (barBeats() * 60) + 1) * 100) / 100;
const mmss = (s) => { const m = Math.floor(s / 60); const x = s - m * 60; return `${String(m).padStart(2, '0')}:${x.toFixed(2).padStart(5, '0')}`; };

const sorted = () => isSorted(L.rows);

export async function openLyrics(node) {
  let file = node.params.file;
  if (!file) {
    // A lyrics block without a file gets its own: lyrics/<block name>.lyr, named in the song; the file is created when you apply.
    file = `lyrics/${node.id}.lyr`;
    if (!(await edit([{ type: 'setParameter', block: node.id, name: 'file', value: file }]))) return;
  }
  let text = '';
  try { text = (await api.get(`/api/file?path=${encodeURIComponent(file)}`)).text; } catch { text = ''; }
  L.node = node; L.file = file; L.snap = text;
  const r = parseLyrics(text);
  L.rows = r.rows; L.bad = r.bad; L.unit = 'bars'; L.sel = -1; L.undo = []; L.redo = [];
  setView('lyr');
  if (r.bad.length) toast(`Line(s) ${r.bad.join(', ')} of the lyrics file are not in the [bar N] text format and are not shown.`);
}

const push = () => { const s = JSON.stringify(L.rows); if (L.undo.length && L.undo[L.undo.length - 1] === s) return; L.undo.push(s); L.redo = []; if (L.undo.length > 200) L.undo.shift(); updateUR(); };
function undoStep() { while (L.undo.length && L.undo[L.undo.length - 1] === JSON.stringify(L.rows)) L.undo.pop(); if (!L.undo.length) { toast('Nothing left to undo in this session.'); return; } L.redo.push(JSON.stringify(L.rows)); L.rows = JSON.parse(L.undo.pop()); render(); }
function redoStep() { if (!L.redo.length) { toast('Nothing to redo.'); return; } L.undo.push(JSON.stringify(L.rows)); L.rows = JSON.parse(L.redo.pop()); render(); }
function updateUR() { const u = $('#ly-undo'); const r = $('#ly-redo'); if (u) { u.disabled = !L.undo.length; u.textContent = `↶ Undo (${L.undo.length})`; } if (r) { r.disabled = !L.redo.length; r.textContent = `↷ Redo (${L.redo.length})`; } }

function sortRows(quiet) {
  if (sorted()) return;
  push();
  L.rows = sortByTime(L.rows);
  L.sel = -1;
  if (!quiet) { render(); toast('Sorted by time (lines with the same bar keep their order). Undo brings the old order back.'); }
}

function render() {
  const root = $('#ly');
  if (!L.node) return;
  root.innerHTML = '';
  const bar = el('div', { className: 'bmbar' });
  const unit = el('span', { className: 'row', style: 'margin:0' }, [['bars', 'bars'], ['seconds', 'seconds']].map(([k, l]) => el('button', { textContent: l, className: L.unit === k ? 'on' : '', onclick: () => { L.unit = k; render(); } })));
  const autoBox = Object.assign(el('input', { type: 'checkbox', checked: L.auto, style: 'width:auto' }), { onchange: (e) => { L.auto = e.target.checked; } });
  bar.append(
    el('button', { id: 'ly-undoback', textContent: '← Undo and back', title: 'Throw away the changes made in this editor and go back', onclick: undoBack }),
    el('b', { textContent: `Lyrics: ${L.node.title}   ${L.file}` }),
    createSaveControl(),
    el('button', { id: 'ly-undo', textContent: '↶ Undo (0)', onclick: undoStep }),
    el('button', { id: 'ly-redo', textContent: '↷ Redo (0)', onclick: redoStep }),
    unit,
    el('button', { id: 'ly-sort', textContent: 'Sort by time', onclick: () => sortRows(false), disabled: sorted() }),
    el('label', { title: 'Sort the lines by time automatically when you press Apply and back', style: 'white-space:nowrap' }, [autoBox, ' sort on apply']),
    el('button', { textContent: 'Import...', onclick: importLyrics }),
    el('button', { textContent: 'Export .lrc', onclick: exportLyrics }),
    el('span', { className: 'sp' }),
    el('span', { id: 'ly-status', className: 'note', style: 'margin:0' }),
    el('button', { id: 'ly-apply', textContent: 'Apply and back →', className: 'primary', title: 'Write the lyrics file (journaled, not a revision) and go back to the node editor', onclick: applyBack }),
  );
  const body = el('div', { className: 'lybody' });
  const left = el('div', { className: 'lyleft' });
  const right = el('div', { className: 'lyright', id: 'ly-right' });
  const tbl = el('div', { className: 'lytbl' });
  tbl.append(el('div', { className: 'lyrow lyhead' }, ['#', L.unit === 'bars' ? 'bar' : 'seconds', 'text', L.unit === 'bars' ? 'seconds' : 'bar', ''].map((t) => el('span', { textContent: t }))));
  L.rows.forEach((r, i) => {
    const row = el('div', { className: `lyrow${i === L.sel ? ' sel' : ''}${r.tag ? ' lytag' : ''}` });
    row.dataset.index = i;
    if (r.tag) {
      const tag = el('input', { value: r.text, placeholder: '[Verse]', className: 'lytxt', title: 'A section tag: it is not sung, it tells ACE what comes next', style: 'font-weight:600' });
      tag.setAttribute('list', 'ly-tags');
      tag.onfocus = () => { push(); L.sel = i; };
      tag.oninput = () => { r.text = tag.value; live(); };
      tag.onchange = () => { const w = tag.value.trim().replace(/^[?|]?$/g, ''); r.text = w ? `[${w}]` : ''; tag.value = r.text; live(); };
      const tacts = el('span', { className: 'lyacts' }, [
        el('button', { textContent: '↑', title: 'Move up', onclick: () => { push(); if (i) { [L.rows[i - 1], L.rows[i]] = [L.rows[i], L.rows[i - 1]]; L.sel = i - 1; render(); } } }),
        el('button', { textContent: '↓', title: 'Move down', onclick: () => { push(); if (i < L.rows.length - 1) { [L.rows[i + 1], L.rows[i]] = [L.rows[i], L.rows[i + 1]]; L.sel = i + 1; render(); } } }),
        el('span'),
        el('button', { textContent: '✕', title: 'Delete this tag', onclick: () => { push(); L.rows.splice(i, 1); L.sel = -1; render(); } }),
      ]);
      row.append(el('span', { textContent: i + 1, className: 'note' }), el('span', { textContent: '§', className: 'note', title: 'section tag' }), tag, el('span', { className: 'alt note', textContent: 'tag' }), tacts);
      tbl.append(row);
      return;
    }
    const num = el('input', { type: 'number', step: L.unit === 'bars' ? 0.25 : 0.5, min: L.unit === 'bars' ? 1 : 0, value: L.unit === 'bars' ? r.bar : Math.round(barSec(r.bar) * 100) / 100 });
    num.onfocus = () => { push(); L.sel = i; };
    num.oninput = () => { const v = Number(num.value); if (!Number.isNaN(v)) { r.bar = L.unit === 'bars' ? v : secBar(v); row.querySelector('.alt').textContent = L.unit === 'bars' ? `${barSec(r.bar).toFixed(2)} s` : `bar ${r.bar}`; live(); } };
    const txt = el('input', { value: r.text, placeholder: 'lyric line...', className: 'lytxt' });
    txt.onfocus = () => { push(); L.sel = i; };
    txt.oninput = () => { r.text = txt.value; live(); };
    const acts = el('span', { className: 'lyacts' }, [
      el('button', { textContent: '↑', title: 'Move up', onclick: () => { push(); if (i) { [L.rows[i - 1], L.rows[i]] = [L.rows[i], L.rows[i - 1]]; L.sel = i - 1; render(); } } }),
      el('button', { textContent: '↓', title: 'Move down', onclick: () => { push(); if (i < L.rows.length - 1) { [L.rows[i + 1], L.rows[i]] = [L.rows[i], L.rows[i + 1]]; L.sel = i + 1; render(); } } }),
      el('button', { textContent: '+', title: 'Insert a line after this one', onclick: () => { push(); L.rows.splice(i + 1, 0, { bar: r.bar + 2, text: '' }); L.sel = i + 1; render(); } }),
      el('button', { textContent: '✕', title: 'Delete this line', onclick: () => { push(); L.rows.splice(i, 1); L.sel = -1; render(); } }),
    ]);
    row.append(el('span', { textContent: i + 1, className: 'note' }), num, txt, el('span', { className: 'alt note', textContent: L.unit === 'bars' ? `${barSec(r.bar).toFixed(2)} s` : `bar ${r.bar}` }), acts);
    tbl.append(row);
  });
  left.append(el('datalist', { id: 'ly-tags' }, TAGS.map((x) => el('option', { value: x }))), tbl, el('div', { className: 'row' }, [
    el('button', { id: 'ly-add-tag', textContent: '+ Add a tag', title: 'Add a section tag such as [Verse] or [Chorus] before the selected line (or at the end)', onclick: () => { push(); L.rows.splice(L.sel >= 0 ? L.sel : L.rows.length, 0, { tag: true, text: '[Verse]' }); render(); } }),
    el('button', { id: 'ly-add', textContent: '+ Add a line', onclick: () => { push(); const lastTimed = timedRows(L.rows).pop()?.r; L.rows.push({ bar: lastTimed ? lastTimed.bar + 2 : (L.node.parts[0] ? L.node.parts[0].at : 1), text: '' }); L.sel = L.rows.length - 1; render(); } }),
    el('span', { className: 'note', textContent: 'Tip: a bar can be fractional (9.5 = halfway through bar 9). The file stays a plain text file: [bar 9] text.', style: 'margin:0' }),
  ]));
  right.append(el('h4', { textContent: 'Checks', style: 'padding:0' }), el('div', { id: 'ly-checks' }), el('h4', { textContent: 'Timing view', style: 'padding:0;margin-top:12px' }), el('div', { id: 'ly-strip', className: 'lystrip' }), el('p', { className: 'note', id: 'ly-strip-note' }), el('h4', { textContent: 'This block', style: 'padding:0;margin-top:12px' }), el('div', { className: 'note', id: 'ly-block' }));
  body.append(left, right);
  root.append(bar, body);
  live();
}

function live() {
  const ck = $('#ly-checks');
  if (!ck) return;
  ck.innerHTML = '';
  const cs = lyricChecks(L.rows, L.node, tempo());
  cs.forEach((c) => {
    const r = el('div', { className: `seerr ${c.sev}`, textContent: `${{ error: '● ', warning: '▲ ', info: 'ℹ ' }[c.sev]}${c.msg}`, style: 'cursor:pointer' });
    r.onclick = () => { if (c.i >= 0) { L.sel = c.i; const t = document.querySelectorAll('.lyrow')[c.i + 1]; if (t) t.querySelector('input.lytxt').focus(); } };
    ck.append(r);
  });
  updateUR();
  const sb = $('#ly-sort');
  if (sb) sb.disabled = sorted();
  const nE = cs.filter((c) => c.sev === 'error').length;
  const nW = cs.filter((c) => c.sev === 'warning').length;
  $('#ly-status').textContent = `${nE} error(s), ${nW} warning(s) · ${timedRows(L.rows).length} lines${lyricsText(L.rows) !== L.snap ? ' · not applied' : ''}`;
  const seg = L.node.parts[0];
  const a = seg ? seg.at : 1;
  const b = seg ? seg.at + seg.length : Math.max(a + 8, ...timedRows(L.rows).map((x) => x.r.bar + 2));
  const strip = $('#ly-strip');
  strip.innerHTML = '';
  const pos = (bar2) => Math.max(0, Math.min(100, ((bar2 - a) / (b - a)) * 100));
  strip.append(el('div', { className: 'lypart', style: 'left:0;width:100%' }));
  L.rows.forEach((r, i) => {
    if (r.tag) return;
    const bad = cs.find((c) => c.i === i && c.sev !== 'info');
    const t = el('i', { title: `line ${i + 1} at bar ${r.bar}: ${r.text}`, style: `left:${pos(r.bar)}%;background:${bad ? (bad.sev === 'error' ? 'var(--bad)' : 'var(--warn)') : 'var(--acc)'}` });
    t.dataset.i = i;
    t.onclick = () => { L.sel = i; const e = document.querySelectorAll('.lyrow')[i + 1]; if (e) e.querySelector('input.lytxt').focus(); };
    strip.append(t);
  });
  $('#ly-strip-note').textContent = `bar ${a} to ${b} (the placement of this lyrics block: ${seg ? `at ${seg.at} for ${seg.length}` : 'none, using the lines themselves'}), ${barSec(b).toFixed(1)} s at the end`;
  $('#ly-block').textContent = `${L.node.title} · ${L.node.params.lang || '-'} · timing ${L.node.params.timing || '-'} · first5s ${L.node.params.first5s || '-'} · tempo ${tempo()}`;
}

async function applyBack() {
  if (L.auto && !sorted()) sortRows(true);
  const errs = lyricChecks(L.rows, L.node, tempo()).filter((c) => c.sev === 'error');
  if (errs.length) { toast(`Fix the ${errs.length} error(s) first: ${errs[0].msg}.`); return; }
  try {
    await api.put('/api/file', { path: L.file, text: lyricsText(L.rows) });
    refreshInputs().catch(() => {});
  } catch (e) { toast(e.message); return; }
  toast('Lyrics written (journaled in AMPLE Redux; Save makes a revision of the song files).');
  back();
}

async function undoBack() {
  if (lyricsText(L.rows) === L.snap || (!L.rows.length && !L.snap)) { back(); return; }
  const ok = await ask('Undo and back', 'Throw away the changes made in this editor session?', [{ label: 'Keep editing', value: false }, { label: 'Undo and back', value: true, primary: true }]);
  if (ok) { back(); toast('Changes discarded.'); }
}

function back() { L.node = null; setView('blocks', true); }

async function importLyrics() {
  const ta = el('textarea', { rows: 8, placeholder: '[00:21.00] first line\n[00:25.50] second line\n\nor an .srt:\n1\n00:00:21,000 --> 00:00:25,000\nfirst line', style: 'width:100%;font:12px ui-monospace,Consolas,monospace' });
  const v = await ask('Import lyrics', `Paste .lrc or .srt text. Times become bars at the project tempo (${tempo()} bpm), counted from the start of the song.`, [{ label: 'Cancel', value: false }, { label: 'Append', value: 'append' }, { label: 'Replace all', value: 'replace', primary: true }], [ta]);
  if (!v) return;
  const rows = [];
  const arr = ta.value.replace(/\r/g, '').split('\n');
  arr.forEach((l, i) => {
    let m = /^\s*\[(\d+):(\d+(?:\.\d+)?)\]\s*(.*)$/.exec(l);
    if (m) { rows.push({ bar: secBar(Number(m[1]) * 60 + Number(m[2])), text: m[3].replace(/<[^>]*>/g, '').trim() }); return; }
    m = /^\s*(\d+):(\d+):(\d+)[,.](\d+)\s*-->/.exec(l);
    if (m) {
      const s = Number(m[1]) * 3600 + Number(m[2]) * 60 + Number(m[3]) + Number(`0.${m[4]}`);
      const t = [];
      for (let k = i + 1; k < arr.length && arr[k].trim(); k++) t.push(arr[k].trim());
      rows.push({ bar: secBar(s), text: t.join(' ') });
    }
  });
  if (!rows.length) { toast('Nothing recognised: expected [mm:ss.xx] lines or .srt blocks.'); return; }
  push();
  L.rows = v === 'replace' ? rows : L.rows.concat(rows);
  render();
  toast(`Imported ${rows.length} line(s).`);
}

function exportLyrics() {
  const t = `${timedRows(L.rows).map((x) => `[${mmss(barSec(x.r.bar))}] ${x.r.text}`).join('\n')}\n`;
  ask('Export .lrc', `Line timing as .lrc (seconds from the song start at ${tempo()} bpm). Select and copy.`, [{ label: 'Close', value: 1, primary: true }], [el('textarea', { rows: 8, readOnly: true, value: t, id: 'ly-export', style: 'width:100%;font:12px ui-monospace,Consolas,monospace' })]);
}

export function initLyrics() {
  registerView('lyr', render);
  addEventListener('keydown', (e) => {
    if (!L.node || $('#ly').style.display === 'none') return;
    const t = (e.target.tagName || '').toLowerCase();
    if (t === 'input' || t === 'textarea' || t === 'select') return;
    const k = e.key.toLowerCase();
    if ((e.ctrlKey || e.metaKey) && k === 'z') { e.preventDefault(); if (e.shiftKey) redoStep(); else undoStep(); }
    if ((e.ctrlKey || e.metaKey) && k === 'y') { e.preventDefault(); redoStep(); }
  });
}
