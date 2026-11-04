const integer = (value, fallback) => Number.isFinite(Number.parseInt(value, 10)) ? Number.parseInt(value, 10) : fallback;

/** Applies AMPLE's deterministic transpose rule to parsed MIDI-event JSON without changing unrelated fields. */
export function transposeMidiEvents(input, params) {
  const root = JSON.parse(JSON.stringify(typeof input === 'string' ? JSON.parse(input) : input));
  if (params.enabled === 'off') return root;
  const semitones = Math.max(-48, Math.min(48, integer(params.semitones, 0)));
  for (const note of root.notes || []) note.pitch = Math.max(0, Math.min(127, integer(note.pitch, 60) + semitones));
  return root;
}
