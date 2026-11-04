// Deterministic pitch-contour grouping and a minimal Standard MIDI File encoder for reviewed vocal notes.

export function notesFromPitch(frames, hopSeconds, confidenceFloor = 0.55) {
  if (!Number.isFinite(hopSeconds) || hopSeconds <= 0) throw new Error('The pitch contour has an invalid hop size.');
  const ordered = frames.filter((frame) => Number.isFinite(frame.t) && Number.isFinite(frame.midi) && Number.isFinite(frame.hz) && Number.isFinite(frame.confidence)).slice().sort((a, b) => a.t - b.t);
  const groups = [];
  for (const frame of ordered) {
    if (frame.hz <= 0 || frame.confidence < confidenceFloor || frame.midi < 0 || frame.midi > 127) continue;
    const pitch = Math.round(frame.midi), timeMs = Math.round(frame.t * 1000), last = groups.at(-1);
    if (last && last.pitch === pitch && timeMs - last.lastMs <= Math.ceil(hopSeconds * 2000)) { last.lastMs = timeMs; last.confidence += frame.confidence; last.frames++; }
    else groups.push({ pitch, startMs: timeMs, lastMs: timeMs, confidence: frame.confidence, frames: 1 });
  }
  return groups.map((group) => {
    const startTick = Math.round(group.startMs * 960 / 1000), endTick = Math.max(startTick + 1, Math.round((group.lastMs + Math.round(hopSeconds * 1000)) * 960 / 1000));
    return { startTick, endTick, pitch: group.pitch, velocity: Math.max(40, Math.min(120, Math.round(40 + 80 * group.confidence / group.frames))), confidence: group.confidence / group.frames };
  });
}

const variable = (value) => {
  const bytes = [value & 0x7f];
  while ((value = Math.floor(value / 128)) > 0) bytes.unshift((value & 0x7f) | 0x80);
  return bytes;
};

export function encodeVocalMidi(notes) {
  const events = [{ tick: 0, order: -1, data: [0xff, 0x51, 0x03, 0x07, 0xa1, 0x20] }];
  for (const note of notes) {
    if (!Number.isSafeInteger(note.startTick) || !Number.isSafeInteger(note.endTick) || note.startTick < 0 || note.endTick <= note.startTick || !Number.isInteger(note.pitch) || note.pitch < 0 || note.pitch > 127 || !Number.isInteger(note.velocity) || note.velocity < 1 || note.velocity > 127) throw new Error('A reviewed vocal note is outside the MIDI range.');
    events.push({ tick: note.startTick, order: 1, data: [0x90, note.pitch, note.velocity] }, { tick: note.endTick, order: 0, data: [0x80, note.pitch, 0] });
  }
  events.sort((a, b) => a.tick - b.tick || a.order - b.order || (a.data[1] || 0) - (b.data[1] || 0));
  const track = []; let previous = 0;
  for (const event of events) { track.push(...variable(event.tick - previous), ...event.data); previous = event.tick; }
  track.push(0, 0xff, 0x2f, 0);
  const chunk = (name, bytes) => [...name].map((char) => char.charCodeAt(0)).concat([(bytes.length >>> 24) & 255, (bytes.length >>> 16) & 255, (bytes.length >>> 8) & 255, bytes.length & 255], bytes);
  const header = [0, 0, 0, 1, 1, 0xe0];
  return new Uint8Array([...chunk('MThd', header), ...chunk('MTrk', track)]);
}
