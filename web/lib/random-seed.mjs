// A random seed for the seed fields: a whole number from 1 to 4294967295 (well inside the unsigned 64-bit range a seed may have).
export function randomSeed(source = globalThis.crypto) {
  const words = new Uint32Array(1);
  if (source?.getRandomValues) source.getRandomValues(words); else words[0] = Math.floor(Math.random() * 4294967296);
  return String(words[0] || 1);
}
