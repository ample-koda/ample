// The live chain of the song: the built sounds that lead to a chosen block loop in the browser through persistent Web Audio nodes for every effect and mixer in between,
// so several blocks in a row can be tuned while they play and bypassed one by one (a dry/wet crossfade: the nodes stay in the graph). The plan (which blocks, in what order)
// is web/lib/live-chain-model.mjs; the song text stays the truth: a knob preview is local, the written value arrives through the store and clears the preview.
// Advisory like every live preview: nothing is built or written here. @ample:live-chain
import { liveEffectGraph, decodedInput, fetchTakeAudio, prepareWorklets, decodePreviewBlob } from './studio/preview/audio.js';
import { api } from './api.js';
import { liveAudioContext } from './live-play.js';
import { playbackOutput } from './playback-volume.js';
import { flat, S, subscribe, subscribeSelection } from './store.js';
import { setExternalPlayer, stop as stopPlayer } from './player.js';
import { toast } from './ui.js';
import { planChain, CHAIN_EFFECTS, SUM_TYPES, baseId } from '../lib/live-chain-model.mjs';
import { mixerGraph, sendGraph } from '../lib/live-mixer.mjs';
import { getToken } from './api.js';
import { localInfo, renderLocal, toAudio, toBuffer, drumVoicePlan } from './live-local.js';
import { createDrumVoice } from './live-drums.js';
import { latencyReport } from '../lib/chain-latency.mjs';
import { outputLatency, getContext } from '../lib/audio-engine.mjs';
import { barBeats } from '../lib/meter.mjs';
import { chainLevel, chainTyped, controlTargets, controlledValue } from '../lib/control-live.mjs';
import { followSeries } from '../lib/control-follow.mjs';
import { parameterLive } from '../lib/live-parameters.mjs';
import { gainAtSeconds } from '../lib/live-auto.mjs';
import { isAutomation } from '../lib/automation-model.mjs';
import { regionAt, positionIn } from '../lib/loop-region.mjs';
import { createCpuMonitor, estimateLoad, heaviest } from '../lib/cpu-monitor.mjs';

let session = null;
const listeners = new Set();
const emit = () => listeners.forEach((f) => f(chainState()));
/** Told when the chain starts, stops or its blocks change (the bar follows it). */
export const onChain = (f) => { listeners.add(f); return () => listeners.delete(f); };

const paramsOf = (id) => ({ ...(flat().nodes.find((n) => n.id === id)?.params || {}) });
/** A volume gain written as auto(...) / fadein(...) / fadeout(...) is a number at the loop position (the server applies the curve per sample; the live graph follows it every 40 ms). */
const withAuto = (id, params) => {
  const s = session;
  if (!s || flat().nodes.find((n) => n.id === id)?.tpl !== 'volume' || !isAutomation(params.gain)) return params;
  const db = gainAtSeconds(params.gain, s.t0 === undefined ? 0 : Math.max(0, loopPosition(s, s.context.currentTime)), barSeconds(s));
  return db === null ? params : { ...params, gain: `${Math.round(Math.max(-60, db) * 10) / 10} dB` };
};
const takeOf = (id) => S.build?.takes?.[id];
const planNow = (target) => planChain({ nodes: flat().nodes.map((n) => ({ id: n.id, tpl: n.tpl, params: n.params })), links: flat().links, target, takeOf });
const signatureOf = (plan) => JSON.stringify(plan.items.map((i) => [i.id, i.kind, i.take || '', i.role || '', i.inputs, i.midi || null, i.key || null]));

const blockLatency = () => [...session.built.values()].reduce((sum, entry) => sum + (entry.graph.latency || 0), 0);
const contextLatency = () => { try { return outputLatency(); } catch { return { base: 0, output: 0 }; } };

/** What the user interface shows: whether it plays, the target, and every block that can be tuned or bypassed. */
export function chainState() {
  if (!session) return { active: false, items: [], notes: [] };
  const { context, t0, loop } = session;
  return {
    active: true, target: session.target, comparingBuilt: Boolean(session.builtTake), swaps: session.swaps || 0, notes: session.plan.notes || [], loop,
    latency: blockLatency(),   // seconds of look-ahead in the chain (a limiter)
    latencyReport: latencyReport({ ...contextLatency(), blocks: [...session.built].map(([id, entry]) => ({ id, title: flat().nodes.find((n) => n.id === id)?.title || id, seconds: entry.graph.latency || 0 })) }),
    position: context && loop > 0 ? loopPosition(session, context.currentTime) : 0,
    loopBars: loopBars(), region: session.region ? { start: session.region.start, length: session.region.length } : null,
    gliding: [...(session.gliding || [])], cpu: chainLoad(), stale: [...(session.stale || [])],
    storedTakes: [...(session.takeTimes || [])].map(([id, created]) => ({ id, created })),   // AI blocks: the stored take that is heard, and when it was made (7.2)
    items: session.plan.items.map((item) => ({ id: item.id, kind: item.kind, tpl: item.tpl, info: item.kind === 'local' ? localInfo(item.id) : '', bypassed: session.bypass.has(item.id), cpuBypassed: session.cpuBypassed.has(item.id) })),
  };
}

const LOOP_KEY = 'ample.live.loopBars';
/** How many bars the live chain loops (0 is the whole take). Remembered per viewer. */
export function loopBars() { try { const n = Number(localStorage.getItem(LOOP_KEY)); return [1, 2, 4, 8].includes(n) ? n : 0; } catch { return 0; } }
const barSeconds = (s) => 60 / s.tempo * barBeats();
/** Where in the take the loop is at audio time `now` (seconds), with or without a region. */
const loopPosition = (s, now) => positionIn(s.region, now, s.t0, s.loop);

/** Loops N bars (1, 2, 4, 8) starting at the bar the loop is in now, or the whole take (0). The playhead does not jump: the region contains it. */
export function setLoopBars(bars) {
  try { localStorage.setItem(LOOP_KEY, String(bars || 0)); } catch { /* no storage */ }
  const s = session;
  if (!s || s.starting) { emit(); return; }
  const now = s.context.currentTime, here = loopPosition(s, now);
  s.region = bars ? regionAt(here, barSeconds(s), bars, s.loop, now) : null;
  applyRegion(s);
  emit();
}

function applyRegion(s) {
  const r = s.region;
  const nodes = [...s.slots.values()].map((slot) => slot.node);
  if (s.builtTake) nodes.push(s.builtTake.node);
  for (const node of s.tracked || []) nodes.push(node);   // the LFO waves loop in step with the sources
  for (const slot of s.slots.values()) slot.node.reset?.();   // the drum hits are queued again for the new loop range
  for (const node of nodes) {
    const length = node.buffer?.duration || 0;
    if (r && r.start + r.length <= length + 1e-6) { node.loopStart = r.start; node.loopEnd = r.start + r.length; } else { node.loopStart = 0; node.loopEnd = 0; }
  }
}

const SAFE_KEY = 'ample.live.safeMode';
/** Safe mode: 'auto' (the heaviest block is bypassed, with a notice, when the load stays over 90 percent) or 'off'. Remembered per viewer. */
export function safeMode() { try { return localStorage.getItem(SAFE_KEY) === 'off' ? 'off' : 'auto'; } catch { return 'auto'; } }
export function setSafeMode(mode) { try { localStorage.setItem(SAFE_KEY, mode === 'off' ? 'off' : 'auto'); } catch { /* no storage */ } emit(); }

/** The load of the live chain: { load (0..1+), level, estimated, mode }. Measured by renderCapacity where the browser has it, else estimated from the blocks. */
export function chainLoad() {
  if (!session) return { load: 0, level: 'ok', estimated: true, mode: safeMode() };
  if (session.measured) return { ...session.cpu.state(), estimated: false, mode: safeMode() };
  const load = estimateLoad(session.plan.items.filter((i) => i.kind === 'effect').map((i) => ({ tpl: i.tpl, bypassed: session.bypass.has(i.id) })));
  return { load, level: load >= .7 ? 'warn' : 'ok', estimated: true, mode: safeMode() };
}

/** One load reading (0..1+) of the audio thread, from renderCapacity or a test. In safe mode a load that stays high bypasses the costliest block, with a notice; only the user brings it back. */
export function chainFeedLoad(value, now = performance.now() / 1000) {
  const s = session;
  if (!s || s.starting) return null;
  s.measured = true;
  const state = s.cpu.feed(value, now);
  if (state.level === 'bypass' && safeMode() === 'auto') {
    const item = heaviest(s.plan.items.filter((i) => i.kind === 'effect').map((i) => ({ id: i.id, tpl: i.tpl, bypassed: s.bypass.has(i.id) })));
    if (item) {
      s.bypass.add(item.id); s.cpuBypassed.add(item.id); s.cpu.reset(); refresh(); emit();
      toast(`The computer cannot keep up: ${flat().nodes.find((n) => n.id === item.id)?.title || item.id} is bypassed (CPU). Switch it on again with its button when there is room.`);
    }
  }
  return state;
}

/** Loudness of a master block of the chain: { momentary, shortTerm, integrated } in LUFS (-Infinity while silent), or null. The target of the block is a reference: `target - momentary` is the distance. */
export function chainLoudness(id) { return session?.built.get(id)?.graph.loudness?.read() ?? null; }

/** The peak levels (0..1+) of the left and right side of a block of the chain, or null. Every effect and mixer is tapped by a pair of analysers. */
export function chainLevels(id) {
  const tap = session?.taps.get(id);
  if (!tap) return null;
  const peak = (analyser) => { analyser.getFloatTimeDomainData(tap.data); let p = 0; for (let i = 0; i < tap.data.length; i++) p = Math.max(p, Math.abs(tap.data[i])); return p; };
  return { left: peak(tap.left), right: peak(tap.right) };
}

/**
 * What goes into and comes out of a block of the chain, for the spectrum view: { rate, pre, post } as dB bins (AnalyserNode.getFloatFrequencyData), or null.
 * Only effects have a before and an after; read only while the view is visible.
 */
export function chainSpectrum(id) {
  const tap = session?.spectra.get(id);
  if (!tap) return null;
  tap.pre.getFloatFrequencyData(tap.preBins); tap.post.getFloatFrequencyData(tap.postBins);
  return { rate: session.context.sampleRate, pre: tap.preBins, post: tap.postBins };
}

/** The last 2048 samples going into and out of a block, for the scope view: { pre, post } or null. */
export function chainScope(id) {
  const tap = session?.spectra.get(id);
  if (!tap) return null;
  tap.pre.getFloatTimeDomainData(tap.preWave); tap.post.getFloatTimeDomainData(tap.postWave);
  return { pre: tap.preWave, post: tap.postWave };
}

let warm = { target: null, timer: 0, token: 0, count: 0, warmed: new Set() };
/** How many pre-warms ran (tests and the status line). */
export const prewarmCount = () => warm.count;
/**
 * Pre-warm (6.3): when a block that can sit in a live chain is selected, decode the built sounds its chain starts from (into the take cache, under the key the chain uses) and load the worklets,
 * so Live chain starts without a download. Cancelled when the selection moves on, never builds anything and never starts a sound.
 */
export function prewarm(target) {
  clearTimeout(warm.timer);
  const token = ++warm.token;
  if (!target || session) return;
  warm.timer = setTimeout(async () => {
    try {
      const plan = planNow(target);
      if (!plan.ok) return;
      const sources = plan.items.filter((i) => i.kind === 'source');
      for (const item of sources) {
        if (token !== warm.token) return;
        await decodedInput(`chain:${item.take}${item.role ? `:${item.role}` : ''}`, () => fetchTakeAudio(item.take, item.role));
      }
      if (token !== warm.token) return;
      await prepareWorklets(getContext());
      warm.count++;
    } catch { /* a pre-warm is only a head start: the real start reports problems */ }
  }, 250);
}
subscribeSelection(() => { const id = S.sel; const node = id && flat().nodes.find((n) => n.id === id); prewarm(node && (CHAIN_EFFECTS.has(node.tpl) || SUM_TYPES.has(node.tpl)) ? id : null); });

/** Plays the chain that ends at `target`. Returns whether it started. */
export async function startChain(target) {
  stopPlayer();
  stopChain({ quiet: true });
  const plan = planNow(target);
  if (!plan.ok) { toast(plan.reason); return false; }
  const mine = { cpu: createCpuMonitor(), cpuBypassed: new Set(), target, plan, signature: signatureOf(plan), bypass: new Set(), override: new Map(), last: new Map(), built: new Map(), taps: new Map(), spectra: new Map(), starting: true, sources: [], disposables: [], slots: new Map(), localKeys: new Map(), renders: new Map(), watch: new Set() };
  session = mine;
  try {
    const buffers = new Map();
    for (const item of plan.items.filter((i) => i.kind === 'source')) buffers.set(item.id, await decodedInput(`chain:${item.take}${item.role ? `:${item.role}` : ''}`, () => fetchTakeAudio(item.take, item.role)));
    if (session !== mine) return false;
    const context = liveAudioContext();
    await context.resume();
    await prepareWorklets(context);
    if (session !== mine) return false;
    Object.assign(mine, { context, buffers, tempo: Number(S.doc?.project?.tempo) || 120, impulses: new Map(), impulseKeys: new Map() });
    // a convolution block needs the impulse response of its `ir` / `file` before its graph is made (the server serves it; length and normalize are done in the graph)
    for (const item of plan.items.filter((i) => i.tpl === 'convolution')) { await loadImpulse(mine, item.id); if (session !== mine) return false; }
    // the blocks the browser makes itself (drums, sampler, cut, fade, ...), in the order of the plan, each from the sound it works on
    for (const item of plan.items.filter((i) => i.kind === 'local')) {
      if (hitDrums(plan, item)) {   // built-in drums that only feed effects: single hits on the audio clock, no loop render
        const voicePlan = await drumVoicePlan(item, { paramsOf: effective });
        (mine.drumPlans ||= new Map()).set(item.id, voicePlan);
        buffers.set(item.id, context.createBuffer(1, Math.max(1, Math.round(voicePlan.loop * context.sampleRate)), context.sampleRate));   // only its length: the loop of the chain
        mine.localKeys.set(item.id, localKey(item));
        mine.watch.add(baseId(item.id));
        if (session !== mine) return false;
        continue;
      }
      buffers.set(item.id, toBuffer(context, await makeLocal(mine, item)));
      mine.localKeys.set(item.id, localKey(item));
      mine.watch.add(baseId(item.id)); item.midi?.transforms.forEach((t) => mine.watch.add(t.id)); mine.watch.add(item.midi?.clip);
      if (session !== mine) return false;
    }
    build(mine);
    startControls(mine);
    startAutomation(mine);
    loadTakeTimes(mine);
    mine.starting = false;
    try { const capacity = context.renderCapacity; if (capacity) { capacity.start({ updateInterval: .25 }); capacity.onupdate = (event) => chainFeedLoad(event.averageLoad); mine.capacity = capacity; } } catch { /* no render capacity here: the figure is estimated */ }
    refresh(true);
    emit();
    return true;
  } catch (error) {
    if (session === mine) { dispose(mine); session = null; }
    emit();
    toast(error?.message || String(error));
    return false;
  }
}

const effective = (id) => ({ ...paramsOf(baseId(id)), ...(session?.override.get(baseId(id)) || {}) });   // a splitter output `s:left` has the parameters of the block `s`
/** What a local block depends on: its parameters, the notes and transforms feeding it, and the blocks it works on. */
const localKey = (item) => JSON.stringify([effective(item.id), item.midi ? [effective(item.midi.clip), item.midi.transforms.map((t) => effective(t.id))] : null, item.inputs.map((i) => session?.localKeys.get(i.from) || ''), S.doc?.project?.tempo]);

const impulseKey = (id) => JSON.stringify([effective(id).ir, effective(id).file]);
/** Fetches and decodes the impulse response of a convolution block (the server picks it from `ir` / `file`) and remembers which values it was made for. */
async function loadImpulse(s, id) {
  const key = impulseKey(id);
  s.impulses.set(id, await decodePreviewBlob(await api.effectImpulse({ path: S.path, block: id, params: effective(id) })));
  s.impulseKeys.set(id, key);
}
/** A convolution whose `ir` or `file` changed gets the new impulse in its idle convolver and crossfades to it (the graph does that), without a restart of the chain. */
function refreshImpulses(s) {
  for (const item of s.plan.items.filter((i) => i.tpl === 'convolution')) {
    if (s.impulseKeys.get(item.id) === impulseKey(item.id) || s.loadingImpulse?.has(item.id)) continue;
    (s.loadingImpulse ||= new Set()).add(item.id);
    const wanted = impulseKey(item.id);
    api.effectImpulse({ path: S.path, block: item.id, params: effective(item.id) }).then(decodePreviewBlob).then((impulse) => {
      if (session !== s) return;
      s.impulses.set(item.id, impulse); s.impulseKeys.set(item.id, wanted);
      s.built.get(item.id)?.graph.replaceImpulse?.(impulse, { ...effective(item.id), ...(s.bypass.has(item.id) ? { enabled: 'off' } : {}) });
    }).catch((error) => { if (session === s) toast(error?.message || String(error)); s.impulseKeys.set(item.id, wanted); }).finally(() => { s.loadingImpulse.delete(item.id); if (session === s) refreshImpulses(s); });
  }
}

async function makeLocal(s, item) {
  const inputs = new Map();
  for (const link of item.inputs) inputs.set(link.from, toAudio(s.buffers.get(link.from)));
  return renderLocal(item, { paramsOf: effective, inputs, context: s.context });
}

/** A local block changed: make it again (and what works on it), then swap the new sound in at the same place of the loop with a short crossfade. */
/** Built-in drums that only feed effects and sums play as single hits; a local block that works on their sound (cut, reverse ...) still needs the rendered loop. */
const hitDrums = (plan, item) => item.tpl === 'drums' && !plan.items.some((other) => other.kind === 'local' && other.inputs.some((link) => link.from === item.id));
/** A knob of the drums: the hits of the new pattern replace the queued ones at once (hits already heard are not rendered again). */
async function updateDrumHits(s, item) {
  const token = (s.drumToken = (s.drumToken || 0) + 1);
  try {
    const voicePlan = await drumVoicePlan(item, { paramsOf: effective });
    if (session !== s || token !== s.drumToken) return;
    s.drumPlans.set(item.id, voicePlan); s.localKeys.set(item.id, localKey(item));
    s.slots.get(item.id)?.node.set(voicePlan);
  } catch (error) { if (session === s) toast(error?.message || String(error)); }
}

function rerender(s, id) {
  const drums = s.drumPlans && s.plan.items.find((i) => i.id === id && s.drumPlans.has(i.id));
  if (drums) { updateDrumHits(s, drums); return; }
  clearTimeout(s.renders.get(id)?.timer);
  const entry = { timer: 0 };
  s.renders.set(id, entry);
  entry.timer = setTimeout(async () => {
    try {
      for (const item of s.plan.items.filter((i) => i.kind === 'local')) {
        const stale = item.id === id || item.inputs.some((i) => s.dirty?.has(i.from));
        if (!stale) continue;
        (s.dirty ||= new Set()).add(item.id);
        const audio = await makeLocal(s, item);
        if (session !== s) return;
        s.buffers.set(item.id, toBuffer(s.context, audio));
        s.localKeys.set(item.id, localKey(item));
        swap(s, item.id, s.buffers.get(item.id));
        if (item.tpl === 'normalize') emit();   // the note on the chip says the new gain
      }
    } catch (error) { if (session === s) toast(error?.message || String(error)); } finally { s.dirty = null; }
  }, 150);
}

function swap(s, id, buffer) {
  const { context } = s, slot = s.slots.get(id), now = context.currentTime;
  const position = s.region ? loopPosition(s, now) : (((now - s.t0) % slot.duration) + slot.duration) % slot.duration;
  const node = context.createBufferSource(), fade = context.createGain();
  node.buffer = buffer; node.loop = true; if (s.region && s.region.start + s.region.length <= buffer.duration) { node.loopStart = s.region.start; node.loopEnd = s.region.start + s.region.length; } fade.gain.value = 0; node.connect(fade).connect(slot.out);
  node.start(now, position % buffer.duration);
  fade.gain.setTargetAtTime(1, now, .008); slot.fade.gain.cancelScheduledValues(now); slot.fade.gain.setTargetAtTime(0, now, .008);
  const old = slot.node; try { old.stop(now + .12); } catch { /* ended */ }
  s.sources.push(node); s.disposables.push(node, fade);
  s.swaps = (s.swaps || 0) + 1;
  if (s.gliding?.delete(id)) emitSoon();   // the exact sound is in: the glide is over
  s.slots.set(id, { node, fade, out: slot.out, duration: buffer.duration });
}

function build(s) {
  const { context, plan } = s, outs = new Map();
  const tap = (id, node) => {
    const splitter = context.createChannelSplitter(2), left = context.createAnalyser(), right = context.createAnalyser();
    left.fftSize = right.fftSize = 1024; node.connect(splitter); splitter.connect(left, 0); splitter.connect(right, 1);
    s.taps.set(id, { left, right, data: new Float32Array(1024) }); s.disposables.push(splitter, left, right);
  };
  // the loop length and the start time are known before the graphs are made: the LFOs of the effects start in step with the loop (live-lfo.mjs)
  const loop = Math.max(0, ...plan.items.filter((i) => i.kind === 'source' || i.kind === 'local').map((i) => s.buffers.get(i.id)?.duration || 0)), when = context.currentTime + .08;
  s.t0 = when; s.loop = loop; s.tracked = new Set();
  const clock = { position: () => (s.loop > 0 ? loopPosition(s, context.currentTime) : 0), loop: () => s.loop, tracked: s.tracked };
  for (const item of plan.items) {
    const out = context.createGain();
    s.disposables.push(out); outs.set(item.id, out);
    if (item.kind === 'source' || item.kind === 'local') {
      const buffer = s.buffers.get(item.id), voicePlan = s.drumPlans?.get(item.id), fade = context.createGain();
      const node = voicePlan ? createDrumVoice(context, fade, { span: () => (s.region ? { start: s.region.start, length: s.region.length } : { start: 0, length: s.loop }), positionAt: (time) => loopPosition(s, time) }) : context.createBufferSource();
      if (voicePlan) node.set(voicePlan); else { node.buffer = buffer; node.loop = true; }
      if (!voicePlan) node.connect(fade);
      fade.connect(out); s.sources.push(node); s.disposables.push(node, fade);
      s.slots.set(item.id, { node, fade, out, duration: buffer.duration });
    } else if (item.kind === 'effect') {
      const input = context.createGain();
      s.disposables.push(input);
      for (const link of item.inputs) outs.get(link.from).connect(input);
      const pre = context.createAnalyser(), post = context.createAnalyser();
      pre.fftSize = post.fftSize = 4096; pre.smoothingTimeConstant = post.smoothingTimeConstant = .7;
      input.connect(pre); out.connect(post); s.disposables.push(pre, post);
      s.spectra.set(item.id, { pre, post, preBins: new Float32Array(pre.frequencyBinCount), postBins: new Float32Array(post.frequencyBinCount), preWave: new Float32Array(2048), postWave: new Float32Array(2048) });
      const graph = liveEffectGraph(context, input, out, item.tpl, withAuto(item.id, paramsOf(item.id)), s.tempo, s.impulses.get(item.id), clock);
      if (item.key && graph.keyInput) { outs.get(item.key).connect(graph.keyInput); graph.setKey(true); }   // sidechain: the key take drives the detector
      s.built.set(item.id, { kind: 'effect', graph });
    } else {
      const lanes = Math.max(4, ...item.inputs.map((i) => i.lane + 1)), mixer = item.tpl === 'send' ? sendGraph(context, lanes) : mixerGraph(context, lanes, { bus: item.tpl === 'bus' });
      for (const link of item.inputs) outs.get(link.from).connect(mixer.lanes[link.lane]);
      mixer.output.connect(out);
      if (mixer.sendOutput) { const sendOut = context.createGain(); s.disposables.push(sendOut); mixer.sendOutput.connect(sendOut); outs.set(`${item.id}:send`, sendOut); }   // the second output of a send
      s.built.set(item.id, { kind: 'sum', graph: mixer });
    }
    tap(item.id, out);   // every block, sources too, has a left and right meter (the mixer view shows them per input)
  }
  s.liveGain = context.createGain(); s.disposables.push(s.liveGain);   // the chain output; the built take of the target can take its place for an A/B
  outs.get(plan.output).connect(s.liveGain).connect(playbackOutput(context));
  s.sources.forEach((node) => node.start(when));
  if (loopBars()) { s.region = regionAt(0, barSeconds(s), loopBars(), loop, when); applyRegion(s); }
}

/** Gives every block its parameters: the song's, with a bypass and the local preview on top. Only blocks whose values changed are touched. */
function refresh(force = false) {
  if (!session || session.starting) return;
  for (const id of session.watch) {   // the written value of a local block or a note transform replaces the preview of its knob
    const song = JSON.stringify(paramsOf(id)), seen = session.watchSong?.get(id);
    (session.watchSong ||= new Map()).set(id, song);
    if (seen !== undefined && seen !== song) session.override.delete(id);
  }
  for (const item of session.plan.items.filter((i) => i.kind === 'local')) {
    const key = localKey(item);
    if (session.localKeys.get(item.id) !== key) { session.localKeys.set(item.id, key); rerender(session, item.id); }
  }
  refreshImpulses(session);
  const anySolo = flat().nodes.some((n) => n.tpl === 'bus' && n.params?.solo === 'on');
  for (const [id, entry] of session.built) {
    const song = paramsOf(id), previous = session.last.get(id);
    if (previous && JSON.stringify(previous.song) !== JSON.stringify(song)) session.override.delete(id);   // the written value has arrived: it replaces the preview
    const params = withAuto(id, { ...song, ...(session.override.get(id) || {}) });
    if (entry.kind === 'effect' && session.bypass.has(id)) params.enabled = 'off';
    const key = JSON.stringify([params, anySolo]);
    if (!force && previous?.key === key) { session.last.set(id, { key, song }); continue; }
    session.last.set(id, { key, song });
    if (entry.kind === 'effect') entry.graph.update(params); else entry.graph.update(params, { anySolo });
  }
}

/** A knob or fader is being moved: the value is heard at once, and the song is written on release (which clears it). */
// Gliding live variant of rewind and scratch (owner wish 2026-11-12, chunk 2.7b): while a knob of such a block turns, its sound is not yet re-made; the loop of the block slows down like a hand on the record
// (playbackRate, approximate), and when the knob stops the exact re-render is swapped in at the same loop position and the speed is back to 1.
const GLIDE_RATE = { rewind: .6, scratch: .45 };
function glide(s, block) {
  const item = s.plan.items.find((i) => i.id === block && i.kind === 'local' && GLIDE_RATE[i.tpl]), slot = item && s.slots.get(block);
  if (!slot) return;
  const now = s.context.currentTime;
  slot.node.playbackRate.cancelScheduledValues(now); slot.node.playbackRate.setTargetAtTime(GLIDE_RATE[item.tpl], now, .04);
  (s.gliding ||= new Set()).add(block); emitSoon();
}
let emitTimer = 0;
const emitSoon = () => { clearTimeout(emitTimer); emitTimer = setTimeout(emit, 30); };

export function chainPreview(block, name, value) {
  if (session && session.watch.has(block)) glide(session, block);
  if (!session || !(session.built.has(block) || session.watch.has(block))) return;
  session.override.set(block, { ...(session.override.get(block) || {}), [name]: String(value) });
  refresh();
}

/**
 * A/B against the build: the built take of the target block plays in place of the live chain (same place of the loop, a short crossfade); false puts the live chain back.
 * Returns the new state, or null when there is nothing to compare with.
 */
export async function setCompareBuilt(on) {
  const s = session;
  if (!s || s.starting) return null;
  if (!on) {
    if (!s.builtTake) return false;
    fade(s, s.liveGain, 1); fade(s, s.builtTake.gain, 0);
    const old = s.builtTake;
    s.builtTake = null;
    setTimeout(() => { try { old.node.stop(); old.node.disconnect(); old.gain.disconnect(); } catch { /* gone */ } }, 200);
    emit();
    return false;
  }
  const take = takeOf(s.target);
  if (!take) { toast(`Build ${s.target} first: there is no built sound of it to compare with.`); return null; }
  const buffer = await decodedInput(`chain:${take}`, () => fetchTakeAudio(take));
  if (session !== s) return null;
  const node = s.context.createBufferSource(), gain = s.context.createGain(), now = s.context.currentTime;
  node.buffer = buffer; node.loop = true; gain.gain.value = 0; node.connect(gain).connect(playbackOutput(s.context));
  // the live chain is late by the look-ahead of its blocks (a limiter); the built sound is delayed by the same amount so the two line up
  node.start(now, (s.region ? loopPosition(s, now - blockLatencyOf(s)) : ((((now - s.t0 - blockLatencyOf(s)) % s.loop) + s.loop) % s.loop)) % buffer.duration);
  s.builtTake = { node, gain };
  applyRegion(s);
  fade(s, s.liveGain, 0); fade(s, gain, 1);
  emit();
  return true;
}
const blockLatencyOf = (s) => [...s.built.values()].reduce((sum, entry) => sum + (entry.graph.latency || 0), 0);
const fade = (s, gain, value) => { const now = s.context.currentTime; gain.gain.cancelScheduledValues(now); gain.gain.setTargetAtTime(value, now, .007); };

/** Bypasses (or brings back) one block of the chain; the block stays in the graph. */
export function setChainBypass(id, on) {
  if (!session) return;
  if (on) session.bypass.add(id); else { session.bypass.delete(id); session.cpuBypassed.delete(id); }
  refresh();
  emit();
}

/** The blocks that a model makes (ace-music, ai-voice, voice-clone, choir) are never built by the live chain: it plays the stored take. When was that take made? Asked once per start. */
const AI_BLOCKS = new Set(['ace-music', 'ai-voice', 'voice-clone', 'choir']);
async function loadTakeTimes(s) {
  s.takeTimes = new Map();
  for (const item of s.plan.items.filter((i) => i.kind === 'source' && AI_BLOCKS.has(i.tpl))) {
    try {
      const response = await fetch(`/api/takes/${encodeURIComponent(item.take)}`, { headers: { 'X-Ample-Token': getToken() } });
      const created = response.ok ? (await response.json()).take?.created : null;
      if (created) s.takeTimes.set(item.id, created);
    } catch { /* offline: no time to show */ }
  }
  if (session === s && s.takeTimes.size) emit();
}

/** Controls (a curve, through maps) that steer parameters of blocks in the chain: evaluated every 40 ms at the loop position and set like a knob that is being moved (plan 2.8). */
function startControls(s) {
  const nodes = flat().nodes.map((n) => ({ id: n.id, tpl: n.tpl, params: n.params }));
  s.controls = controlTargets(nodes, flat().links, (id, param) => s.built.get(id)?.kind === 'effect' && parameterLive(nodes.find((n) => n.id === id)?.tpl, param) === 'param');
  if (!s.controls.length) return;
  // follow: the loudness of the sound that feeds it (a built take of the song), measured once per change of its settings
  const series = new Map();
  const load = (id) => {
    const key = JSON.stringify(effective(id)), link = flat().links.find((l) => l.to === id && l.type === 'audio' && l.inName === 'in' && !l.disabled), take = link && takeOf(link.from);
    series.set(id, { key, data: series.get(id)?.data || null });   // the old series keeps playing until the new one is made
    if (!take) return;
    decodedInput(`chain:${take}`, () => fetchTakeAudio(take)).then((buffer) => {
      if (session !== s) return;
      const data = followSeries(toAudio(buffer), effective(id));
      if (data && series.get(id)?.key === key) series.set(id, { key, data });
    }).catch(() => {});
  };
  const followAt = (id, out, seconds) => {
    const entry = series.get(id);
    if (!entry || entry.key !== JSON.stringify(effective(id))) { load(id); return series.get(id)?.data ? pick(series.get(id).data, out, seconds) : null; }
    return entry.data ? pick(entry.data, out, seconds) : null;
  };
  const pick = (data, out, seconds) => { const list = out === 'gate' ? data.gate : data.values; return list[Math.min(list.length - 1, Math.max(0, Math.floor(seconds * 1000)))]; };
  const tick = () => {
    if (session !== s || s.starting) return;
    const byId = new Map(flat().nodes.map((n) => [n.id, { id: n.id, tpl: n.tpl, params: effective(n.id) }]));
    const bar = loopPosition(s, s.context.currentTime) / barSeconds(s) + 1;
    let changed = false;
    for (const control of s.controls) {
      const level = chainLevel(control.chain, byId, bar, followAt, control.out, loopPosition(s, s.context.currentTime));
      const text = level === null ? null : controlledValue(byId.get(control.target)?.tpl, control.param, level, chainTyped(control.chain, byId));
      if (text === null || s.override.get(control.target)?.[control.param] === text) continue;
      s.override.set(control.target, { ...(s.override.get(control.target) || {}), [control.param]: text }); changed = true;
    }
    if (changed) refresh();
  };
  s.controlTimer = setInterval(tick, 40);
}
/** Volume gains that are curves: heard at the loop position, set again every 40 ms (the graph smooths each step). */
function startAutomation(s) {
  if (!flat().nodes.some((n) => n.tpl === 'volume' && isAutomation(n.params?.gain))) return;
  s.autoTimer = setInterval(() => { if (session === s && !s.starting) refresh(); }, 40);
}

function dispose(s) {
  clearInterval(s.controlTimer); clearInterval(s.autoTimer);
  try { if (s.capacity) { s.capacity.onupdate = null; s.capacity.stop(); } } catch { /* gone */ }
  s.sources.forEach((node) => { try { node.stop(); } catch { /* not started */ } });
  if (s.builtTake) { try { s.builtTake.node.stop(); s.builtTake.node.disconnect(); s.builtTake.gain.disconnect(); } catch { /* gone */ } }
  s.built.forEach((entry) => entry.graph.dispose());
  s.disposables.forEach((node) => { try { node.disconnect(); } catch { /* already apart */ } });
}

/** Stops the chain (the sound stops with it). */
export function stopChain({ quiet = false } = {}) {
  if (!session) return;
  const s = session;
  session = null;
  dispose(s);
  if (!quiet) emit();
}

export const chainActive = () => Boolean(session);

setExternalPlayer({ playing: () => Boolean(session) && !session.starting, take: () => (session ? `chain:${session.target}` : null), stop: () => stopChain(),
  time: () => (session?.context && session.loop > 0 ? loopPosition(session, session.context.currentTime) : 0), duration: () => (session?.loop || 0) });

// the song changed: a different set of blocks or takes builds the graph again (from the start); a changed value is only handed to its block
subscribe(() => {
  if (!session || session.starting) return;
  const plan = planNow(session.target);
  // a block upstream changed after its sound was built (the editor drops its take): keep playing the old build, say so, and wait for the next build (8.1)
  const lost = session.plan.items.filter((i) => i.kind === 'source' && !takeOf(i.id)).map((i) => i.id);
  if (!plan.ok && lost.length) {
    const before = [...(session.stale || [])].sort().join();
    session.stale = new Set(lost);
    if (before !== lost.slice().sort().join()) emit();
    refresh();
    return;
  }
  if (!plan.ok) { stopChain(); toast(plan.reason); return; }
  if (session.stale) { session.stale = null; emit(); }
  if (signatureOf(plan) !== session.signature) {
    const target = session.target, bypass = new Set(session.bypass);
    startChain(target).then(() => { if (session) { bypass.forEach((id) => session.bypass.add(id)); refresh(true); emit(); } });
    return;
  }
  refresh();
});
document.addEventListener('ample:open', () => stopChain());

/** The parameters a control has set on a block of the running chain (tests and the status line), or null. */
export const controlledParams = (id) => (session?.controls?.some((c) => c.target === id) ? { ...(session.override.get(id) || {}) } : null);

/** Tests: how many hits the drums block of the running chain has queued as a plan (0 when it plays as a rendered loop). */
export const chainDrumHits = (id) => session?.slots.get(id)?.node.hitCount ?? 0;
