// Which blocks and lines of the block view are audibly active at a moment of the song (the "play lights"). Pure: it reads the flat graph the studio already has
// (nodes with parts in 1-based bars, links with from/out/to/inp/type/disabled) and a position in bars. @ample:play-fx

/** The links that carry sound or notes. Text and data outputs (warnings, a lyrics line, a style) are never lit. */
import { barBeats } from './meter.mjs';
const SIGNAL_TYPES = new Set(['audio', 'midi', 'render', 'midi-sequence']);

/** How a block looks when it is active: one visual character per kind of work (drums beat, a bass is low and thick, notes flicker, a filter sweeps, a delay or reverb rings, dynamics squash, a mixer merges, the master glows). */
export const FX_KINDS = ['pulse', 'low', 'note', 'wave', 'sweep', 'echo', 'squash', 'merge', 'master', 'quiet'];

const KIND_OF = {
  pulse: ['drums', 'sampler', 'scratch', 'click'],
  low: ['bass'],
  note: ['midi-clip', 'midi', 'arpeggiator', 'chord', 'quantize', 'transpose', 'midi-humanize', 'piano', 'guitar', 'gm', 'synth', 'delay-llama', 'midi-render', 'fire'],
  wave: ['ace-music', 'ai-voice', 'choir', 'voice-clone', 'import-audio', 'ambience', 'granular'],
  sweep: ['trim', 'follow', 'curve', 'map', 'rewind', 'filter', 'eq', 'peq', 'deesser', 'denoise', 'gate', 'tremolo', 'phaser', 'flanger', 'chorus', 'pitch', 'autotune', 'vocoder', 'chop', 'shift', 'stretch', 'reverse', 'bitcrusher', 'distortion', 'normalize', 'fade', 'cut', 'crossfade', 'width', 'pan', 'volume', 'meter'],
  echo: ['delay', 'reverb', 'convolution'],
  squash: ['compressor', 'limiter', 'duck'],
  merge: ['mixer', 'bus', 'join', 'region-merge', 'send', 'splitter', 'sequencer', 'variants'],
  master: ['master'],
  quiet: ['group', 'lyrics', 'mood-scene', 'scorer', 'module', 'silence'],
};
const KIND_BY_TYPE = new Map(Object.entries(KIND_OF).flatMap(([kind, types]) => types.map((t) => [t, kind])));

/** The visual character of a block type (a type the table does not know gets the plain waveform look). */
export const fxKind = (tpl) => KIND_BY_TYPE.get(tpl) ?? 'wave';

/** Every block type the table knows (for the test that checks it covers the templates). */
export const knownTypes = () => [...KIND_BY_TYPE.keys()];

/** The key of a link in the flat graph (the studio's links have no id of their own). */
export const linkKey = (l) => `${l.from}:${l.out}>${l.to}:${l.inp}`;

/**
 * Where in a take (seconds) a song position is. A block's take is local: when it is as long as the bars of the block's parts together, it holds only those bars one after the other
 * (a block at bars 3 and 4 has a take of two bars that starts at its first part); otherwise it is a stretch of the song that starts at `firstBar` (a block without parts, a range
 * preview). -1 when the bar is not in the take.
 */
export function takeTime(entry, bar, tempo, firstBar = 1, parts = []) {
  const spb = secondsPerBar(tempo), total = (parts || []).reduce((sum, p) => sum + p.length, 0);
  if (total > 0 && Math.abs(entry.seconds - total * spb) < spb * 0.5) {
    let before = 0;
    for (const p of [...parts].sort((x, y) => x.at - y.at)) {
      if (bar >= p.at && bar < p.at + p.length) return (before + bar - p.at) * spb;
      before += p.length;
    }
    return -1;
  }
  return (bar - (Number(firstBar) || 1)) * spb;
}

/** The bar of a block counted from its own first bar (0 is the first), through its parts one after the other; a block without parts counts from bar 1. -1 when the bar is outside its parts. */
export function localBar(parts, bar) {
  if (!parts?.length) return Math.max(0, Math.floor(bar) - 1);
  let before = 0;
  for (const p of [...parts].sort((x, y) => x.at - y.at)) {
    if (bar >= p.at && bar < p.at + p.length) return before + Math.floor(bar) - p.at;
    before += p.length;
  }
  return -1;
}

/** The loudness (0 to 1) of a take at a song position, from its peaks ({ peaks: [min, max, ...], seconds }, as the studio gets them): the loudest point from `hold` seconds ago up to now, so a drum between two hits still counts as sounding. null when there is nothing to read; 0 outside the take. */
export function levelAt(entry, bar, tempo, firstBar = 1, hold = 0.5, parts = []) {
  if (!entry?.peaks?.length || !(entry.seconds > 0)) return null;
  const points = entry.peaks.length / 2, time = takeTime(entry, bar, tempo, firstBar, parts);
  if (time < 0 || time >= entry.seconds) return 0;
  const first = Math.max(0, Math.floor(((time - hold) / entry.seconds) * points)), last = Math.min(points - 1, Math.floor(((time + 0.05) / entry.seconds) * points) + 1);
  let level = 0;
  for (let k = first; k <= last; k++) level = Math.max(level, Math.abs(entry.peaks[k * 2]), Math.abs(entry.peaks[k * 2 + 1]));
  return Math.min(1, level);
}

/** Seconds in one bar at a tempo (4/4, like the arrangement). */
export const secondsPerBar = (tempo) => (barBeats() * 60) / (Number(tempo) > 0 ? Number(tempo) : 120);

/** The song position in bars (1-based, fractional) for a playback time in seconds; `firstBar` is the first bar of the take that plays (1 for a whole song). */
export const songBar = (time, tempo, firstBar = 1) => (Number(firstBar) || 1) + Math.max(0, Number(time) || 0) / secondsPerBar(tempo);

/** Whether a block's parts cover a bar; a block without parts is there for the whole song. */
export const inParts = (parts, bar) => !parts?.length || parts.some((p) => bar >= p.at && bar < p.at + p.length);

/**
 * The blocks and links that are lit at a position in bars. A block is lit when it is not disabled, its parts cover the bar, it is a source or gets signal over an enabled
 * link from a lit block, and (when the song has a master) it can reach the master. A link is lit when it carries sound or notes and both its ends are lit.
 * Returns { nodes: Set of block ids, links: Set of link keys }.
 */
export function activity(graph, bar) {
  const nodes = graph.nodes.filter((n) => !n.proxy);
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const links = graph.links.filter((l) => !l.disabled && SIGNAL_TYPES.has(l.type) && byId.has(l.from) && byId.has(l.to));
  const incoming = new Map(nodes.map((n) => [n.id, []]));
  for (const l of links) incoming.get(l.to).push(l);
  // a block that has a sound or notes input (even a disabled one) is never a source: it is dark when nothing lit feeds it
  const takesSignal = new Set(graph.links.filter((l) => SIGNAL_TYPES.has(l.type)).map((l) => l.to));
  const outgoing = new Map(nodes.map((n) => [n.id, []]));
  for (const l of links) outgoing.get(l.from).push(l.to);
  // a block with parts is there inside them; a block without parts (a midi-clip that only feeds an instrument, a mixer, the master) is there when what it feeds is there
  const gates = new Map();
  const gate = (n, seen = new Set()) => {
    if (gates.has(n.id)) return gates.get(n.id);
    if (n.disabledBy || n.flags?.includes('disabled')) return false;
    if (n.parts?.length) { const ok = inParts(n.parts, bar); gates.set(n.id, ok); return ok; }
    if (seen.has(n.id)) return false;
    seen.add(n.id);
    const consumers = outgoing.get(n.id);
    const ok = consumers.length === 0 || consumers.some((id) => gate(byId.get(id), seen));
    gates.set(n.id, ok);
    return ok;
  };
  const placed = (n) => gate(n);

  // the blocks that can reach the master (when there is one): a block whose sound goes nowhere is not part of what is heard
  const heard = new Set();
  const masters = nodes.filter((n) => n.tpl === 'master').map((n) => n.id);
  if (masters.length) {
    const toward = new Map(nodes.map((n) => [n.id, []]));
    for (const l of links) toward.get(l.to).push(l.from);
    const queue = [...masters];
    for (const id of masters) heard.add(id);
    while (queue.length) for (const from of toward.get(queue.shift())) if (!heard.has(from)) { heard.add(from); queue.push(from); }
  }
  const audible = (n) => !masters.length || heard.has(n.id);

  // a block is lit when its own parts cover the bar and a lit block feeds it (a block with no incoming link is a source); repeated until nothing changes, so the order of the blocks does not matter
  const lit = new Set();
  for (let changed = true, round = 0; changed && round <= nodes.length; round++) {
    changed = false;
    for (const n of nodes) {
      if (lit.has(n.id) || !placed(n) || !audible(n)) continue;
      const feeds = incoming.get(n.id);
      if ((feeds.length === 0 && !takesSignal.has(n.id)) || feeds.some((l) => lit.has(l.from))) { lit.add(n.id); changed = true; }
    }
  }
  const litLinks = new Set(links.filter((l) => lit.has(l.from) && lit.has(l.to)).map(linkKey));
  // a clip used in the notes of another clip (notes a.notes |> shift(8bars) |> merge(b.notes ...)) is there in the bars where the pipeline uses it: it lights with the clip that uses it, and the dotted line between them
  for (const ref of noteReferences(graph.nodes)) {
    const consumer = byId.get(ref.to), source = byId.get(ref.from);
    if (!consumer || !source || source.disabledBy || source.flags?.includes('disabled')) continue;
    if (!placed(consumer) || !audible(consumer) || !inParts(consumer.parts, bar)) continue;
    const start = consumer.parts?.length ? Math.min(...consumer.parts.map((p) => p.at)) : 1, local = Math.floor(bar) - start;
    if (!ref.spans.some(([from, to]) => local >= from && local < to)) continue;
    lit.add(ref.from);
    litLinks.add(refKey(ref.from, ref.to));
  }
  return { nodes: lit, links: litLinks };
}

/** The key of the dotted line from a clip to the clip that uses its notes (the same shape as a link key, so the lights treat it alike). */
export const refKey = (from, to) => `${from}:ref>${to}:ref`;

/** The note references of the song: for every clip with a note pipeline, the clips whose notes it uses and the bars (counted from its own first bar, from inclusive, to exclusive) where it uses them. */
export function noteReferences(nodes) {
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const out = [];
  for (const consumer of nodes) {
    const text = consumer.tpl === 'midi-clip' ? String(consumer.params?.notes ?? '') : '';
    if (!/^\s*[A-Za-z(]/.test(text.replace(/#[^\n]*/g, ''))) continue;
    const per = new Map();
    for (const m of text.matchAll(/([A-Za-z_][\w-]*)\.notes\b(?:\s*\|>\s*shift\(\s*(-?\d+(?:\.\d+)?)\s*bars?\s*\))?/g)) {
      const source = byId.get(m[1]);
      if (!source || source === consumer || source.tpl !== 'midi-clip') continue;
      const length = Number(source.params?.bars) || (source.parts?.length ? source.parts.reduce((sum, p) => sum + p.length, 0) : Infinity);
      const offset = Number(m[2]) || 0;
      (per.get(source.id) || per.set(source.id, []).get(source.id)).push([offset, offset + length]);
    }
    for (const [from, spans] of per) out.push({ from, to: consumer.id, spans });
  }
  return out;
}
