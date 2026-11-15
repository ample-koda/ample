// The Performance workspace (issue 266): an opt-in tab next to the Playlist where built takes are put on named lanes
// and launched at the next beat or bar. "Enter Performance" starts a session on the Web Audio clock; "Stop all" ends every clip and every
// queued launch at once;
// "Leave" ends the session. The lanes are a layout of their own (kept in the project's ui-state):
// nothing here changes the song or its arrangement.
// The launch rules live in ./performance.js (createPerformance), the layout rules in ../lib/performance-layout.mjs, the sources in
// ../lib/performance-audio.mjs. While the setting is off, no tab, no section and no timer exist. @ample:performance-view
import { $, ask, el, showMenu, toast } from './ui.js';
import { api } from './api.js';
import { formatSeq, isolatePattern, parseSeq } from '../lib/seq-model.mjs';
import { takeFromReport } from '../lib/build-report-take.mjs';
import { S, build, editAll, flat, logProject, subscribeProgress } from './store.js';
import { getContext } from '../lib/audio-engine.mjs';
import { takeCache } from '../lib/take-cache.mjs';
import { barBeats } from '../lib/meter.mjs';
import { playbackOutput } from './playback-volume.js';
import { audioDataId, notifyPlayer, setExternalPlayer, stop as stopPlayer } from './player.js';
import { getUiKey, loadUiState, setUiKey } from './ui-state.js';
import { QUANTIZE, createPerformance, performanceEnabled, setPerformanceEnabled } from './performance.js';
import { capturedOps, capturedParts, createRecorder, recordEngine } from './performance-capture.js';
import { createLaunchScheduler } from './performance-oneshots.js';
import { createExtras } from './performance-extras.js';
import { chainClipBuffer, chainMissingSources, chainPlanOf } from './live-chain.js';
import { createClipPlayer, createLaneBuses } from '../lib/performance-audio.mjs';
import { loadOrder, withTimeout } from '../lib/performance-load.mjs';
import { MODE_TEXT, clipPlaysIn, liveOnlyClips, modeOf } from '../lib/performance-mode.mjs';
import { saveModel, slug, songText } from '../lib/performance-save.mjs';
import { createMixLog, laneCurves, moved } from '../lib/performance-mixlog.mjs';
import { encodeWav } from './studio/preview/audio.js';
import { createHeardRecorder, heardWav } from './performance-heard.js';
import { barList, barLevels, clippedBars, compareBars, compareText } from '../lib/performance-compare.mjs';
import { planSessionSync } from '../lib/performance-sync.mjs';
import { loopFraction, peaksOf, scalePeaks } from '../lib/performance-peaks.mjs';
import { liveClassOf } from '../lib/live-class.mjs';
import { CUTOFF, GAIN_DB, createLaneMix, createLoadGuard, cutoffAt, positionOfCutoff } from '../lib/performance-lanemix.mjs';
import {
  addClip, addLane, addScene, clickAction, clipCount, clipOptions, emptyLayout, parseLayout, pruneLayout,
  LANE_MODES, busiestScene, chainOptions, parseChainClipId, parsePatternClipId, patternOptions, removeClip, setLaneMixes, removeLane, removeScene, sceneChoice, serializeLayout, setLaneMode, setSceneEntry, statusText,
} from '../lib/performance-layout.mjs';
import { createEngineSceneQueue } from './performance-scenes.js';
import { attachPerformanceInput, bindingsList, clearBinding, detachPerformanceInput, learnAction, onBindingsChange } from './performance-input.js';
import { performanceMixerPanel } from './performance-mixer.js';
import { actionText, clipActionId, parseClipActionId } from '../lib/performance-actions.mjs';

const UI_KEY = 'performanceLayout';
let layout = emptyLayout();
let session = null;      // { ctx, engine, player } while a performance is on
let entering = false;
let mode = 'takes';   // how the next Enter goes: 'takes' (the built takes of the server) or 'live' (also the live chains); remembered in the project
let loadingState = null;   // while the takes are made ready: { cancelled, text } (the button and the header Stop cancel it)
const CLIP_TIMEOUT_MS = 240000;
let timer = 0;
let quantize = QUANTIZE.bar;
let wasBusy = false;
let loaded = false;

export const performanceOn = () => performanceEnabled(localStorage);
export function setPerformanceOn(on) {
  setPerformanceEnabled(localStorage, on);
  syncTab();
  if (on) render();
}

const allClips = () => {
  const seen = new Map();
  for (const lane of layout.lanes) for (const clip of lane.clips) if (!seen.has(clip.id)) seen.set(clip.id, { id: clip.id, label: clip.label });
  return [...seen.values()];
};
const bufferOfClip = (id) => {
  if (parsePatternClipId(id)) return patternBuffer(id);
  if (parseChainClipId(id)) return chainClipBuffer(parseChainClipId(id));
  const take = (S.build.takes || {})[id];
  if (!take) return Promise.reject(new Error('Build the block first: it has no take yet.'));
  return bufferOfTake(take);
};

// ---- patterns of a Sequencer block as clips: each is built alone (a draft: the song and its files stay as they are) and cut to the length of the pattern (issue 266)
const patternNames = new Map();   // block -> the names of its patterns
const patternSeen = new Map();   // block -> what they were read from
const sequenceTextOf = async (node) => {
  const inline = String(node.params?.sequence ?? '').trim();
  if (inline) return inline;
  const file = String(node.params?.file ?? '').replace(/^"|"$/g, '').trim();
  return file ? (await api.get(`/api/file?path=${encodeURIComponent(file)}`)).text : '';
};
/** Reads the patterns of every Sequencer block of the song and draws the lanes again when they changed. */
async function refreshPatterns() {
  let changed = false;
  for (const node of flat().nodes.filter((n) => n.tpl === 'sequencer')) {
    let text = '';
    try { text = await sequenceTextOf(node); } catch { text = ''; }
    if (patternSeen.get(node.id) === text) continue;
    patternSeen.set(node.id, text);
    patternNames.set(node.id, text ? (parseSeq(text).seq?.patterns ?? []).map((p) => p.name) : []);
    changed = true;
  }

  if (changed && $('#b-performance') && performanceOn() && !session) render();
}
const patternBuffers = new Map();
async function patternBuffer(id) {
  const { block, pattern } = parsePatternClipId(id);
  const node = flat().nodes.find((n) => n.id === block);
  if (!node) throw new Error(`The block ${block} is not in the song.`);
  const alone = isolatePattern(parseSeq(await sequenceTextOf(node)).seq ?? { patterns: [] }, pattern);
  if (!alone) throw new Error(`The pattern ${pattern} is not in ${block}.`);
  const value = formatSeq(alone), tempo = S.doc?.project?.tempo || 120, key = `${block}|${pattern}|${tempo}|${value}`;
  if (!patternBuffers.has(key)) {
    const report = await api.post('/api/build', { path: S.path, intent: 'preview', only: block, override: { block, name: 'sequence', value } });
    const made = takeFromReport(report, block);
    if (!made.ok) throw new Error(made.message);
    const full = await bufferOfTake(made.take);
    const bars = alone.patterns.find((p) => p.name === pattern).bars;
    const frames = Math.max(1, Math.min(full.length, Math.round(bars * barBeats() * 60 / tempo * full.sampleRate)));
    const cut = getContext().createBuffer(full.numberOfChannels, frames, full.sampleRate);
    for (let c = 0; c < full.numberOfChannels; c++) cut.copyToChannel(full.getChannelData(c).subarray(0, frames), c);
    patternBuffers.set(key, cut);
  }

  return patternBuffers.get(key);
}
const extras = createExtras({
  clips: allClips,
  busiest: () => busiestScene(layout),
  filteredLanes: () => laneMix.filtered(laneNames()),
  choices: () => [...clipOptions(flat().nodes, S.build.takes || {}), ...patternOptions(flat().nodes, patternNames)],
  planOf: (id) => (parseChainClipId(id) ? chainPlanOf(parseChainClipId(id)) : null),
  bufferOf: bufferOfClip,
  tempo: () => S.doc?.project?.tempo || 120,
  beatsPerBar: () => Math.max(1, Math.round(barBeats())),
  toast,
});
extras.onChange(() => { if ($('#b-performance') && performanceOn()) render(); });

const laneModeOf = (name) => layout.lanes.find((lane) => lane.name === name)?.mode ?? 'loop';
const isAccent = (name) => laneModeOf(name) !== 'loop';

const busy = () => Boolean(session) && Object.values(session.engine.state()).some((clips) => clips.some((c) => c.status !== 'ready'));
const statusOf = (lane, id) => session?.engine.state()[lane]?.find((c) => c.id === id)?.status ?? 'ready';

// The header Stop (and any other playback starting) ends what the performance plays, like the other previews do. The session stays on.
setExternalPlayer({
  playing: () => busy(),
  loading: () => (entering ? 'performance' : null),
  take: () => 'performance',
  stop: () => { if (entering) { cancelLoading(); return; } if (session) { session.scenes.stopAll(); session.accents.stopAll(); paint(); } },
});

/** The tab exists only while the setting is on; off means no button, no section, no timer. */
export function syncTab() {
  const on = performanceOn();
  let button = $('#btabs button[data-t="performance"]');
  let section = $('#b-performance');
  if (on && !button) {
    button = el('button', { textContent: 'Performance' });
    button.dataset.t = 'performance';
    ($('#btabs button[data-t="playlist"]') ?? $('#btabs button[data-t="arr"]')).after(button);
    button.onclick = async () => {
      const { setBottomTab } = await import('./panels.js');
      if (button.classList.contains('on') && !$('#bottom').classList.contains('min')) { $('#b-min').click(); return; }
      if ($('#bottom').classList.contains('min')) $('#b-min').click();
      setBottomTab('performance');
      render();
    };
  }
  if (on && !section) { section = el('div', { className: 'bsec', id: 'b-performance' }); $('#bbody').append(section); }
  if (!on) {
    leave();
    if (button?.classList.contains('on')) $('#btabs button[data-t="arr"]')?.click();
    button?.remove();
    section?.remove();
  }
}

const save = () => { setUiKey(UI_KEY, serializeLayout(layout)); syncSession(); };   // a live session follows every change of the set
const fromStore = () => { layout = parseLayout(getUiKey(UI_KEY)); mode = modeOf(getUiKey('performanceMode')); bindLaneMix(); };

// ---- the mix of the lanes (issue 276): heard at once, kept for the session, back to the layout's with Reset, written into the layout only with Keep
let laneMix = createLaneMix();
let unbindMix = () => {};
let fallback = '';   // what the performer is told while a protection is on (issue 281)
const laneNames = () => layout.lanes.map((lane) => lane.name);
function bindLaneMix() {
  unbindMix();
  laneMix = createLaneMix(new Map(layout.lanes.filter((lane) => lane.mix).map((lane) => [lane.name, lane.mix])));
  unbindMix = laneMix.subscribe(() => { if (session) session.buses.apply(laneMix.settings(laneNames())); });
}
bindLaneMix();

// ------------------------------------------------------------------------------------------------ audio

const bufferOfTake = (take) => takeCache.getDecoded(`chain:${take}`, async () => {
  const dataId = await audioDataId(take);
  const response = await fetch(`/api/data/${dataId}`);
  if (!response.ok) throw new Error('A take could not be loaded.');
  return getContext().decodeAudioData(await response.arrayBuffer());
});

/** Starts the session: stops other playback, loads every take on the lanes, then the clock and the launch engine exist. */
/**
 * The blocks that have to be built before the clips of the lanes can play: a plain clip needs its own take, a live chain the takes of the sounds it starts from. `{ blocks, names }`: the block ids, and the
 * clips they are for.
 */
function neededBuilds(forMode = mode) {
  const takes = S.build.takes || {};
  const blocks = new Set(), names = new Set();
  for (const lane of layout.lanes) for (const c of lane.clips) {
    if (parsePatternClipId(c.id) || !clipPlaysIn(c.id, forMode)) continue;
    const chain = parseChainClipId(c.id);
    if (chain) { const missing = chainMissingSources(chain) ?? []; if (missing.length) { missing.forEach((b) => blocks.add(b)); names.add(c.label); } continue; }
    if (!takes[c.id]) { blocks.add(c.id); names.add(c.label); }
  }

  return { blocks: [...blocks], names: [...names] };
}

const pendingKey = (lane, id) => `${lane}\u0000${id}`;
const hasClip = (lane, id) => layout.lanes.some((l) => l.name === lane && l.clips.some((c) => c.id === id));

/**
 * A live session follows the layout (phase 2): a clip that is taken off stops (at the end of its bar), a new clip is made ready (an unbuilt block is built first, in the background) and
 * then added to the engine, a new lane gets its bus. Called after every change of the layout.
 */
function syncSession() {
  const s = session;
  if (!s || s.mode !== 'live' || entering) return;
  const plan = planSessionSync(s.snapshot, layout);
  s.snapshot = serializeLayout(layout);
  for (const c of plan.removeClips) { s.engine.removeClip(c.lane, c.id); s.pending.delete(pendingKey(c.lane, c.id)); s.accentLane.delete(c.id); }
  for (const name of plan.removedLanes) s.engine.removeLane(name);
  s.buses.apply(laneMix.settings(laneNames()));
  for (const c of plan.addClips) loadIntoSession(s, c);
}

/** Makes a clip that was added during the session ready: builds its block when that has not been done, makes the sound and puts the clip in the engine. */
async function loadIntoSession(s, c) {
  const key = pendingKey(c.lane, c.id);
  const say = (text) => { s.pending.set(key, text); logProject('performance', 'running', `${c.label}: ${text}`); render(); };
  try {
    const plain = !parseChainClipId(c.id) && !parsePatternClipId(c.id);
    if (plain && !(S.build.takes || {})[c.id]) {
      say('Building…');
      for (let waited = 0; S.build.running && waited < 240000; waited += 500) await new Promise((resolve) => setTimeout(resolve, 500));
      if (session !== s || !hasClip(c.lane, c.id)) return;
      await build(c.id);
      if (!(S.build.takes || {})[c.id]) throw new Error(`${c.label} could not be built: look at the build log.`);
    }

    if (session !== s || !hasClip(c.lane, c.id)) return;
    say('Loading…');
    const started = performance.now();
    const buffer = s.buffers.get(c.id) ?? await withTimeout(bufferOfClip(c.id), CLIP_TIMEOUT_MS, `"${c.label}"`);
    if (session !== s || !hasClip(c.lane, c.id)) return;
    s.buffers.set(c.id, buffer);
    const lane = layout.lanes.find((l) => l.name === c.lane);
    s.engine.addClip(c.lane, { id: c.id, label: c.label });
    if (lane && lane.mode !== 'loop') { s.accentLane.set(c.id, c.lane); s.accents.configure(c.id, { mode: lane.mode, length: lane.mode === 'oneshot' ? buffer.duration : 0 }); }
    logProject('performance', 'ok', `${c.label} ready in ${((performance.now() - started) / 1000).toFixed(1)} s`);
  } catch (error) {
    toast(error.message || String(error));
    logProject('performance', 'failed', error.message || String(error));
  } finally {
    s.pending.delete(key);
    if (session === s) render();
  }
}

/** Stops waiting for the takes (the header Stop and the Enter button do this while they load): what is being made is let go of and the tab is free again. */
function cancelLoading() {
  if (!entering || !loadingState || loadingState.cancelled) return;
  loadingState.cancelled = true;
  loadingState.abort();   // the clip that is being made is no longer waited for
  logProject('performance', 'cancelled', 'loading the takes was cancelled');
  toast('Cancelling: the clip that is being made is let go of.');
}

let buildingNeeded = false;
/** Builds the blocks the clips need, one after the other (each build is a build of that block and what it needs), and draws the lanes again. */
async function buildNeeded() {
  if (buildingNeeded || session) return;
  const { blocks } = neededBuilds();
  if (!blocks.length) { toast('Everything the clips need is built.'); return; }
  buildingNeeded = true; render();
  try {
    for (const block of blocks) {
      if (S.build.running) { toast('A build is already running: wait until it is ready, then try again.'); break; }
      await build(block);
    }
  } finally { buildingNeeded = false; render(); }
  const left = neededBuilds().blocks;
  toast(left.length ? `Still not built: ${left.join(', ')}. Look at the build log.` : 'Built what the clips need: they can play now.');
}

/** Chooses how to enter and enters: the mode is remembered for the next time. */
function enterAs(chosen) {
  mode = modeOf(chosen);
  setUiKey('performanceMode', mode);
  return enter();
}

async function enter() {
  if (session || entering) return;
  if (!S.doc) { toast('Open a song first.'); return; }
  const everything = layout.lanes.flatMap((lane) => lane.clips.map((c) => ({ lane: lane.name, ...c })));
  if (!everything.length) { toast('Put at least one clip on a lane first.'); return; }
  const wanted = everything.filter((c) => clipPlaysIn(c.id, mode));   // a live chain is not made in the plain mode
  const liveOnly = liveOnlyClips(layout, mode);
  if (!wanted.length) { toast('These clips are live chains: use Enter Live Performance.', { label: 'Enter Live Performance', run: () => enterAs('live') }); return; }
  const needed = neededBuilds(mode);
  if (needed.names.length) { toast(`Build first: ${needed.names.join(', ')} ${needed.names.length === 1 ? 'needs' : 'need'} ${needed.blocks.join(', ')} built.`, { label: 'Build what is needed', run: buildNeeded }); return; }
  entering = true;
  render();
  stopPlayer();   // ends what else plays; this tab's own Stop must not take that for a cancel, so the loading state starts after it
  notifyPlayer();
  const state = { cancelled: false, text: 'Loading the takes…', abort: () => {}, aborted: null };
  state.aborted = new Promise((_, reject) => { state.abort = () => reject(new Error('Loading cancelled.')); });
  state.aborted.catch(() => {});   // it is only raced: no one has to wait for it
  loadingState = state;
  try {
    const ctx = getContext();
    const resumed = ctx.state !== 'running' ? ctx.resume().catch(() => {}) : null;   // inside the click
    const buffers = new Map();
    // a built take, a pattern made alone or a live chain made from its sources; one after the other, with the progress on the button and in the log, and a way out
    const ids = loadOrder(wanted.map((c) => c.id)), labelOf = (id) => wanted.find((c) => c.id === id)?.label ?? id;
    for (const [n, id] of ids.entries()) {
      if (loadingState.cancelled) throw new Error('Loading cancelled.');
      loadingState.text = `Loading the takes… ${n + 1} of ${ids.length}: ${labelOf(id)}`;
      logProject('performance', 'running', `loading ${labelOf(id)} (${n + 1} of ${ids.length})`);
      render();
      const started = performance.now();
      buffers.set(id, await Promise.race([withTimeout(bufferOfClip(id), CLIP_TIMEOUT_MS, `"${labelOf(id)}"`), loadingState.aborted]));
      logProject('performance', 'ok', `${labelOf(id)} ready in ${((performance.now() - started) / 1000).toFixed(1)} s`);
    }
    if (loadingState.cancelled) throw new Error('Loading cancelled.');
    if (resumed) await Promise.race([resumed, new Promise((resolve) => setTimeout(resolve, 1500))]);
    const buses = createLaneBuses({ ctx, output: playbackOutput(ctx) });
    const player = createClipPlayer({ ctx, output: playbackOutput(ctx), outputFor: (lane) => buses.input(lane), bufferOf: (clip) => buffers.get(clip.id) });
    const bpm = S.doc.project?.tempo || 120;
    const beatsPerBar = Math.max(1, Math.round(barBeats()));
    const base = createPerformance({
      now: () => ctx.currentTime,
      play: player.play,
      bpm,
      beatsPerBar,
      quantize,
    });
    for (const lane of layout.lanes) for (const c of lane.clips) if (clipPlaysIn(c.id, mode)) base.addClip(lane.name, { id: c.id, label: c.label });
    // Record sits between the buttons and the engine: only launches that the clock has confirmed are kept (issue 279)
    const recorded = recordEngine(base, createRecorder({ beatsPerBar }), { now: () => ctx.currentTime, bpm });
    const engine = Object.assign({}, base, recorded);
    const scenes = createEngineSceneQueue(engine, { available: () => true, leadSeconds: 0.1 });   // the engine refuses a clip it does not know
    // the lanes of one-shots and momentary sounds are played by the launch scheduler, on the same clock and the same grid as the engine (issue 280)
    const beatSeconds = 60 / bpm;
    const grid = (t0) => {
      let at = base.nextBar();
      if (quantize === QUANTIZE.beat) while (at - beatSeconds >= t0) at -= beatSeconds;
      return at;
    };
    const accentLane = new Map(layout.lanes.filter((lane) => lane.mode !== 'loop').flatMap((lane) => lane.clips.filter((c) => clipPlaysIn(c.id, mode)).map((c) => [c.id, lane.name])));
    const accents = createLaunchScheduler({
      startVoice: ({ id }, when) => recordVoice(id, player.play({ id, label: id, loop: false }, when, accentLane.get(id)), when),
      quantize: grid,
      now: () => ctx.currentTime,
    });
    for (const lane of layout.lanes) {
      if (lane.mode === 'loop') continue;
      for (const c of lane.clips.filter((x) => clipPlaysIn(x.id, mode))) accents.configure(c.id, { mode: lane.mode, length: lane.mode === 'oneshot' ? (buffers.get(c.id)?.duration ?? 0) : 0 });
    }

    session = { ctx, engine, player, scenes, accents, buses, mode, buffers, accentLane, pending: new Map(), snapshot: serializeLayout(layout), recording: false, beatsPerBar, accentLog: [], heard: createHeardRecorder(ctx) };
    session.engine.start([]);   // the whole set is recorded from here: at the end it can be saved as a song (phase 4)
    session.recording = true;
    // what is done with the faders is logged too, on the same clock as the launches (it is saved as automation, phase 5)
    session.beatSec = 60 / bpm; session.t0 = ctx.currentTime; session.mixLog = createMixLog();
    session.logMix = () => { if (session?.mixLog) session.mixLog.record((ctx.currentTime - session.t0) / session.beatSec, laneMix.settings(laneNames())); };
    session.unsubMix = laneMix.subscribe(session.logMix);
    session.logMix();
    if (liveOnly.length) toast(`${liveOnly.map((c) => c.label).join(', ')} ${liveOnly.length === 1 ? 'is a live chain and stays off' : 'are live chains and stay off'} in this session.`, { label: 'Enter Live Performance', run: async () => { await leave(); enterAs('live'); } });
    buses.apply(laneMix.settings(laneNames()));
    watchLoad(ctx);
    // pads and keys launch the same way the buttons do: through launchAction
    attachPerformanceInput({ launch: launchAction, active: () => Boolean(session) && Boolean($('#b-performance')?.classList.contains('on')) });
    timer = setInterval(paint, 100);
  } catch (error) {
    logProject('performance', 'failed', error.message || String(error));
    toast(error.message || String(error));
  } finally {
    entering = false;
    loadingState = null;
    wasBusy = false;
    notifyPlayer();
    render();
  }
}

/**
 * Protection under load (issue 281): while a session is on and the browser can measure the audio thread, a load that stays high takes the filters of the lanes off the path (their values are kept),
 * and a calm load puts them back. The header says so for as long as it lasts.
 */
const guard = createLoadGuard();
function watchLoad(ctx) {
  const capacity = ctx.renderCapacity;
  if (!capacity) return;
  capacity.onupdate = (event) => {
    const verdict = guard.feed(event.peakLoad);
    if (verdict === 'bypass' && laneMix.filtered(laneNames()).length) {
      laneMix.bypassFilters(true);
      fallback = 'Fallback: the lane filters are off because the load of this device was high.';
      render();
    } else if (verdict === 'restore' && laneMix.filtersBypassed) {
      laneMix.bypassFilters(false);
      fallback = '';
      render();
    }
  };
  try { capacity.start({ updateInterval: 0.5 }); } catch { /* the browser cannot measure now */ }
}
function stopWatchingLoad(ctx) {
  try { ctx.renderCapacity?.stop(); } catch { /* it was not running */ }
  if (ctx.renderCapacity) ctx.renderCapacity.onupdate = null;
  laneMix.bypassFilters(false);
  fallback = '';
}

/** Leaving ends everything that plays or is queued and releases the sources; the arrangement was never touched. */
function leave() {
  clearInterval(timer);
  timer = 0;
  if (!session) return;
  try { session.scenes.stopAll(); session.accents.stopAll(); } catch { /* nothing was scheduled */ }
  session.unsubMix?.();
  if (session.recording) toast('Recording stopped: the performed run was not kept.');
  extras.dispose();
  detachPerformanceInput();
  stopWatchingLoad(session.ctx);
  session.player.releaseAll();
  session.buses.releaseAll();
  session = null;
  wasBusy = false;
  notifyPlayer();
  render();
}

function stopAll() {
  if (!session) return;
  session.scenes.stopAll();   // also drops a scene that waits for the bar
  session.accents.stopAll();
  paint();
  notifyPlayer();
}

/** A press on a clip of a one-shot or momentary lane: it starts on the grid; a momentary one stops when the pad is let go (issue 280). */
function accentPress(lane, id) {
  if (!session) { toast('Enter Performance to launch clips.'); return; }
  session.accents.launch(id);
  paint();
}

function accentRelease(lane, id) {
  if (!session) return;
  session.accents.release(id);
  paint();
}

function clickClip(lane, id) {
  if (!session) { toast('Enter Performance to launch clips.'); return; }
  if (isAccent(lane)) {
    // a pad that cannot say when it is let go starts a one-shot, and a momentary sound is switched on and off by pressing again
    if (laneModeOf(lane) === 'momentary' && session.accents.state(id).playing) accentRelease(lane, id); else accentPress(lane, id);
    return;
  }

  const action = clickAction(statusOf(lane, id));
  try {
    if (action === 'launch') session.engine.launch(lane, id);
    else if (action === 'cancel') session.engine.cancelQueued(lane);
  } catch (error) { toast(error.message || String(error)); }
  paint();
  notifyPlayer();
}

/** A scene is queued for the next bar line; pressing it again while it waits takes it back. */
function queueScene(id) {
  if (!session) { toast('Enter Performance to launch scenes.'); return; }
  const scene = layout.scenes.find((s) => s.id === id);
  if (!scene) return;
  if (session.scenes.pending?.scene.id === id) { session.scenes.cancel(); paint(); return; }
  const result = session.scenes.queue(scene);
  if (!result.ok) toast(result.problems.map((p) => p.reason).join('; '));
  paint();
}

/** What a pad or a key does: a clip, a scene or Stop all. */
function launchAction(action) {
  if (!session) return;
  if (action.kind === 'stop') stopAll();
  else if (action.kind === 'scene') queueScene(action.id);
  else if (action.kind === 'clip') { const at = parseClipActionId(action.id); if (at) clickClip(at.lane, at.clip); }
}

/** Stops what plays on one lane at the next bar line (a queued launch on it is taken back first). */
function stopLaneAtBar(lane) {
  if (!session) return;
  try {
    session.engine.cancelQueued(lane);
    session.engine.launchBatch(session.engine.nextBar(), [{ lane, action: 'stop' }]);
  } catch (error) { toast(error.message || String(error)); }
  paint();
}

// ------------------------------------------------------------------------------------------------ drawing

/** Updates the state of every clip button in place (a timer, 10 times a second, only while a session is on). */
function paint() {
  const box = $('#b-performance');
  if (!box || !session) return;
  session.scenes.tick();
  session.scenes.settle();
  const waiting = session.scenes.pending;
  const note = box.querySelector('.perf-pending');
  if (note) note.textContent = waiting ? `Scene "${waiting.scene.name}" starts on the next bar.` : '';
  const state = session.engine.state();
  session.engine.sync();   // launches whose time has come are confirmed to the recorder
  session.accents.tick();
  for (const button of box.querySelectorAll('button.perf-clip')) {
    if (button.dataset.liveonly || button.dataset.pending) continue;
    if (isAccent(button.dataset.lane)) {
      const accent = session.accents.state(button.dataset.clip), left = session.accents.remaining(button.dataset.clip);
      const status = accent.playing ? 'playing' : accent.queued ? 'queued' : 'ready';
      button.dataset.status = status;
      const text = button.querySelector('.perf-state');
      const words = status === 'playing' && left !== null ? `${statusText(status)}, ${left.toFixed(1)} s left` : statusText(status);
      if (text) text.textContent = words;
      button.setAttribute('aria-label', `${button.dataset.label} on ${button.dataset.lane}: ${words}`);
      continue;
    }

    const status = state[button.dataset.lane]?.find((c) => c.id === button.dataset.clip)?.status ?? 'ready';
    button.dataset.status = status;
    movePlayhead(button, status);
    button.setAttribute('aria-label', `${button.dataset.label} on ${button.dataset.lane}: ${statusText(status)}`);
    const text = button.querySelector('.perf-state');
    if (text) text.textContent = statusText(status);
  }
  const now = busy();
  if (now !== wasBusy) { wasBusy = now; notifyPlayer(); }
}

/** Record: from now on the launches that the clock confirms are kept; stopping offers to put the run in the Playlist (issue 279). */
/**
 * The voices of one-shot and momentary lanes are logged as they start and as they are stopped (a momentary sound when the pad is let go, a one-shot that is played again before it ended): the runs of the saved song,
 * in beats from the start of the take. A sound that ran to its end has no cut; one that was stopped early says how much of it was heard (`endSeconds`).
 */
function recordVoice(id, handle, when) {
  const log = session?.accentLog;
  if (!log) return handle;
  const entry = { clip: id, lane: session.accentLane.get(id), when: handle.startedAt ?? when, end: null, cancelled: false };
  log.push(entry);
  const stop = handle.stop, cancel = handle.cancel;
  handle.stop = (at) => { const t = at === undefined ? (session?.ctx.currentTime ?? 0) : at; entry.end = entry.end === null ? t : Math.min(entry.end, t); return stop(at); };
  handle.cancel = () => { entry.cancelled = true; return cancel(); };
  return handle;
}

function accentRuns(s, finishedAt) {
  const runs = [];
  for (const e of s.accentLog) {
    if (e.cancelled || e.when < s.t0) continue;
    const natural = s.buffers.get(e.clip)?.duration;
    if (!(natural > 0)) continue;
    const end = Math.min(e.end ?? Infinity, e.when + natural, finishedAt);
    if (!(end > e.when)) continue;
    const cut = end - e.when < natural - 0.005;
    runs.push({ clip: e.clip, lane: e.lane, from: (e.when - s.t0) / s.beatSec, to: (end - s.t0) / s.beatSec, ...(cut ? { endSeconds: end - e.when } : {}) });
  }
  return runs;
}

/** Starts the take again from now (what was played before is not kept). */
function restartTake() {
  if (!session?.recording) return;
  const playing = Object.entries(session.engine.state()).flatMap(([lane, clips]) => clips.filter((c) => c.status === 'playing').map((c) => ({ clip: c.id, lane })));
  session.engine.finish();
  session.engine.start(playing);
  session.t0 = session.ctx.currentTime; session.mixLog.reset(); session.logMix(); session.accentLog.length = 0; session.heard?.restart();
  toast('Recording again from now: what you played before is not kept.');
}

/** Leave Performance: the set that was played is offered to be kept (saved as a song, or put in the Playlist of this song), then the session ends. */
async function leaveSession() {
  const s = session;
  if (!s) return;
  const capture = s.recording ? s.engine.finish() : null;
  const finishedAt = s.ctx.currentTime;
  const heard = s.heard?.stop() ?? null;
  if (capture) capture.accents = accentRuns(s, finishedAt);
  s.recording = false;
  const kept = { heard, t0: s.t0, beatSec: s.beatSec, mixSnapshots: s.mixLog?.snapshots() ?? [], buffers: new Map(s.buffers), labels: new Map(layout.lanes.flatMap((lane) => lane.clips.map((c) => [c.id, c.label]))), accents: layout.lanes.some((lane) => lane.mode !== 'loop') };
  leave();
  if (capture) await offerToKeep(capture, kept);
}

const NL = String.fromCharCode(10);
const stamp = () => { const d = new Date(), p = (n) => String(n).padStart(2, '0'); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}.${p(d.getMinutes())}`; };

/** The dialog at the end of a set: save it as a song, put it in the Playlist of this song, or let it go. */
async function offerToKeep(capture, kept) {
  const model = saveModel(capture, (id) => kept.labels.get(id) ?? id);
  if (!model.runs.length) { toast('Nothing was launched, so there is nothing to keep.'); return; }
  const songName = (S.path || '').replace(/^.*\//, '').replace(/\.amplet$/, '');
  const name = el('input', { type: 'text', value: slug(`${songName || 'set'}-live-${stamp().replace(/[ .]/g, '-')}`), 'aria-label': 'Name of the saved song', size: 40 });
  const lanes = [...new Set(model.runs.map((r) => r.lane))], clips = [...new Set(model.runs.map((r) => r.clip))];
  const mix = new Map(lanes.map((name) => [name, laneCurves(kept.mixSnapshots, name, { shiftBeats: model.shiftBeats, beatsPerBar: model.beatsPerBar })]));
  const movedLanes = [...mix].filter(([, curves]) => moved(curves)).map(([lane]) => lane);
  const clipped = kept.heard ? clippedBars(kept.heard.peaks, kept.t0 + model.shiftBeats * kept.beatSec, model.beatsPerBar * kept.beatSec) : { bars: [], peakDb: null };
  const heardSeconds = kept.heard ? kept.heard.chunks.reduce((n, c) => n + c.l.length, 0) / kept.heard.sampleRate : 0;
  const heardMb = Math.round((heardSeconds * kept.heard?.sampleRate * 4) / 1048576);
  const keep = el('input', { type: 'checkbox', checked: heardMb > 0 && heardMb <= 60 });
  const notes = [
    movedLanes.length ? `The mix of ${movedLanes.join(', ')} (volume, pan, filter, mute, solo) is saved as automation; a filter sounds close to what you heard, not the same.` : '',
    model.approximate ? `${model.approximate} stop${model.approximate === 1 ? '' : 's'} came inside a bar: the clip is silenced at that moment in the saved song.` : '',
    model.once ? `${model.once} one-shot or momentary sound${model.once === 1 ? '' : 's'} saved as audio that plays once.` : '',
    clipped.bars.length ? `The sum of the set went past full scale in ${barList(clipped.bars)} (up to +${clipped.peakDb.toFixed(1)} dB). The browser softens that when you listen; the saved song has no limiter and clips there. Turn a lane down, or add a master.` : '',
    kept.heard?.full ? `Only the first ${Math.round(heardSeconds / 60)} minutes of what you heard were kept for the comparison.` : '',
  ].filter(Boolean);
  const bars = Math.max(...model.runs.map((r) => r.bar + r.bars - 1));
  const text = `You played ${model.runs.length} launch${model.runs.length === 1 ? '' : 'es'} of ${clips.length} clip${clips.length === 1 ? '' : 's'} on ${lanes.length} lane${lanes.length === 1 ? '' : 's'} over ${bars} bars.${notes.length ? `${NL}${NL}${notes.join(NL)}` : ''}`;
  const choice = await ask('Keep this performance?', `${text}${NL}${NL}A song holds every clip as you heard it, on the bars where it played (the clips are saved next to it as audio). Or put it in the Playlist of this song.`, [
    { label: 'Save as a song', value: 'song', primary: true },
    { label: 'Put it in the Playlist', value: 'playlist' },
    { label: 'Do not keep', value: false },
  ], [el('label', { className: 'untitled-row' }, ['name ', name]), ...(heardMb > 0 ? [el('label', { className: 'untitled-row' }, [keep, ` also keep what you heard as a WAV file to compare with (${heardMb} MB)`])] : [])]);
  if (choice === 'song') await saveSetAsSong(model, kept, slug(name.value, 'live-set'), mix, { keepHeard: keep.checked });
  else if (choice === 'playlist') await putSetInPlaylist(capture);
}

/** The clips as WAV files in the project, the song that plays them, and the song opened. */
async function saveSetAsSong(model, kept, name, mix = new Map(), options = {}) {
  try {
    toast('Saving the performance…');
    const dir = `${name}-clips`;
    await api.createProjectFolder(dir).catch(() => {});   // it may be there already
    const files = new Map(), used = new Set();
    for (const id of new Set(model.runs.map((r) => r.clip))) {
      const buffer = kept.buffers.get(id);
      if (!buffer) continue;
      let stem = slug(kept.labels.get(id) ?? id, 'clip'), n = 2;
      while (used.has(stem)) stem = `${slug(kept.labels.get(id) ?? id, 'clip')}-${n++}`;
      used.add(stem);
      const result = await api.uploadProject(new File([encodeWav(buffer)], `${stem}.wav`, { type: 'audio/wav' }), dir, 'rename', () => {});
      files.set(id, result?.path ?? `${dir}/${stem}.wav`);
      logProject('performance', 'ok', `saved ${stem}.wav`);
    }

    let heardFile = '';
    if (options.keepHeard && kept.heard?.chunks.length) {
      const saved = await api.uploadProject(new File([heardWav(kept.heard)], `${name}-heard.wav`, { type: 'audio/wav' }), dir, 'rename', () => {});
      heardFile = saved?.path ?? `${dir}/${name}-heard.wav`;
      logProject('performance', 'ok', `saved what you heard as ${heardFile}`);
    }

    const text = songText({ title: name, source: (S.path || '').replace(/^.*\//, '').replace(/\.amplet$/, ''), when: stamp().replace('.', ':'), tempo: S.doc?.project?.tempo || 120, model, files, mix, heardFile });
    // a song is made from a copy of the open one (a valid song with its own history) and then given the text of the set
    const made = await api.post('/api/doc/save-as', { path: S.path, name });
    const doc = await api.get(`/api/doc?path=${encodeURIComponent(made.path)}`);
    await api.put('/api/doc', { path: made.path, text, baseVersion: doc.version });
    await api.post('/api/doc/save', { path: made.path });
    toast(`Saved the performance as ${made.path}.`);
    logProject('performance', 'ok', `saved the performance as ${made.path}`);
    document.dispatchEvent(new CustomEvent('ample:open', { detail: made.path }));
    await compareWithHeard(made.path, model, kept).catch((error) => { logProject('performance', 'failed', `the comparison with what you heard failed: ${error.message || error}`); toast(`The saved song could not be compared with what you heard: ${error.message || error}`); });
  } catch (error) {
    logProject('performance', 'failed', error.message || String(error));
    toast(`The performance could not be saved: ${error.message || error}`);
  }
}

/**
 * Builds the saved song and compares its set bus, bar by bar, with the recording of what was heard (heard = saved): the sentence of the result is shown, every bar goes to the log of the project.
 * Bar 1 of the saved song is the first launch of the set.
 */
async function compareWithHeard(path, model, kept) {
  const heard = kept.heard;
  if (!heard?.chunks.length || heard.firstTime === null || !model.runs.length) return;
  toast('Comparing the saved song with what you heard…');
  const report = await api.post('/api/build', { path });
  const block = (report.blocks || []).find((b) => (b.block ?? b.Block) === 'set');
  const take = block?.takeId ?? block?.TakeId;
  if (!take) throw new Error('the saved song has no render of its set bus');
  const response = await fetch(`/api/data/${await audioDataId(take)}`);
  if (!response.ok) throw new Error('the render of the saved song could not be loaded');
  const saved = await getContext().decodeAudioData(await response.arrayBuffer());
  const rate = heard.sampleRate, barSec = model.beatsPerBar * kept.beatSec;
  const startSample = (kept.t0 + model.shiftBeats * kept.beatSec - heard.firstTime) * rate;
  const heardSamples = heard.chunks.reduce((n, c) => n + c.l.length, 0);
  const bars = Math.min(Math.max(...model.runs.map((r) => r.bar + r.bars - 1)), Math.floor((heardSamples - startSample) / (barSec * rate)));   // only the bars that were listened to to the end
  if (bars < 1) return;
  const heardDb = barLevels(heard.chunks, startSample, barSec * rate, bars);
  const savedDb = barLevels([{ l: saved.getChannelData(0), r: saved.getChannelData(saved.numberOfChannels > 1 ? 1 : 0) }], 0, barSec * saved.sampleRate, bars);
  const result = compareBars(heardDb, savedDb);
  for (const b of result.bars) logProject('performance', Math.abs(b.diff) > 1 ? 'failed' : 'ok', `bar ${b.bar}: heard ${b.heard.toFixed(1)} dB, saved ${b.saved.toFixed(1)} dB (${b.diff > 0 ? '+' : ''}${b.diff.toFixed(1)})`);
  const off = result.bars.filter((b) => Math.abs(b.diff) > 1);
  const detail = off.length ? `${NL}${NL}Bars that differ: ${off.slice(0, 8).map((b) => `${b.bar} (${b.diff > 0 ? '+' : ''}${b.diff.toFixed(1)} dB)`).join(', ')}${off.length > 8 ? ' and more' : ''}.` : '';
  await ask('Heard against saved', `${compareText(result)}${detail}${NL}${NL}Compared over ${bars} bar${bars === 1 ? '' : 's'}; every bar is in the log of the project.`, [{ label: 'OK', value: true, primary: true }]);
}

/** The older way: the launches as Playlist parts of the blocks of this song (a checkpoint first, one undo step). */
async function putSetInPlaylist(capture) {
  const parts = capturedParts(capture);
  if (!Object.keys(parts).length) { toast('Nothing was launched, so there is nothing to keep.'); return; }
  try { await api.post('/api/history/checkpoint', { message: 'Before the performed run' }); } catch { /* nothing changed since the last checkpoint */ }
  await editAll(capturedOps(capture));
  toast('The performed run is in the Playlist. Undo takes it back.');
}

async function toggleRecording() {
  if (!session) return;
  if (!session.recording) {
    const playing = Object.entries(session.engine.state()).flatMap(([lane, clips]) => clips.filter((c) => c.status === 'playing').map((c) => ({ clip: c.id, lane })));
    session.engine.start(playing);
    session.recording = true;
    toast('Recording: the clips you launch are kept until you stop.');
    render();
    return;
  }

  const capture = session.engine.finish();
  session.recording = false;
  render();
  const parts = capturedParts(capture);
  const blocks = Object.keys(parts);
  if (!blocks.length) { toast('Nothing was launched while recording, so there is nothing to keep.'); return; }
  const text = blocks.map((block) => `${block}: ${parts[block].map((p) => `bar ${p.at}${p.length > 1 ? ` for ${p.length} bars` : ''}`).join(', ')}`).join('\n');
  const keep = await ask('Keep the performed run?', `What actually played, as Playlist parts:\n${text}\n\nApplying replaces the parts of these blocks in the Playlist. A checkpoint of the project is made first and Undo takes the change back as one step, so the arrangement you had stays available.`, [{ label: 'Discard the run', value: false }, { label: 'Put it in the Playlist', value: true, primary: true }]);
  if (!keep) return;
  try { await api.post('/api/history/checkpoint', { message: 'Before the performed run' }); } catch { /* nothing changed since the last checkpoint */ }
  await editAll(capturedOps(capture));
  toast('The performed run is in the Playlist. Undo takes it back.');
}

function applyLayout(result) {
  if (!result.ok) { toast(result.reason); return; }
  layout = result.layout;
  save();
  render();
}

const LOCK_HINT = 'Leave Performance to change the lanes';
const isLocked = () => (Boolean(session) && session.mode !== 'live') || entering;   // the live mode changes the set while it plays

const peaksCache = new WeakMap();   // decoded sound -> its peaks
/** Draws the shape of a sound in the canvas of a pad (nothing but a thin line while the sound is not there yet). */
function drawWave(canvas, buffer) {
  const g = canvas.getContext('2d');
  if (!g) return;
  const { width, height } = canvas;
  g.clearRect(0, 0, width, height);
  g.fillStyle = getComputedStyle(canvas).color;
  if (!buffer) { g.globalAlpha = 0.25; g.fillRect(0, Math.floor(height / 2), width, 1); return; }
  let peaks = peaksCache.get(buffer);
  if (!peaks) { peaks = scalePeaks(peaksOf(Array.from({ length: buffer.numberOfChannels }, (_, c) => buffer.getChannelData(c)), width / 2)); peaksCache.set(buffer, peaks); }
  g.globalAlpha = 0.85;
  peaks.forEach((p, i) => { const h = Math.max(1, Math.round(p * (height - 2))); g.fillRect(i * 2, Math.round((height - h) / 2), 1, h); });
}

/** The thin bar of a pad whose block is being built, and the words next to it; called again whenever the build moves. */
function paintBuildBars() {
  const box = $('#b-performance');
  if (!box || !session) return;
  for (const button of box.querySelectorAll('button.perf-clip[data-pending]')) {
    const live = S.build.live?.[button.dataset.clip];
    const bar = button.querySelector('.perf-progress');
    if (!live || !bar) continue;
    const percent = Math.round(Math.min(1, Math.max(0, live.fraction ?? 0)) * 100);
    bar.style.setProperty('--p', String(percent / 100));
    const state = button.querySelector('.perf-state');
    if (state && button.dataset.status === 'pending' && /^Building/.test(state.textContent)) state.textContent = live.state === 'queued' ? 'Waiting to build…' : `Building ${percent}%`;
  }
}
subscribeProgress(paintBuildBars);

/** Moves the thin playhead of the pad of a clip that plays (it loops from the moment it started). */
function movePlayhead(button, status) {
  const head = button.querySelector('.perf-playhead');
  if (!head) return;
  const buffer = session?.buffers.get(button.dataset.clip);
  const started = status === 'playing' && !isAccent(button.dataset.lane) ? session?.engine.positionOf?.(button.dataset.lane, button.dataset.clip) : null;
  if (!buffer || started === null || started === undefined) { head.style.display = 'none'; return; }
  head.style.display = 'block';
  head.style.left = `${(loopFraction(session.ctx.currentTime - started, buffer.duration) * 100).toFixed(1)}%`;
}

function clipCell(lane, clip) {
  const status = statusOf(lane.name, clip.id);
  const grid = quantize === QUANTIZE.bar ? 'bar' : 'beat';
  const button = el('button', {
    type: 'button',
    className: 'perf-clip',
    title: `Launch at the next ${grid}; press again while queued to take it back`,
    onclick: () => { if (!isAccent(lane.name)) clickClip(lane.name, clip.id); },
  }, [el('strong', { textContent: clip.label }), el('span', { className: 'perf-state', textContent: statusText(status) }), el('canvas', { className: 'perf-wave', width: 112, height: 22, 'aria-hidden': 'true' }), el('i', { className: 'perf-progress' }), el('i', { className: 'perf-playhead' })]);
  Object.assign(button.dataset, { lane: lane.name, clip: clip.id, label: clip.label, status });
  drawWave(button.querySelector('canvas'), session?.buffers.get(clip.id) ?? null);
  const pending = session?.pending.get(pendingKey(lane.name, clip.id));
  if (pending) {   // added during the session: not ready yet
    button.disabled = true; button.dataset.pending = '1'; button.dataset.status = 'pending';
    button.querySelector('.perf-state').textContent = pending;
    button.setAttribute('aria-label', `${clip.label} on ${lane.name}: ${pending}`);
    queueMicrotask(paintBuildBars);
  }
  if (session && !clipPlaysIn(clip.id, session.mode)) {   // a live chain in a plain session: it is not in the engine
    button.disabled = true; button.dataset.liveonly = '1'; button.dataset.status = 'liveonly';
    button.querySelector('.perf-state').textContent = 'Live only';
    button.title = 'This clip is a live chain: it plays in a live performance (the arrow beside Enter Performance)';
  }

  if (lane.mode !== 'loop') {
    // pressed and let go are two events: a momentary sound lasts as long as the pad is held (issue 280)
    button.title = lane.mode === 'oneshot' ? 'Plays once from the next grid line' : 'Plays while you hold it, from the next grid line';
    button.onpointerdown = (event) => { event.preventDefault(); accentPress(lane.name, clip.id); };
    button.onpointerup = () => accentRelease(lane.name, clip.id);
    button.onpointerleave = () => accentRelease(lane.name, clip.id);
    button.onkeydown = (event) => { if ((event.key === 'Enter' || event.key === ' ') && !event.repeat) { event.preventDefault(); accentPress(lane.name, clip.id); } };
    button.onkeyup = (event) => { if (event.key === 'Enter' || event.key === ' ') accentRelease(lane.name, clip.id); };
  }

  button.setAttribute('aria-label', `${clip.label} on ${lane.name}: ${statusText(status)}`);
  const remove = el('button', {
    type: 'button',
    className: 'perf-x',
    textContent: '×',
    disabled: isLocked(),
    'aria-label': `Remove ${clip.label} from ${lane.name}`,
    title: isLocked() ? LOCK_HINT : 'Remove this clip from the lane',
    onclick: () => { layout = removeClip(layout, lane.name, clip.id); save(); render(); },
  });
  return el('span', { className: 'perf-cell' }, [button, remove]);
}

function laneRow(lane, options, mix) {
  const choice = el('select', {
    className: 'perf-pick', disabled: isLocked() || !options.length, 'aria-label': `Add a clip to ${lane.name}`, title: 'Put a built block on this lane',
  }, [
    el('option', { value: '', textContent: options.length ? '+ Add clip' : 'Build a block first' }),
    ...options.map((o) => el('option', { value: o.id, textContent: o.unbuilt ? `${o.label} (not built: it is built when you add it)` : o.label })),
  ]);
  choice.onchange = () => {
    const option = options.find((o) => o.id === choice.value);
    if (option) applyLayout(addClip(layout, lane.name, option));
  };
  const mode = el('select', { className: 'perf-mode', 'aria-label': `${lane.name}: how it plays`, disabled: isLocked(), title: isLocked() ? LOCK_HINT : 'A loop plays until it is switched; a one-shot plays once; a momentary sound lasts as long as the pad is held' },
    LANE_MODES.map((value) => el('option', { value, textContent: { loop: 'Loop', oneshot: 'One-shot', momentary: 'Momentary' }[value], selected: lane.mode === value })));
  mode.onchange = () => applyLayout(setLaneMode(layout, lane.name, mode.value));
  const stopAtBar = el('button', {
    type: 'button',
    textContent: 'Stop at bar',
    disabled: !session,
    title: 'Stop this lane at the next bar line',
    onclick: () => stopLaneAtBar(lane.name),
  });
  const removeLaneButton = el('button', {
    type: 'button',
    textContent: 'Remove lane',
    disabled: isLocked(),
    title: isLocked() ? LOCK_HINT : 'Remove this lane',
    onclick: () => { layout = removeLane(layout, lane.name); save(); render(); },
  });
  return el('div', { className: 'perf-lane', role: 'group', 'aria-label': `Lane ${lane.name}` }, [
    el('div', { className: 'perf-laneHead' }, [el('strong', { textContent: lane.name, title: lane.name }), mode]),
    mix.strip(lane.name),
    el('div', { className: 'perf-clips' }, [...lane.clips.map((clip) => clipCell(lane, clip)), choice]),
    el('div', { className: 'perf-laneActions' }, [stopAtBar, removeLaneButton]),
  ]);
}

function headBar() {
  const grid = el('select', {
    'aria-label': 'Launch quantize',
    disabled: isLocked(),
    onchange: (event) => { quantize = event.target.value; render(); },
  }, [[QUANTIZE.bar, 'Next bar'], [QUANTIZE.beat, 'Next beat']]
    .map(([value, text]) => el('option', { value, textContent: text, selected: quantize === value })));
  const ready = clipCount(layout) > 0;
  const enterButton = el('button', {
    type: 'button',
    className: 'primary perf-enter',
    textContent: entering ? `${loadingState?.text ?? 'Loading the takes…'} (press to cancel)` : MODE_TEXT[mode].name,
    disabled: !entering && (!S.doc || !ready),
    title: entering ? 'Stop loading the takes' : ready ? 'Start a performance session: launch clips at the next bar or beat' : 'Put at least one built block on a lane first',
    onclick: () => (entering ? cancelLoading() : enterAs(mode)),
  });
  const more = el('button', {
    type: 'button',
    className: 'perf-enter-more',
    textContent: '▾',
    'aria-label': 'More ways to enter Performance',
    'aria-haspopup': 'menu',
    title: 'More ways to enter',
    disabled: entering || !S.doc || !ready,
    onclick: (event) => {
      event.stopPropagation();   // the click that opens the menu must not reach the handler that closes menus
      const r = event.currentTarget.getBoundingClientRect();
      showMenu(r.left, r.bottom, [
        { label: MODE_TEXT.takes.name + (mode === 'takes' ? ' ✓' : ''), fn: () => enterAs('takes') },
        { label: MODE_TEXT.live.name + (mode === 'live' ? ' ✓' : ''), fn: () => enterAs('live') },
      ]);
    },
  });
  const split = el('span', { className: 'perf-split', title: MODE_TEXT[mode].hint }, [enterButton, more]);
  const needed = session ? { blocks: [] } : neededBuilds();
  const buildButton = needed.blocks.length ? el('button', {
    type: 'button',
    className: 'perf-build',
    textContent: buildingNeeded ? 'Building…' : `Build what is needed (${needed.blocks.length})`,
    disabled: buildingNeeded,
    title: `Build ${needed.blocks.join(', ')}: the sounds that ${needed.names.join(', ')} ${needed.names.length === 1 ? 'needs' : 'need'} before ${needed.names.length === 1 ? 'it' : 'they'} can play`,
    onclick: buildNeeded,
  }) : null;
  const leaveButton = el('button', { type: 'button', className: 'perf-leave', textContent: 'Leave Performance', onclick: () => leaveSession() });
  const recordButton = el('button', {
    type: 'button',
    className: `perf-record${session?.recording ? ' on' : ''}`,
    textContent: session?.recording ? '● Recording the set' : 'Record',
    disabled: !session,
    'aria-pressed': String(Boolean(session?.recording)),
    title: 'Everything you launch is kept from the moment you entered, so that you can save the set as a song when you leave. Press to start the take again from now.',
    onclick: () => restartTake(),
  });
  const loadWord = extras.loadWord();
  const stopButton = el('button', {
    type: 'button',
    className: 'perf-stop',
    textContent: 'Stop all',
    disabled: !session,
    title: 'Stop every clip and clear every queued launch, at once',
    onclick: stopAll,
  });
  const note = session
    ? `Performance${session.mode === 'live' ? ' (live)' : ''} is on: the song and its arrangement stay as they are.`
    : 'Lanes and clips stay in this project; the arrangement is not touched.';
  return el('div', { className: 'perf-head' }, [
    session ? leaveButton : split,
    ...(buildButton ? [buildButton] : []),
    stopButton,
    recordButton,
    el('label', { className: 'perf-q' }, [el('span', { textContent: 'Launch on ' }), grid]),
    el('span', { className: 'note', textContent: note }),
    ...(fallback ? [el('span', { className: 'perf-fallback', role: 'status', textContent: fallback, title: 'A protection is on: the sound is a little simpler until the load of this device is calm again' })] : []),
    ...(loadWord ? [el('span', { className: 'perf-load', role: 'status', textContent: loadWord, title: 'The result of the rehearsal check of this device' })] : []),
  ]);
}

function addLaneForm() {
  const name = el('input', {
    type: 'text', maxLength: 32, placeholder: 'Lane name, for example Drums', 'aria-label': 'New lane name', disabled: isLocked(),
  });
  return el('form', {
    className: 'perf-add',
    onsubmit: (event) => { event.preventDefault(); applyLayout(addLane(layout, name.value)); },
  }, [name, el('button', { type: 'submit', textContent: 'Add lane', disabled: isLocked() })]);
}

/** The editor row of a scene: what it does on every lane that loops (the launch button of the scene is in the scene bar). */
function sceneRow(scene) {
  const picks = layout.lanes.filter((lane) => lane.mode === 'loop').map((lane) => {   // one-shot and momentary lanes are played with their own pads
    const select = el('select', { 'aria-label': `${scene.name}: ${lane.name}`, disabled: isLocked() }, [
      el('option', { value: '', textContent: `${lane.name}: keep` }),
      el('option', { value: 'stop', textContent: `${lane.name}: stop` }),
      ...lane.clips.map((clip) => el('option', { value: `start:${clip.id}`, textContent: `${lane.name}: start ${clip.label}` })),
    ]);
    select.value = sceneChoice(scene, lane.name);
    select.onchange = () => {
      const [intent, clip] = select.value === '' ? ['keep'] : select.value === 'stop' ? ['stop'] : ['start', select.value.slice(6)];
      layout = setSceneEntry(layout, scene.id, lane.name, intent, clip);
      save();
      render();
    };
    return select;
  });
  const bind = el('button', {
    type: 'button',
    textContent: 'Pad or key…',
    title: 'Bind a MIDI pad or a key to this scene',
    onclick: () => learn({ kind: 'scene', id: scene.id }),
  });
  const remove = el('button', {
    type: 'button',
    textContent: 'Remove scene',
    disabled: isLocked(),
    title: isLocked() ? LOCK_HINT : 'Remove this scene',
    onclick: () => { layout = removeScene(layout, scene.id); save(); render(); },
  });
  return el('div', { className: 'perf-scene-row', role: 'group', 'aria-label': `Scene ${scene.name}` }, [el('strong', { className: 'perf-scene-name', textContent: scene.name }), ...picks, bind, remove]);
}

/** The scenes as launch buttons, one row: pressing one queues it for the next bar and every lane changes together. */
function sceneBar() {
  const pills = layout.scenes.map((scene) => el('button', {
    type: 'button',
    className: 'perf-scene',
    textContent: `Scene: ${scene.name}`,
    title: 'Queue this scene for the next bar: every lane changes together',
    onclick: () => queueScene(scene.id),
  }));
  return el('section', { className: 'perf-scenebar', 'aria-label': 'Scenes' }, [
    el('span', { className: 'perf-scenebar-label', textContent: 'Scenes' }),
    ...(pills.length ? pills : [el('span', { className: 'note', textContent: 'No scenes yet: add one under Edit scenes.' })]),
    el('span', { className: 'note perf-pending', role: 'status' }),
  ]);
}

function scenesEditor() {
  const name = el('input', { type: 'text', maxLength: 32, placeholder: 'Scene name, for example Chorus', 'aria-label': 'New scene name', disabled: isLocked() });
  const canAdd = layout.lanes.some((lane) => lane.mode === 'loop');
  const form = el('form', { className: 'perf-add', onsubmit: (event) => { event.preventDefault(); applyLayout(addScene(layout, name.value)); } }, [
    name, el('button', { type: 'submit', textContent: 'Add scene', disabled: isLocked() || !canAdd, title: canAdd ? '' : 'Add a lane that loops first: scenes switch the lanes that loop' }),
  ]);
  return el('details', { className: 'perf-scenes' }, [el('summary', { textContent: 'Edit scenes' }), ...layout.scenes.map(sceneRow), form]);
}

/** Learn: the next key or pad press is bound to the action (Escape cancels). */
function learn(action) {
  if (!session) { toast('Enter Performance to bind pads and keys.'); return; }
  if (learnAction(action)) toast('Press a key or a pad to bind it. Escape cancels.');
}

function padsSection() {
  const names = { stop: 'Stop all' };
  const label = (action) => actionText(action, layout, names);
  const rows = bindingsList().map((b) => el('li', {}, [
    el('span', { textContent: `${b.label} → ${label(b.action)}` }),
    el('button', { type: 'button', textContent: 'Remove', 'aria-label': `Remove the binding of ${b.label}`, onclick: () => clearBinding(b.source) }),
  ]));
  const clips = layout.lanes.flatMap((lane) => lane.clips.map((clip) => el('button', {
    type: 'button', textContent: `Bind ${lane.name}: ${clip.label}`, onclick: () => learn({ kind: 'clip', id: clipActionId(lane.name, clip.id) }),
  })));
  return el('details', { className: 'perf-pads' }, [
    el('summary', { textContent: 'Pads and keys' }),
    el('p', { className: 'note', textContent: 'Choose Bind, then press a key or a pad. They only work while the Performance tab is showing and a session is on.' }),
    el('div', { className: 'perf-bind-row' }, [el('button', { type: 'button', textContent: 'Bind Stop all', onclick: () => learn({ kind: 'stop' }) }), ...clips]),
    rows.length ? el('ul', { className: 'perf-bindings' }, rows) : el('p', { className: 'note', textContent: 'Nothing is bound yet.' }),
  ]);
}

/**
 * The mix of the lanes of this session (issue 276): a gain, a pan, a filter, a mute and a solo per lane, as a strip inside the row of the lane. It is heard at once and kept for the session; Reset goes back to
 * what the layout keeps, Keep writes it into the layout. `start()` is called when the rows are on the page: it paints them and follows every change.
 */
function laneMixStrips() {
  const names = laneNames();
  const paints = [];
  const strip = (name) => {
    const gain = el('input', { type: 'range', min: String(GAIN_DB.min), max: String(GAIN_DB.max), step: '1', 'aria-label': `${name}: gain` });
    const pan = el('input', { type: 'range', min: '-100', max: '100', step: '1', 'aria-label': `${name}: pan` });
    const filter = el('input', { type: 'range', min: '0', max: '1', step: '0.005', 'aria-label': `${name}: filter (low pass, to the right it is open)` });
    const mute = el('button', { type: 'button', textContent: 'M', 'aria-label': `${name}: mute`, title: 'Mute this lane' });
    const solo = el('button', { type: 'button', textContent: 'S', 'aria-label': `${name}: solo`, title: 'Hear only the lanes with solo' });
    gain.oninput = () => laneMix.set(name, { gainDb: Number(gain.value) });
    pan.oninput = () => laneMix.set(name, { pan: Number(pan.value) });
    filter.oninput = () => laneMix.set(name, { cutoff: cutoffAt(Number(filter.value)) });
    mute.onclick = () => laneMix.set(name, { mute: !laneMix.get(name).mute });
    solo.onclick = () => laneMix.set(name, { solo: !laneMix.get(name).solo });
    const fader = (text, input) => el('label', { className: 'perf-fader' }, [el('span', { textContent: text }), input]);
    const box = el('div', { className: 'perf-strip', role: 'group', 'aria-label': `Mix of ${name}` }, [fader('Vol', gain), fader('Pan', pan), fader('Filter', filter), el('span', { className: 'perf-ms' }, [mute, solo])]);
    paints.push(() => {
      const m = laneMix.get(name);
      gain.value = String(m.gainDb); pan.value = String(m.pan); filter.value = String(positionOfCutoff(m.cutoff));
      mute.setAttribute('aria-pressed', String(m.mute)); solo.setAttribute('aria-pressed', String(m.solo));
      box.title = `${m.gainDb} dB · ${m.pan === 0 ? 'centre' : m.pan < 0 ? `left ${-m.pan}` : `right ${m.pan}`} · ${m.cutoff >= CUTOFF.max ? 'filter open' : `filter ${Math.round(m.cutoff)} Hz`}${laneMix.filtersBypassed && m.cutoff < CUTOFF.max ? ' (bypassed)' : ''}`;
    });
    return box;
  };
  const note = el('span', { className: 'note', role: 'status' });
  const paint = () => { paints.forEach((p) => p()); note.textContent = laneMix.size ? `${laneMix.size} lane${laneMix.size === 1 ? '' : 's'} changed in this session, not kept` : 'the mix the layout keeps'; };
  const reset = el('button', { type: 'button', textContent: 'Reset', title: 'Back to the mix the layout keeps', onclick: () => laneMix.reset() });
  const keep = el('button', { type: 'button', textContent: 'Keep in the layout', title: 'Write this mix into the lanes of the layout; Reset then goes back to it', onclick: () => { layout = setLaneMixes(layout, laneMix.keep(names)); save(); render(); } });
  const toolbar = el('div', { className: 'perfmix-bar' }, [el('strong', { textContent: 'Session mix' }), note, reset, keep]);
  return {
    strip,
    toolbar,
    start() {
      paint();
      const off = laneMix.subscribe(() => { if (!toolbar.isConnected) { off(); return; } paint(); });   // a newer render has its own strips
    },
  };
}

/** The temporary mixer of the session: faders and pans heard now, kept in the session, applied to the song only when you say so. */
function mixerSection() {
  return el('details', { className: 'perf-mixer' }, [el('summary', { textContent: 'Session mixer' }), performanceMixerPanel()]);
}

onBindingsChange(() => { if ($('#b-performance') && performanceOn()) render(); });

export function render() {
  const box = $('#b-performance');
  if (!box || !performanceOn()) return;
  if (!loaded) {
    loaded = true;
    loadUiState().then(() => { fromStore(); render(); });
  }
  if (S.doc && !session && !entering) {
    refreshPatterns().catch(() => {});
    const kept = pruneLayout(layout, flat().nodes.map((n) => n.id));
    if (kept.dropped.length) { layout = kept.layout; save(); }
  }
  const options = [...clipOptions(flat().nodes, S.build.takes || {}), ...patternOptions(flat().nodes, patternNames), ...chainOptions(flat().nodes)];
  if (session?.mode === 'live') {   // the block of a clip that is not built yet can be put on a lane: it is built in the background
    const have = new Set(options.map((o) => o.id));
    for (const n of flat().nodes) if (!have.has(n.id) && !(S.build.takes || {})[n.id] && !['midi', 'data', 'visual', 'unknown'].includes(liveClassOf(n.tpl))) options.push({ id: n.id, label: n.title || n.id, unbuilt: true });
  }

  const empty = S.doc ? 'Add a lane, then put built blocks on it.' : 'Open a song to set up its lanes.';
  const mix = laneMixStrips();
  const lanes = layout.lanes.length
    ? layout.lanes.map((lane) => laneRow(lane, options.filter((o) => !lane.clips.some((c) => c.id === o.id)), mix))
    : [el('p', { className: 'note', textContent: empty })];
  box.replaceChildren(headBar(), sceneBar(), el('div', { className: 'perf-lanes' }, lanes), ...(layout.lanes.length ? [mix.toolbar] : []), addLaneForm(), scenesEditor(), padsSection(), mixerSection(), extras.readinessSection(), extras.decksSection(), extras.rehearsalSection());
  box.setAttribute('aria-label', 'Performance');
  mix.start();
  if (session) paint();
}

// the clips of another song are not this song's: a song really opened ends the session and reads the layout of the project again
document.addEventListener('ample:song-opened', () => { leave(); if (loaded) fromStore(); render(); });
