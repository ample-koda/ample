// The line of a song that holds its master seed, as the Song properties panel edits it. The header is
//   composition "Name" { ample 1 tempo 96 meter 3/4 master-seed 7 }
// with the ample and meter clauses optional. Pure, so node --test checks it; the seed stays text (it can be larger than a safe integer).

export const SEED_LINE = /^((?:composition|project)\s+"(?:[^"\\]|\\.)*"\s*\{\s*(?:ample\s+\d+\s+)?tempo\s+\d+(?:\.\d+)?\s+(?:meter\s+\d+\/\d+\s+)?master-seed\s+)(\d+)(\s*\})/m;

/** The seed written in the header of a song text, or null when the header has none in this shape. */
export function seedOfText(text) {
  const match = SEED_LINE.exec(String(text ?? ''));
  return match ? match[2] : null;
}

/** The text with another seed in the header (everything else, the meter too, as it was); the text itself when there is no seed to replace. */
export function withSeed(text, seed) {
  return String(text ?? '').replace(SEED_LINE, (_line, before, _old, after) => before + String(seed) + after);
}
