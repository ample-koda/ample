// Pure colour functions for the piano roll's note faces (PR02: neon-dark glass look). No DOM, no canvas: unit-testable on their own.

/** Maps a MIDI velocity (0-127) to an HSL note-face colour: cool blue at low velocity, hot amber at high velocity. */
export function velocityColor(velocity) {
  const t = Math.max(0, Math.min(127, velocity)) / 127;
  const hue = Math.round(210 - t * 180);     // 210 (cool blue) down to 30 (hot amber)
  const light = Math.round(42 + t * 14);     // a touch brighter at high velocity
  return `hsl(${hue}, 85%, ${light}%)`;
}

/** Darkens (negative) or lightens (positive) an `hsl(h, s%, l%)` string by `deltaL` percentage points; any other string is returned unchanged. */
export function shadeHsl(hslString, deltaL) {
  const m = /^hsl\(\s*([\d.]+)\s*,\s*([\d.]+)%\s*,\s*([\d.]+)%\s*\)$/.exec(hslString);
  if (!m) return hslString;
  const light = Math.max(0, Math.min(100, Number(m[3]) + deltaL));
  return `hsl(${m[1]}, ${m[2]}%, ${light}%)`;
}
