export const midiEventKinds = Object.freeze(['NoteOff', 'NoteOn', 'PolyphonicPressure', 'ControlChange', 'ProgramChange', 'ChannelPressure', 'PitchBend', 'Meta', 'SystemExclusive']);

export function midiEventRecords(source, track) {
  return source.replace(/\r\n/g, '\n').split('\n').flatMap((line, index) => {
    const m = /^\s*# source-event track=(\d+) tick=(\d+) kind=(\w+) channel=(\d+) data1=(\d+) data2=(\d+) meta=(none|[0-9A-Fa-f]{2}) payload=([0-9A-Fa-f]*)\s*$/.exec(line);
    return m && Number(m[1]) === track ? [{ line: index, track, tick: Number(m[2]), kind: m[3], channel: Number(m[4]), data1: Number(m[5]), data2: Number(m[6]), meta: m[7], payload: m[8] }] : [];
  });
}

export function validMidiEvent(event) {
  return Number.isSafeInteger(event.tick) && event.tick >= 0 && event.tick <= 0x7fffffff && midiEventKinds.includes(event.kind) &&
    Number.isInteger(event.channel) && event.channel >= 0 && event.channel <= 15 && Number.isInteger(event.data1) && event.data1 >= 0 && event.data1 <= (event.kind === 'Meta' || event.kind === 'SystemExclusive' ? 255 : 127) &&
    Number.isInteger(event.data2) && event.data2 >= 0 && event.data2 <= 127 && (event.meta === 'none' || /^[0-9A-Fa-f]{2}$/.test(event.meta)) &&
    typeof event.payload === 'string' && event.payload.length <= 131070 && event.payload.length % 2 === 0 && /^[0-9A-Fa-f]*$/.test(event.payload);
}

export function midiEventLine(event) {
  if (!Number.isInteger(event.track) || event.track < 0 || event.track > 255 || !validMidiEvent(event)) return null;
  return `# source-event track=${event.track} tick=${event.tick} kind=${event.kind} channel=${event.channel} data1=${event.data1} data2=${event.data2} meta=${event.meta.toLowerCase()} payload=${event.payload.toLowerCase()}`;
}

export function pageMidiEvents(events, kind, query, page, size) {
  const term = query.trim().toLowerCase();
  const filtered = events.filter((event) => (!kind || event.kind === kind) && (!term || `${event.tick} ch ${event.channel + 1} ${event.kind}`.includes(term)));
  const pages = Math.max(1, Math.ceil(filtered.length / size)), safePage = Math.max(0, Math.min(page, pages - 1));
  return { events: filtered.slice(safePage * size, (safePage + 1) * size), total: filtered.length, page: safePage, pages };
}

export function pairMidiNoteEvents(events) {
  const active = new Map(), paired = new Map();
  for (const event of events) {
    if (event.kind !== 'NoteOn' && event.kind !== 'NoteOff' && !(event.kind === 'NoteOn' && event.data2 === 0)) continue;
    const key = `${event.channel}:${event.data1}`;
    const on = event.kind === 'NoteOn' && event.data2 > 0;
    if (on) {
      const queue = active.get(key) || [];
      queue.push(event);
      active.set(key, queue);
      continue;
    }
    const queue = active.get(key);
    const start = queue?.[0];
    if (!start) continue;
    queue.shift();
    if (event.tick > start.tick) { paired.set(start.line, event.line); paired.set(event.line, start.line); }
  }
  return paired;
}

export function midiEventSummary(event) {
  const name = event.kind === 'Meta' ? `meta ${event.meta}` : event.kind === 'SystemExclusive' ? 'SysEx' : event.kind === 'ControlChange' ? `controller ${event.data1}` : event.kind === 'ProgramChange' ? 'program' : event.kind.includes('Pressure') ? 'pressure' : event.kind === 'PitchBend' ? 'bend' : 'note';
  const payload = event.payload ? ` · payload ${event.payload.length / 2} B ${event.payload.slice(0, 24).toLowerCase()}${event.payload.length > 24 ? '…' : ''}` : '';
  return `${name} · data1 ${event.data1} · data2 ${event.data2}${event.meta === 'none' ? '' : ` · meta ${event.meta}`}${payload}`;
}

/** Notes split at bar boundaries share a tie-id (the ninth field of a compact note); returns one group per tie: its pitch, the bars it spans and its parts. */
export function tieGroups(compactNotes) {
  const groups = new Map();
  for (const entry of String(compactNotes || '').trim().replace(/^"|"$/g, '').split(/\s+/).filter(Boolean)) {
    const fields = entry.split(':');
    if (fields.length !== 9 || !/^\d+$/.test(fields[8]) || !/^\d+$/.test(fields[0]) || !/^\d+$/.test(fields[3])) continue;
    const id = Number(fields[8]), group = groups.get(id) || { id, pitch: Number(fields[3]), bars: [], parts: 0 };
    group.bars.push(Number(fields[0]));
    group.parts += 1;
    groups.set(id, group);
  }
  return [...groups.values()].filter((group) => group.parts > 1).map((group) => ({ ...group, bars: group.bars.sort((a, b) => a - b) })).sort((a, b) => a.id - b.id);
}
