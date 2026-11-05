// The play lights: while the song plays and what plays is the song as it is now (nothing edited since that build or preview), the blocks that are audibly active glow in the block
// view and the lines between them pulse in the direction of the signal. The model is in lib/block-activity.mjs; this file only reads the player, decides whether the lights
// are allowed and writes classes on the existing cards and lines (no redraw per frame). @ample:play-fx
import { api } from './api.js';
import { onPlayer, peaksOf, playerState } from './player.js';
import { S, flat } from './store.js';
import { onGraphPainted } from './canvas.js';
import { activity, fxKind, levelAt, linkKey, localBar, noteReferences, secondsPerBar, songBar, takeTime } from '../lib/block-activity.mjs';

const STORE_KEY = 'ample.playfx';
const SILENT = 0.012;   // a block whose own sound is quieter than this is not lit, even inside its parts
const LOCAL_TAKE = 'audition';   // the id the player gives to the audio of a local (in the browser) preview
const STEP = 1 / 8;   // the lit set is worked out again every eighth of a bar, not every frame

let enabled = true;
try { enabled = localStorage.getItem(STORE_KEY) !== '0'; } catch { /* storage may be blocked: the lights stay on */ }

/** Whether the play lights are switched on (a switch in the Studio menu). */
export const playLightsOn = () => enabled;
export function setPlayLights(on) {
  enabled = Boolean(on);
  try { localStorage.setItem(STORE_KEY, enabled ? '1' : '0'); } catch { /* storage may be blocked */ }
  if (!enabled) clear();
}

const firstBars = new Map();   // take id -> first bar of that take (1 for a whole song; a range preview starts later)
const askedFirstBar = new Set();
const levels = new Map();       // take id -> { peaks, seconds, firstBar }, or 'none' for a take without audio (a MIDI clip)
let loadingLevels = false;
let session = null;            // { take, text, sig, firstBar } of the playback the lights follow
let state = { nodes: new Set(), links: new Set(), bar: 0, active: false };
let lastKey = '';
let applied = { nodes: new Set(), links: new Set() };

const world = () => document.getElementById('world');
const linksSvg = () => document.getElementById('links');

/** The block ids of a block and everything that feeds it (what a preview of one block plays). */
function chainOf(target, graph) {
  const feeds = new Map(graph.nodes.map((n) => [n.id, []]));
  for (const l of graph.links) feeds.get(l.to)?.push(l.from);
  const chain = new Set([target]);
  for (const queue = [target]; queue.length;) for (const from of feeds.get(queue.shift()) || []) if (!chain.has(from)) { chain.add(from); queue.push(from); }
  return chain;
}

/** The block a take belongs to in the current build (the master when several blocks share it), or null when the take is not part of the current chain. */
function targetOf(take) {
  const takes = S.build?.takes || {};
  if (takes.master === take) return 'master';
  // several keys can hold one take (an audio component instance and the processor that gives its sound): the one that is on the board is the block the lights belong to
  const onBoard = new Set(flat().nodes.map((n) => n.id));
  const ids = Object.keys(takes).filter((id) => takes[id] === take);
  return ids.find((id) => onBoard.has(id)) ?? ids[0] ?? null;
}

function loadFirstBar(take) {
  if (firstBars.has(take) || askedFirstBar.has(take)) return;
  askedFirstBar.add(take);
  api.get(`/api/takes/${encodeURIComponent(take)}`).then((record) => { firstBars.set(take, Number(record.plan?.settings?.firstBar) || 1); }).catch(() => { firstBars.set(take, 1); });
}

/** Reads the peaks of the blocks' takes in small batches, once per take, so a block can dim while its own sound is quiet. Nothing is read while a build runs. */
async function loadLevels() {
  if (loadingLevels || S.build?.running) return;
  loadingLevels = true;
  try {
    const takes = [...new Set(Object.values(S.build?.takes || {}))].filter((take) => take && !levels.has(take));
    for (let i = 0; i < takes.length; i += 6) {
      await Promise.all(takes.slice(i, i + 6).map(async (take) => {
        try {
          const [p, record] = await Promise.all([peaksOf(take, 800), api.get(`/api/takes/${encodeURIComponent(take)}`)]);
          levels.set(take, { peaks: p.peaks, seconds: p.seconds, firstBar: Number(record.plan?.settings?.firstBar) || 1 });
        } catch { levels.set(take, 'none'); }
      }));
    }
  } finally { loadingLevels = false; }
}

const levelOf = (id, bar) => {
  const entry = levels.get(S.build?.takes?.[id]);
  return entry && entry !== 'none' ? levelAt(entry, bar, S.doc?.project?.tempo, entry.firstBar, 0.5, flat().nodes.find((n) => n.id === id)?.parts) : null;
};

/** Starts or ends a session: a playback is followed only as long as it is still the song in the studio. Returns the session or null. */
function follow(st) {
  if (!st.playing) { session = null; return null; }
  if (st.take && st.take !== LOCAL_TAKE) {
    if (session?.take !== st.take) {
      const target = targetOf(st.take);
      session = target ? { take: st.take, target, sig: S.build.sig, text: S.doc?.text ?? '' } : null;
    }
    if (!session || targetOf(st.take) !== session.target || S.build.sig !== session.sig) { session = null; return null; }
    loadFirstBar(st.take); loadLevels();
    return firstBars.has(st.take) ? session : null;
  }
  // the local preview in the browser (the player calls it 'audition') has no take of the build: it is the song as it was when it started, from the first bar of the range that was chosen
  if (!session || session.take) session = { take: null, target: 'master', sig: S.build?.sig, text: S.doc?.text ?? '', firstBar: S.range?.firstBar || 1 };
  if ((S.doc?.text ?? '') !== session.text) { session = null; return null; }
  loadLevels();   // the blocks keep the takes of the last build that is still valid (an edit deletes the stale ones), so their waveforms and loudness can follow the preview too
  return session;
}

const endsOf = (key) => key.split('>').map((s) => s.split(':')[0]);

function compute(bar, target) {
  const graph = flat();
  let { nodes, links } = activity(graph, bar);
  if (target !== 'master' && graph.nodes.some((n) => n.id === target)) {
    const chain = chainOf(target, graph);
    // the clips whose notes a clip of the chain uses in a pipeline belong to the chain too (they are not joined by a cable)
    for (let grew = true; grew;) { grew = false; for (const ref of noteReferences(graph.nodes)) if (chain.has(ref.to) && !chain.has(ref.from)) { chain.add(ref.from); grew = true; } }
    nodes = new Set([...nodes].filter((id) => chain.has(id)));
    links = new Set([...links].filter((key) => endsOf(key).every((id) => chain.has(id))));
  }
  // a block with sound of its own dims while it is quiet; its loudness sets how strong the light is
  const lvl = new Map();
  for (const id of [...nodes]) {
    const level = levelOf(id, bar);
    if (level === null) continue;
    if (level < SILENT) nodes.delete(id); else lvl.set(id, level);
  }
  links = new Set([...links].filter((key) => endsOf(key).every((id) => nodes.has(id))));
  return { nodes, links, lvl };
}

/** Which of the shown cards and lines are lit (a group card is lit when any of its blocks is; a line of a group when the line behind it is). */
function apply(nodes, links, lvl = new Map()) {
  const w = world();
  if (!w) return;
  const beat = secondsPerBar(S.doc?.project?.tempo) / 4;
  w.style.setProperty('--fx-beat', `${beat.toFixed(4)}s`);
  const phase = `${(-(playerState().time % (beat * 8))).toFixed(3)}s`;   // every motion repeats in 1, 2, 4 or 8 beats, so one phase puts them all in step with the music
  const flatIds = new Set(flat().nodes.map((n) => n.id));
  const litNode = (id) => nodes.has(id) || (!flatIds.has(id) && (S.groups[id]?.members || []).some((m) => nodes.has(m)));
  const kinds = new Map(flat().nodes.map((n) => [n.id, fxKind(n.tpl)]));
  const shownLit = new Set();
  for (const el of w.querySelectorAll('.node[data-id]')) {
    const id = el.dataset.id, on = litNode(id);
    if (on) shownLit.add(id);
    if (on && !el.classList.contains('fx-on')) el.style.setProperty('--fx-delay', phase);
    el.classList.toggle('fx-on', on);
    if (on && lvl.has(id)) el.style.setProperty('--fx-lvl', lvl.get(id).toFixed(2)); else el.style.removeProperty('--fx-lvl');
    if (on) el.dataset.fx = kinds.get(id) || 'merge'; else { delete el.dataset.fx; el.style.removeProperty('--fx-delay'); }
  }
  const lines = new Set();
  for (const path of linksSvg()?.querySelectorAll('path.vis[data-link]') || []) {
    const on = path.dataset.real ? links.has(path.dataset.real) : shownLit.has(path.dataset.from) && shownLit.has(path.dataset.to) && ['audio', 'midi', 'render'].includes(path.dataset.type);
    path.classList.toggle('fx-on', on);
    const source = lvl.get((path.dataset.real || '').split(':')[0]) ?? lvl.get(path.dataset.from);
    if (on && source !== undefined) path.style.setProperty('--fx-lvl', source.toFixed(2)); else path.style.removeProperty('--fx-lvl');
    if (on) lines.add(path.dataset.link);
  }
  for (const path of linksSvg()?.querySelectorAll('path.vis.ref[data-ref]') || []) path.classList.toggle('fx-on', links.has(path.dataset.ref) && shownLit.has(path.dataset.from) && shownLit.has(path.dataset.to));
  applied = { nodes: shownLit, links: lines };
  w.classList.toggle('fx-playing', nodes.size > 0);
}

function clear() {
  session = null; lastKey = '';
  state = { nodes: new Set(), links: new Set(), bar: 0, active: false };
  const w = world();
  if (!w) return;
  for (const el of w.querySelectorAll('.node.fx-on')) { el.classList.remove('fx-on'); delete el.dataset.fx; }
  for (const path of linksSvg()?.querySelectorAll('path.vis.fx-on') || []) path.classList.remove('fx-on');
  w.classList.remove('fx-playing');
  applied = { nodes: new Set(), links: new Set() };
  for (const tl of document.querySelectorAll('.sectl[data-block]')) tl.setHead?.(null);
}

/**
 * The scrubber of every lit block: the playhead in the block's own waveform moves with the song, at the place of the song that this block's take holds (a block that plays only in bars 9 to 16
 * has a short take, and its playhead runs through it while those bars play). Written every frame, after the transport has put the heads of the cards that are not playing back to 0.
 */
/** The playhead through the section timeline of a drums block (shown in its properties): where in its song the block is now; every other timeline has none. */
function sectionHeads(bar) {
  for (const tl of document.querySelectorAll('.sectl[data-block]')) {
    const id = tl.dataset.block, total = Number(tl.dataset.total) || 0;
    const node = applied.nodes.has(id) ? flat().nodes.find((n) => n.id === id) : null;
    const index = node && total ? localBar(node.parts, bar) : -1;
    tl.setHead?.(index < 0 ? null : ((index % total) + (bar - Math.floor(bar))) / total);
  }
}

function scrub(bar) {
  sectionHeads(bar);
  const w = world();
  if (!w) return;
  const tempo = S.doc?.project?.tempo;
  for (const id of applied.nodes) {
    const entry = levels.get(S.build?.takes?.[id]);
    const card = w.querySelector(`.node[data-id="${CSS.escape(id)}"]`);
    if (!card || !entry || entry === 'none') continue;
    const t = takeTime(entry, bar, tempo, entry.firstBar, flat().nodes.find((n) => n.id === id)?.parts);
    const percent = t < 0 || t >= entry.seconds ? 0 : (t / entry.seconds) * 100;
    const played = card.querySelector('.player .pl-played'), head = card.querySelector('.player .plhead');
    if (played) played.style.width = `${percent.toFixed(2)}%`;
    if (head) head.style.left = `${percent.toFixed(2)}%`;
  }
}

function visible() {
  const w = world();
  return Boolean(w && w.offsetParent !== null && !document.hidden);
}

function tick(st) {
  if (!enabled || !visible()) { if (state.active) clear(); return; }
  const s = follow(st);
  if (!s) { if (state.active) clear(); return; }
  const tempo = S.doc?.project?.tempo, firstBar = s.take ? firstBars.get(s.take) : s.firstBar;
  const bar = songBar(st.time, tempo, firstBar);
  const key = `${s.take}|${Math.floor(bar / STEP)}|${S.doc?.version ?? ''}`;
  if (key === lastKey) { scrub(bar); return; }
  lastKey = key;
  const lit = compute(bar, s.target);
  state = { ...lit, bar, active: true };
  apply(lit.nodes, lit.links, lit.lvl);
  scrub(bar);
}

/** Starts the lights (called once when the Studio starts). */
export function initPlayLights() {
  const button = document.getElementById('z-lights');
  const paint = () => { if (!button) return; button.classList.toggle('on', enabled); button.setAttribute('aria-pressed', String(enabled)); };
  if (button) button.onclick = () => { setPlayLights(!enabled); paint(); };
  paint();
  onPlayer(tick);
  // the cards and lines are drawn again on every change of the song: put the marks back
  onGraphPainted(() => { if (state.active) apply(state.nodes, state.links, state.lvl); });
  document.addEventListener('visibilitychange', () => { if (document.hidden) clear(); });
  // what a test or a curious person can read: the ids that are lit right now
  window.AMPLE_PLAYFX = { get state() { return { active: state.active, bar: state.bar, nodes: [...applied.nodes], links: [...applied.links] }; }, linkKey };
}
