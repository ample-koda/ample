// The editor of Remix: the CodeMirror editor of AMPLE (the same bundle as Studio: highlighting, gutter marks, underlines) with three things the Studio source view does with its store and
// that are written here without it: diagnostics as underlines and gutter marks, a completion list, and a hover tooltip. The answers come from outside (the language server for songs,
// the component checks for component files): this module only asks through the callbacks it is given.

let bundle = null;
const loadBundle = () => bundle ??= import('/vendor/codemirror-ample.js');

const WORD = /[A-Za-z0-9_.\-#]/;
const SNIPPET = /\$\{\d+:([^}]*)\}|\$\{\d+\}|\$\d+/g;

/** Takes the markers of a snippet out ("${1:name}" is "name"), so a completion with a template becomes plain text. */
export const plain = (snippet) => snippet.replace(/\\([$}\\])/g, '$1').replace(SNIPPET, (_, text) => text ?? '');

/** The editor at the position of a text offset. */
const position = (view, offset) => { const line = view.state.doc.lineAt(offset); return { line: line.number - 1, character: offset - line.from }; };
const offsetAt = (view, { line, character }) => { const l = view.state.doc.line(Math.min(Math.max(1, line + 1), view.state.doc.lines)); return Math.min(l.from + character, l.to); };

/**
 * Makes an editor in `host`.
 * Callbacks: onChange(text), complete({line, character}, text) -> [{label, detail, insert?}], hover({line, character}) -> markdown|null, definition({line, character}) -> {path?, line, character}|null, openAt(target).
 */
import { installKeyboardGuard, installTouchMenu } from './editor-touch-menu.js';

export async function createLiteEditor(host, { text = '', readOnly = false, onChange = () => {}, complete = null, hover = null, definition = null, onOpenTarget = () => {} } = {}) {
  const { createAmpleEditor } = await loadBundle();
  host.textContent = '';
  const mirror = {};
  const editor = createAmpleEditor(host, mirror, text);
  const view = editor.view;
  if (readOnly) mirror.readOnly = true;
  let lastDoc = view.state.doc;
  let popup = null;
  let tip = null;
  let completionTimer = 0;
  let hoverTimer = 0;

  // a change of the text from any cause (typing, paste, undo, drop) is seen by comparing the document, so nothing depends on which event the browser sends
  const watch = setInterval(() => {
    if (view.state.doc === lastDoc) return;
    lastDoc = view.state.doc;
    onChange(view.state.doc.toString());
    scheduleCompletion(false);
  }, 160);

  const closePopup = () => { popup?.remove(); popup = null; };
  const hideTip = () => { tip?.remove(); tip = null; };

  function place(element, coords) {
    const box = host.getBoundingClientRect();
    element.style.left = `${Math.max(4, Math.min(coords.left - box.left, box.width - 280))}px`;
    element.style.top = `${coords.bottom - box.top + 4}px`;
  }

  /** The word before the caret (what a chosen completion replaces) and where it starts. */
  function wordBefore() {
    const head = view.state.selection.main.head;
    const line = view.state.doc.lineAt(head);
    let from = head;
    while (from > line.from && WORD.test(line.text[from - line.from - 1])) from--;
    return { from, to: head, prefix: view.state.doc.sliceString(from, head) };
  }

  async function showCompletion(manual) {
    if (!complete) return;
    const { from, prefix } = wordBefore();
    if (!manual && prefix.length < 1) { closePopup(); return; }
    const head = view.state.selection.main.head;
    const items = (await complete(position(view, head), view.state.doc.toString())) ?? [];
    const lower = prefix.toLowerCase();
    const shown = items.filter((i) => i.label.toLowerCase().includes(lower)).sort((a, b) => Number(b.label.toLowerCase().startsWith(lower)) - Number(a.label.toLowerCase().startsWith(lower))).slice(0, 40);
    if (view.state.selection.main.head !== head || shown.length === 0 || (shown.length === 1 && shown[0].label === prefix)) { closePopup(); return; }
    closePopup();
    popup = document.createElement('div');
    popup.className = 'lite-complete';
    popup.setAttribute('role', 'listbox');
    let active = 0;
    const hadFocus = view.hasFocus;   // opened by the button without the keyboard: choosing must not bring it up either
    const rows = shown.map((item, index) => {
      const row = document.createElement('div');
      row.className = 'lite-complete-row';
      row.setAttribute('role', 'option');
      const label = document.createElement('span');
      label.textContent = item.label;
      const detail = document.createElement('small');
      detail.textContent = item.detail ?? '';
      row.append(label, detail);
      row.onmousedown = (event) => { event.preventDefault(); choose(index); };
      popup.append(row);
      return row;
    });
    const paint = () => rows.forEach((row, index) => { row.classList.toggle('on', index === active); row.setAttribute('aria-selected', String(index === active)); if (index === active) row.scrollIntoView({ block: 'nearest' }); });
    const choose = (index) => {
      const item = shown[index];
      const insert = plain(item.insert ?? item.label);
      view.dispatch({ changes: { from, to: view.state.selection.main.head, insert }, selection: { anchor: from + insert.length }, userEvent: 'input.complete' });
      closePopup();
      if (hadFocus) view.focus();
    };
    popup.choose = (key) => {
      if (key === 'ArrowDown') { active = (active + 1) % rows.length; paint(); return true; }
      if (key === 'ArrowUp') { active = (active + rows.length - 1) % rows.length; paint(); return true; }
      if (key === 'Enter' || key === 'Tab') { choose(active); return true; }
      if (key === 'Escape') { closePopup(); return true; }
      return false;
    };
    host.append(popup);
    paint();
    place(popup, view.coordsAtPos(view.state.selection.main.head) ?? host.getBoundingClientRect());
  }

  function scheduleCompletion(manual) {
    clearTimeout(completionTimer);
    if (!complete || readOnly) return;
    const head = view.state.selection.main.head;
    const before = head > 0 ? view.state.doc.sliceString(head - 1, head) : '';
    if (!manual && !WORD.test(before)) { closePopup(); return; }
    completionTimer = setTimeout(() => showCompletion(manual).catch(closePopup), manual ? 0 : 140);
  }

  view.contentDOM.addEventListener('keydown', (event) => {
    if (event.key === ' ' && (event.ctrlKey || event.metaKey)) { event.preventDefault(); scheduleCompletion(true); return; }
    if (popup && popup.choose(event.key)) { event.preventDefault(); event.stopPropagation(); }
  }, true);
  view.contentDOM.addEventListener('blur', () => setTimeout(closePopup, 120));
  // the same as Ctrl+Space, for a screen without a keyboard: a button (no text) in the corner of the editor
  const suggest = document.createElement('button');
  suggest.type = 'button';
  suggest.className = 'lite-suggest';
  suggest.title = 'Suggestions (Ctrl+Space)';
  suggest.setAttribute('aria-label', 'Show suggestions');
  suggest.innerHTML = '<svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h16M4 12h10M4 18h13"/><path d="M19 11v6m-3-3h6" opacity="0"/></svg>';
  suggest.addEventListener('pointerdown', (event) => event.preventDefault());   // the editor keeps its focus and caret
  suggest.addEventListener('click', () => scheduleCompletion(true));   // no focus: on a phone that would bring the keyboard up
  suggest.hidden = readOnly;
  host.append(suggest);
  // a long press on a touch screen opens a menu with copy, cut, paste and select all (the editor draws its own selection, so the browser has no menu for it)
  installTouchMenu(view);
  // and selecting text on a touch screen brings no keyboard and closes the suggestions
  const guard = installKeyboardGuard(view, closePopup);

  view.dom.addEventListener('mousemove', (event) => {
    clearTimeout(hoverTimer);
    if (!hover) return;
    const { clientX: x, clientY: y } = event;
    hoverTimer = setTimeout(async () => {
      const offset = view.posAtCoords({ x, y });
      if (offset === null) { hideTip(); return; }
      const markdown = await hover(position(view, offset)).catch(() => null);
      hideTip();
      if (!markdown) return;
      tip = document.createElement('div');
      tip.className = 'lite-tip';
      tip.textContent = markdown.replace(/[`*_]/g, '').trim();
      host.append(tip);
      const box = host.getBoundingClientRect();
      tip.style.left = `${Math.max(4, Math.min(x - box.left, box.width - 340))}px`;
      tip.style.top = `${y - box.top + 18}px`;
    }, 380);
  });
  view.dom.addEventListener('mouseleave', () => { clearTimeout(hoverTimer); hideTip(); });
  view.dom.addEventListener('click', async (event) => {
    if (!(event.ctrlKey || event.metaKey) || !definition) return;
    const offset = view.posAtCoords({ x: event.clientX, y: event.clientY });
    if (offset === null) return;
    const target = await definition(position(view, offset)).catch(() => null);
    if (target) { event.preventDefault(); onOpenTarget(target); }
  }, true);

  return {
    getText: () => view.state.doc.toString(),
    focus: () => view.focus(),
    /** Opens the list of suggestions at the caret (the button, and Ctrl+Space). */
    complete: () => scheduleCompletion(true),
    /** Underlines and gutter marks for a list of {line (1-based), col (1-based), endCol, severity}. */
    setDiagnostics(list) {
      const doc = view.state.doc;
      const marks = [];
      const gutter = {};
      for (const d of list) {
        const n = Math.min(Math.max(1, d.line || 1), doc.lines);
        const line = doc.line(n);
        const from = Math.min(line.from + Math.max(0, (d.col || 1) - 1), line.to);
        const to = Math.max(from + 1, Math.min(line.from + Math.max(1, (d.endCol || d.col || 1) - 1), line.to));
        marks.push({ from, to, cls: `lite-diag-${d.severity}` });
        if (gutter[n] !== 'error') gutter[n] = d.severity;
      }
      mirror.setMarks(marks);
      mirror.setGutter({ marks: gutter, notes: [] });
    },
    /** Puts the caret at a place and shows it (1-based line, 1-based column). */
    goto(line, col = 1) {
      const offset = offsetAt(view, { line: line - 1, character: Math.max(0, col - 1) });
      view.dispatch({ selection: { anchor: offset }, scrollIntoView: true });
      view.focus();
    },
    /** Replaces a stretch of the text (offsets) and keeps the change in the undo history of the editor. */
    replaceRange(from, to, insert) { view.dispatch({ changes: { from, to, insert }, userEvent: 'input.replace' }); },
    gotoPosition(target) { this.goto((target.line ?? 0) + 1, (target.character ?? 0) + 1); },
    destroy() { guard(); clearInterval(watch); clearTimeout(completionTimer); clearTimeout(hoverTimer); closePopup(); hideTip(); editor.destroy(); },
  };
}
