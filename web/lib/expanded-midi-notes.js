import { barBeats } from './meter.mjs';
const MAX_NOTES = 100_000;
const MAX_TEXT = 4_000_000;
const REQUIRED = ['bar', 'start', 'length', 'pitch', 'velocity'];
const ROUTING = ['channel', 'bank', 'program'];

function timeValue(value, ppq) {
  if (ppq !== 1_000_000) return /^\d+$/.test(value) && Number.isSafeInteger(Number(value)) ? Number(value) : null;
  if (!/^(0|[1-9]\d*)(\.\d{1,6})?$/.test(value)) return null;
  const [whole, fraction = ''] = value.split('.');
  const ticks = Number(whole) * ppq + Number((fraction + '000000').slice(0, 6)) * ppq / 1_000_000;
  return Number.isSafeInteger(ticks) ? ticks : null;
}

const EXT_RANGES = { pan: [-100, 100], fine: [-100, 100], rel: [0, 5000], cut: [0, 100], slide: [0, 1], port: [0, 2000], tie: [0, 2_000_000] };

/** Validates the `;key=value` pairs after a note entry (PR06); a known key outside its range fails, an unknown key only needs a valid integer. */
function validExtensions(text) {
  let hasTie = false;
  for (const pair of text.split(';')) {
    const m = /^([a-z]+)=(-?\d+)$/.exec(pair);
    if (!m) return false;
    const range = EXT_RANGES[m[1]];
    if (range && (Number(m[2]) < range[0] || Number(m[2]) > range[1])) return false;
    if (m[1] === 'tie') { if (hasTie) return false; hasTie = true; }
  }
  return true;
}

function validCompact(compact, ppq) {
  const notes = compact.trim().replace(/^"|"$/g, '').split(/\s+/).filter(Boolean);
  if (notes.length > MAX_NOTES) return false;
  for (const entry of notes) {
    const semi = entry.indexOf(';');
    if (semi >= 0 && !validExtensions(entry.slice(semi + 1))) return false;
    const fields = (semi < 0 ? entry : entry.slice(0, semi)).split(':');
    if (![5, 8, 9].includes(fields.length)) return false;
    const [barText, startText, lengthText, pitchText, velocityText, channelText, bankText, programText, tieText] = fields;
    const bar = Number(barText), pitch = Number(pitchText), velocity = Number(velocityText);
    const start = timeValue(startText, ppq), length = timeValue(lengthText, ppq);
    if (!/^\d+$/.test(barText) || bar < 1 || bar > 1_000_000 || start === null || length === null || length < 1 || start >= barBeats() * ppq || length > barBeats() * ppq - start || !/^\d+$/.test(pitchText) || pitch > 127 || !/^\d+$/.test(velocityText) || velocity < 1 || velocity > 127) return false;
    if (fields.length >= 8 && (![channelText, bankText, programText].every((x) => /^\d+$/.test(x)) || Number(channelText) > 15 || Number(bankText) > 16383 || Number(programText) > 127)) return false;
    if (fields.length === 9 && (!/^\d+$/.test(tieText) || Number(tieText) > 2_000_000)) return false;
    if (fields.length === 9 && semi >= 0 && /(?:^|;)tie=/.test(entry.slice(semi + 1))) return false;
  }
  return true;
}

function named(note) {
  // PR06: the extension suffix (;pan=..;fine=..) is stripped here, not shown in the expanded form yet; the compact text (the roll's own format) keeps it.
  const semi = note.indexOf(';');
  const fields = (semi < 0 ? note : note.slice(0, semi)).split(':');
  const result = [`  note {`, `    bar ${fields[0]}`, `    start ${fields[1]}`, `    length ${fields[2]}`, `    pitch ${fields[3]}`, `    velocity ${fields[4]}`];
  if (fields.length >= 8) result.push(`    channel ${fields[5]}`, `    bank ${fields[6]}`, `    program ${fields[7]}`);
  if (fields.length === 9) result.push(`    tie-id ${fields[8]}`);
  else if (semi >= 0) { const tie = /(?:^|;)tie=(\d+)(?:;|$)/.exec(note.slice(semi + 1)); if (tie) result.push(`    tie-id ${tie[1]}`); }
  result.push('  }');
  return result.join('\n');
}

export function expandMidiNotes(compact, ppq) {
  if (!Number.isInteger(ppq) || ppq < 1 || ppq > 1_000_000 || compact.length > MAX_TEXT || !validCompact(compact, ppq)) return null;
  const notes = compact.trim().replace(/^"|"$/g, '').split(/\s+/).filter(Boolean);
  return ['notes {', ...notes.map(named), '}'].join('\n');
}

export function compactMidiNotes(source, ppq) {
  if (source.length > MAX_TEXT || !Number.isInteger(ppq) || ppq < 1 || ppq > 1_000_000) return null;
  const lines = source.replace(/\r\n/g, '\n').split('\n');
  if (lines[0]?.trim() !== 'notes {') return null;
  const notes = [];
  let fields = null, closed = false;
  for (const raw of lines.slice(1)) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) continue;
    if (closed) return null;
    if (!fields) {
      if (line === 'note {') fields = new Map();
      else if (line === '}') closed = true;
      else return null;
      continue;
    }
    if (line === '}') {
      if (!REQUIRED.every((key) => fields.has(key)) || ROUTING.some((key) => fields.has(key)) && !ROUTING.every((key) => fields.has(key))) return null;
      const values = REQUIRED.map((key) => fields.get(key));
      if (ROUTING.every((key) => fields.has(key))) values.push(...ROUTING.map((key) => fields.get(key)));
      if (fields.has('tie-id') && ROUTING.every((key) => fields.has(key))) values.push(fields.get('tie-id'));
      notes.push(values.join(':') + (fields.has('tie-id') && !ROUTING.every((key) => fields.has(key)) ? `;tie=${fields.get('tie-id')}` : ''));
      if (notes.length > MAX_NOTES) return null;
      fields = null;
      continue;
    }
    const at = line.search(/\s/);
    if (at < 1) return null;
    const key = line.slice(0, at), value = line.slice(at).trim();
    if (![...REQUIRED, ...ROUTING, 'tie-id'].includes(key) || !value || /\s/.test(value) || fields.has(key)) return null;
    fields.set(key, value);
  }
  if (!closed || fields || !validCompact(notes.join(' '), ppq)) return null;
  return notes.join(' ');
}

export function isCompactMidiNotes(value, ppq) { return validCompact(value, ppq); }
