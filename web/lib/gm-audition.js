import { isCompactMidiNotes } from './expanded-midi-notes.js';

export function previewMidiNotes(source, ppq, limit) {
  if (!isCompactMidiNotes(source, ppq) || !Number.isInteger(limit) || limit < 1 || limit > 128) return [];
  return source.trim().split(/\s+/).slice(0, limit).map((text) => {
    const fields = text.split(':'), ticks = (value) => ppq === 1_000_000 ? Math.round(Number(value) * ppq) : Number(value);
    const note = { bar: Number(fields[0]), tick: ticks(fields[1]), ticks: ticks(fields[2]), ppq, pitch: Number(fields[3]), velocity: Number(fields[4]) };
    if (fields.length >= 8) { note.channel = Number(fields[5]); note.bank = Number(fields[6]); note.program = Number(fields[7]); }
    return note;
  });
}
