// The pure part of the sequencer workspace (plan fl-block FL01, FL04): reading, writing and editing a .seq text, and the lanes of the block.
// Twin of SequenceFile in src/Ample.Audio/Engine/SequenceFile.cs (the same grammar). Every edit returns a new sequence and leaves its argument alone. @ample:sequencer

export const TYPES = ['drum', 'loop', 'notes', 'bass', 'piano', 'guitar', 'gm', 'llama'];
export const LANES = 8;
export const NAME = /^[A-Za-z0-9_-]{1,60}$/;
export const DRUM_CYCLE = ['.', 'x', 'X', 'o'];

const words = (line) => {
  const out = [];
  let cur = '', quoted = false;
  for (const c of line) {
    if (c === '"') { quoted = !quoted; cur += c; } else if (/\s/.test(c) && !quoted) { if (cur) { out.push(cur); cur = ''; } } else cur += c;
  }
  if (cur) out.push(cur);
  return out;
};

function settingsOf(list, line) {
  const settings = {};
  for (const w of list) {
    const eq = w.indexOf('=');
    if (eq <= 0) throw new Error(`line ${line}: '${w}' must look like key=value`);
    settings[w.slice(0, eq)] = w.slice(eq + 1).replace(/^"|"$/g, '');
  }
  return settings;
}

/** Reads a .seq text. Returns { seq } or { error } with the line of the mistake. */
export function parseSeq(text) {
  const seq = { channels: [], patterns: [], clips: [], sections: [], markers: [], loop: null, intro: [], playlistNotes: [], footer: [] };
  let pending = [];   // comment lines wait here for the line they stand above
  const take = () => { if (!pending.length) return undefined; const t = pending; pending = []; return t; };
  const withNotes = (item, notes) => (notes ? { ...item, notes } : item);
  try {
    let mode = 'top', header = false, n = 0;
    for (const raw of String(text ?? '').replace(/\r\n/g, '\n').split('\n')) {
      n++;
      const line = raw.replace(/\s+$/, ''), trimmed = line.trim();
      if (!trimmed) continue;
      if (trimmed.startsWith('#')) { pending.push(trimmed); continue; }
      if (!header) { if (trimmed !== 'sequencer 1') throw new Error('the file must start with the line: sequencer 1'); header = true; seq.intro = take() ?? []; continue; }
      const w = words(trimmed);
      if (!/^\s/.test(line)) {
        if (w[0] === 'channel') {
          if (w.length < 3 || !NAME.test(w[1]) || !TYPES.includes(w[2])) throw new Error(`line ${n}: a channel looks like: channel kick drum sample=samples/kick.wav`);
          if (seq.channels.some((c) => c.name === w[1])) throw new Error(`line ${n}: the channel '${w[1]}' is defined twice`);
          seq.channels.push(withNotes({ name: w[1], type: w[2], settings: settingsOf(w.slice(3), n) }, take()));
          mode = 'top';
        } else if (w[0] === 'pattern') {
          if (w.length < 2 || !NAME.test(w[1])) throw new Error(`line ${n}: a pattern looks like: pattern verse bars=4`);
          if (seq.patterns.some((p) => p.name === w[1])) throw new Error(`line ${n}: the pattern '${w[1]}' is defined twice`);
          const s = settingsOf(w.slice(2), n);
          const from = s.from ?? '';
          if (from) {
            const base = seq.patterns.find((x) => x.name === from);
            if (!base) throw new Error(`line ${n}: the pattern '${w[1]}' is a variation of '${from}', which must be defined before it`);
            if (base.from) throw new Error(`line ${n}: '${from}' is itself a variation: a variation starts from a plain pattern`);
          }
          const bars = s.bars === undefined ? (from ? seq.patterns.find((x) => x.name === from).bars : 1) : Number(s.bars);
          if (!Number.isInteger(bars) || bars < 1 || bars > 999) throw new Error(`line ${n}: bars must be a whole number from 1 to 999`);
          seq.patterns.push(withNotes({ name: w[1], bars, color: s.color ?? '', rows: {}, from }, take()));
          mode = 'pattern';
        } else if (w[0] === 'playlist') { seq.playlistNotes = take() ?? []; mode = 'playlist'; }
        else throw new Error(`line ${n}: unknown line '${w[0]}': channel, pattern or playlist`);
        continue;
      }
      if (mode === 'pattern') {
        const space = trimmed.indexOf(' ');
        const pattern = seq.patterns[seq.patterns.length - 1], channel = space < 0 ? trimmed : trimmed.slice(0, space);
        pattern.rows[channel] = space < 0 ? '' : trimmed.slice(space + 1).trim();
        const rowNotes = take();
        if (rowNotes) (pattern.rowNotes ??= {})[channel] = rowNotes;
      } else if (mode === 'playlist') {
        const s = settingsOf(w.slice(w[0] === 'loop' ? 1 : 2), n);
        const int = (k) => { const v = Number(s[k]); return Number.isInteger(v) && v >= 1 && v <= 99999 ? v : null; };
        if (w[0] === 'clip') {
          if (!NAME.test(w[1] ?? '') || int('at') === null || int('for') === null) throw new Error(`line ${n}: a clip looks like: clip verse track=1 at=1 for=4`);
          seq.clips.push(withNotes({ pattern: w[1], track: int('track') ?? 1, at: int('at'), bars: int('for') }, take()));
        } else if (w[0] === 'marker') {
          if (!w[1] || int('at') === null) throw new Error(`line ${n}: a marker looks like: marker Drop at=9`);
          seq.markers.push(withNotes({ name: w[1], at: int('at') }, take()));
        } else if (w[0] === 'loop') {
          if (int('at') === null || int('for') === null) throw new Error(`line ${n}: the loop looks like: loop at=1 for=4`);
          if (seq.loop) throw new Error(`line ${n}: there can be only one loop line`);
          seq.loop = withNotes({ at: int('at'), bars: int('for') }, take());
        } else if (w[0] === 'section') {
          if (!w[1] || int('at') === null || int('for') === null) throw new Error(`line ${n}: a section looks like: section Verse at=1 for=8 color=#4a90d9`);
          seq.sections.push(withNotes({ name: w[1], at: int('at'), bars: int('for'), color: s.color ?? '' }, take()));
        } else throw new Error(`line ${n}: unknown playlist line '${w[0]}': clip, section, marker or loop`);
      } else throw new Error(`line ${n}: an indented line belongs under a pattern or the playlist`);
    }
    seq.footer = pending;
    if (!header) throw new Error('the file must start with the line: sequencer 1');
  } catch (e) { return { error: String(e.message || e) }; }
  return { seq };
}

const settingText = (settings) => Object.entries(settings).map(([k, v]) => ` ${k}=${/\s/.test(v) || v === '' ? `"${v}"` : v}`).join('');

/** Writes a sequence in its canonical form (the same as the engine side). */
export function formatSeq(seq) {
  const out = [];
  const notes = (list, indent = '') => { for (const line of list ?? []) out.push(indent + line); };
  notes(seq.intro);
  out.push('sequencer 1');
  for (const c of seq.channels) { notes(c.notes); out.push(`channel ${c.name} ${c.type}${settingText(c.settings)}`); }
  for (const p of seq.patterns) {
    notes(p.notes);
    out.push(`pattern ${p.name} bars=${p.bars}${p.from ? ` from=${p.from}` : ''}${p.color ? ` color=${p.color}` : ''}`);
    for (const [channel, row] of Object.entries(p.rows)) { notes(p.rowNotes?.[channel], '  '); out.push(`  ${channel} ${row}`.replace(/\s+$/, '')); }
  }
  if (seq.clips.length || seq.sections.length || seq.markers.length || seq.loop) {
    notes(seq.playlistNotes);
    out.push('playlist');
    for (const c of seq.clips) { notes(c.notes, '  '); out.push(`  clip ${c.pattern} track=${c.track} at=${c.at} for=${c.bars}`); }
    for (const s of seq.sections) { notes(s.notes, '  '); out.push(`  section ${s.name} at=${s.at} for=${s.bars}${s.color ? ` color=${s.color}` : ''}`); }
    for (const m of seq.markers) { notes(m.notes, '  '); out.push(`  marker ${m.name} at=${m.at}`); }
    if (seq.loop) { notes(seq.loop.notes, '  '); out.push(`  loop at=${seq.loop.at} for=${seq.loop.bars}`); }
  }
  notes(seq.footer);
  return `${out.join('\n')}\n`;
}

/** The lanes text of the block as { 1: ['kick'], 2: ['hat', 'lead'] }; `all` means every channel. */
export function parseLanes(text, channels) {
  const lanes = {};
  for (const part of String(text ?? '').trim().replace(/^"|"$/g, '').split(/[\s,]+/).filter(Boolean)) {
    const m = /^lane(\d)=(.+)$/.exec(part);
    if (!m || Number(m[1]) < 1 || Number(m[1]) > LANES) continue;
    const names = m[2].split('+').filter(Boolean);
    lanes[Number(m[1])] = names.includes('all') ? channels.map((c) => c.name) : names;
  }
  return lanes;
}

/** The lanes text as it is written: `all` stays the token `all` (a channel added later is still in that lane). Use it for every edit that is not about that lane. */
export const parseLanesRaw = (text) => parseLanes(text, [{ name: 'all' }]);

/** The lanes after the channel `name` is switched in or out of lane `lane`: only that lane is written out as names, the others keep what the source says (including `all`). */
export function toggleLaneChannel(text, channels, lane, name) {
  const current = parseLanes(text, channels)[lane] ?? [];
  return { ...parseLanesRaw(text), [lane]: current.includes(name) ? current.filter((x) => x !== name) : [...current, name] };
}

export const formatLanes = (lanes) => Object.entries(lanes).filter(([, names]) => names.length).sort((a, b) => a[0] - b[0]).map(([lane, names]) => `lane${lane}=${names.join('+')}`).join(' ');

export function uniqueName(base, taken) {
  const clean = String(base || 'item').replace(/[^A-Za-z0-9_-]+/g, '-').replace(/^-+|-+$/g, '') || 'item';
  const used = new Set(taken);
  if (!used.has(clean)) return clean;
  for (let i = 2; ; i++) if (!used.has(`${clean}-${i}`)) return `${clean}-${i}`;
}

// --- edits ---
const copy = (seq) => ({ intro: [...(seq.intro ?? [])], playlistNotes: [...(seq.playlistNotes ?? [])], footer: [...(seq.footer ?? [])], channels: seq.channels.map((c) => ({ ...c, settings: { ...c.settings } })), patterns: seq.patterns.map((p) => ({ ...p, rows: { ...p.rows }, ...(p.rowNotes ? { rowNotes: { ...p.rowNotes } } : {}) })), clips: seq.clips.map((c) => ({ ...c })), sections: seq.sections.map((s) => ({ ...s })), markers: (seq.markers ?? []).map((m) => ({ ...m })), loop: seq.loop ? { ...seq.loop } : null });

export function addChannel(seq, name, type, settings = {}) {
  const out = copy(seq);
  const unique = uniqueName(name || type, out.channels.map((c) => c.name));
  out.channels.push({ name: unique, type, settings });
  return { seq: out, name: unique };
}

export function removeChannel(seq, name) {
  const out = copy(seq);
  out.channels = out.channels.filter((c) => c.name !== name);
  out.patterns.forEach((p) => { delete p.rows[name]; if (p.rowNotes) delete p.rowNotes[name]; });
  return out;
}

export function renameChannel(seq, from, to) {
  if (!NAME.test(to) || seq.channels.some((c) => c.name === to)) return null;
  const out = copy(seq);
  out.channels.forEach((c) => { if (c.name === from) c.name = to; });
  out.patterns.forEach((p) => { if (from in p.rows) { const rows = {}; for (const [k, v] of Object.entries(p.rows)) rows[k === from ? to : k] = v; p.rows = rows; } if (p.rowNotes && from in p.rowNotes) { const notes = {}; for (const [k, v] of Object.entries(p.rowNotes)) notes[k === from ? to : k] = v; p.rowNotes = notes; } });
  return out;
}

/** Sets or clears (value '' or null) settings of a channel. */
export function setChannel(seq, name, patch) {
  const out = copy(seq);
  const channel = out.channels.find((c) => c.name === name);
  if (!channel) return out;
  for (const [k, v] of Object.entries(patch)) { if (k === 'type') channel.type = v; else if (v === null || v === '') delete channel.settings[k]; else channel.settings[k] = String(v); }
  return out;
}

export function moveChannel(seq, name, to) {
  const out = copy(seq);
  const from = out.channels.findIndex((c) => c.name === name);
  if (from < 0) return out;
  const [item] = out.channels.splice(from, 1);
  out.channels.splice(Math.max(0, Math.min(out.channels.length, to)), 0, item);
  return out;
}

export function addPattern(seq, name, bars = 1) {
  const out = copy(seq);
  const unique = uniqueName(name || 'pattern', out.patterns.map((p) => p.name));
  out.patterns.push({ name: unique, bars: Math.max(1, Math.min(999, bars)), color: '', rows: {}, from: '' });
  return { seq: out, name: unique };
}

export function duplicatePattern(seq, from) {
  const out = copy(seq);
  const source = out.patterns.find((p) => p.name === from);
  if (!source) return { seq: out, name: from };
  const unique = uniqueName(`${from}-copy`, out.patterns.map((p) => p.name));
  out.patterns.push({ ...source, name: unique, rows: { ...source.rows }, notes: undefined, rowNotes: undefined });   // the comments stay with the pattern they were written for
  return { seq: out, name: unique };
}

/** A variation of a plain pattern (a variation of a variation starts from the same plain pattern): inherits its rows and its length. */
export function addVariation(seq, from) {
  const source = seq.patterns.find((p) => p.name === from);
  if (!source) return { seq, name: from };
  const base = source.from || source.name;
  const out = copy(seq);
  const baseline = out.patterns.find((p) => p.name === base);
  const unique = uniqueName(`${base}-b`, out.patterns.map((p) => p.name));
  out.patterns.push({ name: unique, bars: baseline.bars, color: baseline.color, rows: {}, from: base });
  return { seq: out, name: unique };
}

/** The rows a pattern really plays: the rows of its base with its own rows on top (a row that is just - silences the channel). */
export function rowsOf(seq, pattern) {
  const rows = {};
  const base = pattern.from ? seq.patterns.find((p) => p.name === pattern.from) : null;
  if (base) Object.assign(rows, base.rows);
  for (const [k, v] of Object.entries(pattern.rows)) { if (pattern.from && v.trim() === '-') delete rows[k]; else rows[k] = v; }
  return rows;
}

/** The song bar that plays at an output bar: inside or after the loop range the range repeats. */
export const sourceBar = (seq, bar) => (seq.loop && bar >= seq.loop.at ? seq.loop.at + ((bar - seq.loop.at) % seq.loop.bars) : bar);

/** Moves a pattern to another place in the row of tabs. A variation has to stay after the pattern it is made from: a move that would break that gives null. */
export function movePattern(seq, name, toIndex) {
  const at = seq.patterns.findIndex((p) => p.name === name);
  if (at < 0) return seq;
  const to = Math.max(0, Math.min(seq.patterns.length - 1, toIndex));
  if (at === to) return seq;
  const out = copy(seq);
  const [moved] = out.patterns.splice(at, 1);
  out.patterns.splice(to, 0, moved);
  const place = new Map(out.patterns.map((p, i) => [p.name, i]));
  if (out.patterns.some((p) => p.from && (place.get(p.from) ?? -1) > place.get(p.name))) return null;
  return out;
}

export function renamePattern(seq, from, to) {
  if (!NAME.test(to) || seq.patterns.some((p) => p.name === to)) return null;
  const out = copy(seq);
  out.patterns.forEach((p) => { if (p.name === from) p.name = to; if (p.from === from) p.from = to; });
  out.clips.forEach((c) => { if (c.pattern === from) c.pattern = to; });
  return out;
}

/** The names that leave with a pattern: the pattern, its variations and the variations of those (a variation cannot outlive its base). */
function patternFamily(seq, name) {
  const gone = new Set([name]);
  for (let grew = true; grew;) {
    grew = false;
    for (const p of seq.patterns) if (p.from && gone.has(p.from) && !gone.has(p.name)) { gone.add(p.name); grew = true; }
  }
  return gone;
}

/** What deleting a pattern takes with it, for the question before it happens: the variations (names) and the number of clips in the playlist. */
export function deletePatternImpact(seq, name) {
  const gone = patternFamily(seq, name);
  return { variations: seq.patterns.filter((p) => gone.has(p.name) && p.name !== name).map((p) => p.name), clips: seq.clips.filter((c) => gone.has(c.pattern)).length };
}

export function deletePattern(seq, name) {
  const out = copy(seq);
  const gone = patternFamily(out, name);
  out.patterns = out.patterns.filter((p) => !gone.has(p.name));
  out.clips = out.clips.filter((c) => !gone.has(c.pattern));
  return out;
}

export function setPattern(seq, name, patch) {
  const out = copy(seq);
  const p = out.patterns.find((x) => x.name === name);
  if (p) { if (patch.bars !== undefined) p.bars = Math.max(1, Math.min(999, Math.round(patch.bars))); if (patch.color !== undefined) p.color = patch.color; }
  return out;
}

export function setRow(seq, pattern, channel, text) {
  const out = copy(seq);
  const p = out.patterns.find((x) => x.name === pattern);
  if (p) { if (text === '' || text === null) { delete p.rows[channel]; if (p.rowNotes) delete p.rowNotes[channel]; } else p.rows[channel] = text; }
  return out;
}

/** The steps of a drum row as one string per bar (16 steps each); a missing bar repeats from the first one. */
export function barsOfRow(row, bars) {
  const groups = String(row ?? '').split('|').map((g) => g.replace(/\s+/g, ''));
  return Array.from({ length: bars }, (_, i) => { const g = groups[i % groups.length] || ''; return Array.from({ length: 16 }, (_, s) => g[s % (g.length || 1)] ?? '.').join(''); });
}

export const formatBars = (bars) => bars.join('|');

export const cycleStep = (c) => DRUM_CYCLE[(DRUM_CYCLE.indexOf(c) + 1) % DRUM_CYCLE.length] ?? 'x';

/** Clicks a step of a drum row of a pattern: writes the whole row back. */
export function toggleStep(seq, pattern, channel, bar, step) {
  const p = seq.patterns.find((x) => x.name === pattern);
  if (!p) return seq;
  const rows = barsOfRow(rowsOf(seq, p)[channel], p.bars);   // an inherited row is the starting point of the variation's own row
  const chars = [...rows[bar]];
  chars[step] = cycleStep(chars[step]);
  rows[bar] = chars.join('');
  return setRow(seq, pattern, channel, formatBars(rows));
}

export function addClip(seq, pattern, at, bars, track = 1) {
  const out = copy(seq);
  const p = seq.patterns.find((x) => x.name === pattern);
  out.clips.push({ pattern, track, at: Math.max(1, Math.round(at)), bars: Math.max(1, Math.round(bars ?? p?.bars ?? 1)) });
  return out;
}

export function updateClip(seq, index, patch) {
  const out = copy(seq);
  const c = out.clips[index];
  if (!c) return out;
  if (patch.at !== undefined) c.at = Math.max(1, Math.round(patch.at));
  if (patch.bars !== undefined) c.bars = Math.max(1, Math.round(patch.bars));
  if (patch.track !== undefined) c.track = Math.max(1, Math.round(patch.track));
  if (patch.pattern !== undefined && out.patterns.some((p) => p.name === patch.pattern)) c.pattern = patch.pattern;
  return out;
}

export const removeClip = (seq, index) => { const out = copy(seq); out.clips.splice(index, 1); return out; };

export function addSection(seq, name, at, bars, color = '') {
  const out = copy(seq);
  out.sections.push({ name: String(name).replace(/\s+/g, '-'), at: Math.max(1, Math.round(at)), bars: Math.max(1, Math.round(bars)), color });
  return out;
}

export function updateSection(seq, index, patch) {
  const out = copy(seq);
  const s = out.sections[index];
  if (s) Object.assign(s, patch.at !== undefined ? { at: Math.max(1, Math.round(patch.at)) } : {}, patch.bars !== undefined ? { bars: Math.max(1, Math.round(patch.bars)) } : {}, patch.color !== undefined ? { color: patch.color } : {}, patch.name !== undefined ? { name: String(patch.name).replace(/\s+/g, '-') } : {});
  return out;
}

export const removeSection = (seq, index) => { const out = copy(seq); out.sections.splice(index, 1); return out; };

export function addMarker(seq, name, at) {
  const out = copy(seq);
  out.markers.push({ name: String(name).replace(/\s+/g, '-') || 'marker', at: Math.max(1, Math.round(at)) });
  return out;
}

export function updateMarker(seq, index, patch) {
  const out = copy(seq);
  const m = out.markers[index];
  if (m) Object.assign(m, patch.at !== undefined ? { at: Math.max(1, Math.round(patch.at)) } : {}, patch.name !== undefined ? { name: String(patch.name).replace(/\s+/g, '-') || m.name } : {});
  return out;
}

export const removeMarker = (seq, index) => { const out = copy(seq); out.markers.splice(index, 1); return out; };

/** Sets the loop range, or clears it with null. */
export function setLoop(seq, loop) {
  const out = copy(seq);
  out.loop = loop ? { at: Math.max(1, Math.round(loop.at)), bars: Math.max(1, Math.round(loop.bars)) } : null;
  return out;
}

/** The number of bars the playlist covers (the end of the last clip). */
export const songBars = (seq) => seq.clips.reduce((m, c) => Math.max(m, c.at + c.bars - 1), 0);

/** Problems the engine would report: a clip of a pattern that does not exist, a row of a channel that does not exist. */
export function check(seq) {
  const problems = [];
  for (const c of seq.clips) if (!seq.patterns.some((p) => p.name === c.pattern)) problems.push(`a clip names the pattern '${c.pattern}', which is not defined`);
  for (const p of seq.patterns) for (const k of Object.keys(p.rows)) if (!seq.channels.some((c) => c.name === (k.endsWith('.events') ? k.slice(0, -'.events'.length) : k))) problems.push(`the pattern '${p.name}' has a row for '${k}', which is not a channel`);
  return problems;
}

/** The lanes with a channel removed or renamed. */
export const lanesWithout = (lanes, name) => Object.fromEntries(Object.entries(lanes).map(([k, v]) => [k, v.filter((n) => n !== name)]));
export const lanesRenamed = (lanes, from, to) => Object.fromEntries(Object.entries(lanes).map(([k, v]) => [k, v.map((n) => (n === from ? to : n))]));

/** The playlist track numbers in use, at least `min` of them. */
export const trackCount = (seq, min = 4) => Math.max(min, ...seq.clips.map((c) => c.track));
