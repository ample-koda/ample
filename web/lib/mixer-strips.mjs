// The strips of the Advanced Mixer and Bus: as many as the block has inputs (2 to 16), with the levels and pans of every lane kept when one is edited.
// Pure, so node --test checks it.

/** How many strips to show: the declared inputs, more when the lists already hold more words (never above 16, never below 2). */
export function stripCount(params = {}) {
  const declared = Math.round(Number(String(params.inputs ?? '').replace(/"/g, '')) || 4);
  const words = (text) => String(text ?? '').replace(/"/g, '').trim().split(/\s+/).filter(Boolean).length;
  return Math.max(2, Math.min(16, Math.max(declared, words(params.gains), words(params.pan))));
}

/** The words of a list padded with `fill` to `count` lanes: a lane that the text does not mention keeps its default and is written back. */
export function laneWords(text, count, fill) {
  const words = String(text ?? '').replace(/"/g, '').trim().split(/\s+/).filter(Boolean);
  return Array.from({ length: Math.max(count, words.length) }, (_, i) => words[i] ?? fill);
}
