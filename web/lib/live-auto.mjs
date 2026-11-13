// The value of a volume gain that is written as an expression (auto(...), fadein(...), fadeout(...)) at a time in the song, for the live chain.
// Same reading as the server (src/Ample.Audio/Engine/Automation.cs, Fades.cs): a point or a fade position is a bar (5, 5.5; 1 = the first bar) or seconds (12s).
// Returns dB (or null when the text is not one of these, or is not understood), so the caller can keep the written value.
const AUTO = /^\s*(auto|fadein|fadeout)\((.*)\)\s*$/s;
const position = (text, barSeconds) => {
  const m = /^(\d+(?:\.\d+)?)\s*(s?)$/i.exec(String(text).trim());
  if (!m) return null;
  const n = Number(m[1]);
  return m[2] ? n : (n - 1) * barSeconds;
};
const length = (text, barSeconds) => {
  const m = /^(\d+(?:\.\d+)?)\s*(s?)$/i.exec(String(text).trim());
  return m ? Number(m[1]) * (m[2] ? 1 : barSeconds) : null;
};
const toDb = (amount) => (amount <= 0 ? -120 : 20 * Math.log10(amount));

/** The gain in dB of `text` at `seconds` from the start of the song; barSeconds = length of one bar. */
export function gainAtSeconds(text, seconds, barSeconds) {
  const m = AUTO.exec(String(text ?? ''));
  if (!m) return null;
  const parts = m[2].split(',').map((p) => p.trim()).filter(Boolean);
  if (m[1] === 'auto') {
    const points = [];
    for (const part of parts) {
      const colon = part.indexOf(':');
      if (colon <= 0) return null;
      const at = position(part.slice(0, colon), barSeconds), value = Number.parseFloat(part.slice(colon + 1).replace(/dB/i, ''));
      if (at === null || !Number.isFinite(value)) return null;
      points.push({ at, value });
    }
    if (!points.length) return null;
    points.sort((a, b) => a.at - b.at);
    if (seconds <= points[0].at) return points[0].value;
    const last = points[points.length - 1];
    if (seconds >= last.at) return last.value;
    let i = 0;
    while (points[i + 1].at <= seconds) i++;
    const a = points[i], b = points[i + 1];
    return a.value + (b.value - a.value) * (seconds - a.at) / Math.max(1e-9, b.at - a.at);
  }
  if (parts.length < 2 || parts.length > 3) return null;
  const start = position(parts[0], barSeconds), span = length(parts[1], barSeconds), shape = (parts[2] || 'lin').toLowerCase();
  if (start === null || span === null || span <= 0 || !['lin', 'linear', 'equal', 'exp'].includes(shape)) return null;
  const p = Math.min(1, Math.max(0, (seconds - start) / span)), q = m[1] === 'fadein' ? p : 1 - p;
  return toDb(shape === 'equal' ? Math.sqrt(q) : shape === 'exp' ? q * q : q);
}
