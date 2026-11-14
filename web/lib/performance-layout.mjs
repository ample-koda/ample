// The performance layout (issue 266): named lanes, each with the clips that can be launched on it. It is kept apart from the
// arrangement: nothing here reads or writes a placement of the song. Pure functions on plain objects, so node --test checks them.
// A layout is { version: 1, lanes: [{ name, clips: [{ id, label }] }] }. The id of a clip is the id of the block whose built take it plays.
// Every function returns a new layout; the one it was given is never changed.

export const LAYOUT_VERSION = 1;
export const MAX_LANES = 16;
export const MAX_CLIPS_PER_LANE = 16;
export const MAX_NAME = 32;

export const emptyLayout = () => ({ version: LAYOUT_VERSION, lanes: [] });

const clean = (text) => String(text ?? '').replace(/\s+/g, ' ').trim();
const sameName = (a, b) => a.toLowerCase() === b.toLowerCase();
const copyLane = (lane) => ({ name: lane.name, clips: lane.clips.map((c) => ({ ...c })) });
const copy = (layout) => ({ version: LAYOUT_VERSION, lanes: layout.lanes.map(copyLane) });

/** Adds an empty lane. A name that is empty, too long or taken (any case) gives { ok: false, reason }. */
export function addLane(layout, rawName) {
  const name = clean(rawName);
  if (!name) return { ok: false, reason: 'A lane needs a name.' };
  if (name.length > MAX_NAME) return { ok: false, reason: `A lane name has at most ${MAX_NAME} characters.` };
  if (layout.lanes.some((lane) => sameName(lane.name, name))) return { ok: false, reason: `There is a lane named "${name}" already.` };
  if (layout.lanes.length >= MAX_LANES) return { ok: false, reason: `A performance has at most ${MAX_LANES} lanes.` };
  const next = copy(layout);
  next.lanes.push({ name, clips: [] });
  return { ok: true, layout: next };
}

export function removeLane(layout, name) {
  const next = copy(layout);
  next.lanes = next.lanes.filter((lane) => lane.name !== name);
  return next;
}

/** Adds a clip (a block with a built take) to a lane. The same block is not added twice to one lane. */
export function addClip(layout, laneName, clip) {
  const lane = layout.lanes.find((l) => l.name === laneName);
  if (!lane) return { ok: false, reason: `There is no lane "${laneName}".` };
  const id = clean(clip?.id);
  if (!id) return { ok: false, reason: 'A clip needs a block.' };
  if (lane.clips.some((c) => c.id === id)) return { ok: false, reason: `"${clean(clip.label) || id}" is on this lane already.` };
  if (lane.clips.length >= MAX_CLIPS_PER_LANE) return { ok: false, reason: `A lane holds at most ${MAX_CLIPS_PER_LANE} clips.` };
  const next = copy(layout);
  next.lanes.find((l) => l.name === laneName).clips.push({ id, label: clean(clip.label) || id });
  return { ok: true, layout: next };
}

export function removeClip(layout, laneName, clipId) {
  const next = copy(layout);
  const lane = next.lanes.find((l) => l.name === laneName);
  if (lane) lane.clips = lane.clips.filter((c) => c.id !== clipId);
  return next;
}

/**
 * The blocks that can be put on a lane: every block with a built take, by title. `nodes` are { id, title }, `takes` maps a block id
 * to its take id. A block without a take is not offered (there is nothing to play yet).
 */
export function clipOptions(nodes, takes) {
  const seen = new Set();
  const out = [];
  for (const node of nodes ?? []) {
    if (!node?.id || seen.has(node.id) || !takes?.[node.id]) continue;
    seen.add(node.id);
    out.push({ id: node.id, label: clean(node.title) || node.id, take: takes[node.id] });
  }
  return out.sort((a, b) => a.label.localeCompare(b.label));
}

/** Drops the clips whose block is gone from the song; says which ones. */
export function pruneLayout(layout, blockIds) {
  const alive = new Set(blockIds);
  const dropped = [];
  const next = copy(layout);
  for (const lane of next.lanes) {
    lane.clips = lane.clips.filter((c) => { if (alive.has(c.id)) return true; dropped.push({ lane: lane.name, id: c.id }); return false; });
  }
  return { layout: next, dropped };
}

export const clipCount = (layout) => layout.lanes.reduce((sum, lane) => sum + lane.clips.length, 0);

/** Reads a stored layout (an object or its JSON text); anything wrong gives an empty layout, never an error. */
export function parseLayout(value) {
  let data = value;
  if (typeof value === 'string') { try { data = JSON.parse(value); } catch { return emptyLayout(); } }
  if (!data || typeof data !== 'object' || !Array.isArray(data.lanes)) return emptyLayout();
  let layout = emptyLayout();
  for (const lane of data.lanes) {
    const added = addLane(layout, lane?.name);
    if (!added.ok) continue;
    layout = added.layout;
    for (const clip of Array.isArray(lane.clips) ? lane.clips : []) {
      const withClip = addClip(layout, clean(lane.name), clip);
      if (withClip.ok) layout = withClip.layout;
    }
  }
  return layout;
}

export const serializeLayout = (layout) => JSON.parse(JSON.stringify(copy(layout)));

export const STATUS_TEXT = { ready: 'Ready', queued: 'Queued', playing: 'Playing' };
export const statusText = (status) => STATUS_TEXT[status] ?? 'Ready';

/** What a click on a clip does for its status: launch it, or take its queued launch back. A clip that plays is left alone. */
export const clickAction = (status) => (status === 'queued' ? 'cancel' : status === 'playing' ? 'none' : 'launch');
