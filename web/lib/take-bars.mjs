// Song bars and the time of the take that is playing. A take of chosen bars (a range) starts at its first bar, so its time 0 is that bar and not bar 1 (issue 101).
// Pure, so node --test checks it.

/** The song bar (1-based) that a time in the take is in. */
export const songBarAt = (seconds, secondsPerBar, firstBar = 1) => Math.floor(seconds / secondsPerBar) + firstBar;

/** The time in the take of the start of a song bar, or null when the bar is before the take starts or after it ends. */
export function takeSecondsOf(bar, secondsPerBar, firstBar, duration) {
  const seconds = (bar - firstBar) * secondsPerBar;
  return seconds < 0 || (duration > 0 && seconds >= duration) ? null : seconds;
}

/** The distance in bars from song bar 1 of a time in the take (the position a playhead is drawn at). */
export const barsFromStart = (seconds, secondsPerBar, firstBar = 1) => seconds / secondsPerBar + (firstBar - 1);
