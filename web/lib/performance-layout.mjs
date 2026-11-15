import { isNeutral, normalizeMix } from './performance-lanemix.mjs';
import { CHAIN_EFFECTS, SUM_TYPES } from './live-chain-model.mjs';
// The performance layout (issue 266): named lanes, each with the clips that can be launched on it. It is kept apart from the
// arrangement: nothing here reads or writes a placement of the song. Pure functions on plain objects, so node --test checks them.
// A layout is { version: 1, lanes: [{ name, clips: [{ id, label }] }] }. The id of a clip is the id of the block whose built take it plays.
// Every function returns a new layout; the one it was given is never changed.

export const LAYOUT_VERSION = 1;
export const MAX_LANES = 16;
export const MAX_CLIPS_PER_LANE = 16;
export const MAX_NAME = 32;
export const MAX_SCENES = 16;
export const INTENTS = ['keep', 'stop', 'start'];

export const emptyLayout = () => ({ version: LAYOUT_VERSION, lanes: [], scenes: [] });

const clean = (text) => String(text ?? '').replace(/\s+/g, ' ').trim();
const sameName = (a, b) => a.toLowerCase() === b.toLowerCase();
/** What a lane plays: a bed that loops until it is switched, accents that play once, or sounds that last as long as a pad is held (issue 280). */
export const LANE_MODES = ['loop', 'oneshot', 'momentary'];
export const normalizeLaneMode = (mode) => (LANE_MODES.includes(mode) ? mode : 'loop');
const copyLane = (lane) => ({ name: lane.name, mode: normalizeLaneMode(lane.mode), ...(lane.mix ? { mix: normalizeMix(lane.mix) } : {}), clips: lane.clips.map((c) => ({ ...c })) });

/** The layout with the mix that the lanes keep (a lane whose mix is the neutral one keeps none). `mixes` maps a lane name to its mix. */
export function setLaneMixes(layout, mixes) {
  const next = copy(layout);
  for (const lane of next.lanes) {
    const mix = mixes.get(lane.name);
    if (mix && !isNeutral(mix)) lane.mix = normalizeMix(mix); else delete lane.mix;
  }

  return next;
}
const copyScene = (scene) => ({ id: scene.id, name: scene.name, lanes: scene.lanes.map((entry) => ({ ...entry })) });
const copy = (layout) => ({ version: LAYOUT_VERSION, lanes: layout.lanes.map(copyLane), scenes: (layout.scenes ?? []).map(copyScene) });

/** Drops the scene entries that point at a lane or a clip that is not there (any more); a scene without entries stays. */
function cleanScenes(layout) {
  for (const scene of layout.scenes) {
    scene.lanes = scene.lanes.filter((entry) => {
      const lane = layout.lanes.find((l) => l.name === entry.lane);
      return lane && (entry.intent !== 'start' || lane.clips.some((c) => c.id === entry.clip));
    });
  }
  return layout;
}

/** Adds an empty lane. A name that is empty, too long or taken (any case) gives { ok: false, reason }. */
export function addLane(layout, rawName) {
  const name = clean(rawName);
  if (!name) return { ok: false, reason: 'A lane needs a name.' };
  if (name.length > MAX_NAME) return { ok: false, reason: `A lane name has at most ${MAX_NAME} characters.` };
  if (layout.lanes.some((lane) => sameName(lane.name, name))) return { ok: false, reason: `There is a lane named "${name}" already.` };
  if (layout.lanes.length >= MAX_LANES) return { ok: false, reason: `A performance has at most ${MAX_LANES} lanes.` };
  const next = copy(layout);
  next.lanes.push({ name, mode: 'loop', clips: [] });
  return { ok: true, layout: next };
}

/** Sets how a lane plays its clips. An accent lane (one-shot or momentary) does not replace the bed that loops in another lane. */
export function setLaneMode(layout, name, mode) {
  if (!layout.lanes.some((lane) => lane.name === name)) return { ok: false, reason: `There is no lane named "${name}".` };
  if (!LANE_MODES.includes(mode)) return { ok: false, reason: 'A lane plays as a loop, a one-shot or a momentary sound.' };
  const next = copy(layout);
  next.lanes.find((lane) => lane.name === name).mode = mode;
  return { ok: true, layout: next };
}

export function removeLane(layout, name) {
  const next = copy(layout);
  next.lanes = next.lanes.filter((lane) => lane.name !== name);
  return cleanScenes(next);
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
  return cleanScenes(next);
}

/** Adds a named scene without entries. The id (scene-1, scene-2, ...) stays with the scene, so a pad bound to it keeps working. */
export function addScene(layout, rawName, wantedId) {
  const name = clean(rawName);
  if (!name) return { ok: false, reason: 'A scene needs a name.' };
  if (name.length > MAX_NAME) return { ok: false, reason: `A scene name has at most ${MAX_NAME} characters.` };
  if (layout.scenes.some((s) => s.name.toLowerCase() === name.toLowerCase())) return { ok: false, reason: `There is a scene named "${name}" already.` };
  if (layout.scenes.length >= MAX_SCENES) return { ok: false, reason: `A performance has at most ${MAX_SCENES} scenes.` };
  let id = /^scene-\d+$/.test(wantedId ?? '') && !layout.scenes.some((s) => s.id === wantedId) ? wantedId : null;
  for (let n = 1; !id; n++) if (!layout.scenes.some((s) => s.id === `scene-${n}`)) id = `scene-${n}`;
  const next = copy(layout);
  next.scenes.push({ id, name, lanes: [] });
  return { ok: true, layout: next };
}

export function removeScene(layout, id) {
  const next = copy(layout);
  next.scenes = next.scenes.filter((scene) => scene.id !== id);
  return next;
}

/**
 * What a scene does on a lane: 'keep' (the lane goes on as it is: no entry), 'stop', or 'start' with a clip of that lane.
 * A lane or a clip that is not there leaves the layout as it was.
 */
export function setSceneEntry(layout, sceneId, laneName, intent, clipId) {
  const lane = layout.lanes.find((l) => l.name === laneName);
  if (!lane || !layout.scenes.some((s) => s.id === sceneId) || !INTENTS.includes(intent)) return layout;
  if (intent === 'start' && !lane.clips.some((c) => c.id === clipId)) return layout;
  const next = copy(layout);
  const scene = next.scenes.find((s) => s.id === sceneId);
  scene.lanes = scene.lanes.filter((entry) => entry.lane !== laneName);
  if (intent === 'stop') scene.lanes.push({ lane: laneName, intent: 'stop' });
  if (intent === 'start') scene.lanes.push({ lane: laneName, intent: 'start', clip: clipId });
  return next;
}

/**
 * The busiest moment of the set (issue 281): the scene that starts the most clips together, with those clips; a layout without scenes is its first clips (one per lane).
 * `{ name, clips }`, or null when nothing plays.
 */
export function busiestScene(layout) {
  let best = null;
  for (const scene of layout.scenes) {
    const clips = scene.lanes.filter((e) => e.intent === 'start').map((e) => layout.lanes.find((l) => l.name === e.lane)?.clips.find((c) => c.id === e.clip)).filter(Boolean);
    if (clips.length && (!best || clips.length > best.clips.length)) best = { name: scene.name, clips };
  }
  if (best) return best;
  const firsts = layout.lanes.filter((l) => l.clips.length).map((l) => l.clips[0]);
  return firsts.length ? { name: null, clips: firsts } : null;
}

/** The choice of a scene on a lane as the select shows it: '' (keep), 'stop' or 'start:<clip>'. */
export function sceneChoice(scene, laneName) {
  const entry = scene?.lanes.find((e) => e.lane === laneName);
  return !entry ? '' : entry.intent === 'stop' ? 'stop' : `start:${entry.clip}`;
}

/**
 * The blocks that can be put on a lane: every block with a built take, by title. `nodes` are { id, title }, `takes` maps a block id
 * to its take id. A block without a take is not offered (there is nothing to play yet).
 */
/** The id of a lane clip that is one pattern of a Sequencer block, heard alone as a loop (not a built take of the block). */
export const patternClipId = (block, pattern) => `pattern:${block}:${pattern}`;

/** The block and the pattern of such an id, or null for any other clip id. */
export function parsePatternClipId(id) {
  const m = /^pattern:([^:]+):(.+)$/.exec(String(id ?? ''));
  return m ? { block: m[1], pattern: m[2] } : null;
}

/**
 * The patterns of the Sequencer blocks that can go on a lane: one option per pattern, named after its block. `patterns` maps a block id to the names of its patterns.
 * They need no built take of the block: the pattern is built alone when the performance starts.
 */
export function patternOptions(nodes, patterns) {
  const out = [];
  for (const node of nodes ?? []) {
    if (node?.tpl !== 'sequencer') continue;
    for (const name of patterns?.get(node.id) ?? []) out.push({ id: patternClipId(node.id, name), label: `${clean(node.title) || node.id} · ${name} (pattern)`, pattern: true });
  }

  return out.sort((a, b) => a.label.localeCompare(b.label));
}

/** The id of a lane clip that is the live chain of a block: its sources and every effect and mixer up to it, made in the browser (issue 272). */
export const chainClipId = (block) => `chain:${block}`;

/** The block of such an id, or null for any other clip id. */
export const parseChainClipId = (id) => { const m = /^chain:(.+)$/.exec(String(id ?? '')); return m ? m[1] : null; };

/** The effects and mixers that can go on a lane as their live chain: one option per block, named after it. They need a built take of the sounds that lead to them, not of the block itself. */
export function chainOptions(nodes) {
  const out = [];
  for (const node of nodes ?? []) {
    if (!node?.id || !(CHAIN_EFFECTS.has(node.tpl) || SUM_TYPES.has(node.tpl))) continue;
    out.push({ id: chainClipId(node.id), label: `${clean(node.title) || node.id} (live chain)`, chain: true });
  }

  return out.sort((a, b) => a.label.localeCompare(b.label));
}

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
    lane.clips = lane.clips.filter((c) => { if (alive.has(c.id) || alive.has(parsePatternClipId(c.id)?.block) || alive.has(parseChainClipId(c.id))) return true; dropped.push({ lane: lane.name, id: c.id }); return false; });
  }
  return { layout: cleanScenes(next), dropped };
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
    layout.lanes.find((l) => l.name === clean(lane.name)).mode = normalizeLaneMode(lane.mode);
    if (lane.mix && typeof lane.mix === 'object') layout.lanes.find((l) => l.name === clean(lane.name)).mix = normalizeMix(lane.mix);
    for (const clip of Array.isArray(lane.clips) ? lane.clips : []) {
      const withClip = addClip(layout, clean(lane.name), clip);
      if (withClip.ok) layout = withClip.layout;
    }
  }
  for (const scene of Array.isArray(data.scenes) ? data.scenes : []) {
    const added = addScene(layout, scene?.name, scene?.id);
    if (!added.ok) continue;
    layout = added.layout;
    const id = layout.scenes.at(-1).id;
    for (const entry of Array.isArray(scene.lanes) ? scene.lanes : []) layout = setSceneEntry(layout, id, entry?.lane, entry?.intent, entry?.clip);
  }
  return layout;
}

export const serializeLayout = (layout) => JSON.parse(JSON.stringify(copy(layout)));

export const STATUS_TEXT = { ready: 'Ready', queued: 'Queued', playing: 'Playing' };
export const statusText = (status) => STATUS_TEXT[status] ?? 'Ready';

/** What a click on a clip does for its status: launch it, or take its queued launch back. A clip that plays is left alone. */
export const clickAction = (status) => (status === 'queued' ? 'cancel' : status === 'playing' ? 'none' : 'launch');
