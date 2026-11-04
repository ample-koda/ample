/** Converts the server's bounded, paired SMF note data into song-timeline events for local instruments. */
import { barBeats } from './meter.mjs';
export function midiSourceEvents(data, parts, fallbackBars) {
  const ppq = Number(data?.ppq);
  if (!Number.isInteger(ppq) || ppq < 1 || data?.truncated || !Array.isArray(data?.notes)) return null;
  const placements = parts?.length ? parts : [{ at: 1, length: fallbackBars }];
  const events = [];
  for (const part of placements) {
    const at = Number(part.at), bars = Number(part.length);
    if (!Number.isInteger(at) || at < 1 || !Number.isInteger(bars) || bars < 1) return null;
    const ticksPerBar = ppq * barBeats(), limit = ticksPerBar * bars;
    for (const note of data.notes) {
      const start = Number(note.start), end = Number(note.end), pitch = Number(note.pitch), velocity = Number(note.velocity);
      if (![start, end, pitch, velocity].every(Number.isFinite) || start < 0 || end <= start || pitch < 0 || pitch > 127) return null;
      if (start >= limit) continue;
      const clippedEnd = Math.min(end, limit), localBar = Math.floor(start / ticksPerBar);
      events.push({ bar: at + localBar, beat: (start % ticksPerBar) / ppq, beats: (clippedEnd - start) / ppq, pitch, velocity, pan: 0 });
    }
  }
  events.sort((a, b) => a.bar - b.bar || a.beat - b.beat || a.pitch - b.pitch);
  return events;
}
