const intervals = (type) => ({ minor: [0, 3, 7], sus2: [0, 2, 7], sus4: [0, 5, 7], '7': [0, 4, 7, 10], maj7: [0, 4, 7, 11], min7: [0, 3, 7, 10], dim: [0, 3, 6], power: [0, 7, 12] }[type] || [0, 4, 7]);
const integer = (value, fallback) => Number.isFinite(Number.parseInt(value, 10)) ? Number.parseInt(value, 10) : fallback;

/** Applies AMPLE's chord intervals and inversion to parsed MIDI event JSON. */
export function chordMidiEvents(input, params) {
  const root = JSON.parse(JSON.stringify(typeof input === 'string' ? JSON.parse(input) : input));
  if (params.enabled === 'off') return root;
  const chord = intervals(params.type), inversion = Math.max(0, Math.min(chord.length - 1, integer(params.inversion, 0))), notes = [];
  for (const source of root.notes || []) {
    const pitch = integer(source.pitch, 60);
    for (let i = 0; i < chord.length; i++) notes.push({ ...source, pitch: Math.max(0, Math.min(127, pitch + chord[i] + (i < inversion ? 12 : 0))) });
  }
  return { ...root, notes };
}
