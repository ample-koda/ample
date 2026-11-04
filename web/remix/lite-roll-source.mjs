// Where the notes of a song are written, for the note editor of Remix: the midi-clip blocks of a .amplet text, and for each the `notes` line (the compact form
// "bar:beat:length:pitch:velocity ..."). Pure text work with offsets, so the page can replace just that value and nothing else in the file.

import { barBeats, setBarBeatsFromText } from '../lib/meter.mjs';

const HEADER = /^(\s*)midi-clip\s+("[^"]*"|\S+)(?:\s+(?:stable|pinned|live)(?:@\S+)?)?(?:\s+at\s+(\d+)\s+for\s+(\d+))?\s*\{\s*$/;   // the class word is the pre-release form
const NOTES = /^(\s*)notes(\s+)(.*)$/;
const BARS = /^\s*bars\s+(\d+)\s*$/;

/**
 * The midi-clip blocks of a song text.
 * @param {string} text the text of a .amplet file
 * @returns {{ name: string, line: number, bars: number, hasNotes: boolean, rollable: boolean, why: string, from: number, to: number, value: string }[]}
 * `from` and `to` are the offsets of the value of the `notes` line (after "notes" and its spaces, up to the end of the line); `rollable` is true for the compact form on one line.
 */
export function findClips(text) {
  setBarBeatsFromText(text);   // a bar has the beats of the meter on the composition line
  const lines = text.split('\n');
  const starts = [];
  let offset = 0;
  for (const line of lines) { starts.push(offset); offset += line.length + 1; }
  const clips = [];
  for (let i = 0; i < lines.length; i++) {
    const header = HEADER.exec(lines[i].replace(/\r$/, ''));
    if (!header) continue;
    const clip = { name: header[2].replace(/^"|"$/g, ''), line: i, bars: header[4] ? Number(header[4]) : 0, hasNotes: false, rollable: false, composed: false, why: 'This clip has no notes line.', from: -1, to: -1, value: '' };
    let depth = 1;
    for (let j = i + 1; j < lines.length && depth > 0; j++) {
      const body = lines[j].replace(/\r$/, '');
      if (/\{\s*$/.test(body)) depth++;
      if (/^\s*\}\s*$/.test(body)) { depth--; continue; }
      const bars = BARS.exec(body);
      if (bars) clip.bars = Number(bars[1]);
      const notes = NOTES.exec(body);
      if (notes && depth === 1) {
        clip.hasNotes = true;
        clip.from = starts[j] + notes[1].length + 'notes'.length + notes[2].length;
        clip.to = starts[j] + body.length;
        clip.value = notes[3];
        if (/^[A-Za-z_][\w-]*\.notes\b/.test(notes[3].trim())) { clip.composed = true; clip.why = 'These notes are built from other clips: the roll shows them, edit the clips they are built from.'; }
        else if (/^\s*\{/.test(notes[3]) || notes[3].trim() === '') { clip.why = 'These notes are written in the expanded form: edit them as text.'; }
        else { clip.rollable = true; clip.why = ''; }
      }
      if (/^\s*(chords|melody)\s/.test(body) && depth === 1 && !clip.hasNotes) clip.why = 'This clip is written with chords or a melody: edit it as text.';
    }
    clips.push(clip);
  }
  return clips;
}

/** Splits at a separator that is not inside parentheses. */
function splitTop(text, sep) {
  const out = []; let depth = 0, last = 0;
  for (let i = 0; i < text.length; i++) {
    if (text[i] === '(') depth++; else if (text[i] === ')') depth--;
    else if (depth === 0 && text.startsWith(sep, i)) { out.push(text.slice(last, i)); last = i + sep.length; i += sep.length - 1; }
  }
  out.push(text.slice(last));
  return out.map((s) => s.trim());
}

const eventsOf = (value) => value.trim().split(/\s+/).filter(Boolean).map((token) => {
  const [main, ...rest] = token.split(';'), p = main.split(':');
  if (p.length < 5 || p.slice(0, 5).some((x) => !Number.isFinite(Number(x)))) throw new Error('note');
  return { at: (Number(p[0]) - 1) * barBeats() + Number(p[1]), len: Number(p[2]), tail: p.slice(3).join(':'), extra: rest.length ? ';' + rest.join(';') : '' };
});

function evaluate(expr, byName, seen) {
  const parts = splitTop(expr, '|>'), head = /^([A-Za-z_][\w-]*)\.notes$/.exec(parts[0]);
  const source = head ? byName.get(head[1]) : null;
  if (!source || seen.has(source.name)) throw new Error('source');
  let events = source.composed ? evaluate(source.value, byName, new Set([...seen, source.name])) : source.rollable ? eventsOf(source.value) : (() => { throw new Error('source'); })();
  for (const step of parts.slice(1)) {
    const call = /^([a-z]+)\((.*)\)$/s.exec(step);
    if (!call) throw new Error('step');
    const arg = call[2].trim();
    if (call[1] === 'shift') {
      const amount = /^([+-]?\d+(?:\.\d+)?)\s*(bars?|beats?)$/.exec(arg);
      if (!amount) throw new Error('step');
      const by = Number(amount[1]) * (amount[2].startsWith('bar') ? barBeats() : 1);
      events = events.map((e) => ({ ...e, at: e.at + by }));
    } else if (call[1] === 'merge') events = events.concat(evaluate(arg, byName, new Set([...seen, source.name])));
    else if (call[1] === 'stretch' && Number(arg) > 0) events = events.map((e) => ({ ...e, at: e.at * Number(arg), len: e.len * Number(arg) }));
    else if (call[1] === 'transpose' && Number.isFinite(Number(arg))) events = events.map((e) => { const q = e.tail.split(':'); q[0] = String(Math.max(0, Math.min(127, Number(q[0]) + Number(arg)))); return { ...e, tail: q.join(':') }; });
    else throw new Error('step');
  }
  return events;
}

/**
 * The notes of a clip that is built from other clips (`notes pad_intro.notes |> shift(4bars) |> merge(...)`) as compact notes text, for the roll to show; null when
 * the line uses a step this page does not compute (the roll then says to read it as text). `names` are the clips it was built from, in order.
 */
export function composedNotes(clips, clip) {
  const byName = new Map(clips.map((c) => [c.name, c]));
  try {
    const events = evaluate(clip.value, byName, new Set([clip.name])).sort((a, b) => a.at - b.at);
    const bpb = barBeats();
    const text = events.map((e) => { const bar = Math.floor(e.at / bpb + 1e-9) + 1, beat = +(e.at - (bar - 1) * bpb).toFixed(6); return `${bar}:${beat}:${+e.len.toFixed(6)}:${e.tail}${e.extra}`; }).join(' ');
    return { text, names: [...clip.value.matchAll(/([A-Za-z_][\w-]*)\.notes/g)].map((m) => m[1]) };
  } catch { return null; }
}

/** The tempo of the song (the project line), 120 when there is none. */
export function tempoOf(text) {
  const match = /^\s*(?:project|composition)\s+(?:"[^"]*"|\S+)\s*\{[^}]*\btempo\s+(\d+(?:\.\d+)?)/m.exec(text);
  return match ? Number(match[1]) : 120;
}

/** The text with the value of one clip's notes line replaced; the rest of the file stays as it is. */
export function withNotes(text, clip, value) {
  return text.slice(0, clip.from) + value + text.slice(clip.to);
}
