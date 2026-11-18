// Live Preview in the code editor (plan/live-preview/PLAN.md, WP6): which text lights up while a build plays, as { from, to, cls } marks for the editor's
// marks layer (web/codemirror/ample-language.js setMarks). Only what can be tied to the audio is marked precisely:
//  - lp-target: the lines of the block that is listened to (selected or pinned);
//  - lp-now:    the literal note tokens (bar:beat:length:pitch:velocity) of a clip that sound at the playhead;
//  - lp-line:   the notes line of a clip whose notes come from a pipeline, a helper or a component (no note-level link: the line, never single notes);
//  - lp-now also: the chord of the bar (`chords`), the melody note (`melody`), the drum section of the bar (`song`) and the step of a voice grid of the built-in
//    kit, the two `auto(...)` points the playhead is between. Each follows the engine's timing in the bars of the block's render request (see `frame`);
//    where the engine's choice depends on more than the text (a sample kit, a grid longer than a bar, a melody word it does not read), nothing is marked.
//  - lp-active: the first line of the other blocks that are audibly active at this bar.
// The marks are made on the text of the build that plays and moved to the current text by matching lines (an edited line loses its mark).
// Pure, tested in node (web/lib/test/listen-highlight.test.mjs).

const DECLARATION = /^[\t ]*[A-Za-z0-9_.-]+\s+("(?:\\.|[^"\\])*"|[A-Za-z0-9_-]+)[^\n]*\{/gm;
const NOTE = /(^|[\s"])(\d+):(\d+(?:\.\d+)?):(\d+(?:\.\d+)?):(\d+):(\d+)((?::\d+){0,4}(?:;[^\s"]*)?)/g;

/** The names of the blocks of a type (`midi-clip`) in the text, in the order they are written. */
export function blockNames(text, type) {
  const out = [];
  for (const match of text.matchAll(DECLARATION)) {
    if (match[0].trim().split(/\s+/)[0] !== type) continue;
    let name = match[1];
    if (name.startsWith('"')) { try { name = JSON.parse(name); } catch { continue; } }
    out.push(name);
  }
  return out;
}

/** The block named `name` in the text: { from, to, open, type } offsets of its first line start, its closing brace (exclusive) and its opening brace, and its block type; null when not found. */
export function blockSpan(text, name) {
  for (const match of text.matchAll(DECLARATION)) {
    let found = match[1];
    if (found.startsWith('"')) { try { found = JSON.parse(found); } catch { continue; } }
    if (found !== name) continue;
    const open = match.index + match[0].lastIndexOf('{');
    let depth = 0, inString = false, inComment = false;
    for (let i = open; i < text.length; i++) {
      const c = text[i];
      if (inComment) { if (c === '\n') inComment = false; continue; }
      if (inString) { if (c === '\\') i++; else if (c === '"') inString = false; continue; }
      if (c === '"') inString = true;
      else if (c === '#') inComment = true;
      else if (c === '{') depth++;
      else if (c === '}' && --depth === 0) return { from: match.index + (match[0].length - match[0].trimStart().length), to: i + 1, open, type: match[0].trim().split(/\s+/)[0] };
    }
    return null;
  }
  return null;
}

/** The `notes` lines of a block span: [{ from, to, literal }] where literal is false for a pipeline, a call or a component expression. */
export function notesLines(text, span) {
  const out = [], body = text.slice(span.open + 1, span.to - 1);
  let offset = span.open + 1;
  for (const line of body.split('\n')) {
    const m = /^(\s*)notes\b\s*(.*)$/.exec(line);
    if (m) {
      const value = m[2].trim();
      const literal = /^"?[\d:.;=\sA-Za-z_-]*"?$/.test(value) && /\d+:\d/.test(value) && !/\|>|\(/.test(value);
      out.push({ from: offset + m[1].length, to: offset + line.length, literal });
    }
    offset += line.length + 1;
  }
  return out;
}

/**
 * The note tokens of a literal notes line that sound at a song bar (fractional, 1-based).
 * `clipFirstBar`: the song bar where bar 1 of the clip is; `quartersPerBar`: quarter-note beats per bar (4 in 4/4).
 */
export function soundingTokens(text, line, songBar, { clipFirstBar = 1, quartersPerBar = 4 } = {}) {
  const out = [], segment = text.slice(line.from, line.to), clipBar = songBar - (clipFirstBar - 1);
  for (const m of segment.matchAll(NOTE)) {
    const bar = Number(m[2]), beat = Number(m[3]), length = Number(m[4]);
    const start = bar + beat / quartersPerBar, end = start + length / quartersPerBar;
    if (clipBar >= start && clipBar < end) {
      const from = line.from + m.index + m[1].length;
      out.push({ from, to: from + m[0].length - m[1].length, cls: 'lp-now' });
    }
  }
  return out;
}

/** The lines of a block body as { from, to, text } (offsets in the whole text). */
function bodyLines(text, span) {
  const out = [];
  let offset = span.open + 1;
  for (const line of text.slice(span.open + 1, span.to - 1).split('\n')) { out.push({ from: offset, to: offset + line.length, text: line }); offset += line.length + 1; }
  return out;
}

/** The words of a parameter line after its keyword ("chords Am F" -> Am, F), without quotes, with their offsets; null when the line is another parameter. */
function words(line, keyword) {
  const m = new RegExp('^(\\s*)' + keyword + '\\s+(.*)$').exec(line.text);
  if (!m) return null;
  const out = [], start = line.from + line.text.length - m[2].length;
  for (const w of m[2].matchAll(/[^\s"]+/g)) { if (w[0].startsWith('#')) break; out.push({ word: w[0], from: start + w.index, to: start + w.index + w[0].length }); }
  return out;
}

const mark = (w) => ({ from: w.from, to: w.to, cls: 'lp-now' });

/**
 * The token of the `index`-th note of a source block: a note of its literal `notes` line (in the order of the text) or a note word of its `melody` (rests and
 * dynamic markers are not notes). Null for a source that is a chord list, a chain or a drum pattern: no note-level link there.
 */
export function sourceToken(text, sourceName, index) {
  const span = blockSpan(text, sourceName);
  if (!span || !Number.isInteger(index) || index < 0) return null;
  const lines = notesLines(text, span);
  if (lines.length) {
    if (!lines.every((l) => l.literal)) return null;
    const tokens = lines.flatMap((line) => [...text.slice(line.from, line.to).matchAll(NOTE)].map((m) => ({ from: line.from + m.index + m[1].length, to: line.from + m.index + m[0].length })));
    return tokens[index] ?? null;
  }
  const melody = bodyLines(text, span).map((l) => words(l, 'melody')).find(Boolean);
  if (!melody) return null;
  const notes = melody.filter((w) => { const p = w.word.toLowerCase().split(':')[0]; return !(DYNAMICS.test(p) && !w.word.includes(':')) && /^[a-g](#|b(?=-?\d))?-?\d+$/.test(p); });
  return notes[index] ? { from: notes[index].from, to: notes[index].to } : null;
}

/**
 * The marks in the sources for the notes of a chain clip that sound at a song bar. `origins` is the list of the clip-notes route (`origins: true`) or of
 * originList(): [{ at, len, pitch, source, index, lap }] in micro-beats from the start of the clip. Each mark carries `user` (the chain clip) and `lap` so that
 * several users of one source can be told apart; a note that has no source note (made by an operator) marks nothing.
 */
export function originMarks({ text, user, origins, songBar, clipFirstBar = 1, quartersPerBar = 4 }) {
  const clipBar = songBar - (clipFirstBar - 1), per = quartersPerBar * 1e6, out = [];
  for (const o of origins ?? []) {
    if (o.source == null || o.index == null) continue;
    const start = 1 + o.at / per, end = start + o.len / per;
    if (!(clipBar >= start && clipBar < end)) continue;
    const token = sourceToken(text, o.source, o.index);
    if (token) out.push({ ...token, cls: 'lp-now', user, lap: o.lap ?? 0 });
  }
  return out;
}

/** chords: one per bar of the request, repeating ((bar - 1) mod count); a dot shares the bar evenly (Renderers.ChordBars). */
export function chordMarks(text, span, bar, quartersPerBar = 4) {
  const line = bodyLines(text, span).map((l) => words(l, 'chords')).find(Boolean);
  if (!line?.length || bar < 1) return [];
  const w = line[(Math.floor(bar) - 1) % line.length], parts = w.word.split('.');
  const index = Math.min(parts.length - 1, Math.floor(((bar % 1) * quartersPerBar) / (quartersPerBar / parts.length)));
  let from = w.from;
  for (let i = 0; i < index; i++) from += parts[i].length + 1;
  return [{ from, to: from + parts[index].length, cls: 'lp-now' }];
}

const DYNAMICS = /^(pp|p|mp|mf|f|ff|<|>)$/i;
/** melody: words of name:beats from the first bar of the request on, no repeat; a note stops at the end of its bar (Renderers.MelodyWithVelocity). */
export function melodyMarks(text, span, bar, first = 1, quartersPerBar = 4) {
  const line = bodyLines(text, span).map((l) => words(l, 'melody')).find(Boolean);
  if (!line?.length) return [];
  const now = (bar - first) * quartersPerBar;   // beats since the first bar of the request
  let at = 0;
  for (const w of line) {
    const parts = w.word.toLowerCase().split(':');
    if (parts.length === 1 && DYNAMICS.test(parts[0])) continue;   // markers take no time
    const beats = Number(parts[1]) > 0 ? Number(parts[1]) : 1, barIndex = Math.floor(at / quartersPerBar);
    const isNote = parts[0] !== 'r' && /^[a-g](#|b(?=-?\d))?-?\d+$/.test(parts[0]);
    const end = at + Math.min(beats, quartersPerBar * (barIndex + 1) - at);
    if (isNote && now >= at && now < end) return [mark(w)];
    if (now < at) return [];
    at += beats;
  }
  return [];
}

/** The sections of a drum song, one name per bar (verse*4 is four bars), with the word it came from. */
function sections(line) {
  const out = [];
  for (const w of line) { const [name, count] = w.word.split('*'); for (let i = 0; i < Math.max(1, Number(count) || 1); i++) out.push({ name, w }); }
  return out;
}

/**
 * The built-in drum kit: the section word of the bar (\`song\`, repeating from the first bar of the request, DrumSong.NameAtBar) and the step under the playhead
 * in every voice grid of the pattern that plays (16 steps in 4/4, or \`steps\`). A grid longer than a bar continues over bars in a way that depends on the bars
 * played before: only the section is marked then. A sample kit (\`kit\` other than builtin) is left alone.
 */
export function drumMarks(text, span, bar, first = 1, quartersPerBar = 4) {
  if (span.type !== 'drums') return [];
  const lines = bodyLines(text, span), find = (k) => lines.map((l) => ({ l, w: words(l, k) })).find((x) => x.w);
  const kit = find('kit');
  if (kit && kit.w[0]?.word !== 'builtin') return [];
  const out = [], index = Math.floor(bar) - first;
  if (index < 0) return out;
  const song = find('song'), pattern = find('pattern'), patterns = find('patterns');
  let chosen = 'pattern';
  if (song?.w.length) {
    const list = sections(song.w), at = list[((index % list.length) + list.length) % list.length];
    chosen = at.name;
    out.push(mark(at.w));
  }
  const stepsLine = find('steps'), stepsInBar = Number(stepsLine?.w[0]?.word) > 0 ? Math.round(Number(stepsLine.w[0].word)) : Math.max(1, Math.round(4 * quartersPerBar));
  const step = Math.min(stepsInBar - 1, Math.floor((bar % 1) * stepsInBar));
  // the voice grids of the pattern that plays: the pattern line itself, or name=grid in the patterns line (up to the next / or the end)
  let grids = [];
  if (chosen === 'pattern' && pattern) grids = pattern.w;
  else if (patterns) {
    const all = patterns.w, start = all.findIndex((w) => w.word.startsWith(chosen + '='));
    if (start >= 0) {
      const first0 = all[start], rest = first0.word.slice(chosen.length + 1);
      grids = [{ word: rest, from: first0.from + chosen.length + 1, to: first0.to }];
      for (let k = start + 1; k < all.length && all[k].word !== '/' && !/^[A-Za-z0-9_-]+=/.test(all[k].word); k++) grids.push(all[k]);
    }
  }
  if (!grids.some((g) => g.word.includes(':')) && chosen === 'pattern' && pattern?.w[0]) out.push(mark(pattern.w[0]));   // a named built-in beat plays every bar
  for (const g of grids) {
    const colon = g.word.indexOf(':');
    if (colon <= 0) continue;
    const steps = g.word.slice(colon + 1).split('@')[0];
    if (!steps.length || steps.length > stepsInBar) continue;   // longer than a bar: its place depends on the bars played before
    const at = g.from + colon + 1 + (step % steps.length);
    out.push({ from: at, to: at + 1, cls: 'lp-now' });
  }
  return out;
}

/**
 * auto(...): the two points the playhead is between (or the nearest end point). Points are in bars from the first bar of the request (5.5 is half way through
 * bar 5) or in seconds (12s), as Automation.ParseSamples reads them; lfo, env and fades are not marked.
 */
export function autoMarks(text, span, bar, first = 1, barSeconds = 2) {
  const out = [];
  for (const line of bodyLines(text, span)) {
    for (const m of line.text.matchAll(/auto\(([^)]*)\)/g)) {
      const inner = m[1], base = line.from + m.index + 5, points = [];
      let offset = 0;
      for (const part of inner.split(',')) {
        const lead = part.length - part.trimStart().length, word = part.trim(), colon = word.indexOf(':');
        const position = colon > 0 ? word.slice(0, colon).trim() : '', seconds = /s$/i.test(position);
        const value = Number(seconds ? position.slice(0, -1) : position);
        if (colon > 0 && Number.isFinite(value)) points.push({ bar: seconds ? first + value / barSeconds : first + (value - 1), from: base + offset + lead, to: base + offset + lead + word.length });
        offset += part.length + 1;
      }
      if (!points.length) continue;
      points.sort((a, b) => a.bar - b.bar);
      let i = points.findLastIndex((p) => p.bar <= bar);
      if (i < 0) { out.push(mark(points[0])); continue; }
      out.push(mark(points[i]));
      if (points[i + 1]) out.push(mark(points[i + 1]));
    }
  }
  return out;
}

/**
 * The marks for one moment.
 * `frame(name)` gives the bars of a block on the song timeline: { first, offset }, so that the bar of the block is songBar - offset and it starts at bar
 * `first`. Live Preview passes { first: 1, offset: 0 } for a placed block and { first: 1, offset: origin - 1 } for one that is not placed (plan 6.4).
 * `barSeconds` is the length of a bar (auto points in seconds).
 * @param {{ text: string, target?: string|null, songBar?: number|null, clipFirstBar?: (name: string) => number, active?: string[],
 *   quartersPerBar?: number, frame?: (name: string) => { first: number, offset: number }, barSeconds?: number, origins?: Record<string, object[]> }} input
 */
export function playbackMarks({
  text, target = null, songBar = null, clipFirstBar = () => 1, active = [], quartersPerBar = 4, frame = () => ({ first: 1, offset: 0 }), barSeconds = 2, origins = {},
}) {
  const marks = [];
  const lit = new Set();
  const markBlock = (name, isTarget) => {
    const span = blockSpan(text, name);
    if (!span) return;
    if (isTarget) marks.push({ from: span.from, to: span.to, cls: 'lp-target' });
    else {
      const end = text.indexOf('\n', span.from);
      marks.push({ from: span.from, to: end < 0 || end > span.to ? span.to : end, cls: 'lp-active' });
    }
    if (songBar === null) return;
    const add = (list) => list.forEach((t) => { if (!lit.has(t.from)) { lit.add(t.from); marks.push(t); } });
    const { first, offset } = frame(name), bar = songBar - offset;
    // what a clip plays: literal notes first, then melody, then chords (Renderers: structured notes win over melody, melody over chords)
    const notes = notesLines(text, span);
    if (!notes.length && span.type === 'midi-clip') {
      const melody = melodyMarks(text, span, bar, first, quartersPerBar);
      const hasMelody = bodyLines(text, span).some((l) => words(l, 'melody')?.length);
      add(melody.length ? melody : (hasMelody ? [] : chordMarks(text, span, bar, quartersPerBar)));
    }
    add(drumMarks(text, span, bar, first, quartersPerBar));
    add(autoMarks(text, span, bar, first, barSeconds));
    for (const line of notes) {
      if (line.literal) {
        const tokens = soundingTokens(text, line, songBar, { clipFirstBar: clipFirstBar(name), quartersPerBar });
        tokens.forEach((t) => { if (!lit.has(t.from)) { lit.add(t.from); marks.push(t); } });
      } else if (isTarget || active.includes(name)) marks.push({ from: line.from, to: line.to, cls: 'lp-line' });
    }
    // a chain: the notes of its sources that sound now (origins of this clip, when the build brought them)
    if (origins[name]) originMarks({ text, user: name, origins: origins[name], songBar, clipFirstBar: clipFirstBar(name), quartersPerBar }).forEach((m) => marks.push(m));
  };
  if (target) markBlock(target, true);
  for (const name of active) if (name !== target) markBlock(name, false);
  return marks;
}

/**
 * Moves marks made on `fromText` (the text of the build that plays) to `toText` (the editor now): every mark goes to the nearest line with the same text
 * (within `reach` lines); a mark on a line that was edited is dropped. A mark that spans lines keeps only when every line it covers is found unchanged.
 */
export function moveMarks(marks, fromText, toText, reach = 400) {
  if (fromText === toText) return marks;
  const fromLines = fromText.split('\n'), toLines = toText.split('\n');
  const starts = (lines) => { const s = [0]; for (const l of lines) s.push(s[s.length - 1] + l.length + 1); return s; };
  const fromStarts = starts(fromLines), toStarts = starts(toLines);
  const lineOf = (offset) => { let lo = 0, hi = fromLines.length - 1; while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (fromStarts[mid] <= offset) lo = mid; else hi = mid - 1; } return lo; };
  const find = (line) => {
    const want = fromLines[line];
    for (let d = 0; d <= reach; d++) {
      if (toLines[line - d] === want) return line - d;
      if (toLines[line + d] === want) return line + d;
    }
    return -1;
  };
  const out = [];
  for (const mark of marks) {
    const a = lineOf(mark.from), b = lineOf(Math.max(mark.from, mark.to - 1));
    const at = find(a);
    if (at < 0) continue;
    let ok = true;
    for (let k = a + 1; k <= b && ok; k++) ok = toLines[at + (k - a)] === fromLines[k];
    if (!ok) continue;
    const shift = toStarts[at] - fromStarts[a];
    out.push({ ...mark, from: mark.from + shift, to: mark.to + shift });
  }
  return out;
}
