// The rules for the names and sizes of a Remix project, the same numbers as the server (LiteLimits / LitePolicy). The page keeps to them first, so a refusal comes before a request;
// the server checks them again. The limits are asked from the server (GET /api/remix/limits) when the page starts; these are the defaults.

export const DEFAULT_LIMITS = Object.freeze({
  maxFiles: 200, maxDepth: 6, maxNameLength: 80, maxTextBytes: 512 * 1024, maxAudioBytes: 25 * 1024 * 1024, maxSoundFontBytes: 40 * 1024 * 1024,
  maxProjectBytes: 64 * 1024 * 1024, buildSeconds: 120, maxOutputSeconds: 8 * 60, concurrentBuilds: 2, outputMinutes: 10, maxKeptOutputs: 16,
});

export const TEXT_EXTENSIONS = new Set(['.amplet', '.amplec', '.ampleseq', '.txt', '.md', '.json']);
export const SOUNDFONT_EXTENSIONS = new Set(['.sf2', '.sfz']);
export const EXTENSIONS = new Set([...TEXT_EXTENSIONS, ...SOUNDFONT_EXTENSIONS, '.wav', '.flac', '.mid', '.midi']);
const RESERVED = new Set(['CON', 'PRN', 'AUX', 'NUL', ...Array.from({ length: 10 }, (_, i) => `COM${i}`), ...Array.from({ length: 10 }, (_, i) => `LPT${i}`)]);
const SEGMENT = /^[A-Za-z0-9][A-Za-z0-9._ -]*$/;

export const extensionOf = (path) => { const m = /\.[^./]*$/.exec(path); return m ? m[0].toLowerCase() : ''; };
export const isText = (path) => TEXT_EXTENSIONS.has(extensionOf(path));
export const isSong = (path) => extensionOf(path) === '.amplet';
export const isComponent = (path) => extensionOf(path) === '.amplec';
export const isAudio = (path) => ['.wav', '.flac'].includes(extensionOf(path));

/** A path in the one form Remix accepts, or an Error that says what is wrong (the message is shown to the person). */
export function cleanPath(path, limits = DEFAULT_LIMITS) {
  if (typeof path !== 'string' || path.trim() === '') throw new Error('A file needs a name.');
  if (path.includes('\\') || path.startsWith('/') || path.includes('//')) throw new Error(`"${shown(path)}" is not a plain relative path (use / between names).`);
  const segments = path.split('/');
  if (segments.length > limits.maxDepth) throw new Error(`"${shown(path)}" is deeper than ${limits.maxDepth} folders.`);
  for (const segment of segments) {
    if (segment === '.' || segment === '..' || segment.length > limits.maxNameLength || !SEGMENT.test(segment) || segment.endsWith('.') || segment.endsWith(' ')
      || RESERVED.has(segment.split('.')[0].toUpperCase())) {
      throw new Error(`"${shown(path)}": names are letters, digits, space, dot, dash and underscore, start with a letter or digit and are at most ${limits.maxNameLength} characters.`);
    }
  }
  return path;
}

/** What a file may weigh, by its kind. */
export function sizeCap(path, limits = DEFAULT_LIMITS) {
  const ext = extensionOf(path);
  return SOUNDFONT_EXTENSIONS.has(ext) ? limits.maxSoundFontBytes : TEXT_EXTENSIONS.has(ext) ? limits.maxTextBytes : limits.maxAudioBytes;
}

/** Throws when a file of this name and size may not go into a Remix project. */
export function checkFile(path, size, limits = DEFAULT_LIMITS) {
  cleanPath(path, limits);
  if (!EXTENSIONS.has(extensionOf(path))) throw new Error(`"${shown(path)}": a Remix project can hold ${[...EXTENSIONS].sort().join(', ')}.`);
  const cap = sizeCap(path, limits);
  if (size > cap) throw new Error(`"${shown(path)}" is ${Math.ceil(size / 1024)} KB; the most for this kind of file is ${Math.floor(cap / 1024)} KB.`);
}

const shown = (text) => (text.length > 80 ? `${text.slice(0, 80)}...` : text);
