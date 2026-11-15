// The Performance workspace (issue 266): an opt-in tab next to the Playlist where built takes are put on named lanes
// and launched at the next beat or bar. "Enter Performance" starts a session on the Web Audio clock; "Stop all" ends every clip and every
// queued launch at once;
// "Leave" ends the session. The lanes are a layout of their own (kept in the project's ui-state):
// nothing here changes the song or its arrangement.
// The launch rules live in ./performance.js (createPerformance), the layout rules in ../lib/performance-layout.mjs, the sources in
// ../lib/performance-audio.mjs. While the setting is off, no tab, no section and no timer exist. @ample:performance-view
import { $, ask, el, toast } from './ui.js';
import { api } from './api.js';
import { S, editAll, flat } from './store.js';
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
import { createClipPlayer } from '../lib/performance-audio.mjs';
import {
  addClip, addLane, addScene, clickAction, clipCount, clipOptions, emptyLayout, parseLayout, pruneLayout,
  LANE_MODES, removeClip, removeLane, removeScene, sceneChoice, serializeLayout, setLaneMode, setSceneEntry, statusText,
} from '../lib/performance-layout.mjs';
import { createEngineSceneQueue } from './performance-scenes.js';
import { attachPerformanceInput, bindingsList, clearBinding, detachPerformanceInput, learnAction, onBindingsChange } from './performance-input.js';
import { performanceMixerPanel } from './performance-mixer.js';
import { actionText, clipActionId, parseClipActionId } from '../lib/performance-actions.mjs';

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

const allClips = () => {
  const seen = new Map();
  for (const lane of layout.lanes) for (const clip of lane.clips) if (!seen.has(clip.id)) seen.set(clip.id, { id: clip.id, label: clip.label });
  return [...seen.values()];
};
const bufferOfClip = (id) => { const take = (S.build.takes || {})[id]; if (!take) return Promise.reject(new Error('Build the block first: it has no take yet.')); return bufferOfTake(take); };
const extras = createExtras({
  clips: allClips,
  firstClips: () => layout.lanes.filter((lane) => lane.clips.length).map((lane) => lane.clips[0]),
  choices: () => clipOptions(flat().nodes, S.build.takes || {}),
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
  stop: () => { if (session) { session.scenes.stopAll(); session.accents.stopAll(); paint(); } },
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
    const bpm = S.doc.project?.tempo || 120;
    const beatsPerBar = Math.max(1, Math.round(barBeats()));
    const base = createPerformance({
      now: () => ctx.currentTime,
      play: player.play,
      bpm,
      beatsPerBar,
      quantize,
    });
    for (const lane of layout.lanes) for (const c of lane.clips) base.addClip(lane.name, { id: c.id, label: c.label });
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
    const accentLane = new Map(layout.lanes.filter((lane) => lane.mode !== 'loop').flatMap((lane) => lane.clips.map((c) => [c.id, lane.name])));
    const accents = createLaunchScheduler({
      startVoice: ({ id }, when) => player.play({ id, label: id, loop: false }, when, accentLane.get(id)),
      quantize: grid,
      now: () => ctx.currentTime,
    });
    for (const lane of layout.lanes) {
      if (lane.mode === 'loop') continue;
      for (const c of lane.clips) accents.configure(c.id, { mode: lane.mode, length: lane.mode === 'oneshot' ? (buffers.get(c.id)?.duration ?? 0) : 0 });
    }

    session = { ctx, engine, player, scenes, accents, recording: false };
    // pads and keys launch the same way the buttons do: through launchAction
    attachPerformanceInput({ launch: launchAction, active: () => Boolean(session) && Boolean($('#b-performance')?.classList.contains('on')) });
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
  try { session.scenes.stopAll(); session.accents.stopAll(); } catch { /* nothing was scheduled */ }
  if (session.recording) toast('Recording stopped: the performed run was not kept.');
  extras.dispose();
  detachPerformanceInput();
  session.player.releaseAll();
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
    button.setAttribute('aria-label', `${button.dataset.label} on ${button.dataset.lane}: ${statusText(status)}`);
    const text = button.querySelector('.perf-state');
    if (text) text.textContent = statusText(status);
  }
  const now = busy();
  if (now !== wasBusy) { wasBusy = now; notifyPlayer(); }
}

/** Record: from now on the launches that the clock confirms are kept; stopping offers to put the run in the Playlist (issue 279). */
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
const isLocked = () => Boolean(session) || entering;

function clipCell(lane, clip) {
  const status = statusOf(lane.name, clip.id);
  const grid = quantize === QUANTIZE.bar ? 'bar' : 'beat';
  const button = el('button', {
    type: 'button',
    className: 'perf-clip',
    title: `Launch at the next ${grid}; press again while queued to take it back`,
    onclick: () => { if (!isAccent(lane.name)) clickClip(lane.name, clip.id); },
  }, [el('strong', { textContent: clip.label }), el('span', { className: 'perf-state', textContent: statusText(status) })]);
  Object.assign(button.dataset, { lane: lane.name, clip: clip.id, label: clip.label, status });
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
  const mode = el('select', { className: 'perf-mode', 'aria-label': `${lane.name}: how it plays`, disabled: isLocked(), title: isLocked() ? LOCK_HINT : 'A loop plays until it is switched; a one-shot plays once; a momentary sound plays while its pad is held' },
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
    el('div', { className: 'perf-laneHead' }, [el('strong', { textContent: lane.name }), mode, stopAtBar, removeLaneButton]),
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
  const leaveButton = el('button', { type: 'button', className: 'perf-leave', textContent: 'Leave Performance', onclick: async () => { if (session?.recording) await toggleRecording(); leave(); } });
  const recordButton = el('button', {
    type: 'button',
    className: `perf-record${session?.recording ? ' on' : ''}`,
    textContent: session?.recording ? 'Stop recording' : 'Record',
    disabled: !session,
    'aria-pressed': String(Boolean(session?.recording)),
    title: 'Keep the clips you launch, at the time they really started, and offer them as Playlist parts',
    onclick: () => toggleRecording().catch((error) => toast(error.message || String(error))),
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
    ? 'Performance is on: the song and its arrangement stay as they are.'
    : 'Lanes and clips stay in this project; the arrangement is not touched.';
  return el('div', { className: 'perf-head' }, [
    session ? leaveButton : enterButton,
    stopButton,
    recordButton,
    el('label', { className: 'perf-q' }, [el('span', { textContent: 'Launch on ' }), grid]),
    el('span', { className: 'note', textContent: note }),
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

function sceneRow(scene) {
  const picks = layout.lanes.map((lane) => {
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
  const launch = el('button', {
    type: 'button',
    className: 'perf-scene',
    textContent: `Scene: ${scene.name}`,
    title: 'Queue this scene for the next bar: every lane changes together',
    onclick: () => queueScene(scene.id),
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
  return el('div', { className: 'perf-scene-row', role: 'group', 'aria-label': `Scene ${scene.name}` }, [launch, ...picks, bind, remove]);
}

function scenesSection() {
  const name = el('input', { type: 'text', maxLength: 32, placeholder: 'Scene name, for example Chorus', 'aria-label': 'New scene name', disabled: isLocked() });
  const form = el('form', { className: 'perf-add', onsubmit: (event) => { event.preventDefault(); applyLayout(addScene(layout, name.value)); } }, [
    name, el('button', { type: 'submit', textContent: 'Add scene', disabled: isLocked() || !layout.lanes.length, title: layout.lanes.length ? '' : 'Add a lane first' }),
  ]);
  return el('section', { className: 'perf-scenes', 'aria-label': 'Scenes' }, [
    el('h4', { textContent: 'Scenes' }),
    el('p', { className: 'note perf-pending', role: 'status' }),
    ...layout.scenes.map(sceneRow), form,
  ]);
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
    const kept = pruneLayout(layout, flat().nodes.map((n) => n.id));
    if (kept.dropped.length) { layout = kept.layout; save(); }
  }
  const options = clipOptions(flat().nodes, S.build.takes || {});
  const empty = S.doc ? 'Add a lane, then put built blocks on it.' : 'Open a song to set up its lanes.';
  const lanes = layout.lanes.length
    ? layout.lanes.map((lane) => laneRow(lane, options.filter((o) => !lane.clips.some((c) => c.id === o.id))))
    : [el('p', { className: 'note', textContent: empty })];
  box.replaceChildren(headBar(), el('div', { className: 'perf-lanes' }, lanes), addLaneForm(), scenesSection(), padsSection(), mixerSection(), extras.readinessSection(), extras.decksSection(), extras.rehearsalSection());
  box.setAttribute('aria-label', 'Performance');
  if (session) paint();
}

// the clips of another song are not this song's: a song really opened ends the session and reads the layout of the project again
document.addEventListener('ample:song-opened', () => { leave(); if (loaded) fromStore(); render(); });
