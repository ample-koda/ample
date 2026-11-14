/**
 * Schedules each metronome beat exactly once. The cursor remembers the last beat it handed out as an absolute tick (laps of the loop counted,
 * so a beat of lap 3 differs from the same beat of lap 2) instead of comparing audio times, which differ between two rounds by the age of the
 * playhead they were computed from. Ticks only; no clock in here.
 */
import { metronomeClicks } from './lite-roll-model.mjs';
import { barBeats } from './meter.mjs';

export function createMetronomeCursor({ ppq, loopStart = 0, loopEnd = 0, barLength = barBeats() }) {
  const span = loopEnd > loopStart ? loopEnd - loopStart : 0;
  let lap = 0, lastPos = null, lastKey = -Infinity;
  return {
    /** The clicks to schedule for the window [pos, pos + window) ticks that were not handed out before: [{ offset (ticks from pos), accent }]. `pos` is the position in the clip (wrapped in the loop). */
    round(pos, window) {
      if (span && lastPos != null && pos < lastPos && lastPos - pos > span / 2) lap++;   // wrapped
      else if (lastPos != null && pos < lastPos - ppq) lastKey = Math.min(lastKey, Math.round(lap * span + pos) - 1);   // the position moved back by more than a beat (a big tempo change): beats ahead of it are due again
      lastPos = pos;
      const out = [];
      for (const click of metronomeClicks(pos, window, ppq, loopStart, loopEnd, barLength)) {
        const key = Math.round(lap * span + pos + click.offset);
        if (key <= lastKey) continue;
        lastKey = key; out.push(click);
      }
      return out;
    },
  };
}
