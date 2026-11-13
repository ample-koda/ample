// The source editor: the song as text with live diagnostics, completion, hover, go to definition, rename, format and quick
// fixes from the language server. Apply updates the working copy (not a revision); Save makes the revision; leaving the
// editor needs a song that compiles. Other text files of the project (lyrics, chords) open here too. @ample:source-editor
// @ample:source @ample:ctrl-click @ample:language-support

import { noteOpItems } from '../lib/note-ops.mjs';
import { NOTE_SIGNATURES, NOTE_TYPES } from '../lib/note-catalog.mjs';
import { activeParam, argumentNote, callContext, signatureOf, typedCompletion } from '../lib/note-typed.mjs';
import { helperItems, helpersOfText } from './song-helpers.js';
import { helperDiagnostics } from '../lib/note-lang.mjs';
import { lengthDiagnostics } from '../lib/block-length.mjs';
import { withoutSilenced } from '../lib/ignore-comments.mjs';
import { blankBlocks, docDiagnostics, docMarkdown, docsOf, hoverText } from '../lib/doc-comments.mjs';
import { NOTE_BUILTINS } from '../lib/note-catalog.mjs';
import { variableHover, variableSpots } from '../lib/variable-types.mjs';
import { docTipShown, hideDocTip, showDocTip } from './doc-tip.js';
import { evaluate, seedSites, stableNameOf } from '../lib/note-pipeline.mjs';
import { api } from './api.js';
import { $, ask, el, showMenu, toast } from './ui.js';
import { S, applyText, edit, flat, notify, notifySelection, readOnly, refreshActiveModule, refreshInputs, select, setBeforeSave, subscribe } from './store.js';
import { LspClient } from './lsp.js';
import { registerView, setView, V } from './views.js';
import { focusNode, showConnectionContextMenu } from './canvas.js';
import { linkAt, linksIn } from '../lib/source-links.js';
import { hasValidInlineNotesAt, openMidiNotesAtLine } from './midi-editors.js';
import { applyDrop, dropTargets, nearestTarget } from '../lib/drop-targets.mjs';
import { formatValue, kindById, kindOf } from '../lib/file-kinds.mjs';
import { UPLOAD_FOLDER, draggedFile, outsideDragKind } from './dragfile.js';
import { uploadProjectFiles, openProjectFilePicker } from './filepicker.js';
import { rankFiles } from '../lib/fuzzy.mjs';
import { liveMarks } from '../lib/build-errors.mjs';
import { beforeSave, createSaveControl, refreshSaveControls, typedProbe } from './savecontrol.js';
import { resolveTypedConflict } from './typed-conflict.js';
import { seqDiagnostics } from '../lib/seq-diagnostics.mjs';
import { getRecoveryDraft, listRecoveryDrafts, removeRecoveryDraft, saveRecoveryDraft } from '../lib/recovery-drafts.mjs';
import { wordRangeBefore } from '../lib/word-range.mjs';
import { consumeRecoveryAcceptance } from '../lib/work-state.mjs';
import { recentFilePaths, rememberRecentFile } from './recent-files.js';
import { diffHunks, diffStats, lineDiff } from './diff.js';
import { barBeats } from '../lib/meter.mjs';

const SE = { file: null, textFile: null, text: '', orig: '', edit: true, diags: [], lsp: null, files: [], ctrl: false, comp: null, timer: 0, loadedFor: '', fontSize: 12 };
let sourceParseTimer = 0, sourceParseSeq = 0;
const lines = (t) => t.split('\n');
const escH = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const isSong = () => SE.file === S.path;
/** Whether the editor shows the song itself (Koda's suggestion is drawn only then). */
export const editorShowsSong = () => isSong();
const isEditableSourceFile = (path) => path === 'ample.lock' || /\.(?:module|amplec|lyr|amplelrc|lrc|chords|txt|md|csv|json|seq|ampleseq)$/i.test(path);

/** The selected lines of the editor (null without a selection); taken at once, because opening a menu or a dialog moves the focus away from the text. */
function editorSelection() {
  const ta = $('#se-ta');
  if (!ta || ta.selectionStart === ta.selectionEnd) return null;
  return { from: ta.value.slice(0, ta.selectionStart).split('\n').length, to: ta.value.slice(0, ta.selectionEnd).split('\n').length };
}

/** The right-click menu of the editor: the usual text actions and, with the selection kept as it is, the AI actions. */
/** On a phone the long press that selects code also opens the menu of the editor: the keyboard has no business there. It goes away while the menu is open (and a tap in the editor brings it back). */
function hideKeyboardForMenu() {
  if (!matchMedia('(pointer: coarse)').matches) return;
  const content = $('#se-cm .cm-content'), host = $('#se-cm');
  if (!content || !host) return;
  content.blur();   // first: the keyboard guard of the editor (installKeyboardGuard) lets the keyboard back in on blur, which would undo the line below
  content.setAttribute('inputmode', 'none');
  host.addEventListener('pointerdown', () => content.setAttribute('inputmode', 'text'), { once: true, capture: true });
}

async function openEditorMenu(x, y) {
  const ta = $('#se-ta');
  if (!ta) return;
  hideErrTip();   // the tooltip of an error must not lie over the menu
  const start = ta.selectionStart, end = ta.selectionEnd, has = start !== end, locked = readOnly(), snap = editorSelection();
  const selectionText = snap ? lines(ta.value).slice(snap.from - 1, snap.to).join('\n') : '';
  const back = () => { ta.focus(); ta.setSelectionRange(start, end); };
  const run = (fn) => () => { back(); fn(); };
  const ai = await import('./ai.js');
  void ai.aiStatus(true).catch(() => {});
  hideKeyboardForMenu();
  showMenu(x, y, [
    has ? { label: 'Ask Koda about this', fn: () => import('./koda.js').then((m) => m.ask(m.codeSubject(ta.value, start, end))) } : null,
    // the menu works on the text of the editor itself (the textarea is a hidden stand-in of CodeMirror: the browser's cut, copy and insertText commands depend on its own selection)
    has && !locked ? { label: 'Cut', fn: async () => { back(); try { await navigator.clipboard.writeText(ta.value.slice(start, end)); ta.setRangeText('', start, end, 'end'); ta.oninput(); } catch { toast('The browser did not allow writing the clipboard: use Ctrl+X.'); } } } : { head: 'Cut' },
    has ? { label: 'Copy', fn: async () => { back(); try { await navigator.clipboard.writeText(ta.value.slice(start, end)); } catch { toast('The browser did not allow writing the clipboard: use Ctrl+C.'); } } } : { head: 'Copy' },
    locked ? { head: 'Paste' } : { label: 'Paste', fn: async () => { back(); try { const pasted = await navigator.clipboard.readText(); ta.setRangeText(pasted, start, end, 'end'); ta.oninput(); } catch { toast('The browser did not allow reading the clipboard: use Ctrl+V.'); } } },
    { label: 'Select all', fn: run(() => ta.select()) },
    '-',
    { label: 'AI', onOpen: back, children: ai.aiItems({ context: isSeqFile() ? 'sequence' : 'source', path: SE.file || S.path, song: S.path, selection: () => snap, selectionText }) },
  ].filter(Boolean));   // the items that need a selection are null without one
}

function silenceLintInSource(d) {
  const ta = $('#se-ta');
  if (!ta || readOnly() || !/\.amplet$/i.test(SE.file || '') || !/^L\d{3}$/.test(d.code)) return;
  const original = ta.value, selectionStart = ta.selectionStart, selectionEnd = ta.selectionEnd, sourceLines = lines(original), blockLine = d.range.start.line;
  let ignoreLine = -1;
  for (let i = blockLine - 1; i >= 0; i--) {
    const line = sourceLines[i].trim();
    if (line && !line.startsWith('#')) break;
    if (/^#\s*ample-ignore\b/.test(line)) { ignoreLine = i; break; }
  }
  let changeOffset, changeLength, replacement;
  if (ignoreLine >= 0) {
    const indent = sourceLines[ignoreLine].match(/^\s*/)[0], codes = sourceLines[ignoreLine].trim().replace(/^#\s*ample-ignore\s+/, '').split(/[\s,]+/).filter(Boolean);
    if (codes.includes(d.code)) return;
    changeOffset = sourceLines.slice(0, ignoreLine).join('\n').length + (ignoreLine ? 1 : 0);
    changeLength = sourceLines[ignoreLine].length;
    replacement = sourceLines[ignoreLine] = `${indent}# ample-ignore ${[...codes, d.code].join(', ')}`;
  } else {
    changeOffset = sourceLines.slice(0, blockLine).join('\n').length + (blockLine ? 1 : 0);
    changeLength = 0;
    replacement = `# ample-ignore ${d.code}\n`;
  }
  ta.value = ignoreLine >= 0 ? sourceLines.join('\n') : `${original.slice(0, changeOffset)}${replacement}${original.slice(changeOffset)}`;
  const delta = replacement.length - changeLength;
  ta.focus(); ta.setSelectionRange(selectionStart + (changeOffset <= selectionStart ? delta : 0), selectionEnd + (changeOffset <= selectionEnd ? delta : 0)); ta.oninput();
}
const isSeqFile = () => /\.(ampleseq|seq)$/i.test(SE.file || '');
/** Files with problems to show: the song (from the language server) and a sequence file (found here, while it is typed). */
const isComponentFile = () => /\.amplec$/i.test(SE.file || '');
const hasDiags = () => isSong() || isSeqFile() || isComponentFile();
const projectPaths = () => (SE.files || []).map((entry) => entry.path || entry).filter((path) => typeof path === 'string');
let componentCheckTimer = 0, componentCheckSeq = 0;
/** A component file has no language-server session: the server checks the text of the editor while it is typed (the parser's diagnostics, shown like those of a song). */
function refreshComponentDiagnostics() {
  clearTimeout(componentCheckTimer);
  componentCheckTimer = setTimeout(async () => {
    const seq = ++componentCheckSeq, file = SE.file, text = $('#se-ta') ? $('#se-ta').value : SE.text;
    let result;
    try { result = await api.post('/api/components/check-text', { text }); } catch { return; }
    if (seq !== componentCheckSeq || file !== SE.file) return;
    SE.diags = result.diagnostics || [];
    renderProblems(); gutter(); overlay(); status();
  }, 250);
}
function refreshSeqDiagnostics() {
  if (isComponentFile()) { refreshComponentDiagnostics(); return; }
  if (!isSeqFile()) return;
  SE.diags = seqDiagnostics($('#se-ta') ? $('#se-ta').value : SE.text, SE.files?.length ? projectPaths() : null);
  renderProblems(); gutter(); overlay(); status();
}
const isModuleSource = () => !!S.modulePath && SE.file === S.modulePath;

function blockAtOffset(text, offset, knownNames) {
  const declarations = /^[\t ]*[A-Za-z0-9_-]+\s+("(?:\\.|[^"\\])*"|[A-Za-z0-9_-]+)[^\n]*\{/gm;
  const matches = [];
  for (const match of text.matchAll(declarations)) {
    const open = match.index + match[0].lastIndexOf('{');
    let depth = 0, quote = false, escaped = false, comment = false, close = text.length;
    for (let i = open; i < text.length; i++) {
      const c = text[i];
      if (comment) { if (c === '\n') comment = false; continue; }
      if (quote) { if (escaped) escaped = false; else if (c === '\\') escaped = true; else if (c === '"') quote = false; continue; }
      if (c === '#') { comment = true; continue; }
      if (c === '"') { quote = true; continue; }
      if (c === '{') depth++;
      else if (c === '}' && --depth === 0) { close = i; break; }
    }
    if (offset > match.index && offset <= close) {
      let name = match[1];
      if (name.startsWith('"')) { try { name = JSON.parse(name); } catch { continue; } }
      if (knownNames.has(name)) matches.push({ name, span: close - open });
    }
  }
  return matches.sort((a, b) => a.span - b.span)[0]?.name || null;
}

/** The call of a dynamic helper (name(), hourShift(3)) under the caret that can be baked at the call site, or null. */
function bakeableCallAtCaret() {
  const ta = $('#se-ta');
  if (!ta || !isSong() || ta.selectionStart !== ta.selectionEnd || !S.doc || ta.value !== S.doc.text) return null;   // the server edits the applied text
  const names = new Set((S.doc.helpers || []).filter((h) => h.kind === 'helper' && h.dynamic && !h.baked && !(h.dependencies || []).includes('os-random')).map((h) => h.name.toLowerCase()));
  if (!names.size) return null;
  const { line, ch } = caretLine();
  const text = lines(ta.value)[line] || '';
  if (/^\s*(dynamic\s+)?helper\b/.test(text)) return null;
  const code = text.includes('#') ? text.slice(0, text.indexOf('#')) : text;
  for (const m of code.matchAll(/([A-Za-z_]\w*)\s*\(/g)) {
    if (ch < m.index || ch > m.index + m[1].length || !names.has(m[1].toLowerCase())) continue;   // the caret is on the name
    if (/\bbake\s+$/.test(code.slice(0, m.index))) return null;
    return { line: line + 1, col: m.index + 2, name: m[1] };
  }
  return null;
}

async function bakeCallAtCaret() {
  const call = bakeableCallAtCaret();
  if (!call) { toast('Put the cursor on the name of a call of a dynamic helper, like phase() or hourShift(3).'); return; }
  if (await edit([{ type: 'bakeCallSite', path: S.path, line: call.line, col: call.col }])) toast('Baked ' + call.name + '() here with the value of the last build. Undo takes it back.');
}

function updateBakeCall() {
  const button = $('#se-bakecall');
  if (button) button.style.display = bakeableCallAtCaret() ? '' : 'none';
}

function syncSourceCursor() {
  updateSig();
  updateBakeCall();
  if (!isSong() || (S.sourceEditMessage && !S.sourceEditMessage.startsWith('Place the text cursor'))) return;
  const nodes = flat().nodes;
  const name = blockAtOffset($('#se-ta')?.value || '', $('#se-ta')?.selectionStart || 0, new Set(nodes.map((node) => node.id)));
  const id = name || null;
  const selectionChanged = S.sel !== (id || null) || S.selSet.size !== (id ? 1 : 0);
  const message = id ? null : 'Place the text cursor inside a block to view its properties.';
  if (selectionChanged) select(id || null);
  if (S.sourceEditMessage !== message || selectionChanged) { S.sourceEditMessage = message; notifySelection(); }
}

function blockOpenOffset(text, target) {
  const declarations = /^[\t ]*[A-Za-z0-9_-]+\s+("(?:\\.|[^"\\])*"|[A-Za-z0-9_-]+)[^\n]*\{/gm;
  for (const match of text.matchAll(declarations)) {
    let name = match[1];
    if (name.startsWith('"')) { try { name = JSON.parse(name); } catch { continue; } }
    if (name === target) return match.index + match[0].lastIndexOf('{');
  }
  return -1;
}

function scheduleSourceSync() {
  if (!isSong()) return;
  const sequence = ++sourceParseSeq;
  clearTimeout(sourceParseTimer);
  sourceParseTimer = setTimeout(async () => {
    const text = $('#se-ta')?.value || '';
    try {
      const parsed = await api.post('/api/doc/parse', { text, path: S.path });
      if (sequence !== sourceParseSeq || text !== $('#se-ta')?.value) return;
      const errorCount = (parsed.diagnostics || []).filter((diagnostic) => diagnostic.severity === 'error').length;
      if (errorCount) {
        S.sourceEditMessage = `Properties are hidden while the source has ${errorCount} error${errorCount === 1 ? '' : 's'}. Fix the code to sync the block view.`;
        notifySelection();
        return;
      }
      S.sourceEditMessage = null;
      if (text !== S.doc?.text) {
        const doc = await applyText(text);
        if (!doc || sequence !== sourceParseSeq || text !== $('#se-ta')?.value) return;
        SE.syncedText = text;
      }
      syncSourceCursor();
    } catch { /* Diagnostics own invalid/incomplete source; a later edit retries the parse. */ }
  }, 450);
}

function syncPropertiesText() {
  if (!isSong() || !S.doc || !SE.syncedText || SE.text !== SE.syncedText || S.doc.text === SE.text) return;
  const name = S.sel;
  const ta = $('#se-ta');
  SE.text = S.doc.text;
  SE.syncedText = SE.text;
  ta.value = SE.text;
  if (name) {
    const open = blockOpenOffset(SE.text, name);
    if (open >= 0) ta.setSelectionRange(open + 1, open + 1);
  }
  ta.oninput?.();
  SE.syncedText = SE.text;
}

export const editorText = () => ($('#se-ta') ? $('#se-ta').value : SE.text);

let draftTimer = 0;
function saveSourceDraft() {
  clearTimeout(draftTimer);
  const text = $('#se-ta')?.value ?? SE.text;
  if (!SE.file || SE.textFile !== SE.file || !SE.edit || text === SE.orig || S.peek) return;
  try { saveRecoveryDraft(localStorage, S.project?.id || '', SE.file, text, SE.orig, Date.now()); } catch { /* storage can be full */ }
}
function saveSourceDraftSoon() { clearTimeout(draftTimer); draftTimer = setTimeout(saveSourceDraft, 250); }
function clearSourceDraft(path) { try { removeRecoveryDraft(localStorage, S.project?.id || '', path); } catch { /* local storage can be unavailable */ } }
export async function saveRecoveryDraftsForCheckpoint() {
  saveSourceDraft();
  const drafts = listRecoveryDrafts(localStorage, S.project?.id || '');
  const pending = [];
  const conflicts = [];
  for (const draft of drafts) {
    if (draft.text === draft.baseText) { clearSourceDraft(draft.path); continue; }
    if (/\.amplet$/i.test(draft.path)) {
      let current;
      try { current = await api.get(`/api/doc?path=${encodeURIComponent(draft.path)}`); } catch (e) { if (/There is no file/.test(e.message)) continue; throw e; }   // moved or removed file: its draft stays, the checkpoint goes on
      if (current.text !== draft.baseText && current.text !== draft.text) { conflicts.push(draft.path); continue; }   // asked below, once for all of them
      pending.push({ draft, current, kind: 'song' });
    } else if (/^modules\/.*\.module$/i.test(draft.path)) {
      const current = await api.get(`/api/module/source?path=${encodeURIComponent(draft.path)}`);
      if (current.text !== draft.baseText && current.text !== draft.text) { conflicts.push(draft.path); continue; }   // asked below, once for all of them
      pending.push({ draft, current, kind: 'module' });
    } else {
      const current = await api.get(`/api/file?path=${encodeURIComponent(draft.path)}`);
      if (current.text !== draft.baseText && current.text !== draft.text) { conflicts.push(draft.path); continue; }   // asked below, once for all of them
      pending.push({ draft, current, kind: 'file' });
    }
  }
  if (conflicts.length) {
    // a file changed on disk since its unsaved text was kept: the person chooses what the checkpoint does with it
    const list = conflicts.map((path) => `- ${path}`).join('\n');
    const choice = await ask('Unsaved text no longer fits the saved file', `These files changed since their unsaved text was kept:\n${list}\n\nWhat should the checkpoint do?`, [
      { label: 'Checkpoint what is saved', value: 'skip', primary: true },
      { label: 'Discard the unsaved text and checkpoint', value: 'discard' },
      { label: 'Cancel', value: undefined },
    ]);
    if (!choice) throw new Error('Checkpoint cancelled: review those files first.');
    if (choice === 'discard') for (const path of conflicts) clearSourceDraft(path);
  }
  for (const { draft, current, kind } of pending) {
    if (kind === 'song') {
      if (current.text !== draft.text) await api.put('/api/doc', { path: draft.path, text: draft.text, baseVersion: current.version });
      await api.post('/api/doc/save', { path: draft.path, suppressCheckpoint: true });
    } else if (kind === 'module') {
      if (current.text !== draft.text) await api.put('/api/module/source', { path: draft.path, text: draft.text, baseVersion: current.version });
      await api.post('/api/module/doc/save', { path: draft.path });
    } else if (current.text !== draft.text) await api.put('/api/file', { path: draft.path, text: draft.text });
    clearSourceDraft(draft.path);
    if (draft.path === SE.file) { SE.text = SE.orig = draft.text; if ($('#se-ta')) $('#se-ta').value = draft.text; }
  }
  if (drafts.some((draft) => draft.path === S.path)) await (await import('./store.js')).reloadDoc();
  if (drafts.some((draft) => draft.path === S.modulePath)) await refreshActiveModule();
}
async function recoverSourceDraft(path, savedText) {
  const draft = getRecoveryDraft(localStorage, S.project?.id || '', path);
  const accepted = consumeRecoveryAcceptance(localStorage, S.project?.id || '', path, 'source', draft?.updated, savedText);
  if (!draft || typeof draft.text !== 'string' || draft.text === savedText) { if (draft) clearSourceDraft(path); return savedText; }
  if (accepted) return draft.text;
  // no question: the unsaved text of this browser is what opens (marked unsaved); save it, or close the tab to discard it
  toast(`Your unsaved changes in ${path.split('/').pop()} (${new Date(draft.updated).toLocaleString()}) are back. Save them, or close the tab to discard them.`);
  return draft.text;
}
export function showRecoveryDiff(path, savedText, draftText, when, kind) {
  return new Promise((resolve) => {
    const dialog = el('dialog', { className: 'recovery-diff-dialog', 'aria-label': `Recovery diff for ${path}` });
    // only the file that is being opened: its saved text against its recovered text. What you choose replaces the recovered data of this file (a restored text is an unsaved change like any other).
    const close = (value) => { dialog.close(); dialog.remove(); resolve(value); };
    const count = (text) => String(text).split('\n').length;
    const headInfo = el('p'), headStats = el('p', { className: 'recovery-diff-summary' });
    const heading = el('div', {}, [el('h2', { textContent: 'Recover unsaved work?' }), headInfo, headStats]);
    dialog.append(el('div', { className: 'recovery-diff-head' }, [heading]));
    const body = el('div', { className: 'recovery-diff-body' });
    const panes = el('div', { className: 'recovery-diff-panes' });
    const unified = el('section', { className: 'recovery-diff-unified' }, [el('h3', { textContent: 'Combined changes' })]);
    const unifiedCode = el('div', { className: 'recovery-diff-code' });
    unified.append(unifiedCode);
    const STORE = 'ample.recoveryDiffView';
    let view = ''; try { view = localStorage.getItem(STORE) || ''; } catch { /* ignore */ }
    if (view !== 'split' && view !== 'unified') view = matchMedia('(min-width: 1100px)').matches ? 'split' : 'unified';
    const toggle = el('div', { className: 'recovery-diff-toggle', role: 'group', 'aria-label': 'Diff view' });
    const setView = (v) => {
      view = v; panes.dataset.view = v;
      try { localStorage.setItem(STORE, v); } catch { /* ignore */ }
      for (const btn of toggle.children) btn.setAttribute('aria-pressed', String(btn.dataset.view === v));
    };
    [['split', 'Side by side'], ['unified', 'Combined']].forEach(([v, label]) => toggle.append(el('button', { type: 'button', textContent: label, 'data-view': v, onclick: () => setView(v) })));
    const toolbarName = el('span', { textContent: path }), toolbar = el('div', { className: 'recovery-diff-toolbar' }, [toolbarName, toggle]);
    const saved = el('section', {}, [el('h3', { textContent: 'Last saved' })]), recovered = el('section', {}, [el('h3', { textContent: 'Recovered work' })]);
    const savedCode = el('div', { className: 'recovery-diff-code' }), recoveredCode = el('div', { className: 'recovery-diff-code' });
    const row = (side, line) => {
      const present = side === 'saved' ? line.op !== '+' : line.op !== '-';
      const number = side === 'saved' ? line.oldNumber : line.newNumber;
      return el('span', { className: `recovery-diff-line${present && line.op !== ' ' ? ` ${line.op === '+' ? 'added' : 'removed'}` : ''}` }, [el('span', { className: 'recovery-diff-number', textContent: number ?? '' }), el('span', { className: 'recovery-diff-marker', textContent: present ? line.op : '' }), el('span', { className: 'recovery-diff-text', textContent: present ? line.text : '' })]);
    };
    const showDiff = (shownPath, savedT, draftT, shownWhen) => {
      const diff = lineDiff(savedT, draftT), stats = diffStats(diff);
      toolbarName.textContent = shownPath;
      headInfo.textContent = `${shownPath} · last saved ${new Blob([savedT]).size.toLocaleString()} bytes / ${count(savedT)} lines · recovered ${new Blob([draftT]).size.toLocaleString()} bytes / ${count(draftT)} lines${shownWhen ? ` · draft from ${shownWhen}` : ''}`;
      headStats.textContent = `Changes: +${stats.added} lines, −${stats.removed} lines`;
      savedCode.replaceChildren(); recoveredCode.replaceChildren(); unifiedCode.replaceChildren();
      for (const hunk of diffHunks(diff, 3)) for (const line of hunk.lines) {
        savedCode.append(row('saved', line)); recoveredCode.append(row('recovered', line));
        const cls = line.op === '+' ? ' added' : line.op === '-' ? ' removed' : '';
        unifiedCode.append(el('span', { className: `recovery-diff-line recovery-diff-uline${cls}` }, [el('span', { className: 'recovery-diff-number', textContent: line.oldNumber ?? '' }), el('span', { className: 'recovery-diff-number', textContent: line.newNumber ?? '' }), el('span', { className: 'recovery-diff-marker', textContent: line.op === ' ' ? '' : line.op }), el('span', { className: 'recovery-diff-text', textContent: line.text })]));
      }
    };
    showDiff(path, savedText, draftText, when);
    saved.append(savedCode); recovered.append(recoveredCode); panes.append(saved, recovered, unified);
    const main = el('div', { className: 'recovery-diff-main' }, [toolbar, panes]);
    body.append(main); dialog.append(body); setView(view);
    dialog.append(el('div', { className: 'recovery-diff-actions' }, [
      el('button', { type: 'button', textContent: 'Continue with saved file', onclick: () => close('saved') }),
      el('button', { type: 'button', className: 'primary', textContent: 'Restore recovered work', onclick: () => close('restore') }),
    ]));
    dialog.addEventListener('cancel', (event) => { event.preventDefault(); close('cancel'); });   // Esc: leave without choosing; nothing is restored, discarded or remembered
    document.body.append(dialog);
    dialog.showModal();
  });
}
async function confirmEditorSwitch(path) {
  if (!SE.file || SE.text === SE.orig) return true;
  // the text is already kept as a recovery draft of this file (Koda hands the files it changed over that way): going to another file loses nothing, so there is nothing to ask
  const kept = getRecoveryDraft(localStorage, S.project?.id || '', SE.file);
  if (kept && kept.text === SE.text) return true;
  const choice = await ask('Unsaved changes in this file', `${SE.file} has changes that are not applied. Keep a recovery draft and open ${path}, discard them, or stay here?`, [
    { label: 'Cancel', value: 'cancel' },
    { label: 'Save and open', value: 'save', primary: true },
    { label: 'Keep draft and open', value: 'keep' },
    { label: 'Discard and open', value: 'discard' },
  ]);
  if (choice === 'save') {
    if (!(await apply())) return false;
    if (isSong()) { await api.post('/api/doc/save', { path: S.path }); await (await import('./store.js')).reloadDoc(); }
    if (isModuleSource()) await api.post('/api/module/doc/save', { path: SE.file });
    return true;
  }
  if (choice === 'keep') { saveSourceDraft(); return true; }
  if (choice === 'discard') { clearSourceDraft(SE.file); return true; }
  return false;
}
addEventListener('pagehide', saveSourceDraft);

/* ---------- loading ---------- */
// a load that is overtaken by a newer one (a quick tab or view switch) must not put its text into the state of the newer file
let loadToken = 0;
async function loadFile(file) {
  const loadMine = ++loadToken;
  SE.textFile = null;   // SE.text and SE.orig belong to no file until this load has finished; nothing is written meanwhile
  sourceParseSeq++;
  clearTimeout(sourceParseTimer);
  S.sourceEditMessage = null;
  SE.file = file;
  SE.edit = file !== 'ample.lock';
  if (isModuleSource()) {
    const r = await api.get(`/api/module/source?path=${encodeURIComponent(file)}`);
    const drafted = await recoverSourceDraft(file, r.text);
    if (loadMine !== loadToken) return;
    SE.text = drafted;
    SE.orig = r.text;
  } else if (isSong()) {
    const drafted = await recoverSourceDraft(file, S.doc.text);
    if (loadMine !== loadToken) return;
    SE.text = drafted;
    SE.orig = S.doc.text;
    SE.syncedText = SE.text;
    const errorCount = (S.doc.diagnostics || []).filter((diagnostic) => diagnostic.severity === 'error').length;
    if (errorCount) S.sourceEditMessage = `Properties are hidden while the source has ${errorCount} error${errorCount === 1 ? '' : 's'}. Fix the code to sync the block view.`;
  } else {
    const r = await api.get(`/api/file?path=${encodeURIComponent(file)}`);
    const drafted = await recoverSourceDraft(file, r.text);
    if (loadMine !== loadToken) return;
    SE.text = drafted;
    SE.orig = r.text;
    // an unsaved draft is the working copy of the file too: its tab shows the unsaved mark, and a build that uses the file reads it (this is how the files that Koda changed arrive)
    if (SE.text !== r.text) api.put('/api/wc', { path: file, text: SE.text }).catch(() => {});
  }
  SE.textFile = file;
  rememberRecentFile(file);
  SE.diags = []; SE.allDiags = [];
  if (isSong()) {
    if (SE.lsp) SE.lsp.close();
    SE.lsp = new LspClient(`file:///${encodeURIComponent(file)}`);
    SE.offline = false;
    SE.lsp.onDiagnostics = (d) => { SE.allDiags = d; showDiagnostics(); };
    SE.lsp.onOffline = () => {
      if (SE.offline || !isSong()) return;
      SE.offline = true;
      toast('The language server is not reachable: only the checks of the browser are shown. AMPLE keeps trying to reconnect.');
      localDiagnostics();
      // the socket drops when AMPLE restarts (a deploy) or the connection hiccups: try again every few seconds instead of staying offline until the page is reloaded
      const client = SE.lsp;
      let tries = 0;
      const again = async () => {
        if (SE.lsp !== client || client.closing || !isSong() || !SE.offline) return;
        if (++tries > 20) return;
        try { await client.open(SE.text); SE.sentText = SE.text; SE.offline = false; toast('The language server is back.'); }
        catch { setTimeout(again, Math.min(15000, 2000 * tries)); }
      };
      setTimeout(again, 2000);
    };
    SE.sentText = SE.text;
    SE.lsp.open(SE.text).catch(() => SE.lsp?.onOffline());
  }
}

async function files() {
  try { SE.files = await api.get('/api/project/files'); } catch { SE.files = []; }
}

/* ---------- building the view ---------- */
let building = null;
async function build() {
  const se = $('#se');
  se.innerHTML = '';
  const ed = el('div', { className: 'seedit' });
  const bar = el('div', { className: 'bmbar', id: 'se-bar' });
  const body = el('div', { className: 'sebody' });
  const gut = el('pre', { className: 'gutter', id: 'se-gut' });
  const wrap = el('div', { className: 'sewrap' });
  const hl = el('pre', { id: 'se-hl' });
  const lk = el('pre', { id: 'se-links' });
  const ghost = el('pre', { id: 'se-ghost', 'aria-hidden': 'true' });
  const ta = el('textarea', { id: 'se-ta', spellcheck: false, wrap: 'off' });
  ta.style.display = 'none';
  const cmHost = el('div', { className: 'se-cm', id: 'se-cm' });
  setEditorFontSize(SE.fontSize);
  body.style.setProperty('--se-font-size', `${SE.fontSize}px`);
  body.style.setProperty('--se-line-height', `${textLine()}px`);
  cmHost.addEventListener('wheel', (e) => {
    if (!e.ctrlKey) return;
    e.preventDefault();
    if (e.deltaY) setEditorFontSize(SE.fontSize + (e.deltaY < 0 ? 1 : -1));
  }, { capture: true, passive: false });
  let editorMade; SE.cmReady = new Promise((resolve) => { editorMade = resolve; });   // a second render() sees the textarea at once, but the editor behind it is not made yet
  try {
    const codeMirror = await import('../vendor/codemirror-ample.js');
    const made = codeMirror.createAmpleEditor(cmHost, ta, SE.text);
    const { installTouchMenu, installKeyboardGuard } = await import('./editor-touch-menu.js');
    if (made?.view) { installTouchMenu(made.view); installKeyboardGuard(made.view, closeComp); }   // a long press on a phone: copy, cut, paste, select all; selecting text brings no keyboard and closes the suggestions
  } finally { editorMade(); }
  ta.oninput = () => { setTimeout(updateSig, 0); setTimeout(updateBakeCall, 0); SE.text = ta.value; saveSourceDraftSoon(); syncSoon(); refreshSeqDiagnostics(); gutter(); overlay(); status(); refreshSaveControls(); if (isSong()) { S.sourceEditMessage = 'Checking source...'; scheduleSourceSync(); notifySelection(); } clearTimeout(SE.timer); SE.timer = setTimeout(() => { SE.timer = 0; if (isSong() && SE.offline) localDiagnostics(); else if (isSong() && SE.lsp) { SE.sentText = ta.value; SE.lsp.change(ta.value).catch(() => {}); } }, 250); };
  ta.onselectionchange = syncSourceCursor;
  ta.onscroll = () => { gut.scrollTop = ta.scrollTop; hl.scrollTop = ta.scrollTop; hl.scrollLeft = ta.scrollLeft; lk.scrollTop = ta.scrollTop; lk.scrollLeft = ta.scrollLeft; ghost.scrollTop = ta.scrollTop; ghost.scrollLeft = ta.scrollLeft; };
  ta.onclick = async (e) => { if (e.ctrlKey || e.metaKey) { e.preventDefault(); await ctrlClick(); } else closeComp(); };
  ta.onmousemove = async (e) => { if (!(e.ctrlKey || e.metaKey) || !isSong()) { $('#se-tip').style.display = 'none'; ta.style.cursor = ''; return; } const t = await hoverAt(e); const tip = $('#se-tip'); ta.style.cursor = t ? 'pointer' : ''; if (t) { tip.textContent = t; tip.style.display = 'block'; tip.style.left = `${e.clientX + 14}px`; tip.style.top = `${e.clientY + 16}px`; } else tip.style.display = 'none'; };
  ta.onmouseleave = () => { hideDoc(); $('#se-tip').style.display = 'none'; if (timeTip) timeTip.style.display = 'none'; hideErrTipSoon(); };
  ta.addEventListener('mousemove', (e) => { showTimeTip(e); docHover(e); const ctrl = e.ctrlKey || e.metaKey; const i = lineAt(e); const built = buildMarks()[i], mark = built || diagMark(i); const note = mark?.head === 'Note'; if (ctrl && !note) return; if (note && !ctrl) { hideErrTipSoon(); return; } if (built || (mark && ctrl)) showErrTip(mark, i); else hideErrTipSoon(); });
  ta.addEventListener('input', hideErrTip);
  ta.addEventListener('input', hideDoc);
  ta.addEventListener('keydown', hideDoc);
  ta.addEventListener('pointerup', (e) => { if (e.pointerType === 'touch') docHover(e, true); });
  ta.addEventListener('pointerdown', (e) => { if (e.pointerType === 'touch' && docTipShown()) hideDoc(); });
  ta.addEventListener('contextmenu', (e) => { e.preventDefault(); openEditorMenu(e.clientX, e.clientY).catch((error) => toast(error.message || String(error))); });
  ta.addEventListener('beforeinput', () => { SE.leaving = ta.value[ta.selectionStart - 1] || ''; });   // the character a backspace is about to remove
  ta.addEventListener('input', (e) => ghostAfterInput(e));
  ta.addEventListener('blur', hideGhost);
  ta.onkeydown = (e) => {
    if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && e.key.toLowerCase() === 'p') { e.preventDefault(); $('#se-switch')?.focus(); return; }
    const comp = $('#se-comp');
    if (SE.ghost && comp.style.display !== 'block') {
      if (e.key === 'Tab' && !e.shiftKey && !e.ctrlKey && !e.metaKey) { e.preventDefault(); acceptGhost(); return; }
      if (e.key === 'Escape') { e.preventDefault(); SE.quiet = { line: SE.ghost.line }; hideGhost(); return; }
      if (!['Shift', 'Control', 'Alt', 'Meta'].includes(e.key)) hideGhost();
    }
    if (comp.style.display === 'block') {
      if (e.key === 'Escape') { e.preventDefault(); SE.quiet = { line: caretLine().line }; closeComp(); return; }
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); moveComp(e.key === 'ArrowDown' ? 1 : -1); return; }
      if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); pickComp(); return; }
      // the editor deletes with its own key map (no input event comes of it): make the list again after it has done so
      if (e.key === 'Backspace' || e.key === 'Delete') setTimeout(() => refreshComp().catch(closeComp), 0);
    }
    if (e.key === 'Tab') { e.preventDefault(); ta.setRangeText('  ', ta.selectionStart, ta.selectionEnd, 'end'); ta.oninput(); }
    if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); apply(); }
    if ((e.ctrlKey || e.metaKey) && e.code === 'Space') { e.preventDefault(); SE.quiet = null; hideGhost(); openComp(); }
    if (e.key === 'F2') { e.preventDefault(); renameAtCaret(); }
  };
  addEventListener('keydown', (e) => { if (e.key === 'Control' || e.key === 'Meta') { SE.ctrl = true; paintLinks(); } });
  addEventListener('keyup', (e) => { if (e.key === 'Control' || e.key === 'Meta') { SE.ctrl = false; $('#se-tip').style.display = 'none'; paintLinks(); } });
  addEventListener('blur', () => { SE.ctrl = false; paintLinks(); });
  const dz = el('div', { id: 'se-drop', 'aria-hidden': 'true' });
  wrap.append(cmHost, hl, lk, ghost, ta, dz);
  // the same as Ctrl+Space, for a screen without a keyboard: a button (no text) in the corner of the text
  const suggest = el('button', { id: 'se-suggest', type: 'button', className: 'se-suggest', title: 'Suggestions (Ctrl+Space)', 'aria-label': 'Show suggestions' });
  suggest.innerHTML = '<svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h16M4 12h10M4 18h13"/><path d="M19 11v6m-3-3h6" opacity="0"/></svg>';
  suggest.onpointerdown = (e) => e.preventDefault();   // the text keeps its focus and caret
  suggest.onclick = () => { SE.compNoFocus = !document.querySelector('#se-cm .cm-content:focus'); SE.quiet = null; hideGhost(); openComp(); };   // no focus: on a phone that would bring the keyboard up
  wrap.append(suggest);
  // a file dragged over the text: the places where it fits light up, the nearest one is marked
  ta.ondragover = (e) => dragOver(e);
  ta.ondragenter = (e) => dragOver(e);
  ta.ondragleave = () => clearDropTargets();
  ta.ondrop = (e) => { e.preventDefault(); dropFile(e).catch((error) => toast(error.message || String(error))); };
  body.append(gut, wrap);
  bar.append(
    buildSwitcher(),
    el('b', { id: 'se-title' }),
    el('button', { id: 'se-close-file', textContent: '×', title: 'Close this source file and return to the song', 'aria-label': 'Close current source file', onclick: closeCurrentSourceFile }),
    el('button', { textContent: 'Apply (Ctrl+Enter)', className: 'primary', id: 'se-apply', onclick: apply }),
    el('button', { textContent: 'Revert', id: 'se-revert', onclick: revert }),
    createSaveControl(),
    el('button', { textContent: 'Check', id: 'se-check', title: 'Shows what the language server found', onclick: () => showCheck() }),
    el('button', { textContent: 'CLI output', id: 'se-cli', title: 'What ample check would print', onclick: showCli }),
    el('button', { textContent: 'Fix all', id: 'se-fixall', title: 'Apply every safe quick fix', onclick: fixAll }),
    el('button', { textContent: 'Format', id: 'se-format', onclick: format }),
    el('button', { textContent: 'Bake this call', id: 'se-bakecall', style: 'display:none', title: 'The cursor is on the name of a call of a dynamic helper, like phase() or hourShift(3): freeze the value the last build had, right here (the helper itself stays dynamic)', onclick: bakeCallAtCaret }),
    el('button', { textContent: 'Show ignored', id: 'se-unignore', style: 'display:none', title: 'Show the warnings and notes that were ignored in this browser again', onclick: () => { ignoredThisSession.clear(); showDiagnostics(); } }),
    el('button', { textContent: 'Rename...', id: 'se-rename', title: 'Rename the block under the cursor everywhere (F2)', onclick: renameAtCaret }),
    el('button', { textContent: 'Insert file...', id: 'se-insertfile', title: 'Put a project file in the song at the cursor: a path in the line it fits, or a new block', onclick: () => insertFileAtCaret().catch((error) => toast(error.message || String(error))) }),
    el('button', { textContent: 'AI ▾', id: 'se-ai', title: 'Ask Codex (only when you ask: nothing changes until you accept a proposal)', onmousedown: () => { SE.aiSel = editorSelection(); }, onclick: (event) => { const r = event.currentTarget.getBoundingClientRect(), snap = SE.aiSel !== undefined ? SE.aiSel : editorSelection(); SE.aiSel = undefined; import('./ai.js').then((m) => m.openAiMenu(r.left, r.bottom + 2, { context: isSeqFile() ? 'sequence' : 'source', path: SE.file || S.path, song: S.path, selection: () => snap })).catch((error) => toast(error.message || String(error))); } }),
    el('button', { textContent: 'Show block on the canvas', title: 'Select the block under the cursor on the canvas', id: 'se-show', onclick: showBlockFromCaret }),
    el('button', { textContent: 'Reference', title: 'Language reference: syntax, block types, diagnostics, lint rules', onclick: () => window.open('/help', '_blank') }),
    el('span', { className: 'note', id: 'se-status', style: 'margin:0' }),
  );
  const prob = el('div', { className: 'seprob', id: 'se-prob' });
  ed.append(bar, body, el('div', { id: 'se-comp' }), el('div', { id: 'se-sig' }), problemGrip(prob), prob);
  se.append(ed);
}

const PROB_HEIGHT = 'ample.sourceProblemsHeight';

/** The bar above the problem list: drag it to show more or fewer warnings (the list scrolls when it is taller than the space); the height is remembered. */
function problemGrip(prob) {
  try { const saved = Number(localStorage.getItem(PROB_HEIGHT)); if (saved >= 40) prob.style.maxHeight = `${saved}px`; } catch { /* storage may be blocked */ }
  const grip = el('div', { className: 'seprob-grip', role: 'separator', 'aria-orientation': 'horizontal', 'aria-label': 'Resize the list of warnings', title: 'Drag to show more or fewer warnings' });
  grip.onpointerdown = (down) => {
    down.preventDefault();
    grip.setPointerCapture(down.pointerId);
    const startY = down.clientY, start = prob.getBoundingClientRect().height;
    const limit = () => Math.max(120, Math.round(prob.parentElement.getBoundingClientRect().height * 0.7));
    grip.onpointermove = (move) => { prob.style.maxHeight = `${Math.round(Math.min(limit(), Math.max(40, start + startY - move.clientY)))}px`; };
    grip.onpointerup = () => {
      grip.onpointermove = grip.onpointerup = null;
      try { localStorage.setItem(PROB_HEIGHT, String(parseInt(prob.style.maxHeight, 10))); } catch { /* storage may be blocked */ }
    };
  };
  return grip;
}

async function ensureBuilt() {
  if ($('#se-ta')) return;
  if (!building) building = build();
  try { await building; } catch (error) { building = null; throw error; }
}

/* ---------- rendering ---------- */
async function render() {
  await ensureBuilt();
  if (!S.path && !S.modulePath) {
    SE.files = [];
    $('#se-title').textContent = 'No song loaded';
    $('#se-status').textContent = S.project ? 'Create a song to edit source.' : 'Reopen AMPLE from its secure launch link.';
    $('#se-ta').readOnly = true;
        return;
  }
  if (!SE.file || (isSong() && SE.loadedFor !== `${S.path}`)) { await loadFile(S.path); SE.loadedFor = S.path; }
  await files();
  syncPropertiesText();
  refreshSeqDiagnostics();
  const ta = $('#se-ta');
  if (ta.value !== SE.text) ta.value = SE.text;
  ta.readOnly = !SE.edit;
  $('#se-title').textContent = SE.file;
  // Koda can be pointed at the name of the song (Ctrl and drag, or two taps and drag): then it looks at the whole file, as it does at the breadcrumb of the blocks view
  if (isSong()) { $('#se-title').setAttribute('data-koda-song', ''); $('#se-title').title = 'Ctrl+drag Koda here: it looks at the whole song'; } else { $('#se-title').removeAttribute('data-koda-song'); $('#se-title').removeAttribute('title'); }
  $('#se-close-file').hidden = isSong();
    renderProblems();
  gutter();
  overlay();
  status();
  if (isSong() && !S.sourceEditMessage) syncSourceCursor();
}

/** Opens a text file or another song of the project in the editor (asks first when this file has changes that were not applied). */
async function openFileByName(k) {
      if (/\.amplet$/.test(k) && k !== S.path) {
        // another song is not served by /api/file: the open handler switches S.path and the editor reloads then
        document.dispatchEvent(new CustomEvent('ample:open', { detail: k }));
        return;
      }
      if (!(await confirmEditorSwitch(k))) return;
      if (!isEditableSourceFile(k) && !/\.amplet$/i.test(k)) { toast(`${k} is not a text file; open its block or use it as an audio source.`); return; }
      SE.loadedFor = '';
      SE.file = null;
      await loadFile(k);
      SE.loadedFor = k === S.path ? S.path : '';
      render();
}

async function closeCurrentSourceFile() {
  if (!SE.file || isSong()) { setView('blocks'); return; }
  if (!(await confirmEditorSwitch(S.path || 'the node editor'))) return;
  if (S.path) {
    SE.loadedFor = '';
    await loadFile(S.path);
    SE.loadedFor = S.path;
    render();
  } else setView('blocks');
}

/** The quick switcher: type a few letters of a file, arrows and Enter open it. Ctrl+P puts the cursor in it. */
function buildSwitcher() {
  const input = el('input', { id: 'se-switch', type: 'search', placeholder: 'Open a file... (Ctrl+P)', 'aria-label': 'Open a file of the project', autocomplete: 'off', role: 'combobox', 'aria-expanded': 'false', 'aria-controls': 'se-switchlist' });
  const list = el('div', { id: 'se-switchlist', role: 'listbox', className: 'se-switchlist' });
  let items = [], at = 0, hideTimer = 0;   // hideTimer: the hide that a blur plans; a new focus or typing cancels it (else it hides the list that was just reopened)
  const hide = () => { list.style.display = 'none'; input.setAttribute('aria-expanded', 'false'); };
  const paint = () => {
    list.innerHTML = '';
    items.forEach((path, i) => {
      const row = el('div', { role: 'option', className: i === at ? 'on' : '', textContent: path });
      row.id = 'se-switch-' + i;
      row.onmousedown = (e) => { e.preventDefault(); choose(path); };
      list.append(row);
    });
    list.style.display = items.length ? 'block' : 'none';
    input.setAttribute('aria-expanded', String(items.length > 0));
    input.setAttribute('aria-activedescendant', items.length ? 'se-switch-' + at : '');
  };
  const choose = (path) => { hide(); input.value = ''; input.blur(); openFileByName(path).catch((error) => toast(error.message || String(error))); };
  const refresh = () => { clearTimeout(hideTimer); items = rankFiles(SE.files.map((f) => f.path).filter((path) => isEditableSourceFile(path) || /\.amplet$/i.test(path)), input.value, recentFilePaths()); at = 0; paint(); };
  input.onfocus = refresh;
  input.oninput = refresh;
  input.onblur = () => { clearTimeout(hideTimer); hideTimer = setTimeout(hide, 120); };
  input.onkeydown = (e) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); at = Math.min(items.length - 1, at + 1); paint(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); at = Math.max(0, at - 1); paint(); }
    else if (e.key === 'Enter') { e.preventDefault(); if (items[at]) choose(items[at]); }
    else if (e.key === 'Escape') { input.value = ''; hide(); input.blur(); }
  };
  return el('span', { className: 'se-switcher' }, [input, list]);
}


const sev = (d) => (d.severity === 1 ? 'error' : d.severity === 2 ? 'warning' : 'info');
const errCount = () => SE.diags.filter((d) => d.severity === 1).length;

function renderProblems() {
  const box = $('#se-prob');
  if (!box) return;
  box.innerHTML = '';
  if (!hasDiags()) { box.append(el('div', { className: 'note', textContent: 'No diagnostics for this file.' })); return; }
  if (!SE.diags.length) box.append(el('div', { className: 'note', textContent: 'No problems.' }));
  SE.diags.forEach((d) => {
    const r = el('div', { className: `seerr ${sev(d)}`, textContent: `${{ error: '● ', warning: '▲ ', info: 'ℹ ' }[sev(d)]}${d.range.start.line + 1}:${d.range.start.character + 1} ${d.code} ${d.message.split('\n')[0]}` });
    r.dataset.code = d.code;
    r.title = d.message;
    r.onclick = () => gotoLine(d.range.start.line + 1, d.range.start.character + 1, d.range.end.character + 1);
    r.ondblclick = () => {   // the details of the error (message, fixes, copy) in the same popup the hover used to open
      const line = d.range.start.line, mark = buildMarks()[line] || diagMark(line);
      if (!mark) return;
      showErrTip(mark, line);
      setTimeout(() => document.addEventListener('pointerdown', (ev) => { if (!ev.target.closest?.('#se-errtip')) hideErrTip(); }, { once: true, capture: true }), 0);
    };
    if (isSong() && /\.amplet$/i.test(SE.file || '') && /^L\d{3}$/.test(d.code) && !readOnly()) r.oncontextmenu = (e) => { e.preventDefault(); e.stopPropagation(); showMenu(e.clientX, e.clientY, [{ label: `Disable ${d.code} for this block`, fn: () => silenceLintInSource(d) }]); };
    box.append(r);
  });
}

// like the marks: handed to the editor in the next frame
let gutterFrame = 0, latestGutter = null;
function applyGutter(ta, value) {
  latestGutter = value;
  if (gutterFrame) return;
  gutterFrame = requestAnimationFrame(() => { gutterFrame = 0; if (latestGutter && typeof ta.setGutter === 'function') ta.setGutter(latestGutter); });
}

function gutter() {
  const ta = $('#se-ta');
  const g = $('#se-gut');
  if (!ta || !g) return;
  const n = lines(ta.value).length;
  const by = {};
  const noteLines = new Set();
  if (SE.edit) for (let i = 0; i < n; i++) if (hasValidInlineNotesAt(ta.value, i)) noteLines.add(i);
  if (hasDiags()) SE.diags.forEach((d) => { const l = d.range.start.line + 1; const rank = { error: 3, warning: 2, info: 1 }; if (!by[l] || rank[sev(d)] > rank[by[l]]) by[l] = sev(d); });
  Object.keys(buildMarks()).forEach((i) => { by[Number(i) + 1] = 'error'; });
  let s = '';
  for (let i = 1; i <= n; i++) s += `${noteLines.has(i - 1) ? '✎ ' : ({ error: '● ', warning: '▲ ', info: 'ℹ ' }[by[i]] || '  ')}${i}\n`;
  const openNotes = async (line) => {
    if (!noteLines.has(line)) return;
    await openMidiNotesAtLine(line, () => ta.value, (text) => { ta.value = text; ta.oninput(); });
  };
  if (typeof ta.setGutter === 'function') {
    // the numbers are the gutter of the editor itself (see ample-language.js); the old column stays out of the way
    g.style.display = 'none';
    const marks = {};
    Object.entries(by).forEach(([line, kind]) => { marks[line] = kind; });
    applyGutter(ta, { marks, notes: [...noteLines].map((i) => i + 1) });
    ta.onGutterClick = openNotes;
    return;
  }
  g.textContent = s;
  g.scrollTop = ta.scrollTop;
  g.style.cursor = noteLines.size ? 'pointer' : '';
  g.onclick = async (e) => {
    const line = Math.floor((e.clientY - g.getBoundingClientRect().top + g.scrollTop) / textLine());
    await openNotes(line);
  };
}

/* ---------- the red marks of a failed build ---------- */
/** The lines of the open song that a failed build points at and that were not edited since: { lineIndex: { blocks, messages, code } }. */
function buildMarks() {
  const b = S.buildErrors;
  const ta = $('#se-ta');
  if (!b || !ta || !isSong() || b.path !== S.path) return {};
  return liveMarks(b.items, lines(ta.value));
}

let errTipTimer = 0;
const hideErrTip = () => { clearTimeout(errTipTimer); const tip = $('#se-errtip'); if (tip) tip.style.display = 'none'; };
const hideErrTipSoon = () => { clearTimeout(errTipTimer); errTipTimer = setTimeout(hideErrTip, 280); };

/** The size the person gave the tip of an error by dragging its corner ({ w, h }), or null (it fits its text). */
function savedErrTipSize() {
  try { const v = JSON.parse(localStorage.getItem('ample.errtip.size') || 'null'); return v && v.w >= 240 && v.h >= 120 ? v : null; } catch { return null; }
}
function errTipFor() {
  let tip = $('#se-errtip');
  if (tip) return tip;
  tip = el('div', { id: 'se-errtip' });
  tip.onmouseenter = () => clearTimeout(errTipTimer);
  tip.onmouseleave = hideErrTipSoon;
  tip.addEventListener('mouseup', () => { try { if (tip.style.display !== 'none') localStorage.setItem('ample.errtip.size', JSON.stringify({ w: Math.round(tip.offsetWidth), h: Math.round(tip.offsetHeight) })); } catch { /* no storage: the size is not kept */ } });
  tip.addEventListener('dblclick', (e) => { if (e.target === tip) { try { localStorage.removeItem('ample.errtip.size'); } catch { /* nothing kept */ } tip.style.width = ''; tip.style.height = ''; } });
  document.body.append(tip);
  return tip;
}

/** The problems the language server found on a line (a squiggle), told in the same little box as a build error; none while the diagnostics wait for the text they belong to. */
function diagMark(line) {
  const ta = $('#se-ta');
  if (!ta || !(isSeqFile() || isSong() || isComponentFile()) || (isSong() && SE.lsp && SE.sentText !== ta.value)) return null;
  const found = SE.diags.filter((d) => d.range.start.line === line);
  if (!found.length) return null;
  const worst = found.some((d) => d.severity === 1) ? 'Error' : found.some((d) => d.severity === 2) ? 'Warning' : 'Note';
  const code = found.map((d) => (typeof d.code === 'object' ? d.code?.value : d.code)).find(Boolean);
  return { head: worst, blocks: [], messages: found.map((d) => d.message), code: code ? String(code) : '', hint: '', diags: found, line };
}
function showErrTip(mark, lineIndex) {
  clearTimeout(errTipTimer);
  const ta = $('#se-ta'), rect = ta.getBoundingClientRect(), tip = errTipFor();
  const message = mark.messages.join('\n');
  tip.replaceChildren(
    el('div', { className: 'et-head' }, [el('b', { textContent: mark.head || 'Build error' }), el('span', { className: 'et-blocks', textContent: mark.blocks.join(', ') }), mark.code ? el('span', { className: 'et-code', textContent: mark.code }) : '']),
    el('pre', { className: 'et-msg', textContent: message }),
    el('div', { className: 'et-foot' }, [
      el('button', { textContent: 'Copy message', onclick: () => navigator.clipboard.writeText(message).then(() => toast('Error copied.')) }),
      ...(mark.hint ? [el('span', { className: 'et-hint', textContent: mark.hint })] : []),   // only a hint that says something (not "fix the line and the mark goes")
    ]));
  // the fixes the server offers for this line, and the actions of a note bake: one click each, applied as one edit
  if (mark.diags && SE.lsp) {
    const foot = tip.querySelector('.et-foot'), shown = tip;
    SE.lsp.ask('textDocument/codeAction', { range: mark.diags[0].range, context: { diagnostics: mark.diags } }).then((actions) => {
      if (tip !== shown || !foot.isConnected) return;
      for (const action of (actions || []).filter((a) => a.kind === 'quickfix' && a.edit?.changes)) {
        foot.prepend(el('button', { className: 'et-fix', textContent: action.title, onclick: () => { applyEdits(Object.values(action.edit.changes).flat()); hideErrTip(); } }));
      }
    }).catch(() => {});
    const soft = mark.diags.filter((d) => d.severity > 1);
    if (soft.length && isSong()) {
      foot.append(
        el('button', { className: 'et-fix', textContent: 'Ignore this session', title: 'Hide it in this browser until the page is reloaded; the song is not changed', onclick: () => ignoreThisSession(soft) }),
        el('button', { className: 'et-fix', textContent: 'Suppress here', title: 'Write a comment (# ample-ignore CODE) so the song says it knows; an error is never suppressed', onclick: () => suppressHere(mark.line, soft) }),
      );
    }
    if (mark.diags.some((d) => String(typeof d.code === 'object' ? d.code?.value : d.code) === 'AMP2039')) {
      foot.prepend(el('button', { className: 'et-fix', textContent: 'Pick a seed now', onclick: async () => { try { const r = await api.post('/api/notes/random-seed', { path: S.path }); if (await applyText(r.text)) { hideErrTip(); toast(`Picked ${r.count} seed${r.count === 1 ? '' : 's'} and wrote ${r.count === 1 ? 'it' : 'them'} into the song: from now on it is reproducible.`); } } catch (error) { toast(error.message); } } }));
    }
    const bakeCode = mark.diags.map((d) => (typeof d.code === 'object' ? d.code?.value : d.code)).find((c) => ['AMP2030', 'AMP2031', 'AMP2034'].includes(String(c)));
    if (bakeCode) {
      const block = enclosingBlockName(SE.text, mark.line);
      if (block) {
        const run = async (detach) => { try { const r = await api.post('/api/notes/bake', { path: S.path, block, detach }); if (await applyText(r.text)) { hideErrTip(); toast(detach ? 'The clip is plain notes now.' : `Baked ${r.notes} notes into "${r.stage}".`); } } catch (error) { toast(error.message); } };
        foot.prepend(el('button', { className: 'et-fix', textContent: 'Turn into plain notes', onclick: () => run(true) }));
        foot.prepend(el('button', { className: 'et-fix', textContent: bakeCode === 'AMP2034' ? 'Bake it' : 'Bake again', onclick: () => run(false) }));
      }
    }
  }
  const size = savedErrTipSize();   // the size the person gave it by dragging its corner
  tip.style.width = size ? Math.min(size.w, window.innerWidth - 24) + 'px' : '';
  tip.style.height = size ? Math.min(size.h, window.innerHeight - 24) + 'px' : '';
  tip.style.display = 'flex';
  const top = rect.top + TEXT_TOP + (lineIndex + 1) * textLine() - ta.scrollTop + 4;
  const height = tip.offsetHeight;
  tip.style.left = Math.max(8, Math.min(rect.left + 24, window.innerWidth - tip.offsetWidth - 12)) + 'px';
  tip.style.top = (top + height > window.innerHeight - 8 ? Math.max(8, top - height - textLine() - 8) : top) + 'px';
}

/** The log of the build leads here: open the code with the line of this block's failure in view. */
export async function showBuildError(name) {
  const item = S.buildErrors?.items?.[name];
  if (!item) return;
  if (!setView('src')) return;
  await render();
  const marks = buildMarks();
  const at = Object.keys(marks).find((i) => marks[i].blocks.includes(name));
  gotoLine((at !== undefined ? Number(at) : item.line) + 1);
}

/** The "at X for Y" of a block header (bars): where it is in the text and the times in seconds, from the tempo of the song (4 beats a bar). */
// type name class[@version]: a component type has dots (koda.notes.echoNotes) and a class without a version (stable) is allowed
const BLOCK_HEAD = /^\s*[A-Za-z][\w.-]*\s+[A-Za-z][\w-]*\s+[A-Za-z][\w-]*(?:@[\d.]+)?\s/;
function timeSpanIn(line) {
  if (!BLOCK_HEAD.test(line)) return null;
  const m = /\bat\s+(\d+(?:\.\d+)?)\s+for\s+(\d+(?:\.\d+)?)\b/.exec(line);
  if (!m) return null;
  const at = Number(m[1]), len = Number(m[2]), spb = (barBeats() * 60) / (songTempo() || 120);
  const sec = (x) => { const v = Math.round(x * 10) / 10; const t = `${v} s`; return v >= 60 ? `${t} (${Math.floor(v / 60)}:${String(Math.round(v % 60)).padStart(2, '0')})` : t; };
  return { s: m.index, e: m.index + m[0].length, text: `start ${sec((at - 1) * spb)} · end ${sec((at - 1 + len) * spb)} · duration ${sec(len * spb)}` };
}
/** The tempo as typed in the project line of the text (what is typed counts at once), else the one the song was last read with. */
const songTempo = () => Number(new RegExp('^(?:composition|project)\\s[^{]*{[^}]*\\btempo\\s+(\\d+(?:\\.\\d+)?)', 'm').exec(SE.text || '')?.[1]) || Number(S.doc?.project?.tempo) || 0;
/* ---------- the tooltip of what is under the pointer: a helper or built-in with its signature, doc comment and parameters (a tap on a touch screen) ---------- */
let docTimer = 0, docKey = '', docWidth = { size: 0, w: 0 };
const hideDoc = () => { clearTimeout(docTimer); docKey = ''; hideDocTip(); };
function charWidth() {
  if (docWidth.size === SE.fontSize && docWidth.w) return docWidth.w;
  // measured inside the editor itself, so that it has the font of the editor (the size the editor really uses)
  const host = document.querySelector('.cm-content') || document.body;
  const probe = el('span', { textContent: 'x'.repeat(40), style: 'position:absolute;visibility:hidden;white-space:pre' });
  host.append(probe); const w = probe.getBoundingClientRect().width / 40; probe.remove();
  docWidth = { size: SE.fontSize, w };
  return w;
}
/** The word under a column; a quantity such as 2bars is one word (its unit is what is explained). */
function wordUnder(text, ch) {
  for (const m of text.matchAll(/[0-9.]*[A-Za-z_][A-Za-z0-9_-]*/g)) if (ch >= m.index && ch < m.index + m[0].length) return { word: m[0], start: m.index };
  return null;
}
/** Where a # comment starts in a line (outside a string, not right after a letter or digit), or -1. */
function commentAt(text) {
  let inString = false;
  for (let k = 0; k < text.length; k++) {
    if (text[k] === '"') inString = !inString;
    else if (!inString && text[k] === '#' && !(k > 0 && /[A-Za-z0-9]/.test(text[k - 1]))) return k;
  }
  return -1;
}
/** The hover text of a word of the song: from the language server, or built here (the same text) when it cannot be reached. Null when there is nothing to say. */
async function docTextAt(lineNo, ch) {
  const all = lines(String(SE.text || '').replace(/\r\n/g, '\n'));
  const text = all[lineNo] ?? '';
  const hit = wordUnder(text, ch);
  if (!hit || (commentAt(text) >= 0 && hit.start > commentAt(text))) return null;
  if (SE.lsp && !SE.offline) {
    try {
      const r = await SE.lsp.ask('textDocument/hover', { position: { line: lineNo, character: hit.start + 1 } });
      const value = r?.contents?.value;
      return value && value.startsWith('`') ? value : null;   // what the server says about a word of a block in general is not worth a tooltip
    } catch { /* offline: built here */ }
  }
  return offlineHover(all, lineNo, hit, text);
}
function offlineHover(all, lineNo, hit, text) {
  const docs = docsOf(all);
  const spotsTable = helpersOfText(blankBlocks(all.join('\n')).text);
  const spots = variableSpots(spotsTable);
  const variable = variableHover(spots, all, lineNo, hit.start + 1, hit.word, docs);
  if (variable) return variable;
  const blocks = S.doc?.blocks || [];
  const named = blocks.find((b) => b.name === hit.word && b.line === lineNo);
  if (named && docs.has(lineNo)) return `\`${named.type} ${named.name}\`\n\n${docMarkdown(docs.get(lineNo))}`;
  const lead = /^[0-9.]+/.exec(hit.word)?.[0] ?? '', word = hit.word.slice(lead.length);
  const after = text.slice(hit.start + hit.word.length), before = text.slice(0, hit.start + lead.length);
  const insideBlock = blocks.some((b) => b.line < lineNo && (b.closeLine == null || b.closeLine < 0 || b.closeLine >= lineNo));
  return hoverText(word, after, { helpers: helpersOfText(blankBlocks(all.join('\n')).text).signatures(), docs, catalog: NOTE_SIGNATURES, builtins: NOTE_BUILTINS, before, insideBlock, inferred: spots.returns });
}
function docHover(e, immediate = false) {
  const ta = $('#se-ta');
  if (!ta || !isSong() || e.ctrlKey || e.metaKey) { hideDoc(); return; }
  const r = ta.getBoundingClientRect();
  const lineNo = Math.floor((e.clientY - r.top + ta.scrollTop - TEXT_TOP) / textLine());
  const ch = Math.floor((e.clientX - r.left + ta.scrollLeft - 8) / charWidth());
  const hit = lineNo >= 0 && ch >= 0 ? wordUnder(lines(SE.text || '')[lineNo] || '', ch) : null;
  if (!hit) { hideDoc(); return; }
  const key = `${lineNo}:${hit.start}`;
  if (key === docKey) return;
  hideDoc();
  docKey = key;
  const x = e.clientX, y = e.clientY;
  const run = async () => { const text = await docTextAt(lineNo, hit.start + 1).catch(() => null); if (text && docKey === key) showDocTip(text, x, y); };
  if (immediate) run(); else docTimer = setTimeout(run, 380);
}

let timeTip = null;
function showTimeTip(e) {
  const ta = $('#se-ta');
  const hide = () => { if (timeTip) timeTip.style.display = 'none'; };
  if (!ta || !isSong() || e.ctrlKey || e.metaKey) { hide(); return false; }
  const r = ta.getBoundingClientRect();
  const lineNo = Math.floor((e.clientY - r.top + ta.scrollTop - TEXT_TOP) / textLine());
  const sp = lineNo >= 0 ? timeSpanIn(lines(SE.text)[lineNo] || '') : null;
  if (!sp) { hide(); return false; }
  const probe = el('span', { textContent: 'x', style: `position:absolute;visibility:hidden;font:${SE.fontSize}px ui-monospace,Consolas,monospace` });
  document.body.append(probe); const charW = probe.getBoundingClientRect().width; probe.remove();
  const ch = Math.floor((e.clientX - r.left + ta.scrollLeft - 8) / charW);
  if (ch < sp.s || ch >= sp.e) { hide(); return false; }
  if (!timeTip) { timeTip = el('div', { id: 'se-timetip', role: 'tooltip' }); document.body.append(timeTip); }
  timeTip.textContent = sp.text;
  timeTip.style.display = 'block';
  timeTip.style.left = `${Math.min(e.clientX + 12, innerWidth - 20 - timeTip.offsetWidth)}px`;
  timeTip.style.top = `${e.clientY + 18}px`;
  return true;
}

// The marks are handed to the editor in the next frame: a change of the editor in the middle of its own input event disturbs what is being typed, and several changes of the text in one frame need one set of marks.
let markFrame = 0, latestMarks = null;
function applyMarks(ta, marks) {
  latestMarks = marks;
  if (markFrame) return;
  markFrame = requestAnimationFrame(() => { markFrame = 0; if (latestMarks && typeof ta.setMarks === 'function') ta.setMarks(latestMarks); });
}

function overlay() {
  const ta = $('#se-ta');
  const hl = $('#se-hl');
  if (!ta || !hl) return;
  const by = {};
  // the diagnostics belong to the text the language server was sent last; when the text was replaced from outside (a block edited on the canvas, a revert) they point at the wrong lines, so they wait for fresh ones
  const stale = isSong() && SE.lsp && SE.sentText !== ta.value;
  if (stale && !SE.timer) { SE.sentText = ta.value; SE.lsp.change(ta.value).catch(() => {}); }
  if ((isSeqFile() || isSong() || isComponentFile()) && !stale) SE.diags.forEach((d) => { (by[d.range.start.line] = by[d.range.start.line] || []).push({ s: d.range.start.character, e: Math.max(d.range.start.character + 1, d.range.end.character), cls: `sq ${sev(d)}` }); });
  const texts = lines(ta.value);
  if (isSong()) texts.forEach((t, i) => { const sp = timeSpanIn(t); if (sp) (by[i] = by[i] || []).push({ s: sp.s, e: sp.e, cls: 'tm' }); });   // where a block sits in time: dotted underline, the seconds on hover
  Object.keys(buildMarks()).forEach((i) => { const t = texts[Number(i)] ?? ''; (by[Number(i)] = by[Number(i)] || []).push({ s: t.length - t.trimStart().length, e: Math.max(t.length, 1), cls: 'sq error build' }); });
  if (typeof ta.setMarks === 'function') {
    // the marks go into the editor itself (see ample-language.js): { from, to, cls } in offsets of the text
    const marks = [];
    let offset = 0;
    texts.forEach((t, i) => {
      for (const r of by[i] || []) {
        let from = Math.max(0, Math.min(r.s, t.length)), to = Math.max(from, Math.min(r.e, t.length));
        if (to === from && t.length > 0) { from = t.length - 1; to = t.length; }   // a problem at the very end of a line: the last character
        marks.push({ from: offset + from, to: offset + to, cls: r.cls });
      }

      offset += t.length + 1;
    });
    SE.diagMarks = marks;
    applyMarks(ta, marks.concat(SE.linkMarks || []));
    hl.textContent = '';
  } else hl.innerHTML = texts.map((t0, i) => {
    const rs = by[i];
    if (!rs) return escH(t0);
    const t = `${t0} `;
    const pts = new Set([0, t.length]);
    rs.forEach((r) => { r.s = Math.max(0, Math.min(r.s, t.length - 1)); r.e = Math.max(r.s + 1, Math.min(r.e, t.length)); pts.add(r.s); pts.add(r.e); });
    const P = [...pts].sort((a, b) => a - b);
    let out = '';
    for (let k = 0; k < P.length - 1; k++) {
      const cl = rs.filter((r) => r.s <= P[k] && r.e >= P[k + 1]).map((r) => r.cls).join(' ');
      const seg = escH(t.slice(P[k], P[k + 1]));
      out += cl ? `<span class="${cl}">${seg}</span>` : seg;
    }
    return out;
  }).join('\n');
  hl.scrollTop = ta.scrollTop;
  hl.scrollLeft = ta.scrollLeft;
  paintLinks();
}

/* ---------- dropping a file into the text (plan project-panel PP04) ---------- */
const TEXT_TOP = 6;
const textLine = () => SE.fontSize * 1.5;
function setEditorFontSize(size) {
  SE.fontSize = Math.max(8, Math.min(24, size));
  const line = `${SE.fontSize * 1.5}px`, font = `${SE.fontSize}px`;
  for (const node of [$('#se-cm'), $('.sebody')]) if (node) { node.style.setProperty('--se-font-size', font); node.style.setProperty('--se-line-height', line); }
  gutter(); overlay();
  if ($('#se-comp')?.style.display === 'block') placeComp($('#se-comp'));
}
let dropState = null;   // { kind, targets, path, isDir } for the file that is being dragged

function dragSource(e) {
  if (!isSong() || !SE.edit) return null;
  const inside = draggedFile();
  if (inside) return { kind: inside.kind, path: inside.path, isDir: inside.isDir };
  const outside = outsideDragKind(e);
  if (outside === null) return null;
  return { kind: outside || null, path: null, isDir: false, outside: true };
}

function lineAt(e) {
  const ta = $('#se-ta'), rect = ta.getBoundingClientRect();
  return Math.max(0, Math.floor((e.clientY - rect.top + ta.scrollTop - TEXT_TOP) / textLine()));
}

function dragOver(e) {
  const src = dragSource(e);
  if (!src) return;
  e.preventDefault();
  e.dataTransfer.dropEffect = 'copy';
  const ta = $('#se-ta');
  const kind = src.kind || null;
  const targets = kind ? dropTargets(ta.value, S.templates, kind) : { fields: [], inserts: [] };
  dropState = { ...src, targets };
  paintDropTargets(nearestTarget(targets, lineAt(e)));
}

function paintDropTargets(near) {
  const dz = $('#se-drop'), ta = $('#se-ta');
  if (!dz || !dropState) return;
  dz.innerHTML = '';
  const mark = (line, cls, between) => {
    const d = el('div', { className: `dt ${cls}` });
    d.style.top = `${TEXT_TOP + line * textLine() - ta.scrollTop - (between ? 1 : 0)}px`;
    dz.append(d);
  };
  for (const f of dropState.targets.fields) mark(f.line, near && near.type === 'field' && near.line === f.line ? 'field on' : 'field');
  for (const i of dropState.targets.inserts) mark(i.line, near && near.type === 'insert' && near.line === i.line ? 'insert on' : 'insert', true);
  if (!dropState.targets.fields.length && !dropState.targets.inserts.length) dz.append(el('div', { className: 'dt none', textContent: 'No block takes this file' }));
}

function clearDropTargets() { dropState = null; const dz = $('#se-drop'); if (dz) dz.innerHTML = ''; }

async function dropFile(e) {
  const src = dragSource(e);
  const line = lineAt(e);
  clearDropTargets();
  if (!src) return;
  let path = src.path, isDir = src.isDir, kind = src.kind;
  if (src.outside) {
    const files = [...(e.dataTransfer.files || [])];
    if (!files.length) return;
    // a file from outside is uploaded into the project first, in the folder of its kind
    kind = kindOf(files[0].name) || src.kind;
    const folder = (kind && UPLOAD_FOLDER[kind.id]) || 'assets';
    if (!kind) { toast('AMPLE does not know what to do with this kind of file.'); return; }
    const done = await uploadProjectFiles(files.slice(0, 1), folder, () => {});
    if (!done[0] || !done[0].path) return;
    path = done[0].path; isDir = false;
  }
  await placeFile(path, isDir, line, e.altKey);
}

/** Puts a project file in the text: into the line that fits at `line`, or as a new block near it. replace overwrites a path that is already there. */
async function placeFile(path, isDir, line, replace = false) {
  const ta = $('#se-ta');
  const kind = kindOf(path, isDir);
  if (!kind) { toast('This kind of file cannot be used in a song.'); return; }
  const targets = dropTargets(ta.value, S.templates, kind);
  const target = nearestTarget(targets, line);
  if (!target) { toast('No block takes this file here. Drop it on a file line, or between two blocks.'); return; }
  let result = applyDrop(ta.value, target, path, S.templates, { replace, isDir });
  if (result.needsConfirm) {
    const yes = await ask('Replace the file?', `This line points at ${result.existing}. Use ${path} instead?`, [{ label: 'Replace', value: true, primary: true }, { label: 'Keep', value: false }]);
    if (!yes) return;
    result = applyDrop(ta.value, target, path, S.templates, { replace: true, isDir });
  }
  if (result.error) { toast(result.error); return; }
  // one transaction of the editor, so one Ctrl+Z takes the drop back (execCommand does nothing on the hidden stand-in of the CodeMirror editor)
  ta.focus();
  ta.setRangeText(result.text, 0, ta.value.length, 'end');
  ta.oninput();
  const offset = result.text.split('\n').slice(0, result.line).join('\n').length + (result.line ? 1 : 0);
  ta.setSelectionRange(offset, offset);
  ta.scrollTop = Math.max(0, result.line * textLine() - ta.clientHeight / 2);
  toast(target.type === 'field' ? `Set ${target.param} of ${target.block} to ${path}` : `Added a block for ${path}`);
}

/** The keyboard way: choose a project file, and it goes where the cursor is (the line it fits, or a new block just below). */
async function insertFileAtCaret() {
  if (!isSong() || !SE.edit) { toast('Open a song to insert a file reference.'); return; }
  const ta = $('#se-ta');
  const line = ta.value.slice(0, ta.selectionStart).split('\n').length - 1;
  const path = await openProjectFilePicker('', [], 'file');
  if (!path) return;
  await placeFile(path, false, line, false);
}

function status() {
  const st = $('#se-status');
  if (!st) return;
  const dirty = SE.edit && SE.text !== SE.orig;
  refreshSaveControls();
  $('#se-apply').disabled = !SE.edit || !dirty;
  $('#se-revert').disabled = !SE.edit || !dirty;
  const song = isSong();
  ['se-check', 'se-fixall', 'se-format', 'se-rename', 'se-cli', 'se-show'].forEach((id) => { $(`#${id}`).style.display = song ? '' : 'none'; });
  const e = errCount();
  const stability = song && S.doc?.stability && S.doc.stability !== 'stable' ? ' · ' + ({ dynamic: 'dynamic (not reproducible until baked)', 'baked-dynamic': 'baked dynamic', stale: 'a bake is stale' }[S.doc.stability] || S.doc.stability) : '';
  st.textContent = song || isSeqFile() ? `${e} error(s), ${SE.diags.length - e} other${dirty ? ' · not applied' : ''}${stability}` : dirty ? 'not applied' : '';
  st.title = song ? (S.doc?.stabilityNotes || []).join('\n') : '';
}

const summary = () => `${errCount()} error(s), ${SE.diags.length - errCount()} other.`;

/** Check: nothing found is a short note; otherwise a window with every finding (the line, the code, the whole message), a click goes to the line. */
function showCheck() {
  if (!SE.diags.length) { toast(hasDiags() ? 'No problems found.' : 'No diagnostics for this file.'); return; }
  const count = (s) => SE.diags.filter((d) => sev(d) === s).length;
  const list = el('div', { className: 'check-list' }, SE.diags.map((d) => {
    const [first, ...more] = d.message.split('\n');
    const row = el('button', { type: 'button', className: `check-row ${sev(d)}`, title: 'Go to this line' }, [
      el('span', { className: 'check-mark', textContent: { error: '●', warning: '▲', info: 'ℹ' }[sev(d)] }),
      el('span', { className: 'check-pos', textContent: `${d.range.start.line + 1}:${d.range.start.character + 1}` }),
      el('span', { className: 'check-code', textContent: String(d.code ?? '') }),
      el('span', { className: 'check-msg' }, [first, ...(more.length ? [el('small', { textContent: more.join(' ') })] : [])]),
    ]);
    row.onclick = () => { $('#dlg')?.close(); gotoLine(d.range.start.line + 1, d.range.start.character + 1, d.range.end.character + 1); };
    return row;
  }));
  ask('Check', `${count('error')} error(s), ${count('warning')} warning(s), ${count('info')} note(s)${SE.file ? ' in ' + SE.file.split('/').pop() : ''}. Click one to go to its line.`, [{ label: 'Close', value: null, primary: true }], [list]);
}

function showCli() {
  const text = SE.diags.map((d) => `${SE.file}:${d.range.start.line + 1}:${d.range.start.character + 1}: ${sev(d)} ${d.code} ${d.message.split('\n')[0]}`).join('\n') || `${SE.file}: no problems`;
  ask('ample check', 'This is what the command line tool prints for this text.', [{ label: 'Close', value: true, primary: true }], [el('pre', { textContent: text, style: 'width:100%;max-height:260px;overflow:auto;font:12px ui-monospace,Consolas,monospace' })]);
}

/* ---------- moving around ---------- */
export function gotoLine(line, col = 1, endCol = col) {
  const ta = $('#se-ta');
  if (!ta) return;
  const ls = lines(ta.value);
  let pos = 0;
  for (let i = 0; i < line - 1 && i < ls.length; i++) pos += ls[i].length + 1;
  ta.focus();
  ta.setSelectionRange(pos + col - 1, pos + Math.max(col, endCol) - 1);
  ta.scrollTop = Math.max(0, (line - 4) * textLine());
  $('#se-gut').scrollTop = ta.scrollTop;
  $('#se-hl').scrollTop = ta.scrollTop;
}

/** The first time the source view opens, the editor is laid out a moment after render(): wait until it has a size, or the caret and scroll would go nowhere. */
async function sourceReady() {
  await Promise.race([SE.cmReady, new Promise((resolve) => setTimeout(resolve, 4000))]);
  for (let i = 0; i < 60; i++) {
    const ta = $('#se-ta');
    if (ta && ta.clientHeight > 0 && $('#se-gut') && $('#se-hl')) return true;
    await new Promise((resolve) => requestAnimationFrame(resolve));
  }
  return false;
}

/** Opens the current song at a diagnostic from the Song log. */
export async function showSongDiagnostic(d) {
  if (!setView('src')) return;
  await render();
  const start = d.range?.start;
  const line = start ? start.line + 1 : d.line || 1;
  const col = start ? start.character + 1 : d.col || 1;
  const endCol = start ? (d.range.end?.character || start.character) + 1 : d.endCol || col;
  await sourceReady();
  gotoLine(line, col, endCol);
  requestAnimationFrame(() => gotoLine(line, col, endCol));   // once more after the first paint of the editor
}

/** Shows the source of a block of the node editor. */
export async function gotoSource(block) {
  if (!setView('src')) return;
  // the source editor may still be showing another project file (a .txt, a lyrics file, ...); "go to source" always means the song itself,
  // so switch to it the same way the file switcher would (its own unsaved-changes prompt included) rather than silently discarding edits.
  if (!isSong() && S.path) await openFileByName(S.path);
  if (!isSong()) return;   // the user chose to keep unsaved edits in the other file instead of switching away
  await render();
  const hit = lines(SE.text).findIndex((l, i) => i === block.line);
  const target = (hit >= 0 ? hit : block.line) + 1;
  await sourceReady();
  gotoLine(target);
  requestAnimationFrame(() => gotoLine(target));   // once more after the first paint of the editor
}

function caretLine() {
  const ta = $('#se-ta');
  const before = ta.value.slice(0, ta.selectionStart).split('\n');
  return { line: before.length - 1, ch: before[before.length - 1].length };
}

async function showBlockFromCaret() {
  if (!isSong()) return;
  const { line } = caretLine();
  let syms = [];
  try { syms = await SE.lsp.ask('textDocument/documentSymbol', {}); } catch { syms = []; }
  const own = syms.filter((s) => s.range.start.line <= line && s.range.end.line >= line)[0];
  if (!own) { toast('Put the cursor inside a block first.'); return; }
  if (!setView('blocks')) return;
  select(own.name);
  notify();
  const node = (await import('./store.js')).current().nodes.find((n) => n.id === own.name);
  if (node) focusNode(node);
}

/** The names the links of this song can point to. */
const linkNames = () => ({ blocks: (S.doc && S.doc.blocks ? S.doc.blocks.map((b) => b.name) : []), files: SE.files.map((f) => f.path) });

/** While Ctrl is held, everything that can be clicked is underlined: block names and project files. */
function paintLinks() {
  const lk = $('#se-links');
  const ta = $('#se-ta');
  if (!lk || !ta) return;
  // in the CodeMirror editor the underlines are marks of the editor itself (they follow its gutter, its lines and its scrolling); the old overlay drew them at the wrong place
  if (typeof ta.setMarks === 'function') {
    lk.innerHTML = '';
    let marks = [];
    if (SE.ctrl && isSong()) {
      const { blocks, files } = linkNames();
      let offset = 0;
      for (const t of lines(ta.value)) {
        for (const l of linksIn(t, blocks, files)) marks.push({ from: offset + l.start, to: offset + l.end, cls: 'lk-mark' });
        offset += t.length + 1;
      }
    }
    if (marks.length === 0 && !(SE.linkMarks || []).length) return;
    SE.linkMarks = marks;
    applyMarks(ta, (SE.diagMarks || []).concat(marks));
    return;
  }
  if (!SE.ctrl || !isSong()) { lk.innerHTML = ''; return; }
  const { blocks, files } = linkNames();
  lk.innerHTML = lines(ta.value).map((t) => {
    const ls = linksIn(t, blocks, files);
    let out = '';
    let at = 0;
    ls.forEach((l) => { out += escH(t.slice(at, l.start)) + `<u class="lk">${escH(t.slice(l.start, l.end))}</u>`; at = l.end; });
    return out + escH(t.slice(at));
  }).join('\n');
  lk.scrollTop = ta.scrollTop;
  lk.scrollLeft = ta.scrollLeft;
}

/** Ctrl+click on a file name: a text file or a song opens in the source editor; a file without source (audio) selects the block that uses it. */
async function openFileLink(path) {
  const known = SE.files.some((f) => f.path === path);
  if (!known || (!isEditableSourceFile(path) && !/\.amplet$/i.test(path))) { await showBlockFromCaret(); toast(`${path} has no source to show (it is not a text file of the project); showing its block.`); return; }
  if (!(await confirmEditorSwitch(path))) return;
  if (/\.amplet$/.test(path) && path !== S.path) { document.dispatchEvent(new CustomEvent('ample:open', { detail: path })); return; }
  SE.loadedFor = '';
  SE.file = null;
  await loadFile(path);
  SE.loadedFor = path === S.path ? S.path : '';
  render();
}

export async function openProjectTextFile(path) {
  SE.files = await api.get('/api/project/files');
  if (!SE.files.some((file) => file.path === path) || (!isEditableSourceFile(path) && !/\.amplet$/i.test(path))) { toast(`${path} is not an editable text file.`); return false; }
  if (!(await confirmEditorSwitch(path))) return false;
  SE.loadedFor = '';
  SE.file = null;
  await loadFile(path);
  SE.loadedFor = path === S.path ? S.path : '';
  setView('src', true);
  render();
  return true;
}

/** Going to a song (its tab, the Project panel): the Source view shows that song again instead of a text file that was open before. */
export function resetSourceFile() {
  if (SE.file && SE.file !== S.path && !/\.amplet$/i.test(SE.file)) { SE.file = null; SE.loadedFor = ''; if (V.mode === 'src') render(); }   // already looking at Source: it shows the song at once
}

/** Ctrl+click on the value of a `melody` or `chords` line of a midi-clip opens the melody editor of that clip as a window; the view (code or blocks) stays as it is. */
async function openMelodyAt(line, ch) {
  const all = lines(SE.text);
  const m = /^(\s*)(melody|chords)\s+\S/.exec(all[line] || '');
  if (!m || ch < m[1].length + m[2].length) return false;
  let name = null;
  for (let i = line - 1; i >= 0 && !name; i--) { const h = /^\s*midi-clip\s+("(?:[^"\\]|\\.)*"|\S+)\s.*\{\s*$/.exec(all[i]); if (h) name = h[1].replace(/^"|"$/g, ''); else if (/^\}/.test(all[i])) break; }
  if (!name) return false;
  if (errCount()) { toast('The song has errors: fix them first to use the melody editor.'); return true; }
  if (SE.text !== SE.orig && !(await apply())) return true;
  const { openMelodyEditor } = await import('./melody-editor.js');
  openMelodyEditor(name);
  return true;
}

async function ctrlClick() {
  if (!isSong()) return;
  const { line, ch } = caretLine();
  const names = linkNames();
  const link = linkAt(lines(SE.text)[line] || '', ch, names.blocks, names.files);
  if (link && link.kind === 'file') { await openFileLink(link.target); return; }
  if (link && link.kind === 'connection') {
    if (errCount()) { toast('The song has errors: fix them first to use the node editor.'); return; }
    if (SE.text !== SE.orig && !(await apply())) return;
    if (!setView('blocks')) return;
    const owner = S.doc.blocks.find((block) => block.froms.some((from) => from.line === line));
    if (owner) showConnectionContextMenu(owner.name, link.source, link.output, link.input);
    else toast('Could not find the block that owns this connection.');
    return;
  }
  if (await openMelodyAt(line, ch)) return;
  let def = null;
  try { def = await SE.lsp.ask('textDocument/definition', { position: { line, character: ch } }); } catch { def = null; }
  if (!def) return;
  // the type of a component that comes from another file: open that file at its declaration
  if (def.file) { await openFileLink(def.file); gotoLine(def.range.start.line + 1, def.range.start.character + 1, def.range.end.character + 1); return; }
  if (errCount()) { toast('The song has errors: fix them first to use the node editor (the text is shown at the definition).'); gotoLine(def.range.start.line + 1, def.range.start.character + 1, def.range.end.character + 1); return; }
  const text = lines(SE.text)[def.range.start.line] || '';
  const name = text.slice(def.range.start.character, def.range.end.character).replace(/^"|"$/g, '');
  if (!(S.doc?.blocks || []).some((block) => block.name === name)) { gotoLine(def.range.start.line + 1, def.range.start.character + 1, def.range.end.character + 1); return; }   // a helper, a constant or a type: its declaration is in the code
  if (SE.text !== SE.orig && !(await apply())) return;
  if (!setView('blocks')) return;
  select(name);
  notify();
  const node = (await import('./store.js')).current().nodes.find((n) => n.id === name);
  if (node) focusNode(node);
}

async function hoverAt(e) {
  const ta = $('#se-ta');
  const r = ta.getBoundingClientRect();
  const lineNo = Math.floor((e.clientY - r.top + ta.scrollTop - TEXT_TOP) / textLine());
  const probe = el('span', { textContent: 'x', style: `position:absolute;visibility:hidden;font:${SE.fontSize}px ui-monospace,Consolas,monospace` });
  document.body.append(probe); const charW = probe.getBoundingClientRect().width; probe.remove();
  const ch = Math.floor((e.clientX - r.left + ta.scrollLeft - 8) / charW);
  if (lineNo < 0) return null;
  try {
    const names = linkNames();
    const link = linkAt(lines(SE.text)[lineNo] || '', Math.max(0, ch), names.blocks, names.files);
    if (link && link.kind === 'file') return `Ctrl+click: open ${link.target}`;
    if (link && link.kind === 'connection') return `Ctrl+click: select connection ${link.source}.${link.output} → ${link.input} and open its menu`;
    const def = await SE.lsp.ask('textDocument/definition', { position: { line: lineNo, character: Math.max(0, ch) } });
    return def ? `Ctrl+click: go to the block (line ${def.range.start.line + 1})` : null;
  } catch { return null; }
}

/* ---------- apply, revert and the actions of the server ---------- */
// valid typed source syncs to the working copy; a save still makes the revision
const typedHere = () => !!$('#se-ta') && SE.edit && !!SE.file && $('#se-ta').value !== SE.orig;
typedProbe(typedHere);
beforeSave(async () => { if (typedHere() && !(await apply())) throw new Error('The source text was not applied, so nothing was saved.'); });

export async function apply() {
  const ta = $('#se-ta');
  if (SE.textFile !== SE.file) return false;   // the text in the editor is not (yet) the text of SE.file: never write it there
  SE.text = ta.value;
  if (!SE.edit) return false;
  if (isModuleSource()) {
    try { S.moduleDoc = await api.put('/api/module/source', { path: SE.file, text: SE.text, baseVersion: S.moduleDoc.version }); await refreshActiveModule(); SE.orig = SE.text; clearSourceDraft(SE.file); toast(`Applied ${SE.file} to the working copy.`); }
    catch (e) { toast(e.message); return false; }
  } else if (isSong()) {
    // the song changed somewhere else (the blocks, an undo, the assistant) while this text was typed: applying it as it is would drop those changes
    if (S.doc && S.doc.text !== SE.orig && SE.text !== SE.orig && SE.text !== S.doc.text) {
      const { choice, text } = await resolveTypedConflict(SE.orig, SE.text, S.doc.text);
      if (choice === 'cancel') return false;
      if (choice === 'song') {
        SE.text = SE.orig = SE.syncedText = ta.value = S.doc.text;
        if (SE.lsp) SE.lsp.change(SE.text).catch(() => {});
        gutter(); overlay(); status(); refreshSaveControls();
        toast('Kept the song as it is. The typed text is still in the recovery drafts.');
        return true;
      }
      if (choice === 'merge') SE.text = ta.value = text;
    }
    if (SE.lsp) { await SE.lsp.change(SE.text); await new Promise((r) => setTimeout(r, 150)); }
    const doc = await applyText(SE.text);
    if (!doc) return false;
    SE.orig = SE.text;
    SE.syncedText = SE.text;
    toast(errCount() ? `Applied to the working copy with ${errCount()} error(s): the blocks with errors are marked red.` : 'Applied to the working copy (not saved: Save makes a revision).');
  } else {
    try { await api.put('/api/file', { path: SE.file, text: SE.text }); refreshInputs().catch(() => {}); SE.orig = SE.text; clearSourceDraft(SE.file); if (isComponentFile()) componentFileWritten().catch(() => {}); toast(`Wrote ${SE.file} (journaled in AMPLE Redux).`); } catch (e) { toast(e.message); return false; }
  }
    status();
  return true;
}

/** A component file was written: the board reads its components again and the song is read with them, so the palette, the cards and the build see the new code without a reload. */
async function componentFileWritten() {
  await (await import('./components.js')).loadComponents(S.path);
  await (await import('./store.js')).reloadDoc();
  notify();
}

export async function openModuleSource() {
  if (!S.modulePath) return;
  if (!(await confirmEditorSwitch(S.modulePath))) return;
  await loadFile(S.modulePath);
  setView('src', true);
  render();
}

async function revert() {
  if (!(await ask('Revert file', `Throw away your changes to ${SE.file} and go back to the saved text?`, [{ label: 'Revert', value: true, primary: true }, { label: 'Keep editing', value: false }]))) return;
  clearSourceDraft(SE.file);
  SE.text = isSong() ? S.doc?.text || SE.orig : SE.orig;
  SE.syncedText = SE.text;
  S.sourceEditMessage = null;
  $('#se-ta').value = SE.text;
  if (isSong() && SE.lsp) SE.lsp.change(SE.text).catch(() => {});
  gutter(); overlay(); status();   if (isSong()) syncSourceCursor();
  toast(`Reverted ${SE.file}.`);
}

/** What the person told AMPLE to stop showing in this browser until the page is reloaded: path, code and the text of the line. The song is not changed. */
const ignoredThisSession = new Set();
const lineKey = (d) => `${S.path}|${typeof d.code === 'object' ? d.code?.value : d.code}|${(lines(SE.text)[d.range.start.line] || '').trim()}`;

/** The diagnostics of the server minus the warnings and notes that were ignored for this session (an error is never hidden). */
function showDiagnostics() {
  const all = SE.allDiags || [];
  SE.diags = ignoredThisSession.size ? all.filter((d) => d.severity === 1 || !ignoredThisSession.has(lineKey(d))) : all;
  if (isSong()) { S.songDiagnostics = SE.diags; notify(); }
  const back = $('#se-unignore');
  if (back) { back.style.display = ignoredThisSession.size ? '' : 'none'; back.textContent = `Show ignored (${ignoredThisSession.size})`; }
  renderProblems(); gutter(); overlay(); status();
}

/** The bars of the notes of a midi-clip as the browser can work them out (a literal, or a pipeline with the helpers of the song); null when it cannot. */
function clipBarsOf(block, text) {
  try {
    const clips = (S.doc?.blocks || []).filter((b) => b.type === 'midi-clip');
    const value = (b) => String(b.parameters?.find((p) => p.name === 'notes')?.value ?? '').trim().replace(/^"|"$/g, '');
    const sources = Object.fromEntries(clips.filter((b) => b.name !== block.name && value(b)).map((b) => [b.name, value(b)]));
    const own = value(block);
    if (!own) return null;
    const r = evaluate(own, { sources, tempo: S.doc?.project?.tempo ?? 120, beatsPerBar: Number(S.doc?.project?.beatsPerBar) || 4, helpers: helpersOfText(text) });
    return r.errors.length ? null : Math.max(1, Math.ceil(r.span / 4_000_000));
  } catch { return null; }
}

/** Without the language server: the checks of the helpers that the browser can do by itself (the same ones, from the shared corpus), shown like the diagnostics of the server. */
function localDiagnostics() {
  const ta = $('#se-ta');
  const text = ta ? ta.value : SE.text;
  SE.sentText = text;
  // a block comment is read as blanks (the same lines), like the parser of the server does; one that is never closed is an error
  const blanked = blankBlocks(text.replace(/\r\n/g, '\n'));
  const open = blanked.unterminated < 0 ? [] : [{ code: 'AMP1116', severity: 1, message: 'This block comment is never closed: it runs to the end of the file.', range: { start: { line: blanked.unterminated, character: 0 }, end: { line: blanked.unterminated, character: (text.split('\n')[blanked.unterminated] || '').length } } }];
  SE.allDiags = withoutSilenced([...open, ...helperDiagnostics(blanked.text, helpersOfText(text)), ...docDiagnostics(text.replace(/\r\n/g, '\n').split('\n'), blanked.text.split('\n'), helpersOfText(text)), ...lengthDiagnostics(S.doc?.blocks || [], blanked.text.split('\n'), { clipBars: (block) => clipBarsOf(block, text) })], text);
  showDiagnostics();
}

function ignoreThisSession(diags) {
  for (const d of diags) ignoredThisSession.add(lineKey(d));
  hideErrTip();
  showDiagnostics();
  toast('Hidden in this browser until you reload the page. The song is not changed.');
}

/** Writes "# ample-ignore CODE" so the song says it knows: at the end of the line, or on the line above for a block header (a header cannot end with a comment). */
function suppressHere(lineIndex, diags) {
  const text = lines(SE.text)[lineIndex] ?? '';
  const codes = [...new Set(diags.map((d) => String(typeof d.code === 'object' ? d.code?.value : d.code)).filter(Boolean))];
  if (!codes.length) return;
  const edits = [];
  if (/^\S.*\{\s*$/.test(text)) {
    const above = lines(SE.text)[lineIndex - 1] ?? '';
    if (lineIndex > 0 && /^\s*#\s*ample-ignore\s+/.test(above)) edits.push({ range: { start: { line: lineIndex - 1, character: above.trimEnd().length }, end: { line: lineIndex - 1, character: above.length } }, newText: ', ' + codes.join(', ') });
    else edits.push({ range: { start: { line: lineIndex, character: 0 }, end: { line: lineIndex, character: 0 } }, newText: '# ample-ignore ' + codes.join(', ') + '\n' });
  } else {
    const hash = (() => { let inString = false; for (let k = 0; k < text.length; k++) { if (text[k] === '"') inString = !inString; else if (!inString && text[k] === '#' && !(k > 0 && /[A-Za-z0-9]/.test(text[k - 1]))) return k; } return -1; })();
    if (hash >= 0 && /^#\s*ample-ignore\s+/.test(text.slice(hash))) edits.push({ range: { start: { line: lineIndex, character: text.trimEnd().length }, end: { line: lineIndex, character: text.length } }, newText: ', ' + codes.join(', ') });
    else if (hash >= 0) edits.push({ range: { start: { line: lineIndex, character: hash }, end: { line: lineIndex, character: hash } }, newText: '# ample-ignore ' + codes.join(', ') + '  ' });   // before the comment that is there
    else edits.push({ range: { start: { line: lineIndex, character: text.trimEnd().length }, end: { line: lineIndex, character: text.length } }, newText: '  # ample-ignore ' + codes.join(', ') });
  }
  applyEdits(edits);
  hideErrTip();
  toast('Written as a comment: ' + codes.join(', ') + ' is ignored here. Undo takes it back.');
}

function applyEdits(edits) {
  const ta = $('#se-ta');
  const offs = (l, c) => { let p = 0; const ls = lines(ta.value); for (let i = 0; i < l && i < ls.length; i++) p += ls[i].length + 1; return p + c; };
  let text = ta.value;
  [...edits].sort((a, b) => offs(b.range.start.line, b.range.start.character) - offs(a.range.start.line, a.range.start.character)).forEach((e) => {
    const a = offs(e.range.start.line, e.range.start.character);
    const b = offs(e.range.end.line, e.range.end.character);
    text = text.slice(0, a) + e.newText + text.slice(b);
    // keep later offsets right by recomputing from the new text
    ta.value = text;
  });
  ta.value = text;
  ta.oninput();
}

async function fixAll() {
  if (SE.timer) { clearTimeout(SE.timer); SE.timer = 0; }
  if (SE.lsp && isSong()) { SE.sentText = SE.text; await SE.lsp.change(SE.text); }
  const total = lines(SE.text).length;
  const actions = await SE.lsp.ask('textDocument/codeAction', { range: { start: { line: 0, character: 0 }, end: { line: total, character: 0 } }, context: { diagnostics: [] } });
  const all = actions.find((a) => a.kind === 'source.fixAll');
  const edits = all?.edit?.changes ? Object.values(all.edit.changes).flat() : [];
  if (!edits.length) {
    const ai = await import('./ai.js');
    const st = await ai.aiStatus(true).catch(() => null);
    if (st?.project?.assistant && st.project.assistant !== 'none') {
      const warnings = SE.diags.filter((d) => d.severity === 2);
      const errors = SE.diags.filter((d) => d.severity === 1);
      const adviceOnly = warnings.length > 0 && !errCount();
      const useAi = await ask(adviceOnly ? 'Warnings need your judgment' : 'No automatic fixes available', adviceOnly ? 'Would you like Codex to explain these warnings and suggest options? It will not change or connect anything.' : warnings.length ? 'Would you like Codex to fix the errors and suggest what to do about the warnings without applying those suggestions?' : 'Would you like Codex to try fixing the remaining issues in this file?', [{ label: 'No, thanks', value: false }, { label: adviceOnly ? 'Ask Codex for advice' : 'Ask Codex', value: true, primary: true }]);
      if (useAi) {
        const sequence = isSeqFile();
        const warningText = warnings.map((d) => `Line ${d.range.start.line + 1} ${d.code}: ${d.message}`).join('\n');
        const errorText = errors.map((d) => `Line ${d.range.start.line + 1} ${d.code}: ${d.message}`).join('\n');
        const prefill = adviceOnly ? `Explain these warnings and suggest safe options for resolving them:\n${warningText}\n\nTreat the current routing and connections as intentional. Do not add, remove, or change any block connections, and do not modify the song. If a possible solution requires changing connections, explain it and ask me before proposing that change.` : warnings.length ? `Fix the errors below with the smallest safe changes. For the warnings, explain what they mean and suggest what I could do, but do not implement those suggestions or change connections to address them. Treat current routing and connections as intentional; ask me before proposing any connection changes.\n\nErrors to fix:\n${errorText}\n\nWarnings for advice only:\n${warningText}` : 'Try to fix the remaining diagnostics in this file. Make the smallest safe changes and explain what you changed.';
        ai.openAiPrompt({ title: adviceOnly ? 'Ask Codex for advice' : sequence ? 'Fix this sequence' : 'Fix this file', kind: adviceOnly ? 'explain' : sequence ? 'modify-sequence' : 'modify-file', scope: sequence ? 'sequence' : 'file', hint: prefill, prefill, path: SE.file || S.path, song: S.path });
      }
      return;
    }
    toast('There is nothing to fix automatically.');
    return;
  }
  applyEdits(edits);
  toast(`${all.title}.`);
}

async function format() {
  const edits = await SE.lsp.ask('textDocument/formatting', { options: { tabSize: 2, insertSpaces: true } });
  if (!edits.length) { toast('Already formatted.'); return; }
  applyEdits(edits);
  toast('Formatted.');
}

async function renameAtCaret() {
  if (!isSong()) return;
  const { line, ch } = caretLine();
  const prep = await SE.lsp.ask('textDocument/prepareRename', { position: { line, character: ch } });
  if (!prep) { toast('Put the cursor on a block name first.'); return; }
  const input = el('input', { value: prep.placeholder });
  const ok = await ask(`Rename "${prep.placeholder}"`, 'The block and every reference to it change.', [{ label: 'Cancel', value: false }, { label: 'Rename', value: true, primary: true }], [input]);
  if (!ok || !input.value.trim() || input.value === prep.placeholder) return;
  try {
    const edit = await SE.lsp.ask('textDocument/rename', { position: { line, character: ch }, newName: input.value.trim() });
    applyEdits(Object.values(edit.changes)[0]);
    toast(`Renamed to ${input.value.trim()}.`);
  } catch (e) { toast(e.message); }
}

/* ---------- completion ---------- */
// While typing, the best guess shows in grey after the caret and Tab takes it (Ctrl+Space opens the whole list to choose from). Escape hides it until something is typed on another line or a space is typed or removed.
/** The word being typed; in a settings line a name has a dot (acestep.backend), elsewhere it has not. */
const wordBefore = (before) => wordRangeBefore(before);

function hideGhost() {
  SE.ghost = null;
  SE.ghostSeq = (SE.ghostSeq || 0) + 1;
  const g = $('#se-ghost');
  if (g) g.textContent = '';
}

async function ghostAfterInput(e) {
  hideGhost();
  if (SE.comp && $('#se-comp')?.style.display === 'block') { refreshComp().catch(closeComp); return; }   // an open list follows what is typed
  const ta = $('#se-ta');
  if (!ta || (!isSong() && !isSeqFile() && !isComponentFile()) || (isSong() && !SE.lsp) || ta.selectionStart !== ta.selectionEnd) return;
  const typed = e.inputType === 'insertText' || e.inputType === 'insertCompositionText';
  const spaceGone = e.inputType === 'deleteContentBackward' && SE.leaving === ' ';
  if (!typed && !spaceGone) return;
  const { line, ch } = caretLine();
  if (SE.quiet) { if (SE.quiet.line !== line || e.data === ' ' || spaceGone) SE.quiet = null; else return; }
  const all = lines(ta.value), before = all[line].slice(0, ch), after = all[line].slice(ch);
  const comment = all[line].trimStart();
  if (comment.startsWith('#') && !/^#\s+view\s+(?:group|size|pin)(?:\s|$)/.test(comment)) return;
  if (/^[\w@-]/.test(after)) return;
  const local = localCompletions(ta.value, line, before, ch);
  const word = local?.word ?? wordBefore(before);
  if (!word && !local?.allowEmpty) return;
  if (local?.kind === 'files') {
    if (local.items.length) { SE.comp = { items: local.items, from: local.from, line, idx: 0 }; drawComp(); }
    else closeComp();
    return;
  }
  const seq = SE.ghostSeq, text = ta.value, at = ta.selectionStart;
  let res;
  try { res = await editorCompletions(text, line, ch, local); } catch { return; }
  if (seq !== SE.ghostSeq || ta.value !== text || ta.selectionStart !== at) return;   // typed on meanwhile
  const item = (res.items || []).find((i) => i.label.length > word.length && i.label.toLowerCase().startsWith(word.toLowerCase()));
  if (!item) return;
  SE.ghost = { item, word, line, ch };
  const g = $('#se-ghost');
  g.textContent = '';
  g.append(text.slice(0, at), el('span', { className: 'gh', textContent: item.label.slice(word.length) }));
  g.scrollTop = ta.scrollTop; g.scrollLeft = ta.scrollLeft;
}

function acceptGhost() {
  const { item, word, line, ch } = SE.ghost;
  hideGhost();
  SE.comp = { items: [item], from: ch - word.length, line, idx: 0 };
  pickComp();
}

/** The items for the typed word: a list of the editor itself (a local one) is kept as it is, otherwise the words that start with it come first and then the ones that contain it. */
function rankComp(items, word, local) {
  if (local) return items.slice(0, 40);
  const w = word.toLowerCase();
  if (!w) return items.slice(0, 40);
  const starts = items.filter((i) => i.label.toLowerCase().startsWith(w));
  const contains = items.filter((i) => !i.label.toLowerCase().startsWith(w) && i.label.toLowerCase().includes(w));
  return [...starts, ...contains].slice(0, 40);
}

/** The open list is made again for the word at the caret after every keystroke (it closes when nothing fits, when the caret leaves the line or the word is gone). */
async function refreshComp() {
  const ta = $('#se-ta');
  if (!ta || !SE.comp) return;
  const { line, ch } = caretLine();
  if (line !== SE.comp.line || ta.selectionStart !== ta.selectionEnd) { closeComp(); return; }
  const text = ta.value, before = lines(text)[line].slice(0, ch), local = localCompletions(text, line, before, ch);
  const word = local?.word ?? wordBefore(before);
  if (!word && !local?.allowEmpty) { closeComp(); return; }
  const seq = (SE.compSeq = (SE.compSeq || 0) + 1);
  const res = local?.kind === 'files' ? { items: local.items } : await editorCompletions(text, line, ch, local);
  if (seq !== SE.compSeq || !SE.comp || ta.value !== text) return;   // typed on meanwhile
  const items = rankComp(res.items || [], word, local);
  if (!items.length) { closeComp(); return; }
  SE.comp = { items, from: local?.from ?? ch - word.length, line, idx: 0 };
  drawComp();
}

/** The signature of the call under the cursor with real types, the defaults and the active parameter in bold, and a line about the argument (what it is, and what is converted). */
function updateSig() {
  const box = $('#se-sig'), ta = $('#se-ta');
  if (!box) return;
  const hide = () => { box.style.display = 'none'; };
  if (!ta || (!isSong() && !isSeqFile()) || ta.selectionStart !== ta.selectionEnd) { hide(); return; }
  const { line, ch } = caretLine();
  const all = lines(ta.value);
  const ctx = callContext(all.slice(0, line).concat([(all[line] || '').slice(0, ch)]).join('\n'));
  const extra = helpersOfText(ta.value).signatures();
  const sig = ctx && signatureOf(ctx.call, extra);
  if (!sig) { hide(); return; }
  const active = activeParam(ctx, extra);
  const head = el('div', {}, [el('b', { textContent: sig.id }), document.createTextNode('(')]);
  sig.params.forEach((p, i) => {
    if (i) head.append(document.createTextNode(', '));
    head.append(p.name === active?.name ? el('strong', { textContent: p.text }) : document.createTextNode(p.text));
  });
  head.append(document.createTextNode(') -> ' + sig.returns));
  const about = active ? (NOTE_TYPES[active.type]?.doc || (sig.effect === 'dynamic' ? 'dynamic helper: its result can differ between builds until it is baked' : '')) : '';
  const note = argumentNote(ctx, extra);
  box.replaceChildren(head, ...(about || note ? [el('small', { textContent: [about, note].filter(Boolean).join('  \u00b7  ') })] : []));
  box.style.display = 'block';
}

async function openComp() {
  if (!isSong() && !isSeqFile() && !isComponentFile()) return;
  hideGhost();
  const { line, ch } = caretLine();
  const text = $('#se-ta').value, before = lines(text)[line].slice(0, ch), local = localCompletions(text, line, before, ch);
  const res = await editorCompletions(text, line, ch, local);
  const items = res.items || [];
  if (!items.length) { toast('No suggestions here.'); return; }
  const word = local?.word ?? wordBefore(before);
  SE.comp = { items: rankComp(items, word, local), from: local?.from ?? ch - word.length, line, idx: 0 };
  if (!SE.comp.items.length) SE.comp.items = items.slice(0, 40);
  drawComp();
}

/** The completions the browser knows by itself (pipelines, files, view lines); a component file asks the server, so an attribute line (#[...]) must not be taken for a view comment. */
function localCompletions(text, line, before, ch) {
  return isComponentFile() && /^\s*#\[/.test(before) ? null : sourceCompletions(text, line, before, ch);
}

function sourceCompletions(text, line, before, ch) {
  // a note pipeline: the operators after |>, and the clips a notes line can read
  const seedArgument = /seed\s*=\s*([A-Za-z0-9_."()-]*)$/.exec(before);
  if (seedArgument) {
    const used = new Set();
    for (const n of flat().nodes.filter((x) => x.tpl === 'midi-clip')) {
      for (const site of seedSites(n.params.notes ?? '')) if (site.stable) used.add(site.stable);
      for (const [k, v] of Object.entries(n.params)) if (k.startsWith('random-stream:')) { const name = stableNameOf(String(v)); if (name) used.add(name); }
    }
    const word = seedArgument[1];
    return { word, from: ch - word.length, allowEmpty: true, items: [
      ...[...used].sort().map((name) => ({ label: `stable("${name}")`, detail: 'A stable namespace of this song (follows the master-seed)' })),
      { label: 'stable("new-name")', detail: 'A new stable namespace: a name is only a label of a random family' },
      { label: 'randomSeed()', detail: 'Pick a seed from OS randomness once (the editor offers to write the number into the song)' },
      { label: '123456', detail: 'An explicit, pinned seed: it does not follow the master-seed' },
    ].filter((item) => item.label.startsWith(word)) };
  }
  // the typed places of a call: the values of the type of the parameter being typed, or the parameters not given yet (docs/language/15-musical-types.md)
  const typedPlace = typedCompletion(lines(text).slice(0, line).concat([before]).join('\n'), helpersOfText(text).signatures());
  if (typedPlace) return { word: typedPlace.word, from: ch - typedPlace.word.length, allowEmpty: true, items: typedPlace.items };
  const pipeStep = /\|>\s*([A-Za-z]*)$/.exec(before);
  if (pipeStep) return { word: pipeStep[1], from: ch - pipeStep[1].length, allowEmpty: true, items: [...noteOpItems(), ...helperItems(text, { afterPipe: true })].filter((item) => item.label.toLowerCase().startsWith(pipeStep[1].toLowerCase())) };
  const notesStart = /^\s*notes\s+([A-Za-z0-9_-]*)$/.exec(before);
  if (notesStart && /^[A-Za-z_]/.test(notesStart[1])) return { word: notesStart[1], from: ch - notesStart[1].length, allowEmpty: false, items: [...flat().nodes.filter((n) => n.tpl === 'midi-clip' && n.id.startsWith(notesStart[1])).map((n) => ({ label: n.id + '.notes', detail: 'Read the notes of this clip (then |> operators)' })), ...helperItems(text, { afterPipe: false, capabilities: S.doc?.capabilities }).filter((item) => item.label.toLowerCase().startsWith(notesStart[1].toLowerCase()))] };
  const pack = /(?:^|\s)file(?:=|\s+)pack:([\w-]*)$/i.exec(before);
  if (pack) return { word: pack[1], from: ch - pack[1].length, allowEmpty: true, items: [{ label: 'musescore-general', detail: 'General MIDI SoundFont pack' }] };
  const pathValue = /(?:^|\s)([\w-]+)(?:=|\s+)"?([^\s"]*)$/i.exec(before);
  if (pathValue) {
    const [, parameterName, query] = pathValue;
    const channel = /^\s*channel\s+\S+\s+(\w+)/i.exec(before);
    let accepted = null, folder = false;
    if (channel) {
      const channelType = channel[1].toLowerCase();
      if (parameterName === 'sample' && channelType === 'drum') accepted = ['.wav', '.flac', '.mp3', '.ogg', '.m4a', '.aif', '.aiff'];
      else if (parameterName === 'file' && channelType === 'gm') accepted = ['.sf2'];
      else if (parameterName === 'file' && channelType === 'loop') accepted = ['.wav', '.flac', '.mp3', '.ogg', '.m4a', '.aif', '.aiff'];
      else if (parameterName === 'set' && ['bass', 'guitar', 'notes', 'piano'].includes(channelType)) folder = true;
    } else {
      const blockType = enclosingBlockType(text, line);
      const parameter = S.templates.find((template) => template.id === blockType)?.parameters.find((item) => item.name === parameterName);
      if (parameter?.kind === 'file') accepted = parameter.accept || [];
      if (parameter?.kind === 'sampleset') folder = true;
    }
    if (accepted || folder) {
      const dirs = new Set();
      if (folder) for (const file of SE.files) {
        if (kindOf(file.path)?.id !== 'audio') continue;
        const parts = file.path.split('/');
        for (let i = 1; i < parts.length; i++) dirs.add(parts.slice(0, i).join('/'));
      }
      const openingQuote = pathValue[0].includes('"');
      const items = (folder ? [...dirs] : SE.files.map((file) => file.path).filter((path) => accepted.includes(kindOf(path)?.ext[0])))
        .filter((path) => path.toLowerCase().includes(query.toLowerCase())).slice(0, 40)
        .map((path) => ({ label: path, insertText: openingQuote ? `${path}"` : formatValue(path), detail: folder ? 'Project sample folder' : 'Project file' }));
      return { kind: 'files', word: query, from: ch - query.length, allowEmpty: true, items };
    }
  }
  if (!/^\s*channel\s+/i.test(before)) return null;
  const word = /[\w-]*$/.exec(before)[0];
  if (/=$/.test(before)) return { word, from: ch, items: [] };
  const words = before.trim().split(/\s+/), type = (words[2] || '').toLowerCase();
  const common = ['vol', 'pan', 'mute', 'solo', 'color'];
  const specific = type === 'gm' ? ['file', 'program', 'bank', 'channel'] : type === 'drum' ? ['sample'] : ['file', 'set', 'wave', 'shift', 'voicing', 'stagger', 'bars'];
  const items = [...specific, ...common].map((label) => ({ label, detail: label === 'file' && type === 'gm' ? 'SoundFont file or pack: alias' : `Channel ${label}` }));
  return { word, from: ch - word.length, items: items.filter((item) => item.label.startsWith(word.toLowerCase())) };
}

/** The name of the block a line belongs to (the header above it: type name [pinned@version] ... {). */
function enclosingBlockName(text, line) {
  const sourceLines = lines(text);
  for (let i = Math.min(line, sourceLines.length - 1); i >= 0; i--) {
    const m = /^(?!(?:component|use|module|import|embedded|composition|project)\s)(\S+)\s+("(?:[^"\\]|\\.)*"|\S+)(?:\s.*)?\{\s*$/.exec(sourceLines[i]);   // v1 header (class word optional)
    if (m) return m[2].replace(/^"|"$/g, '');
  }
  return null;
}

function enclosingBlockType(text, line) {
  const sourceLines = lines(text);
  let depth = 0;
  for (let i = line - 1; i >= 0; i--) {
    const row = sourceLines[i];
    if (row.trimStart().startsWith('#')) continue;
    for (let j = row.length - 1; j >= 0; j--) {
      if (row[j] === '}') depth++;
      else if (row[j] === '{') {
        if (!depth) return /^\s*([\w-]+)\s+(?:"(?:[^"\\]|\\.)*"|[\w-]+).*\{\s*$/.exec(row)?.[1] || null;
        depth--;
      }
    }
  }
  return null;
}

async function editorCompletions(text, line, ch, local) {
  if (local) return { items: local.items };
  // a component file has no language-server session: the server completes the text of the editor (attributes, types, the ui block, processors)
  if (isComponentFile()) return api.post('/api/components/complete', { text, line, character: ch });
  if (!isSong() || !SE.lsp) return { items: [] };
  await SE.lsp.change(text);
  return SE.lsp.ask('textDocument/completion', { position: { line, character: ch } });
}

function drawComp() {
  const c = $('#se-comp');
  const keep = c.style.display === 'block' ? c.scrollTop : 0;   // drawing again must not throw the list back to its top
  c.style.display = 'block';
  c.innerHTML = '';
  // the name column is as wide as the longest name (at most 28 characters), so every description starts at the same place
  c.style.setProperty('--cw', `${Math.max(8, Math.min(28, Math.max(...SE.comp.items.map((it) => String(it.label).length)) + 1))}ch`);
  SE.comp.items.forEach((it, i) => {
    const d = el('div', { className: i === SE.comp.idx ? 'on' : '' }, [el('span', { textContent: it.label, title: it.label }), el('small', { textContent: it.detail || '' })]);
    d.onmousedown = (e) => { e.preventDefault(); SE.comp.idx = i; pickComp(); };
    c.append(d);
  });
  placeComp(c);
  c.scrollTop = keep;
  c.querySelector('.on')?.scrollIntoView({ block: 'nearest' });
}

/** The list sits under the word being completed (above the line when there is no room below), like in a code editor. */
function placeComp(c) {
  const ta = $('#se-ta'), host = c.offsetParent || c.parentElement;
  const probe = el('span', { textContent: 'x'.repeat(50), style: `position:absolute;visibility:hidden;white-space:pre;font:${SE.fontSize}px/${textLine()}px ui-monospace,Consolas,monospace` });
  document.body.append(probe);
  const charW = probe.getBoundingClientRect().width / 50;
  probe.remove();
  const row = lines(ta.value)[SE.comp.line] || '';
  const col = row.slice(0, SE.comp.from).replace(/\t/g, '  ').length;
  const t = ta.getBoundingClientRect(), h = host.getBoundingClientRect();
  const caretTop = t.top + TEXT_TOP + SE.comp.line * textLine() - ta.scrollTop;
  const left = Math.max(0, Math.min(t.left + 8 + col * charW - ta.scrollLeft, innerWidth - c.offsetWidth - 8) - h.left);
  const below = caretTop + textLine(), room = innerHeight - below - 8, need = Math.min(c.scrollHeight, 200);
  c.style.left = `${left}px`;
  c.style.right = 'auto';
  if (room >= need || caretTop < need + 8) { c.style.top = `${below - h.top}px`; c.style.bottom = 'auto'; }
  else { c.style.top = 'auto'; c.style.bottom = `${h.bottom - caretTop}px`; }
}

function moveComp(d) { SE.comp.idx = (SE.comp.idx + d + SE.comp.items.length) % SE.comp.items.length; drawComp(); }

function closeComp() { SE.comp = null; SE.compNoFocus = false; const c = $('#se-comp'); if (c) c.style.display = 'none'; }

function pickComp() {
  if (!SE.comp) return;
  const it = SE.comp.items[SE.comp.idx];
  const ta = $('#se-ta');
  let text = it.insertText || it.label;
  if (it.insertTextFormat === 2) text = text.replace(/\$\{\d+:([^}]*)\}/g, '$1').replace(/\$0/g, '').replace(/\\([$}\\])/g, '$1');
  const ls = lines(ta.value);
  let start = 0;
  for (let i = 0; i < SE.comp.line; i++) start += ls[i].length + 1;
  ta.setRangeText(text, start + SE.comp.from, ta.selectionStart, 'end');
  const keepOff = SE.compNoFocus;   // opened by the button without the keyboard: choosing must not bring it up either
  closeComp();
  if (!keepOff) ta.focus();
  ta.oninput();
}

/* ---------- leaving ---------- */
/** Keeps unapplied editor text until the user explicitly agrees to switch songs. */
export async function confirmSongSwitch(path) {
  if (!SE.file || path === S.path) return true;
  const text = $('#se-ta')?.value ?? SE.text;
  if (text === SE.orig) return true;
  SE.text = text;
  // no question any more: a song with typed text stays open as a tab with its unsaved changes; the text goes into its working copy first (plan/editor-tabs.md)
  if (isSong() && await syncSong({ quiet: true })) return true;
  return confirmEditorSwitch(path);
}

function leave() {
  const ta = $('#se-ta');
  if (!ta) return true;
  SE.text = ta.value;
  if (isModuleSource() && SE.text !== SE.orig) return apply();
  if (!isSong() || SE.text === SE.orig) return true;
  syncSong({ quiet: true });   // the Blocks view shows what the editor shows: the text goes into the working copy, errors or not (a block with an error goes red, only a text that cannot be read into blocks greys the view out)
  return true;
}

let syncTimer = 0;
/** Brings the typed text of the code view into the working copy of the song, without a question and without a toast. Quiet: it gives up when the song changed elsewhere in the meantime (the Apply button asks). */
async function syncSong({ quiet = true } = {}) {
  const ta = $('#se-ta');
  if (!ta || !isSong() || !SE.edit || !S.doc) return true;
  if (SE.textFile !== SE.file) return false;
  SE.text = ta.value;
  if (SE.text === SE.orig) return true;
  if (S.doc.text !== SE.orig && SE.text !== S.doc.text) return quiet ? false : apply();
  const doc = await applyText(SE.text);
  if (!doc) return false;
  SE.orig = SE.text;
  SE.syncedText = SE.text;
  return true;
}
const syncSoon = () => { clearTimeout(syncTimer); syncTimer = setTimeout(() => { if (V.mode === 'src') syncSong({ quiet: true }).catch(() => {}); }, 1500); };

export function initSource() {
  // Leaving for the Blocks view makes the song's tab the active one (tabs.js); the editor must follow, or the tab says song while the editor still holds a sequence file and a later Save mixes the two. Typed text is kept as a recovery draft of its own file first.
  document.addEventListener('ample:view', (e) => {
    if (e.detail !== 'blocks' || !SE.file || SE.file === S.path || /.amplet$/i.test(SE.file)) return;
    saveSourceDraft();
    SE.file = null; SE.textFile = null; SE.loadedFor = '';
  });
  document.addEventListener('ample:song-opened', () => {
    rememberRecentFile(S.path);
    SE.file = null;
    SE.loadedFor = '';
    if (V.mode === 'src') render().catch((error) => toast(error.message || String(error)));
  });
  registerView('src', render);
  subscribe(() => { if (V.mode === 'src' && $('#se-hl')) { gutter(); overlay(); } });   // a new build brings the marks or takes them away
  V.guard = leave;
  setBeforeSave(() => (V.mode === 'src' ? syncSong({ quiet: true }) : true));
  setInterval(() => { if (V.mode === 'src' && document.visibilityState === 'visible') syncSong({ quiet: true }).catch(() => {}); }, 6000);   // now and then, whatever else happens
  // the song changed somewhere else (a revert, an undo, a block editor): the text follows, unless there is text typed here that is not applied
  subscribe(() => {
    const ta = $('#se-ta');
    if (V.mode !== 'src' || !ta || !isSong() || !S.doc || SE.text !== SE.orig || ta.value !== SE.orig || S.doc.text === SE.orig) return;
    // text that was typed and then taken back to the loaded text (a Format, an undo) is not "nothing typed" while the document still holds the typed version: that version is what the next sync replaces
    if (SE.syncedText && SE.text !== SE.syncedText) return;
    SE.text = SE.orig = SE.syncedText = ta.value = S.doc.text;
    if (SE.lsp) SE.lsp.change(SE.text).catch(() => {});
    gutter(); overlay(); status();   });
  document.addEventListener('ample:open-module-source', () => openModuleSource());
  document.addEventListener('ample:open-source-file', (event) => openFileByName(event.detail).catch((error) => toast(error.message || String(error))));
}
