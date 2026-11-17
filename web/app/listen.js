// Live Preview in the code editor (plan/live-preview/PLAN.md, WP3/WP4/WP6): listen to the block under the cursor (or the song, or bars) while you edit.
// A change is built after a short typing pause (store.build: one build at a time, the same build the Build button makes, so the music stays the code), the
// new take comes in at the loop boundary, and the code that plays lights up. The decisions live in web/lib/listen-session.mjs (when to build), the loop in
// web/lib/listen-segments.mjs, the marks in web/lib/listen-highlight.mjs; this file reads the store and draws the bar. @ample:listen-preview
import { S, build, flat, reportTakes, subscribe, subscribeSelection, stopBuild } from './store.js';
import { el, toast } from './ui.js';
import { barBeats } from '../lib/meter.mjs';
import { activity } from '../lib/block-activity.mjs';
import { masterTakeOf, songState } from '../lib/transport-model.mjs';
import { createListenSession } from '../lib/listen-session.mjs';
import { barSeconds, barsLoop, playSegments, rangeBuild, silentWindows } from '../lib/listen-segments.mjs';
import { moveMarks, playbackMarks } from '../lib/listen-highlight.mjs';
import { applyPendingNow, listenPlayerState, loadTake, loadTakeSlice, onListenPlayer, playEntry, setLoading, stopListenPlayer } from './listen-player.js';

const STORE = 'ample.listen';
const DEFAULTS = { target: 'cursor', firstBar: 1, lastBar: 4, trigger: 'pause', timing: 'boundary', silence: 'edges', quality: 'preview', barsMethod: 'from-start' };
const readSettings = () => { try { return { ...DEFAULTS, ...JSON.parse(localStorage.getItem(STORE) || '{}') }; } catch { return { ...DEFAULTS }; } };
const settings = readSettings();
if (settings.barsMethod === 'lead-in') settings.barsMethod = 'from-start';   // the two-bar lead-in was replaced (decision 13)
const save = () => { try { const { pinned, ...kept } = settings; localStorage.setItem(STORE, JSON.stringify(kept)); } catch { /* storage may be blocked */ } };
const L = { pinned: null, ports: {}, marksHook: null, editorText: () => S.doc?.text || '', lastText: null, frame: 0, lastMarkKey: '' };

const AUDIO = (port) => port && port[1] === 'audio';
const tempo = () => Number(S.doc?.project?.tempo) || 120;
const node = (id) => flat().nodes.find((n) => n.id === id);

/** What a block really sounds as: itself when it has an audio output, else the block its notes or control feed (the first one). */
export function audibleBlock(id, graph = flat(), seen = new Set()) {
  const n = graph.nodes.find((x) => x.id === id);
  if (!n || seen.has(id)) return null;
  seen.add(id);
  if ((n.outs || []).some(AUDIO)) return id;
  const next = graph.links.find((l) => l.from === id && !l.disabled && l.type !== 'audio');
  return next ? audibleBlock(next.to, graph, seen) : null;
}

/** The audio outputs of a block (port names). */
const audioOuts = (id) => (node(id)?.outs || []).filter(AUDIO).map((p) => p[0]);

/** The take role to play: the port picked for this block (when it still exists), else the first audio output that something reads, else the first one. */
function outputRole(id) {
  const outs = audioOuts(id);
  if (L.ports[id] && outs.includes(L.ports[id])) return L.ports[id];
  const read = flat().links.find((l) => l.from === id && !l.disabled && outs.includes(l.outName));
  return read?.outName || outs[0] || '';
}

/**
 * Where a block's take sits, as the engine renders it (BlockTiming.cs): a block that is placed (it has parts, or its sound input comes from a placed block) renders
 * on the song timeline from bar 1, and its sound is in the span of those parts; a block that is not placed renders its own length from its origin, the first bar
 * of the first block above it with parts (through its sound, render or notes input).
 * Returns { placed, span: { first, last } | null, origin }.
 */
function timingOf(id) {
  const graph = flat(), byId = new Map(graph.nodes.map((n) => [n.id, n]));
  const partsSpan = (n) => (n?.parts?.length ? { first: Math.min(...n.parts.map((p) => p.at)), last: Math.max(...n.parts.map((p) => p.at + p.length - 1)) } : null);
  const input = (at, names) => graph.links.find((l) => l.to === at && !l.disabled && names.includes(l.inName));
  let at = id, seen = new Set();
  while (at && !seen.has(at)) {   // placed: parts here or through the sound input
    seen.add(at);
    const span = partsSpan(byId.get(at));
    if (span) return { placed: true, span, origin: 1 };
    at = input(at, ['in', 'render_in', 'audio'])?.from;
  }
  at = id; seen = new Set();
  while (at && !seen.has(at)) {   // not placed: the origin through the sound, render or notes input
    seen.add(at);
    const span = partsSpan(byId.get(at));
    if (span) return { placed: false, span: null, origin: span.first };
    at = input(at, ['in', 'render_in', 'audio', 'notes'])?.from;
  }
  return { placed: false, span: null, origin: 1 };
}

/** The job of the moment: what to build and play, with the settings that change it. */
function currentJob() {
  const quality = settings.quality === 'full' ? undefined : 'preview';
  if (settings.target === 'song') return { kind: 'song', quality, key: `song:${settings.quality}` };
  if (settings.target === 'bars') {
    const first = Math.max(1, Math.round(settings.firstBar)), last = Math.max(first, Math.round(settings.lastBar));
    return {
      kind: 'bars', range: { firstBar: first, bars: last - first + 1 }, quality, fastDraft: settings.barsMethod === 'fast-draft',
      key: `bars:${first}-${last}:${settings.quality}:${settings.barsMethod}`,
    };
  }
  const picked = L.pinned || S.sel;
  const id = picked ? audibleBlock(picked) : null;
  if (!id) {
    const fallback = picked
      ? `${picked} has no sound of its own and feeds nothing audible: listening to the song`
      : 'no block under the cursor: listening to the song';
    return { kind: 'song', quality, key: `song:${settings.quality}`, fallback };
  }
  const role = outputRole(id);
  return { kind: 'block', id, picked, role, quality, key: `block:${id}.${role}:${settings.quality}` };
}

/** Waits until a build that is not ours ends (the store runs one build at a time). */
async function storeFree() { while (S.build.running) await new Promise((resolve) => setTimeout(resolve, 120)); }

const failedMessage = (report, id) => {
  const item = (report?.blocks || []).find((b) => (b.block ?? b.Block) === id && ['failed', 'skipped'].includes(b.status ?? b.Status));
  return item ? `${id}: ${item.message ?? item.Message ?? item.status ?? item.Status}` : null;
};

/** Builds a job with the store and says what to play: { take, role, loop, takeFirstBar, method, text, job }. */
async function buildJob(job) {
  await storeFree();
  const text = S.doc?.text || '', barSec = barSeconds(tempo(), barBeats());
  if (job.kind === 'block') {
    const report = await build(job.id, null, job.quality, false, { separate: true });   // its takes stay apart from S.build.takes (plan 6.1)
    if (!report) { if (S.build.error) throw new Error(S.build.error); return null; }
    const failed = failedMessage(report, job.id);
    if (failed) throw new Error(failed);
    const take = reportTakes(report)[job.id];   // only the take of this build, never a take of the normal build state
    if (!take) throw new Error(`${job.id} made no sound in this build.`);
    const unpinned = report.unpinnedComponents || [];   // components a normal build would pin (plan 6.3): this preview is not reproducible yet
    const timing = timingOf(job.id);
    const loop = timing.placed ? { start: (timing.span.first - 1) * barSec, end: timing.span.last * barSec } : null;   // not placed: the whole take loops
    return { take, role: job.role, loop, takeFirstBar: timing.placed ? 1 : timing.origin, method: 'block', text, job, unpinned };
  }
  if (job.kind === 'bars') {
    // decision 13 (plan 6.4): a full render of the code now: cut the bars out of it (exact); else build from bar 1 to one bar past the range (the timeline
    // and state of the whole song, longer); a fast draft builds the range alone and says it is not exact. Only the chosen bars are downloaded.
    const takes = S.build.takes || {}, nodes = flat().nodes, master = masterTakeOf(takes, nodes.map((n) => n.id), nodes);
    const made = S.build.master;
    if (master && made && made.range === null && made.text === text && songState(nodes, takes, S.build.status || {}).upToDate) {
      return { take: master, role: '', loop: barsLoop(job.range, barSec, 1), takeFirstBar: 1, method: 'cut', text, job, slice: true };
    }
    const plan = rangeBuild(job.range, { fastDraft: job.fastDraft });
    L.buildingNote = plan.method === 'from-start' && plan.before > 0
      ? `rendering bars 1-${plan.build.bars} to play bars ${job.range.firstBar}-${job.range.firstBar + job.range.bars - 1}: \
${plan.before} bar${plan.before === 1 ? '' : 's'} before the range make it take longer`
      : '';
    paint();
    let report;
    try { report = await build(null, plan.build, job.quality, false, { separate: true }); } finally { L.buildingNote = ''; }
    if (!report) { if (S.build.error) throw new Error(S.build.error); return null; }
    const take = report.masterTakeId ?? report.MasterTakeId;
    if (!take) throw new Error(report.builtWithErrors ? 'The range was built with errors: see the build log.' : 'The range made no sound.');
    const unpinned = report.unpinnedComponents || [];
    return { take, role: '', loop: barsLoop(job.range, barSec, plan.takeFirstBar), takeFirstBar: plan.takeFirstBar, method: plan.method, before: plan.before, text, job, unpinned, slice: true };
  }
  const report = await build(null, null, job.quality, false, { keepPlaying: true, separate: true });
  if (!report) { if (S.build.error) throw new Error(S.build.error); return null; }
  const take = report.masterTakeId ?? report.MasterTakeId;
  if (!take) throw new Error('The song made no sound.');
  return { take, role: '', loop: null, takeFirstBar: 1, method: 'song', text, job, unpinned: report.unpinnedComponents || [] };
}

/** The loop and its segments for the player: silence trimmed (or skipped) only for a block, and only as the setting says. */
async function entryOf(result) {
  setLoading(true);
  try {
    // bars: only the chosen stretch is downloaded; the loop and the marks then count from where the stretch starts (sliceOffset)
    const piece = result.slice && result.loop ? await loadTakeSlice(result.take, result.role, result.loop.start, result.loop.end) : { buffer: await loadTake(result.take, result.role), offset: 0 };
    const buffer = piece.buffer, sliceOffset = piece.offset;
    const wanted = result.loop ? { start: result.loop.start - sliceOffset, end: result.loop.end - sliceOffset } : null;
    const loop = wanted ? { start: Math.max(0, Math.min(wanted.start, buffer.duration)), end: Math.min(wanted.end, buffer.duration) } : { start: 0, end: buffer.duration };
    if (loop.end - loop.start < 0.05) { loop.start = 0; loop.end = buffer.duration; }
    const mode = result.job.kind === 'block' ? settings.silence : 'original';
    const channels = Array.from({ length: buffer.numberOfChannels }, (_, c) => buffer.getChannelData(c));
    const cut = playSegments(silentWindows(channels, buffer.sampleRate), loop, { mode, barSec: barSeconds(tempo(), barBeats()) });
    return { ...result, buffer, loop, sliceOffset, segments: cut.segments, silent: cut.silent, skipped: cut.skipped };
  } finally { setLoading(false); }
}

const session = createListenSession({
  build: (job) => buildJob(job),
  cancelBuild: () => stopBuild(),
  play: async (result) => { const entry = await entryOf(result); await playEntry(entry, { timing: settings.timing, onStop: () => session.stop() }); },
  stopPlayback: () => stopListenPlayer({ quiet: true }),
  onChange: () => paint(),
});

export const listening = () => session.state().active;
export function startListening() { const job = currentJob(); if (job.fallback) toast(job.fallback); L.lastText = S.doc?.text ?? null; return session.start(job).catch((e) => toast(e.message || String(e))); }
export function stopListening() { session.stop(); }
export function toggleListening() { if (listening()) stopListening(); else startListening(); }
/** Ctrl+Enter: the valid editor content was applied; build the target now (and play it when listening; else it is ready for Play). */
export async function buildTargetNow() {
  if (listening()) return session.setJob(currentJob(), true);
  try { await buildJob(currentJob()); toast('Built: Ctrl+Shift+Space plays it.'); } catch (e) { toast(e.message || String(e)); }
}
/** Ctrl+Shift+Enter: the waiting build runs at once (never a second one next to a running build). */
export function flushListening() { return session.flush(); }

// ---- triggers: the applied text (after the typing pause), a save, the block under the cursor ----
subscribe(() => {
  const text = S.doc?.text ?? null, wasDirty = L.dirty;
  L.dirty = !!S.doc?.dirty;
  if (!listening()) { L.lastText = text; return; }
  if (text !== L.lastText) {
    L.lastText = text;
    if (settings.trigger === 'pause') session.setJob(currentJob());
  } else if (settings.trigger === 'save' && wasDirty && !L.dirty) session.setJob(currentJob(), true);
  paint();
});
subscribeSelection(() => {
  if (!listening() || settings.target !== 'cursor' || L.pinned) { paint(); return; }
  const job = currentJob();
  if (job.key !== session.state().job?.key) session.setJob(job);
  paint();
});

// ---- the bar ----
const bar = { root: null };
function select(id, label, options, value, onchange) {
  const s = el('select', { id, title: label, 'aria-label': label });
  for (const [v, text] of options) s.append(el('option', { value: v, textContent: text }));
  s.value = value;
  s.onchange = () => onchange(s.value);
  return s;
}
const setting = (name) => (value) => { settings[name] = value; save(); if (listening()) session.setJob(currentJob(), name !== 'timing' && name !== 'trigger'); paint(); };

/** The Live Preview bar of the code editor. */
export function listenBar() {
  const play = el('button', { id: 'lp-play', className: 'primary', textContent: '▶ Listen', title: 'Listen while you edit (Ctrl+Shift+Space)', onclick: () => toggleListening() });
  const target = select('lp-target', 'What to listen to', [['cursor', 'Block at the cursor'], ['song', 'Whole song'], ['bars', 'Bars']], settings.target, (v) => { setting('target')(v); });
  const first = el('input', { id: 'lp-first', type: 'number', min: '1', value: String(settings.firstBar), title: 'First bar', 'aria-label': 'First bar', style: 'width:4.5em' });
  const last = el('input', { id: 'lp-last', type: 'number', min: '1', value: String(settings.lastBar), title: 'Last bar (included)', 'aria-label': 'Last bar', style: 'width:4.5em' });
  first.onchange = () => setting('firstBar')(Number(first.value) || 1);
  last.onchange = () => setting('lastBar')(Number(last.value) || 1);
  const pin = el('button', {
    id: 'lp-pin', textContent: '📌', title: 'Pin: keep listening to this block while the cursor moves elsewhere', 'aria-pressed': 'false',
    onclick: () => { L.pinned = L.pinned ? null : (S.sel || null); paint(); if (listening()) session.setJob(currentJob()); },
  });
  const port = el('select', { id: 'lp-port', title: 'Which output of the block to hear', 'aria-label': 'Output', style: 'display:none' });
  port.onchange = () => { const job = currentJob(); if (job.kind === 'block') L.ports[job.id] = port.value; if (listening()) session.setJob(currentJob(), true); paint(); };
  const status = el('span', { id: 'lp-status', className: 'note', role: 'status', 'aria-live': 'polite' });
  const applyNow = el('button', {
    id: 'lp-apply', textContent: 'Apply now', title: 'Play the new build now instead of at the end of the loop (Ctrl+Shift+Enter)',
    style: 'display:none', onclick: () => applyPendingNow(),
  });
  const more = el('details', { className: 'lp-more' }, [el('summary', { textContent: 'Settings', title: 'When to build, when to hear it, silence, quality' }),
    el('div', { className: 'lp-settings' }, [
      select('lp-trigger', 'Build', [['pause', 'Build after a typing pause'], ['save', 'Build when I save'], ['manual', 'Build only with Ctrl+Enter']], settings.trigger, setting('trigger')),
      select('lp-timing', 'Hear the new build', [['boundary', 'At the end of the loop'], ['now', 'At once']], settings.timing, setting('timing')),
      select('lp-silence', 'Silence in a block preview', [
        ['edges', 'Trim silence at the edges'], ['original', 'Original timing'], ['gaps', 'Also skip long silences (1 bar and 2 s, under -60 dB)'],
      ], settings.silence, setting('silence')),
      select('lp-quality', 'Quality', [['preview', 'Preview quality (fast)'], ['full', 'Full quality']], settings.quality, setting('quality')),
      select('lp-bars-method', 'Bars', [
        ['from-start', 'Bars: as in the whole song (built from bar 1, longer)'], ['fast-draft', 'Bars: fast draft (the range alone, not exact)'],
      ], settings.barsMethod, setting('barsMethod')),
      el('span', { className: 'note', textContent: 'Keys in the editor: Ctrl+Enter builds now, Ctrl+Shift+Enter runs the waiting build, Ctrl+Shift+Space listens or stops. Space types.' }),
    ])]);
  bar.root = el('div', { className: 'bmbar lp-bar', id: 'se-listen' }, [play, target, first, last, pin, port, status, applyNow, more]);
  paint();
  return bar.root;
}

const methodText = (r) => {
  const range = r?.job?.range, bars = range ? `bars ${range.firstBar}-${range.firstBar + range.bars - 1}` : 'the bars';
  if (r?.method === 'cut') return `${bars} cut from the full render (exact)`;
  if (r?.method === 'from-start') {
    if (r.before <= 0) return `${bars} as in the whole song`;
    return `${bars} as in the whole song: built from bar 1 (${r.before} bar${r.before === 1 ? '' : 's'} before the range rendered, not played)`;
  }
  if (r?.method === 'fast-draft') return `FAST DRAFT: ${bars} built alone, not an exact preview of these bars in the whole song`;
  return '';
};
function targetText(job) {
  if (!job) return '';
  if (job.kind === 'song') return 'the song';
  if (job.kind === 'bars') return `bars ${job.range.firstBar}-${job.range.firstBar + job.range.bars - 1}`;
  const name = audioOuts(job.id).length > 1 && job.role ? `${job.id}.${job.role}` : job.id;
  return job.picked && job.picked !== job.id ? `${name} (fed by ${job.picked})` : name;
}

function paint() {
  const root = bar.root;
  if (!root?.isConnected) return;
  const st = session.state(), player = listenPlayerState();
  // what is heard: the entry the player plays (a newer build may still load or wait for the loop boundary)
  const result = player.entry || st.result;
  root.querySelector('#lp-play').textContent = st.active ? '■ Stop' : '▶ Listen';
  root.querySelector('#lp-first').style.display = root.querySelector('#lp-last').style.display = settings.target === 'bars' ? '' : 'none';
  const pin = root.querySelector('#lp-pin');
  pin.style.display = settings.target === 'cursor' ? '' : 'none';
  pin.setAttribute('aria-pressed', String(!!L.pinned)); pin.classList.toggle('on', !!L.pinned);
  pin.title = L.pinned ? `Pinned to ${L.pinned}: click to follow the cursor again` : 'Pin: keep listening to this block while the cursor moves elsewhere';
  // the output choice: only for a block with more than one audio output
  const job = settings.target === 'cursor' ? currentJob() : null, outs = job?.kind === 'block' ? audioOuts(job.id) : [];
  const port = root.querySelector('#lp-port');
  port.style.display = outs.length > 1 ? '' : 'none';
  if (outs.length > 1) {
    if ([...port.options].map((o) => o.value).join() !== outs.join()) port.replaceChildren(...outs.map((o) => el('option', { value: o, textContent: `output ${o}` })));
    port.value = job.role;
  }
  const apply = root.querySelector('#lp-apply');
  apply.style.display = player.pending && player.remaining > 4 ? '' : 'none';
  const parts = [];
  if (!st.active) parts.push(st.status === 'stopped' ? 'Stopped' : '');
  else {
    const heard = st.playing ? `hearing build #${st.playing}${result ? ` of ${targetText(result.job)}` : ''}` : '';
    if (st.status === 'building') parts.push(`building${st.building !== null ? ` #${st.building}` : ''}${L.buildingNote ? ` (${L.buildingNote})` : ''}${heard ? `, still ${heard}` : ''}`);
    else if (st.status === 'error') parts.push(`build failed: ${st.error}${heard ? ` (still ${heard})` : ''}`);
    else if (player.loading) parts.push('loading the sound...');
    else if (player.pending) parts.push(`new build ready, plays in ${Math.ceil(player.remaining)} s`);
    else if (heard) parts.push(heard);
    if (st.pending) parts.push('waiting for a typing pause');
    if (result && methodText(result)) parts.push(methodText(result));
    if (result?.unpinned?.length) parts.push(`not pinned: ${result.unpinned.join(', ')} (a normal build pins it; until then this preview is not reproducible)`);
    if (player.entry?.silent) parts.push('silent in this loop');
    else if (player.entry?.skipped > 0.05) parts.push(`${player.entry.skipped.toFixed(1)} s of silence skipped`);
    const typed = L.editorText(), playingText = result?.text;
    if (playingText && typed !== playingText) parts.push(S.doc?.text === playingText ? 'the code has changes that are not built yet' : 'you hear an older version');
  }
  const status = root.querySelector('#lp-status');
  status.textContent = parts.filter(Boolean).join(' · ');
  status.title = status.textContent;   // the line is cut with an ellipsis when it is long (one line, so the editor never jumps)
  status.classList.toggle('bad', st.status === 'error');
  status.classList.toggle('draft', result?.method === 'fast-draft');
  root.querySelector('#lp-target').value = settings.target;
  scheduleMarks();
}

// ---- the marks: the target block, and what plays now (on the text of the build that plays, moved to the text in the editor) ----
/** The source editor gives the hook that puts playback marks in the editor (source.js setPlaybackMarks) and how to read its text. */
export function connectEditor({ setMarks, text }) { L.marksHook = setMarks; L.editorText = text; scheduleMarks(); }

function scheduleMarks() {
  if (L.frame) return;
  L.frame = setTimeout(() => { L.frame = 0; updateMarks(); if (listening() || L.pinned) scheduleMarks(); }, 66);   // at most 15 updates a second
}

function updateMarks() {
  if (!L.marksHook) return;
  const st = session.state(), player = listenPlayerState(), result = player.entry || st.result, typed = L.editorText();   // what is heard: the entry the player plays
  const marks = [];
  const target = L.pinned || (st.active && st.job?.kind === 'block' ? st.job.picked || st.job.id : null);
  if (target) marks.push(...playbackMarks({ text: typed, target }).filter((m) => m.cls === 'lp-target'));
  if (st.active && player.playing && result && player.takeTime !== null && result.takeFirstBar !== null) {
    const barSec = barSeconds(tempo(), barBeats()), songBar = result.takeFirstBar + (player.takeTime + (result.sliceOffset || 0)) / barSec;
    const graph = flat(), lit = activity(graph, Math.floor(songBar));
    const active = result.job.kind === 'block' ? [...lit.nodes].filter((id) => upstreamOf(result.job.id, graph).has(id)) : [...lit.nodes];
    // the bars of each block on the song's timeline: a placed block in song bars from bar 1, a block that is not placed in its own bars from 1 at its
    // origin. A range build keeps the song's timeline (measured, plan 6.4: the same bars of a range build and of the whole song are the same samples).
    const frames = new Map();
    const frame = (name) => {
      if (!frames.has(name)) { const t = timingOf(name); frames.set(name, t.placed ? { first: 1, offset: 0 } : { first: 1, offset: t.origin - 1 }); }
      return frames.get(name);
    };
    const clipFirstBar = (name) => graph.nodes.find((n) => n.id === name)?.parts?.[0]?.at || 1;
    const now = playbackMarks({
      text: result.text, target: null, songBar, active, quartersPerBar: barBeats(), barSeconds: barSec, frame, clipFirstBar,
    });
    marks.push(...moveMarks(now, result.text, typed));
  }
  const key = marks.map((m) => `${m.from}-${m.to}-${m.cls}`).join(',');
  if (key === L.lastMarkKey) return;
  L.lastMarkKey = key;
  L.marksHook(marks);
}

/** The blocks a block hears (itself and everything that feeds it). */
function upstreamOf(id, graph) {
  const out = new Set([id]), queue = [id];
  while (queue.length) { const at = queue.shift(); for (const l of graph.links) if (l.to === at && !l.disabled && !out.has(l.from)) { out.add(l.from); queue.push(l.from); } }
  return out;
}

onListenPlayer(() => paint());
