import { StreamLanguage, HighlightStyle, forceParsing, indentUnit, syntaxHighlighting, syntaxTreeAvailable } from '@codemirror/language';
import { tags, Tag } from '@lezer/highlight';
import { EditorState, Compartment, StateEffect, StateField } from '@codemirror/state';
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import { Decoration, EditorView, GutterMarker, gutter, keymap } from '@codemirror/view';
import tokens from '../../spec/tokens.json';
import { NOTE_SIGNATURES, NOTE_TYPES } from '../lib/note-catalog.mjs';

const keywordSet = new Set(tokens.keywords);
const blockHeader = /^[\t ]*([A-Za-z0-9_-]+)\s+(?:"(?:[^"\\]|\\.)*"|[A-Za-z0-9_-]+)[^{#\n]*\{/;
const namePattern = /^[A-Za-z0-9_-]+/;

// The language layer (docs/language/14): helpers, types, constants and the expressions of note pipelines and property formulas.
const DEFINITION_WORDS = new Set(['helper', 'dynamic', 'type', 'let', 'var']);
/** The declaration words of a component file (.amplec): a line that starts like a declaration of a component, its ports, its properties, its process, a migration or its seal. */
const COMPONENT_LINE = /^\s*(?:component\s+[\w.]+|embedded\s+module|input\s+\w+\s*:|output\s+\w+\s*:|property\s+\w+\s*:|process\s*\{|migrate\s+\d|seal\s*\{)/;
const COMPONENT_WORDS = new Set(['component', 'embedded', 'module', 'input', 'output', 'property', 'process', 'migrate', 'seal', 'version', 'current', 'deprecated']);
const CONTROL_WORDS = new Set(['if', 'else', 'for', 'in', 'while', 'repeat', 'return', 'emit', 'max', 'stop']);
const OPERATOR_WORDS = new Set(['is', 'and', 'or', 'not', 'as', 'bake', 'override']);
const TYPE_NAMES = new Set([...Object.keys(NOTE_TYPES), 'Bool', 'String', 'Float', 'Gain', 'Percent', 'Seed', 'StableSeed', 'RandomStream', 'Note', 'Notes']);
const SCALE_NAMES = new Set(NOTE_TYPES.ScaleMode?.values || []);
const BUILTIN_FUNCTIONS = new Set([...Object.keys(NOTE_SIGNATURES), 'Int', 'Float', 'Round', 'Floor', 'Ceil', 'Truncate', 'count', 'span', 'min', 'max', 'abs', 'at', 'len', 'Note', 'currentTime', 'currentDate', 'osRandom', 'environment', 'random', 'choose', 'chance', 'stable', 'randomSeed', 'random-seed']);
const UNIT = /^(?:st|oct|cents|beats?|bars?|ms|dB|Hz|kHz|s)(?![A-Za-z0-9_])|^%/;
const IDENTIFIER = /^[A-Za-z_][A-Za-z0-9_]*(?:-[A-Za-z][A-Za-z0-9_]*)*/;

/** A block comment (slash-star ... star-slash) starts here: not right after a letter or digit (that is a division), like the parser of the server reads it. */
const startsBlock = (stream) => stream.match('/*', false) && !(stream.pos > 0 && /[A-Za-z0-9]/.test(stream.string[stream.pos - 1]));
/** The rest of a block comment on this line (it may go on over the next lines). */
function blockComment(stream, state) {
  if (!state.inComment) { stream.match('/*'); state.inComment = true; }
  if (stream.skipTo('*/')) { stream.match('*/'); state.inComment = false; } else stream.skipToEnd();
  return 'comment';
}

/** One token of an expression line (a helper, a type, a constant, a notes expression or a property formula). */
function expressionToken(stream, state) {
  if (startsBlock(stream)) return blockComment(stream, state);
  if (state.pendingUnit) { state.pendingUnit = false; if (stream.match(UNIT)) return 'unit'; }
  if (stream.peek() === '"') {
    stream.next();
    while (!stream.eol()) { const c = stream.next(); if (c === '"') break; if (c === '\\' && !stream.eol()) stream.next(); }
    state.prev = 'operand';
    return 'string';
  }
  if (stream.match(/^[A-G][#b]?-?\d(?![A-Za-z0-9_])/) || stream.match(/^[A-G][#b](?![A-Za-z0-9_])/)) { state.prev = 'operand'; return 'atom'; }   // C4, F#2, A#
  if (stream.peek() === '#') { stream.skipToEnd(); return 'comment'; }
  if (state.prev === 'op' ? stream.match(/^[+-]?\d+(?:\.\d+)?/) : stream.match(/^\d+(?:\.\d+)?/)) { state.pendingUnit = true; state.prev = 'operand'; return 'number'; }
  if (stream.match('|>')) { state.prev = 'op'; state.afterPipe = true; return 'operator'; }
  if (stream.match(/^(?:->|\.\.=|\.\.|==|!=|<=|>=|[+*/%<>=|-])/)) { state.prev = 'op'; return 'operator'; }
  if (stream.match(/^[([{]/)) { state.depth++; state.prev = 'op'; return 'punctuation'; }
  if (stream.match(/^[)\]}]/)) { state.depth = Math.max(0, state.depth - 1); state.prev = 'operand'; return 'punctuation'; }
  if (stream.match('.')) { state.afterDot = true; return 'punctuation'; }
  if (stream.match(/^[,:;@]/)) { state.prev = 'op'; return 'punctuation'; }
  if (stream.match(IDENTIFIER)) {
    const word = stream.current();
    const call = !!stream.match(/^\s*\(/, false);
    const piped = state.afterPipe;
    state.afterPipe = false;
    state.prev = 'operand';
    if (state.afterDot) { state.afterDot = false; return call ? 'variableName.function' : 'propertyName'; }
    if (state.declName) { const kind = state.declName; state.declName = false; return kind === 'type' ? 'typeName' : call ? 'variableName.definition.function' : 'variableName.definition'; }
    if (OPERATOR_WORDS.has(word) && !call) { state.prev = 'op'; return 'operatorKeyword'; }
    if (piped || (call && BUILTIN_FUNCTIONS.has(word))) return 'variableName.function.standard';
    if (DEFINITION_WORDS.has(word)) { state.declName = word === 'dynamic' ? false : word === 'type' ? 'type' : true; state.prev = 'op'; return 'definitionKeyword'; }
    if (CONTROL_WORDS.has(word) && !call) { state.prev = 'op'; return 'controlKeyword'; }
    if (word === 'true' || word === 'false' || SCALE_NAMES.has(word)) return 'atom';
    if (call) return 'variableName.function';
    if (/^[A-G]$/.test(word)) return 'atom';   // a pitch class
    if (TYPE_NAMES.has(word) || /^[A-Z]/.test(word)) return 'typeName';
    if (state.depth > 0 && stream.match(/^\s*=(?!=)/, false)) return 'propertyName';   // a named argument: voices=3
    return 'variableName';
  }
  stream.next();
  return null;
}

/** One token of an attribute (#[MinValue(0%), Tags("a", "b")]): the name is not the value, and a value is coloured by what it is. */
function attributeToken(stream, state) {
  const attr = state.inAttr;
  if (stream.peek() === '"') {
    stream.next();
    while (!stream.eol()) { const c = stream.next(); if (c === '"') break; if (c === '\\' && !stream.eol()) stream.next(); }
    return 'string';
  }
  if (stream.match(']')) { if (attr.depth === 0) state.inAttr = null; return 'meta'; }
  if (stream.match('(')) { attr.depth++; return 'punctuation'; }
  if (stream.match(')')) { attr.depth = Math.max(0, attr.depth - 1); if (attr.depth === 0) attr.name = false; return 'punctuation'; }
  if (stream.match(',')) { if (attr.depth === 0) attr.name = true; return 'punctuation'; }
  if (stream.match(/^[+-]?\d+(?:\.\d+)?[A-Za-z%]*/)) return 'number';
  if (stream.match(/^[A-Za-z_][\w-]*/)) { if (attr.depth === 0) { attr.name = false; return 'annotation'; } return 'atom'; }
  stream.next();
  return null;
}

// the word ample in the composition line (the AMPLE version): its own tag, so it can get a colour of its own
const ampleWordTag = Tag.define();
const ampleMode = StreamLanguage.define({
  name: 'ample',
  tokenTable: { 'ample-word': ampleWordTag },
  startState: () => ({ inComment: false, depth: 0, exprLine: false, prev: 'op', afterDot: false, afterPipe: false, pendingUnit: false, declName: false, inBlock: false, header: false, expectName: false, lineStart: true, lineHasToken: false, patternLine: false, expectValue: false, connectionStage: '' }),
  blankLine: (state) => { state.lineStart = true; state.lineHasToken = false; state.patternLine = false; state.expectValue = false; state.connectionStage = ''; },
  token(stream, state) {
    if (state.inComment) return blockComment(stream, state);
    if (stream.sol()) {
      state.exprLine = state.depth > 0 || /^\s*(?:\|>|(?:dynamic\s+)?helper\b|type\s+[A-Z]|let\s)/.test(stream.string);
      state.prev = 'op'; state.afterDot = false; state.afterPipe = false; state.pendingUnit = false; state.declName = false;
    }
    if (stream.sol()) { state.inAttr = null; state.lineStart = true; state.lineHasToken = false; state.header = false; state.expectName = false; state.patternLine = false; state.expectValue = false; state.connectionStage = ''; }
    if (stream.eatSpace()) return null;
    if (state.inAttr) return attributeToken(stream, state);
    if (state.lineStart && stream.match('#[')) { state.inAttr = { depth: 0, name: true }; state.lineStart = false; state.lineHasToken = true; return 'meta'; }   // an attribute of a component: #[DisplayName("...")]
    if (startsBlock(stream)) return blockComment(stream, state);
    if (state.exprLine) return expressionToken(stream, state);
    if (stream.peek() === '#') { stream.skipToEnd(); return 'comment'; }
    if (stream.match(/(?:stable|pinned|live)@\d+(?:\.\d+)*/)) { state.lineStart = false; state.lineHasToken = true; return 'atom'; }
    if (state.patternLine && stream.match(/[xXoO.-]+/)) return 'string';
    if (state.patternLine && stream.match('|')) return 'operator';
    if (state.expectValue && stream.match(/[A-Za-z_./][A-Za-z0-9_./-]*/)) { state.expectValue = false; state.lineStart = false; state.lineHasToken = true; return 'string'; }
    if (stream.peek() === '"') {
      stream.next();
      while (!stream.eol()) { const c = stream.next(); if (c === '"') break; if (c === '\\' && !stream.eol()) stream.next(); }
      state.lineStart = false; state.lineHasToken = true;
      if (state.expectName) state.expectName = false;
      state.expectValue = false;
      return 'string';
    }
    if (stream.match(/-?\d+(?:\.\d+)?/)) { state.expectValue = false; state.lineStart = false; state.lineHasToken = true; return 'number'; }
    if (stream.match('->')) { state.connectionStage = 'input'; state.lineStart = false; state.lineHasToken = true; return 'operator'; }
    if (stream.match(/[{}.,@]/)) {
      const punctuation = stream.current();
      if (punctuation === '{') state.inBlock = true;
      if (punctuation === '}') state.inBlock = false;
      if (punctuation === '.' && state.connectionStage === 'source-dot') state.connectionStage = 'port';
      state.lineStart = false; state.lineHasToken = true;
      return 'punctuation';
    }
    if (stream.match(namePattern)) {
      const word = stream.current();
      let style = null;
      // the word ample inside the composition line gets its own colour (an easter egg)
      if (word === 'ample' && !state.lineStart && /^\s*(?:composition|project)\s+"(?:[^"\\]|\\.)*"\s*\{\s*$/.test(stream.string.slice(0, stream.start))) style = 'ample-word';
      else if (stream.match(/^\s*=/, false)) { style = 'propertyName'; state.expectValue = true; }
      else if (state.lineStart && COMPONENT_WORDS.has(word) && COMPONENT_LINE.test(stream.string)) { style = 'keyword'; }
      else if (!state.lineStart && COMPONENT_WORDS.has(word) && /^\s*component\s/.test(stream.string)) style = 'keyword';   // version, current, deprecated after the name
      else if (state.lineStart) {
        if (blockHeader.exec(stream.string)?.[1] === word) { style = 'typeName'; state.header = true; state.expectName = true; }
        else if (keywordSet.has(word)) { style = 'keyword'; if (word === 'from') state.connectionStage = 'source'; }
        else if (state.inBlock) style = 'propertyName';
      } else if (state.connectionStage === 'source') { style = 'variableName'; state.connectionStage = 'source-dot'; }
      else if (state.connectionStage === 'port') { style = 'propertyName'; state.connectionStage = 'arrow'; }
      else if (state.connectionStage === 'input') { style = 'labelName'; state.connectionStage = ''; }
      else if (state.expectName) { style = 'variableName'; state.expectName = false; }
      else if (state.header && keywordSet.has(word)) style = 'keyword';
      else if (state.inBlock && !state.lineHasToken) style = 'propertyName';
      else if (keywordSet.has(word)) style = 'keyword';
      if (state.inBlock && style === 'propertyName' && !state.patternLine && /^\s*(?:bake\s|\(|[A-Za-z_][\w-]*\s*\(|[A-Za-z_][\w-]*\.[A-Za-z_]|\|>)/.test(stream.string.slice(stream.pos))) { state.exprLine = true; state.prev = 'op'; }
      if (state.inBlock && ['kick', 'snare', 'hat'].includes(word)) state.patternLine = true;
      if (state.inBlock && stream.match(/^=/, false)) state.expectValue = true;
      state.lineStart = false; state.lineHasToken = true;
      return style;
    }
    stream.next(); state.lineStart = false; state.lineHasToken = true;
    return null;
  },
});

const ampleColors = syntaxHighlighting(HighlightStyle.define([
  { tag: ampleWordTag, class: 'cm-ample-word' },
  { tag: tags.keyword, color: 'var(--data)' },
  { tag: tags.typeName, color: 'var(--audio)', fontWeight: '700' },
  { tag: tags.variableName, color: 'var(--studio)' },
  { tag: tags.propertyName, color: 'var(--audio)', fontWeight: '600' },
  { tag: tags.labelName, color: 'var(--midi)', fontWeight: '700' },
  { tag: tags.string, color: 'var(--midi)' },
  { tag: tags.number, color: 'var(--studio)' },
  { tag: tags.comment, color: 'var(--dim)', fontStyle: 'italic' },
  { tag: tags.meta, color: 'var(--dim)' },
  { tag: tags.annotation, color: 'var(--data)', fontWeight: '700' },
  { tag: tags.operator, color: 'var(--acc)' },
  { tag: tags.punctuation, color: 'var(--dim)' },
  { tag: tags.atom, color: 'var(--warn)' },
  { tag: tags.definitionKeyword, color: 'var(--data)', fontWeight: '700' },
  { tag: tags.controlKeyword, color: 'color-mix(in srgb,var(--data) 35%,#f0607f)', fontWeight: '700' },
  { tag: tags.operatorKeyword, color: 'var(--acc)' },
  { tag: tags.function(tags.variableName), color: 'color-mix(in srgb,var(--midi) 60%,var(--acc))' },
  { tag: tags.standard(tags.function(tags.variableName)), color: 'color-mix(in srgb,var(--studio) 70%,var(--audio))' },
  { tag: tags.definition(tags.function(tags.variableName)), color: 'color-mix(in srgb,var(--midi) 60%,var(--acc))', fontWeight: '700' },
  { tag: tags.definition(tags.variableName), color: 'var(--studio)', fontStyle: 'italic' },
  { tag: tags.unit, color: 'color-mix(in srgb,var(--studio) 55%,var(--dim))' },
]));

// The marks of the page (underlines of diagnostics and so on) are decorations of the editor: { from, to, cls } in offsets of the text. A layer of its own that is moved along with a script lagged behind the text while scrolling a phone.
const setMarks = StateEffect.define();
const marksField = StateField.define({
  create: () => Decoration.none,
  update(marks, tr) {
    marks = marks.map(tr.changes);
    for (const effect of tr.effects) if (effect.is(setMarks)) marks = Decoration.set(effect.value.filter((r) => r.to > r.from).map((r) => Decoration.mark({ class: r.cls }).range(r.from, r.to)), true);
    return marks;
  },
  provide: (field) => EditorView.decorations.from(field),
});

// The line numbers with the mark of a problem in front: ● error, ▲ warning, ℹ note, ✎ a line with notes that the roll can open. A column of its own that was moved along with a script lagged behind the text while scrolling a phone.
const setGutter = StateEffect.define();
const gutterField = StateField.define({
  create: () => ({ marks: {}, notes: [] }),
  update(value, tr) { for (const effect of tr.effects) if (effect.is(setGutter)) return effect.value; return value; },
});
class Label extends GutterMarker {
  constructor(text, kind) { super(); this.text = text; this.kind = kind; }
  eq(other) { return other.text === this.text && other.kind === this.kind; }
  toDOM() { const span = document.createElement('span'); span.className = 'ag-' + this.kind; span.textContent = this.text; return span; }
}
const GLYPH = { error: '● ', warning: '▲ ', info: 'ℹ ', note: '✎ ' };
const lineGutter = (mirror) => gutter({
  class: 'cm-ample-gutter',
  lineMarker(view, line) {
    const n = view.state.doc.lineAt(line.from).number, g = view.state.field(gutterField);
    const kind = g.notes.includes(n) ? 'note' : g.marks[n] || 'plain';
    return new Label((GLYPH[kind] || '  ') + n, kind);
  },
  lineMarkerChange: (update) => update.docChanged || update.transactions.some((tr) => tr.effects.some((effect) => effect.is(setGutter))),
  initialSpacer: () => new Label('  99999', 'plain'),
  domEventHandlers: { mousedown: (view, line) => { mirror.onGutterClick?.(view.state.doc.lineAt(line.from).number - 1); return false; } },
});

export function createAmpleEditor(host, mirror, initialText) {
  const editable = new Compartment();
  let parseFrame = 0;
  const view = new EditorView({
    state: EditorState.create({ doc: initialText, extensions: [
      ampleMode, ampleColors, marksField, gutterField, lineGutter(mirror), history(), indentUnit.of('  '), keymap.of([indentWithTab, ...defaultKeymap, ...historyKeymap]),
      EditorView.updateListener.of(({ docChanged, viewportChanged, selectionSet, view: updatedView }) => {
        if (selectionSet) updatedView.dom.onselectionchange?.();
        if ((!docChanged && !viewportChanged) || syntaxTreeAvailable(updatedView.state, updatedView.viewport.to)) return;
        if (parseFrame) cancelAnimationFrame(parseFrame);
        parseFrame = requestAnimationFrame(() => { parseFrame = 0; if (updatedView.dom.isConnected) forceParsing(updatedView, updatedView.viewport.to, 16); });
      }),
      editable.of(EditorView.editable.of(true)),
      EditorView.theme({
        '&': { height: '100%', backgroundColor: 'transparent', color: 'var(--text)' },
        '.cm-scroller': { overflow: 'auto', fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 'var(--se-font-size, 12px)', lineHeight: 'var(--se-line-height, 18px)', tabSize: '2' },
        '.cm-content': { padding: '6px 8px', caretColor: 'var(--text)' },
        '.cm-line': { padding: '0' },
        '.cm-gutters': { backgroundColor: 'transparent', color: 'var(--dim)', border: 'none' },   // the base theme of CodeMirror paints the gutter light grey
        '&.cm-focused': { outline: 'none' },
        '.cm-selectionBackground, &.cm-focused .cm-selectionBackground': { backgroundColor: 'color-mix(in srgb,var(--acc) 35%,transparent)' },
      }),
    ] }), parent: host,
  });

  const cmDom = view.dom, scroller = view.scrollDOM;
  let clickHandler = null;
  const props = {
    value: { get: () => view.state.doc.toString(), set: (value) => view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: String(value) } }) },
    selectionStart: { get: () => view.state.selection.main.from }, selectionEnd: { get: () => view.state.selection.main.to },
    scrollTop: { get: () => scroller.scrollTop, set: (value) => { scroller.scrollTop = value; } },
    scrollLeft: { get: () => scroller.scrollLeft, set: (value) => { scroller.scrollLeft = value; } },
    clientHeight: { get: () => scroller.clientHeight },
    readOnly: { get: () => !view.state.facet(EditorView.editable), set: (value) => view.dispatch({ effects: editable.reconfigure(EditorView.editable.of(!value)) }) },
    style: { get: () => view.contentDOM.style },
    oninput: { get: () => cmDom.oninput, set: (value) => { cmDom.oninput = value; } },
    onselectionchange: { get: () => cmDom.onselectionchange, set: (value) => { cmDom.onselectionchange = value; } },
    onkeydown: { get: () => cmDom.onkeydown, set: (value) => { cmDom.onkeydown = value; } },
    onclick: { get: () => clickHandler, set: (value) => { if (clickHandler) cmDom.removeEventListener('click', clickHandler, true); clickHandler = value; if (clickHandler) cmDom.addEventListener('click', clickHandler, true); } },
    onmousemove: { get: () => cmDom.onmousemove, set: (value) => { cmDom.onmousemove = value; } },
    onmouseleave: { get: () => cmDom.onmouseleave, set: (value) => { cmDom.onmouseleave = value; } },
    onblur: { get: () => view.contentDOM.onblur, set: (value) => { view.contentDOM.onblur = value; } },
    onscroll: { get: () => scroller.onscroll, set: (value) => { scroller.onscroll = value; } },
    ondragover: { get: () => cmDom.ondragover, set: (value) => { cmDom.ondragover = value; } },
    ondragenter: { get: () => cmDom.ondragenter, set: (value) => { cmDom.ondragenter = value; } },
    ondragleave: { get: () => cmDom.ondragleave, set: (value) => { cmDom.ondragleave = value; } },
    ondrop: { get: () => cmDom.ondrop, set: (value) => { cmDom.ondrop = value; } },
  };
  Object.defineProperties(mirror, props);
  mirror.focus = () => view.focus();
  mirror.setMarks = (ranges) => { const length = view.state.doc.length; view.dispatch({ effects: setMarks.of(ranges.map((r) => ({ ...r, from: Math.max(0, Math.min(r.from, length)), to: Math.max(0, Math.min(r.to, length)) }))) }); };
  mirror.addEventListener = (type, listener, options) => (type === 'blur' ? view.contentDOM : cmDom).addEventListener(type, listener, options);
  // the place of the text: the box of the editor without its gutter (the page works out columns from the left edge of the text)
  mirror.getBoundingClientRect = () => { const r = scroller.getBoundingClientRect(), w = view.dom.querySelector('.cm-gutters')?.offsetWidth || 0; return new DOMRect(r.left + w, r.top, Math.max(0, r.width - w), r.height); };
  mirror.setGutter = (value) => view.dispatch({ effects: setGutter.of(value) });
  mirror.setSelectionRange = (from, to) => view.dispatch({ selection: { anchor: from, head: to } });
  mirror.select = () => mirror.setSelectionRange(0, view.state.doc.length);
  mirror.setRangeText = (text, from, to, mode = 'preserve') => {
    const selection = mode === 'end' ? { anchor: from + text.length } : undefined;
    view.dispatch({ changes: { from, to, insert: text }, selection, userEvent: 'input' });
  };
  mirror.removeEventListener = (type, listener, options) => (type === 'blur' ? view.contentDOM : cmDom).removeEventListener(type, listener, options);
  mirror.dispatchEvent = cmDom.dispatchEvent.bind(cmDom);
  return { view, destroy: () => { if (parseFrame) cancelAnimationFrame(parseFrame); view.destroy(); } };
}

export function createAmplePreview(host, text) {
  const mirror = {};
  const editor = createAmpleEditor(host, mirror, text);
  mirror.readOnly = true;
  editor.view.dispatch({ selection: { anchor: 0, head: editor.view.state.doc.length } });
  editor.view.contentDOM.tabIndex = -1;
  editor.view.contentDOM.setAttribute('aria-label', 'Selected source lines');
  editor.view.contentDOM.setAttribute('aria-readonly', 'true');
  return editor;
}
