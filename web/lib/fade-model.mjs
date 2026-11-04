// The pure part of the fade editor (plan fades F04): reading and writing fadein(start, length, shape) and fadeout(...), and the curve for a drawing.
// Twin of Fades.cs in Ample.Core (the engine uses integers, this draws with floating point, which is enough for a picture). @ample:fades

export const FADE_SHAPES = ['lin', 'equal', 'exp'];

export const isFade = (text) => /^\s*fade(in|out)\(/.test(String(text ?? ''));

/** Where fades make sense: the gain of a volume block and the mix of the effects that have one. */
export const FADEABLE = { volume: ['gain'], delay: ['mix'], reverb: ['mix'], chorus: ['mix'], flanger: ['mix'], phaser: ['mix'], bitcrusher: ['mix'] };
export const fadeable = (type, name) => !!FADEABLE[type]?.includes(name);

const POSITION = /^(\d{1,6})(?:\.(\d{1,6}))?\s*(s)?$/;

function position(text) {
  const m = POSITION.exec(String(text).trim());
  if (!m) return null;
  return { value: Number(`${m[1]}.${m[2] ?? '0'}`), seconds: !!m[3], text: String(text).trim() };
}

/** A fade text as { fadeIn, start: { value, seconds }, length: { value, seconds }, shape }, or null when it is not a well formed fade. */
export function parseFade(text) {
  const m = /^\s*(fadein|fadeout)\((.*)\)\s*$/s.exec(String(text ?? ''));
  if (!m) return null;
  const parts = m[2].split(',').map((p) => p.trim());
  if (parts.length < 2 || parts.length > 3) return null;
  const start = position(parts[0]), length = position(parts[1]);
  const shape = (parts[2] ?? 'lin').toLowerCase().replace(/^linear$/, 'lin');
  if (!start || !length || length.value <= 0 || (!start.seconds && start.value < 1) || !FADE_SHAPES.includes(shape)) return null;
  return { fadeIn: m[1] === 'fadein', start, length, shape };
}

/** Writes a fade: numbers as given, seconds with an s. */
export function formatFade({ fadeIn, start, length, shape = 'lin' }) {
  const pos = (p) => `${String(p.value).replace(/^(\d+)\.0+$/, '$1')}${p.seconds ? 's' : ''}`;
  return `${fadeIn ? 'fadein' : 'fadeout'}(${pos(start)}, ${pos(length)}, ${shape})`;
}

/** The amount (0 to 1) of a shape at a position p (0 to 1) in the fade. */
export function shapeAmount(shape, p) {
  const x = Math.min(1, Math.max(0, p));
  return shape === 'equal' ? Math.sqrt(x) : shape === 'exp' ? x * x : x;
}

/** The amount (0 to 1) of a fade at a time in seconds from the beginning of the song, for a song with the given seconds per bar. */
export function fadeAmountAt(spec, seconds, secondsPerBar) {
  const toSeconds = (p, bar1) => (p.seconds ? p.value : (bar1 ? p.value - 1 : p.value) * secondsPerBar);
  const start = toSeconds(spec.start, true), span = Math.max(1e-6, toSeconds(spec.length, false));
  const p = seconds <= start ? 0 : seconds >= start + span ? 1 : (seconds - start) / span;
  return spec.fadeIn ? shapeAmount(spec.shape, p) : shapeAmount(spec.shape, 1 - p);
}
