// Parameter snapshots and morph (local-preview chunk 6.2): a snapshot is the values of the live parameters of one block; a morph is the blend between two of them.
// Pure maths on the text values the song stores ("-6 dB", "250 ms", "triangle").

/** { n, unit } for a numeric text such as "-6 dB" or "1500", or null. */
export function parseValue(text) {
  const match = /^\s*(-?\d+(?:\.\d+)?(?:e[+-]?\d+)?)\s*([A-Za-z%]*)\s*$/i.exec(String(text ?? ''));
  return match ? { n: Number(match[1]), unit: match[2] } : null;
}

const round = (value, unit) => Number((unit === 'dB' ? Math.round(value * 10) / 10 : Math.abs(value) >= 100 ? Math.round(value) : Math.round(value * 100) / 100).toFixed(2));

/**
 * The value between a and b at t (0..1) for one parameter definition ({ kind, unit, scale }). Numbers blend linearly, or in the log domain for a log scale or Hz (the midpoint of
 * 100 Hz and 10 kHz is 1 kHz); anything else (a choice, a pattern) keeps a below t = 0.5 and takes b from there. Returns the text to write.
 */
export function interpolate(definition, a, b, t) {
  const x = parseValue(a), y = parseValue(b), k = Math.max(0, Math.min(1, t));
  if (!x || !y || (definition.kind && definition.kind !== 'number')) return k < 0.5 ? String(a) : String(b);
  const unit = definition.unit || y.unit || x.unit, log = (definition.scale === 'log' || unit === 'Hz') && x.n > 0 && y.n > 0;
  const n = log ? Math.exp(Math.log(x.n) + (Math.log(y.n) - Math.log(x.n)) * k) : x.n + (y.n - x.n) * k;
  const value = round(n, unit);
  return unit ? `${value} ${unit}` : String(value);
}

/** The snapshot of a block: name -> text for the given names, from its parameters (a missing value takes the definition's default). */
export function takeSnapshot(params, definitions, names) {
  const out = {};
  for (const definition of definitions) if (names.includes(definition.name) && definition.name !== 'enabled') out[definition.name] = String(params[definition.name] ?? definition.default ?? '');
  return out;
}

/** The writes that morph from snapshot a to b at t: [{ name, value }] for the parameters both snapshots have and that differ. */
export function morphWrites(definitions, a, b, t) {
  const writes = [];
  for (const definition of definitions) {
    if (!(definition.name in a) || !(definition.name in b)) continue;
    const value = interpolate(definition, a[definition.name], b[definition.name], t);
    if (value !== (t < 0.5 ? a : b)[definition.name] || a[definition.name] !== b[definition.name]) writes.push({ name: definition.name, value });
  }
  return writes;
}
