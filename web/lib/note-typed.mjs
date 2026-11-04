// The musical type model for the browser (docs/language/15-musical-types.md): the typed spellings the evaluator understands (+7st, -1oct, 50cents, 25%, C2), and the typed completion, signature help
// and argument notes of the editor. Everything reads the catalogue generated from spec/note-ops.json (note-catalog.mjs); nothing here is hand-written per operator. @ample:note-typed

import { NOTE_SIGNATURES, NOTE_TYPES } from './note-catalog.mjs';
import { tryRoot } from './note-random.mjs';

// ---------- the typed spellings (the twin of NotePipeline.Typed in C#)

const gcd = (a, b) => { a = a < 0n ? -a : a; b = b < 0n ? -b : b; while (b) [a, b] = [b, a % b]; return a || 1n; };
const rat = (n, d = 1n) => { if (d < 0n) { n = -n; d = -d; } const g = gcd(n, d); return { n: n / g, d: d / g }; };
const mul = (a, b) => rat(a.n * b.n, a.d * b.d);

const NOTE_NAME = /^([A-Ga-g])([#b]?)(-?\d)$/;

/** A note name (C4, F#3, Bb2) as a MIDI pitch (C4 is 60), or null. */
export function noteNameToPitch(text) {
  const m = NOTE_NAME.exec(String(text));
  if (!m) return null;
  const pc = tryRoot(m[1] + m[2]);
  if (pc === null) return null;
  const value = 12 * (Number(m[3]) + 1) + pc;
  return value >= 0 && value <= 127 ? value : null;
}

export function typedValue(param, node) {
  const type = NOTE_TYPES[param.type];
  if (node.kind === 'quantity') {
    if (param.type === 'Interval' && node.unit === 'st') return { ...node, unit: null };
    if (param.type === 'Interval' && node.unit === 'oct') return { ...node, unit: null, value: mul(node.value, rat(12n)) };
    if (param.type === 'Interval' && node.unit === 'cents') return { ...node, unit: null, value: mul(node.value, rat(1n, 100n)) };
    if (node.unit === '%' && type?.percent) return { ...node, unit: null, value: mul(node.value, rat(1n, 100n)) };
  }
  if (node.kind === 'word' && type?.noteNames) {
    const pitch = noteNameToPitch(node.text);
    if (pitch !== null) return { kind: 'quantity', value: rat(BigInt(pitch)), unit: null, start: node.start, end: node.end, text: String(pitch) };
  }
  if (node.kind === 'range' && param.type === 'Range' && param.of) {
    const element = { ...param, type: param.of, of: undefined };
    return { ...node, from: typedValue(element, node.from), to: typedValue(element, node.to) };
  }
  return node;
}

function typedCall(node) {
  if (node.name === 'randomSeed') node = { ...node, name: 'random-seed' };
  const sig = NOTE_SIGNATURES[node.name];
  const args = node.args.map((a, i) => {
    const param = !sig ? null : a.kind === 'named' ? sig.params.find((p) => p.name === a.name) : sig.params[i];
    const inner = a.kind === 'named' ? a.value : a;
    const converted = param ? typedValue(param, typed(inner)) : typed(inner);
    return a.kind === 'named' ? { ...a, value: converted } : converted;
  });
  return { ...node, args };
}

/** Rewrites the typed spellings to the plain ones the evaluator knows, so that +7 and +7st (25% and 0.25, C2 and 36) are the same expression with the same recipe. */
export function typed(node) {
  switch (node?.kind) {
    case 'pipeline': return { ...node, source: typed(node.source), steps: node.steps.map(typed) };
    case 'call': return typedCall(node);
    case 'named': return { ...node, value: typed(node.value) };
    case 'method': return { ...node, target: typed(node.target), args: node.args.map(typed) };
    default: return node;
  }
}

// ---------- the typed signature

/** The type of a parameter as it is written in a signature: Count, Range<Pitch>, Values... */
export function typeText(param) {
  if (param.type === 'Range' && param.of) return `Range<${param.of}>`;
  if (param.type === 'Values') return 'Values...';
  return param.type;
}

/** `walk(PitchClass root, ScaleMode scale, Count steps = 16, ...) -> Notes` as parts, so that the active parameter can be shown in bold. */
export function signatureOf(name, extra = null) {
  const sig = NOTE_SIGNATURES[name] ?? extra?.[name];
  if (!sig) return null;
  const params = sig.params.map((p) => ({ name: p.name, type: typeText(p), required: !!p.required, default: p.default ?? null, text: `${typeText(p)} ${p.name}${p.default != null ? ` = ${p.default}` : ''}` }));
  return { id: sig.id, receiver: sig.receiver, returns: sig.returns, doc: sig.doc, effect: sig.effect ?? 'pure', params };
}

// ---------- the call around the cursor

/**
 * The innermost call that is still open at the end of `before` (the text up to the cursor, possibly several lines): its name, the index of the argument being typed, the name of that argument when it is
 * written as name=value, the text of its value so far, and whether the cursor is inside a quoted string. Null when the cursor is not inside a call.
 */
export function callContext(before) {
  const stack = [];
  let inString = false;
  for (let i = 0; i < before.length; i++) {
    const c = before[i];
    if (c === '"') { inString = !inString; continue; }
    if (inString) continue;
    if (c === '#' && !(i > 0 && /[A-Za-z0-9]/.test(before[i - 1]))) { const eol = before.indexOf('\n', i); if (eol < 0) return null; i = eol; continue; }
    if (c === '(') {
      let s = i;
      while (s > 0 && /[A-Za-z0-9_-]/.test(before[s - 1])) s--;
      stack.push({ name: before.slice(s, i), argStart: i + 1, index: 0, open: i, piped: /\|>\s*$/.test(before.slice(0, s)) });
    } else if (c === ')') stack.pop();
    else if (c === ',' && stack.length) { const top = stack[stack.length - 1]; top.index++; top.argStart = i + 1; }
  }
  const top = stack[stack.length - 1];
  if (!top || !top.name) return null;
  const argText = before.slice(top.argStart);
  const named = /^\s*([A-Za-z][A-Za-z0-9_-]*)\s*=\s*([\s\S]*)$/.exec(argText);
  const used = [...before.slice(top.open + 1).matchAll(/(?:^|[(,\s])([A-Za-z][A-Za-z0-9_-]*)\s*=/g)].map((m) => m[1]);
  return { call: top.name, piped: top.piped, index: top.index, named: named ? named[1] : null, valueText: (named ? named[2] : argText).replace(/^\s+/, ''), inString, used: new Set(used) };
}

/** Which parameter of the signature the cursor is in (by name when written as name=, else by position; a Values parameter takes every later position). */
export function activeParam(ctx, extra = null) {
  const sig = NOTE_SIGNATURES[ctx?.call] ?? extra?.[ctx?.call];
  if (!sig) return null;
  if (ctx.named) return sig.params.find((p) => p.name === ctx.named) ?? null;
  const skip = ctx.piped && sig.kind === 'helper' && sig.receiver === 'Notes' ? 1 : 0;   // the piped notes are the first argument of a helper
  const valuesAt = sig.params.findIndex((p) => p.type === 'Values');
  if (valuesAt >= 0 && ctx.index + skip >= valuesAt) return sig.params[valuesAt];
  return sig.params[ctx.index + skip] ?? null;
}

// ---------- typed completion

const DURATIONS = [['0.25beat', 'a sixteenth'], ['0.5beat', 'an eighth'], ['1beat', 'a quarter'], ['2beats', 'a half'], ['1bar', 'one bar'], ['2bars', 'two bars'], ['250ms', 'a quarter of a second'], ['1s', 'one second']];
const INTERVALS = [['+7st', 'a fifth up'], ['+12st', 'an octave up'], ['-12st', 'an octave down'], ['+5st', 'a fourth up'], ['+4st', 'a major third up'], ['+3st', 'a minor third up'], ['-1oct', 'one octave down'], ['+2st', 'a whole tone up']];

function valueItems(param, type) {
  switch (type?.kind) {
    case 'vocabulary':
      return type.values.map((v) => ({ label: v, detail: type.name === 'ScaleMode' ? `${param.type}: a scale` : `${param.type}` }));
    case 'refined': {
      const out = (type.suggest ?? param.suggest ?? []).map((v) => ({ label: String(v), detail: `${param.type} (${type.doc})` }));
      if (type.noteNames) out.unshift(...['C2', 'C3', 'C4', 'A4'].map((v) => ({ label: v, detail: `Pitch: note name, ${noteNameToPitch(v)}` })), ...[36, 48, 60].map((v) => ({ label: String(v), detail: 'Pitch: MIDI number' })));
      if (type.percent) out.push({ label: '25%', detail: 'Probability: 0.25' });
      return out;
    }
    case 'quantity': return type.name === 'Interval' ? INTERVALS.map(([label, detail]) => ({ label, detail: `Interval: ${detail}` })) : type.name === 'Time' ? DURATIONS.filter(([l]) => /s$/.test(l)).map(([label, detail]) => ({ label, detail: `Time: ${detail}` })) : DURATIONS.filter(([l]) => !/\d(ms|s)$/.test(l)).map(([label, detail]) => ({ label, detail: `Length: ${detail}` }));
    case 'union': return DURATIONS.map(([label, detail]) => ({ label, detail: `Duration: ${detail}` }));
    case 'window': return [['1..5', 'bars 1 to 4'], ['1..=4', 'bars 1 to 4, including 4'], ['bars(1..5)', 'bar positions'], ['beats(1..9)', 'beat positions'], ['time(0s..2s)', 'seconds']].map(([label, detail]) => ({ label, detail: `Window: ${detail}` }));
    case 'choice': return (param.options ?? []).map((v) => ({ label: v, detail: `${param.name}: ${v}` }));
    case 'range': return param.of === 'Pitch' ? [{ label: '48..=72', detail: 'Range<Pitch>: C3 to C5' }] : param.of === 'Velocity' ? [{ label: '60..=100', detail: 'Range<Velocity>' }] : [{ label: '-5..=5', detail: 'Range: from..=to' }];
    case 'vocabulary-list': return ['Am', 'F', 'C', 'G7', 'Dm', 'Em', 'Cmaj7', 'F#m7b5'].map((v) => ({ label: v, detail: 'Chord symbol' }));
    default: return [];
  }
}

/** The completion for the place at the end of `before`: the values of the type of the parameter being typed, or the names of the parameters not given yet. Null when this is not a typed place. */
export function typedCompletion(before, extra = null) {
  const ctx = callContext(before);
  const sig = ctx && (NOTE_SIGNATURES[ctx.call] ?? extra?.[ctx.call]);
  if (!sig) return null;
  const param = activeParam(ctx, extra);
  if (param?.type === 'RandomSource' || param?.type === 'Notes' || param?.type === 'Text' || param?.type === 'Identifier') return null;
  const typedWord = /[A-Za-z0-9#+.%-]*$/.exec(ctx.valueText)?.[0] ?? '';
  let items = [];
  if (ctx.inString) {
    if (param?.type !== 'Chords') return null;
    const word = /[A-Za-z0-9#/]*$/.exec(ctx.valueText)?.[0] ?? '';
    return { word, items: valueItems(param, NOTE_TYPES.Chords).filter((i) => i.label.toLowerCase().startsWith(word.toLowerCase())) };
  }
  if (param) items = valueItems(param, { name: param.type, ...NOTE_TYPES[param.type] });
  if (!ctx.named && ctx.valueText.trim() === '') {
    const skipFirst = ctx.piped && sig.kind === 'helper' && sig.receiver === 'Notes';
    const free = sig.params.filter((p, i) => !(skipFirst && i === 0) && !ctx.used.has(p.name) && p.type !== 'Values');
    items = [...items, ...free.map((p) => ({ label: `${p.name}=`, detail: `${typeText(p)}${p.default != null ? ` = ${p.default}` : ''}${p.required ? ' (required)' : ''}` }))];
  }
  const lower = typedWord.toLowerCase();
  return { word: typedWord, items: items.filter((i) => i.label.toLowerCase().startsWith(lower)) };
}

/** What the editor says about the argument at the cursor beyond its type: the conversions the evaluator makes (a bare interval is semitones, 25% is 0.25, C2 is MIDI 36). */
export function argumentNote(ctx, extra = null) {
  const param = activeParam(ctx, extra);
  if (!param) return '';
  const value = ctx.valueText.trim().replace(/[,)].*$/s, '');
  if (param.type === 'Interval' && /^[+-]?\d+(\.\d+)?$/.test(value)) return `${value} is read as ${value}st (semitones)`;
  if (NOTE_TYPES[param.type]?.percent && /^\d+(\.\d+)?%$/.test(value)) return `${value} is ${Number(value.slice(0, -1)) / 100}`;
  if (NOTE_TYPES[param.type]?.noteNames && noteNameToPitch(value) !== null) return `${value} is MIDI ${noteNameToPitch(value)}`;
  if ((param.type === 'Duration' || param.type === 'Length' || param.type === 'Time') && /^[+-]?\d+(\.\d+)?$/.test(value)) return `${value} needs a unit: beat, bar, tick, ms or s`;
  if (/^(Duration|Time)$/.test(param.type) && /\d(ms|s)$/.test(value)) return 'a time in seconds depends on the tempo: the tempo becomes part of the recipe';
  return '';
}
