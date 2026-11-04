/** The trance gate of tremolo `shape gate`, shared by the browser render and the live preview (the server twin is TremoloRenderer.GateLevel). */

/** Openness 0..1 of one pattern character: x, X and 1 are open (1 stays open so older patterns mean the same), a to i are 10 to 90 percent, anything else (. and so on) is closed. */
export function gateOpenness(char) {
  if (char === 'x' || char === 'X' || char === '1') return 1;
  return typeof char === 'string' && char.length === 1 && char >= 'a' && char <= 'i' ? (char.charCodeAt(0) - 96) / 10 : 0;
}

/** The gate as a value from -1 (closed) to 1 (open) at a song position, with a linear fade of rampSamples at every change. */
export function gateLevel(pattern, position, stepLength, rampSamples) {
  if (!pattern.length) return 1;
  const value = (s) => gateOpenness(pattern[((s % pattern.length) + pattern.length) % pattern.length]) * 2 - 1;
  const step = Math.floor(position / stepLength), within = position - step * stepLength, current = value(step), previous = value(step - 1);
  return within < rampSamples ? previous + (current - previous) * (within / rampSamples) : current;
}
