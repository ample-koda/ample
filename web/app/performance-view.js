// The Performance workspace (issue 266): an opt-in tab next to the Playlist where built takes are put on named lanes
// and launched at the next beat or bar. "Enter Performance" starts a session on the Web Audio clock; "Stop all" ends every clip and every
// queued launch at once;
// "Leave" ends the session. The lanes are a layout of their own (kept in the project's ui-state):
// nothing here changes the song or its arrangement.
// The launch rules live in ./performance.js (createPerformance), the layout rules in ../lib/performance-layout.mjs, the sources in
// ../lib/performance-audio.mjs. While the setting is off, no tab, no section and no timer exist. @ample:performance-view
import { $, el, toast } from './ui.js';
import { S, flat } from './store.js';
import { getContext } from '../lib/audio-engine.mjs';
import { takeCache } from '../lib/take-cache.mjs';
import { barBeats } from '../lib/meter.mjs';
import { playbackOutput } from './playback-volume.js';
import { audioDataId, notifyPlayer, setExternalPlayer, stop as stopPlayer } from './player.js';
import { getUiKey, loadUiState, setUiKey } from './ui-state.js';
import { QUANTIZE, createPerformance, performanceEnabled, setPerformanceEnabled } from './performance.js';
import { createClipPlayer } from '../lib/performance-audio.mjs';
import {
  addClip, addLane, clickAction, clipCount, clipOptions, emptyLayout, parseLayout, pruneLayout,
  removeClip, removeLane, serializeLayout, statusText,
} from '../lib/performance-layout.mjs';

const UI_KEY = 'performanceLayout';
let layout = emptyLayout();
let session = null;      // { ctx, engine, player } while a performance is on
let entering = false;
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

const busy = () => Boolean(session) && Object.values(session.engine.state()).some((clips) => clips.some((c) => c.status !== 'ready'));
const statusOf = (lane, id) => session?.engine.state()[lane]?.find((c) => c.id === id)?.status ?? 'ready';

// The header Stop (and any other playback starting) ends what the performance plays, like the other previews do. The session stays on.
setExternalPlayer({
  playing: () => busy(),
  loading: () => (entering ? 'performance' : null),
  take: () => 'performance',
  stop: () => { if (session) { session.engine.stopAll(); paint(); } },
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

const save = () => setUiKey(UI_KEY, serializeLayout(layout));
const fromStore = () => { layout = parseLayout(getUiKey(UI_KEY)); };

// ------------------------------------------------------------------------------------------------ audio

const bufferOfTake = (take) => takeCache.getDecoded(`chain:${take}`, async () => {
  const dataId = await audioDataId(take);
  const response = await fetch(`/api/data/${dataId}`);
  if (!response.ok) throw new Error('A take could not be loaded.');
  return getContext().decodeAudioData(await response.arrayBuffer());
});

/** Starts the session: stops other playback, loads every take on the lanes, then the clock and the launch engine exist. */
async function enter() {
  if (session || entering) return;
  if (!S.doc) { toast('Open a song first.'); return; }
  const takes = S.build.takes || {};
  const wanted = layout.lanes.flatMap((lane) => lane.clips.map((c) => ({ lane: lane.name, ...c })));
  if (!wanted.length) { toast('Put at least one clip on a lane first.'); return; }
  const missing = [...new Set(wanted.filter((c) => !takes[c.id]).map((c) => c.label))];
  if (missing.length) { toast(`Build first: no take yet for ${missing.join(', ')}.`); return; }
  entering = true;
  render();
  stopPlayer();
  notifyPlayer();
  try {
    const ctx = getContext();
    const resumed = ctx.state !== 'running' ? ctx.resume().catch(() => {}) : null;   // inside the click
    const buffers = new Map();
    for (const c of wanted) if (!buffers.has(c.id)) buffers.set(c.id, await bufferOfTake(takes[c.id]));
    if (resumed) await Promise.race([resumed, new Promise((resolve) => setTimeout(resolve, 1500))]);
    const player = createClipPlayer({ ctx, output: playbackOutput(ctx), bufferOf: (clip) => buffers.get(clip.id) });
    const engine = createPerformance({
      now: () => ctx.currentTime,
      play: player.play,
      bpm: S.doc.project?.tempo || 120,
      beatsPerBar: Math.max(1, Math.round(barBeats())),
      quantize,
    });
    for (const lane of layout.lanes) for (const c of lane.clips) engine.addClip(lane.name, { id: c.id, label: c.label });
    session = { ctx, engine, player };
    timer = setInterval(paint, 100);
  } catch (error) {
    toast(error.message || String(error));
  } finally {
    entering = false;
    wasBusy = false;
    notifyPlayer();
    render();
  }
}

/** Leaving ends everything that plays or is queued and releases the sources; the arrangement was never touched. */
function leave() {
  clearInterval(timer);
  timer = 0;
  if (!session) return;
  try { session.engine.stopAll(); } catch { /* nothing was scheduled */ }
  session.player.releaseAll();
  session = null;
  wasBusy = false;
  notifyPlayer();
  render();
}

function stopAll() {
  if (!session) return;
  session.engine.stopAll();
  paint();
  notifyPlayer();
}

function clickClip(lane, id) {
  if (!session) { toast('Enter Performance to launch clips.'); return; }
  const action = clickAction(statusOf(lane, id));
  try {
    if (action === 'launch') session.engine.launch(lane, id);
    else if (action === 'cancel') session.engine.cancelQueued(lane);
  } catch (error) { toast(error.message || String(error)); }
  paint();
  notifyPlayer();
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
  const state = session.engine.state();
  for (const button of box.querySelectorAll('button.perf-clip')) {
    const status = state[button.dataset.lane]?.find((c) => c.id === button.dataset.clip)?.status ?? 'ready';
    button.dataset.status = status;
    button.setAttribute('aria-label', `${button.dataset.label} on ${button.dataset.lane}: ${statusText(status)}`);
    const text = button.querySelector('.perf-state');
    if (text) text.textContent = statusText(status);
  }
  const now = busy();
  if (now !== wasBusy) { wasBusy = now; notifyPlayer(); }
}

function applyLayout(result) {
  if (!result.ok) { toast(result.reason); return; }
  layout = result.layout;
  save();
  render();
}

const LOCK_HINT = 'Leave Performance to change the lanes';
const isLocked = () => Boolean(session) || entering;

function clipCell(lane, clip) {
  const status = statusOf(lane.name, clip.id);
  const grid = quantize === QUANTIZE.bar ? 'bar' : 'beat';
  const button = el('button', {
    type: 'button',
    className: 'perf-clip',
    title: `Launch at the next ${grid}; press again while queued to take it back`,
    onclick: () => clickClip(lane.name, clip.id),
  }, [el('strong', { textContent: clip.label }), el('span', { className: 'perf-state', textContent: statusText(status) })]);
  Object.assign(button.dataset, { lane: lane.name, clip: clip.id, label: clip.label, status });
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

function laneRow(lane, options) {
  const choice = el('select', {
    className: 'perf-pick', disabled: isLocked() || !options.length, 'aria-label': `Add a clip to ${lane.name}`,
  }, [
    el('option', { value: '', textContent: options.length ? 'Add a built block…' : 'Build a block first' }),
    ...options.map((o) => el('option', { value: o.id, textContent: o.label })),
  ]);
  choice.onchange = () => {
    const option = options.find((o) => o.id === choice.value);
    if (option) applyLayout(addClip(layout, lane.name, option));
  };
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
    el('div', { className: 'perf-laneHead' }, [el('strong', { textContent: lane.name }), stopAtBar, removeLaneButton]),
    el('div', { className: 'perf-clips' }, [...lane.clips.map((clip) => clipCell(lane, clip)), choice]),
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
    textContent: entering ? 'Loading the takes…' : 'Enter Performance',
    disabled: entering || !S.doc || !ready,
    title: ready ? 'Start a performance session: launch clips at the next bar or beat' : 'Put at least one built block on a lane first',
    onclick: enter,
  });
  const leaveButton = el('button', { type: 'button', className: 'perf-leave', textContent: 'Leave Performance', onclick: leave });
  const stopButton = el('button', {
    type: 'button',
    className: 'perf-stop',
    textContent: 'Stop all',
    disabled: !session,
    title: 'Stop every clip and clear every queued launch, at once',
    onclick: stopAll,
  });
  const note = session
    ? 'Performance is on: the song and its arrangement stay as they are.'
    : 'Lanes and clips stay in this project; the arrangement is not touched.';
  return el('div', { className: 'perf-head' }, [
    session ? leaveButton : enterButton,
    stopButton,
    el('label', { className: 'perf-q' }, [el('span', { textContent: 'Launch on ' }), grid]),
    el('span', { className: 'note', textContent: note }),
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

export function render() {
  const box = $('#b-performance');
  if (!box || !performanceOn()) return;
  if (!loaded) {
    loaded = true;
    loadUiState().then(() => { fromStore(); render(); });
  }
  if (S.doc && !session && !entering) {
    const kept = pruneLayout(layout, flat().nodes.map((n) => n.id));
    if (kept.dropped.length) { layout = kept.layout; save(); }
  }
  const options = clipOptions(flat().nodes, S.build.takes || {});
  const empty = S.doc ? 'Add a lane, then put built blocks on it.' : 'Open a song to set up its lanes.';
  const lanes = layout.lanes.length
    ? layout.lanes.map((lane) => laneRow(lane, options.filter((o) => !lane.clips.some((c) => c.id === o.id))))
    : [el('p', { className: 'note', textContent: empty })];
  box.replaceChildren(headBar(), el('div', { className: 'perf-lanes' }, lanes), addLaneForm());
  box.setAttribute('aria-label', 'Performance');
  if (session) paint();
}

// the clips of another song are not this song's: a song really opened ends the session and reads the layout of the project again
document.addEventListener('ample:song-opened', () => { leave(); if (loaded) fromStore(); render(); });
