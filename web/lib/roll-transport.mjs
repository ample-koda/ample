/** Clip transport is independent of note selection and the scrolled viewport. All positions are ticks. */
import { barBeats } from './meter.mjs';
export function rollPlaybackRange(position, rows, ppq, loop, selection) {
  const bar = barBeats() * ppq, end = Math.max(bar, Math.ceil(rows.reduce((last, note) => Math.max(last, note.at + note.length), 0) / bar) * bar);
  const selectedStart = selection.length ? Math.min(...selection.map((note) => note.at)) : null;
  const start = selectedStart ?? Math.max(0, position);
  return { from: start >= end ? 0 : start, loopStart: 0, end, loop: loop ? end : 0 };
}

/** Map the audio clock to the clip, retaining the full loop even when playback starts halfway through it. */
export function rollPosition(position, from, loopStart, loopEnd) {
  if (position < from) return from;
  return loopEnd > loopStart ? loopStart + ((position - loopStart) % (loopEnd - loopStart)) : position;
}
