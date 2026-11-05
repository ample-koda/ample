// The States view of the Playlist, for a project that has a game score (game/score.json, see docs/game-score.md): a matrix of lines against states, where a cell is the
// level (0 to 1) of a layer in a state, a State overlay that dims what is off in a chosen state, and an audition of the built stems. The tab does not exist for a project
// without a score. A level is written to game/score.json through the file route (the command bus), validated by the rules of the server. Plan: PV08. @ample:playlist-states
import { api } from './api.js';
import { S } from './store.js';
import { toast } from './ui.js';
import { stop as stopPlayer } from './player.js';

export const scoreState = { loadedFor: null, summary: null, def: null, show: false, overlay: null };
let audition = null, context = null;

/** Looks for the score of the project once per song; null (no States) when there is none or it cannot be read. */
export async function loadScore(force = false) {
  const key = `${S.project?.id || ''}:${S.path}`;
  if (!force && scoreState.loadedFor === key) return scoreState.def;
  scoreState.loadedFor = key;
  scoreState.summary = null;
  scoreState.def = null;
  try {
    const summary = await api.get('/api/game/score');
    if (!summary?.exists) return null;
    scoreState.summary = summary;
    const file = await api.get(`/api/file?path=${encodeURIComponent(summary.path)}`);
    scoreState.def = JSON.parse(file.text);
  } catch {
    scoreState.def = null;
  }
  return scoreState.def;
}

export const hasScore = () => !!scoreState.def && !!scoreState.summary?.ok;
export const states = () => (scoreState.def?.states || []).map((s) => s.id);
export const layers = () => scoreState.def?.layers || [];
export const levelOf = (state, layerId) => Number(scoreState.def?.states?.find((s) => s.id === state)?.levels?.[layerId] ?? 0);

/** The layer of each line: the layers whose block is a block of the line; a layer whose block is not in the song has no line. */
export function layerMap(model) {
  const map = new Map();
  for (const layer of layers()) {
    const line = model.lines.find((l) => l.chain.includes(layer.block) || l.sources.some((s) => s.block === layer.block || s.pre.includes(layer.block)) || l.noteSides.some((n) => n.steps.includes(layer.block)));
    map.set(layer.id, line || null);
  }
  return map;
}

/** The level a line has in a state: the highest level of the layers that sit on it (null when no layer does). */
export function lineLevel(model, line, state) {
  const levels = [...layerMap(model)].filter(([, l]) => l === line).map(([id]) => levelOf(state, id));
  return levels.length ? Math.max(...levels) : null;
}

/** The reason a level is not allowed, or null: a number from 0 to 1, for a layer and a state that exist. */
export function levelProblem(state, layerId, value) {
  if (!scoreState.def?.states?.some((s) => s.id === state)) return `There is no state "${state}" in the score.`;
  if (!layers().some((l) => l.id === layerId)) return `There is no layer "${layerId}" in the score.`;
  if (!Number.isFinite(value) || value < 0 || value > 1) return 'A level is a number from 0 to 1.';
  return null;
}

/** Sets a level in game/score.json. The file keeps its two-space formatting and key order. Returns false (with a message) when it is refused. */
export async function setLevel(state, layerId, value) {
  const problem = levelProblem(state, layerId, value);
  if (problem) { toast(problem); return false; }
  const next = structuredClone(scoreState.def);
  const target = next.states.find((s) => s.id === state);
  target.levels = target.levels || {};
  if (value === 0) delete target.levels[layerId]; else target.levels[layerId] = Math.round(value * 100) / 100;
  try {
    await api.put('/api/file', { path: scoreState.summary.path, text: `${JSON.stringify(next, null, 2)}\n` });
    scoreState.def = next;
    return true;
  } catch (e) {
    toast(e.message || String(e));
    return false;
  }
}

export function stopAudition() { try { audition?.forEach((n) => n.stop()); } catch { /* already stopped */ } audition = null; }

/** Plays the built stems of the score with the levels of a state, looping. Renders nothing: it needs a build of the score. */
export async function auditionState(state) {
  stopAudition();
  stopPlayer();
  let manifest;
  try { manifest = await api.get(`/api/game/manifest?id=${encodeURIComponent(scoreState.def.name)}`); } catch { toast('Build the score first (it has no stems yet).'); return false; }
  context ||= new (window.AudioContext || window.webkitAudioContext)();
  try {
    const nodes = [];
    for (const stem of manifest.stems || []) {
      const level = levelOf(state, stem.id);
      if (!level) continue;
      const data = await (await fetch(stem.url)).arrayBuffer();
      const source = context.createBufferSource();
      source.buffer = await context.decodeAudioData(data);
      source.loop = true;
      const gain = context.createGain();
      gain.gain.value = level / Math.sqrt(Math.max(1, (manifest.stems || []).length / 4));
      source.connect(gain).connect(context.destination);
      nodes.push(source);
    }
    if (!nodes.length) { toast(`Nothing is audible in the state ${state}.`); return false; }
    const start = context.currentTime + 0.05;
    nodes.forEach((n) => n.start(start));
    audition = nodes;
    return true;
  } catch (e) {
    toast(e.message || String(e));
    return false;
  }
}
