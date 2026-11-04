// Suggestions for the Templates tab that need no model: what the song lacks, read from its blocks (plan templates-library T05). Pure. @ample:templates

/**
 * nodes: [{ id, tpl, params }] of the song. Returns [{ id, title, why, query }] where query is what to ask of the beats catalogue (kind and so on).
 */
export function suggestions(nodes) {
  const out = [];
  const types = new Set(nodes.map((n) => n.tpl));
  const hasChords = nodes.some((n) => n.tpl === 'midi-clip' && String(n.params?.chords ?? '').trim());
  if (!types.has('drums')) out.push({ id: 'drums', title: 'Start with a groove', why: 'The song has no drums yet.', query: { kind: 'drum-groove' } });
  if (hasChords && !types.has('bass')) out.push({ id: 'bass', title: 'Put a bass under the chords', why: 'There are chords but no bass. These beats come with a bass line.', query: { kind: 'groove-set' } });
  if (!hasChords && types.has('drums') && types.size <= 3) out.push({ id: 'harmony', title: 'Add harmony', why: 'There are drums but no chords. These beats come with chords and bass.', query: { kind: 'groove-set' } });
  return out;
}

/** How the catalogue is asked for a suggestion: the filters of the tab for it, on top of the tempo of the song. */
export const queryFor = (suggestion, tempo) => ({ ...suggestion.query, ...(tempo ? { bpm: tempo } : {}) });
