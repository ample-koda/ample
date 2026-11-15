// The two ways to enter Performance: with the built takes of the server (the default: safe and exact), or live, which also plays the live chains made in the browser and (in later
// steps) lets the set be changed while it plays. Pure, so node --test checks it.

export const MODES = ['takes', 'live'];

/** A mode from anything: only 'live' is live, everything else is the default. */
export const modeOf = (value) => (value === 'live' ? 'live' : 'takes');

/** Whether a clip plays in a mode: a live chain (its id is `chain:<block>`) plays in the live mode only; a built take and a pattern play in both. */
export const clipPlaysIn = (clipId, mode) => modeOf(mode) === 'live' || !String(clipId).startsWith('chain:');

/** The clips of a layout that do not play in a mode, one entry each: { lane, id, label }. */
export function liveOnlyClips(layout, mode) {
  const seen = new Set(), out = [];
  for (const lane of layout?.lanes ?? []) for (const clip of lane.clips) {
    if (clipPlaysIn(clip.id, mode) || seen.has(clip.id)) continue;
    seen.add(clip.id); out.push({ lane: lane.name, id: clip.id, label: clip.label });
  }

  return out;
}

export const MODE_TEXT = {
  takes: { name: 'Enter Performance', hint: 'The built takes of the server: every clip is a take, the layout stays as it is while it plays.' },
  live: { name: 'Enter Live Performance', hint: 'Also plays the live chains made in the browser (advanced).' },
};
