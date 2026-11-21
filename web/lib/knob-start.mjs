// Where a knob starts when its parameter is not written in the source: at the default of the block (spec/templates), not at the bottom of its range.
// A knob that started from its minimum showed values the block does not use (a speech block with no rate showed 0.1, one with no gain -60 dB).

/** The number in a parameter text such as "-6 dB", "50%" or "0.8" (null when there is none). */
export function numberOf(text) {
  const parsed = Number.parseFloat(String(text ?? '').replace(/[a-z %]/gi, ''));
  return Number.isFinite(parsed) ? parsed : null;
}

/**
 * The value a knob starts from: the written value, else the default of the block (taken from its definitions), else the fallback (the bottom of the knob).
 * @param {Record<string, string>} params the parameters written in the source
 * @param {string} name the parameter of the knob
 * @param {Array<{ name: string, default?: string }> | undefined} definitions the parameters of the block as the spec describes them
 * @param {number} fallback the value when neither is known
 */
export function knobStart(params, name, definitions, fallback) {
  const written = numberOf(params?.[name]);
  if (written !== null && String(params[name]).trim() !== '') return written;
  const fromSpec = numberOf(definitions?.find?.((definition) => definition.name === name)?.default);
  return fromSpec ?? fallback;
}
