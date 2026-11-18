// Live Preview in Remix: listen to the song, or to a range of bars of it, while you edit it. The song loops; when you stop typing it is built again (at most once
// every 12 seconds, so the shared server stays free for everybody), and the new sound comes in at the end of the loop while the last good build keeps playing when
// a build fails. The code that plays lights up. The session, the loop and the marks are the same as Studio's (web/lib/listen-*.mjs); what is Remix's is here: the
// build goes through the Lite server (the whole song, so a range of bars is cut from the render of the whole song, which is exact), and the page's own controls.
//
// What Listen is bound to: the song that is built (its path and its text as the build took it, not the file that is open in the editor), the selection of bars
// that is current when the sound is ready, and the project. Another song, or another project, ends the listening; a component or another text file may be
// open meanwhile (it is part of the build, and its edits build again), and the marks are drawn only in the file they belong to. @ample:listen-preview
import { alive, createListenBuilder, playLoaded } from '../lib/listen-cancel.mjs';
import { createListenSession } from '../lib/listen-session.mjs';
import { createLoopPlayer } from '../lib/listen-loop.mjs';
import { moveMarks, playbackMarks } from '../lib/listen-highlight.mjs';
import { activeBlocks, barAt, loopOf, meterBeats, placedBlocks, statusText } from '../lib/listen-remix.mjs';
import { tempoOf } from './lite-roll-source.mjs';

/** The time between two builds of Live Preview, and the pause after the last key before a change builds. */
export const MIN_GAP_MS = 12000, PAUSE_MS = 2500;
const STORE = 'ample-remix-listen';

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const load = () => { try { return JSON.parse(localStorage.getItem(STORE) || '{}'); } catch { return {}; } };
const save = (value) => { try { localStorage.setItem(STORE, JSON.stringify(value)); } catch { /* storage is off: the choice is for this visit only */ } };

/**
 * @param {object} host what the page gives
 * @param {(selector: string) => HTMLElement} host.$
 * @param {(options: { listen: true }) => Promise<any>} host.build the page's build; with listen it gives { ...result, blob, entry, entryText },
 *   the result when the build did not work, null when nothing was built
 * @param {() => string} host.getText the text of the file that is open in the editor
 * @param {() => string|null} host.activePath the path of the file that is open in the editor
 * @param {() => string|null} host.entryPath the path of the song a build would take now
 * @param {(path: string) => boolean} host.isSong whether a path is a song
 * @param {() => boolean} host.hasSong whether a song is open (the bar is there for a song)
 * @param {(marks: object[]) => void} host.setMarks puts marks in the editor
 * @param {() => string|null} host.buildError why the last build gave nothing
 * @param {() => boolean} host.isBuilding whether a build runs
 * @param {() => void} host.abortBuild cancels the build of Live Preview that runs (never a build of the page itself)
 * @param {() => void} host.pauseOutput pauses the page's own player (one sound at a time)
 * @param {(text: string, kind?: string) => void} host.toast
 * @param {{ pause?: number, gap?: number }} [options] the pause after the last key and the distance between two builds, in ms (the tests make them short)
 */
export function createRemixListen(host, { pause = PAUSE_MS, gap = MIN_GAP_MS } = {}) {
  const { $ } = host;
  const saved = load();
  const settings = { target: saved.target === 'bars' ? 'bars' : 'song', first: Math.max(1, Number(saved.first) || 1), last: Math.max(1, Number(saved.last) || 4) };
  // render: the newest sound that was decoded (it may not fit the selection of bars); last: the render that plays; entryPath: the song that is listened to
  const L = { context: null, nextAt: 0, render: null, last: null, entryPath: null, lastKey: '', timer: 0 };
  const context = () => (L.context ??= new (window.AudioContext || window.webkitAudioContext)());
  const loop = createLoopPlayer({ context, output: (c) => c.destination, beforeStart: () => host.pauseOutput() });

  const currentJob = () => (settings.target === 'bars'
    ? { kind: 'bars', firstBar: settings.first, lastBar: Math.max(settings.first, settings.last) }
    : { kind: 'song' });

  // ---- the builds of the session: wait for a build of the page itself, keep the distance of 12 seconds (also from one Listen to the next), then build and take
  // the sound. Only the build that Live Preview itself runs can be cancelled by Stop; the wait for the distance is not a build.
  async function waitForGap(signal) {
    while (Date.now() < L.nextAt) {
      if (!alive(signal)) return false;
      paint();
      await sleep(250);
    }
    return alive(signal);
  }
  const firstProblem = (result) => {
    const d = (result.diagnostics || []).find((x) => x.severity === 'error') || (result.diagnostics || [])[0];
    return d ? `${d.code || ''} ${d.message}`.trim() : 'The build did not work.';
  };
  const builder = createListenBuilder({
    isBusy: host.isBuilding,
    stopStore: host.abortBuild,
    beforeBuild: (job, signal) => waitForGap(signal),   // (job, signal): the token is the second argument
    buildNow: async (job, signal) => {
      L.nextAt = Date.now() + gap;
      const outcome = await host.build({ listen: true });
      if (!alive(signal)) return null;
      if (!outcome) throw new Error(host.buildError() || 'Nothing was built.');
      if (!outcome.ok) throw new Error(firstProblem(outcome));
      L.entryPath = outcome.entry;
      // the text of the song as the build took it: the tempo, the meter and the marks come from this, not from the file that is open now
      return { blob: outcome.blob, text: outcome.entryText ?? '', entry: outcome.entry, take: outcome.outputId };
    },
  });

  /** The loop of a render for the job: the whole render, or the bars cut out of it. Throws when the bars are not in the song. */
  function entryFor(render, job) {
    const quarters = meterBeats(render.text), barSeconds = (quarters * 60) / tempoOf(render.text);
    const cut = loopOf(render.buffer.duration, barSeconds, job);
    if (!cut) throw new Error(`The song has no bars ${job.firstBar}-${job.lastBar}: it is ${Math.floor(render.buffer.duration / barSeconds)} bars long.`);
    return {
      take: render.take, buffer: render.buffer, segments: cut.segments, loop: cut.loop, takeFirstBar: cut.firstBar,
      text: render.text, path: render.entry, job, barSeconds, quarters,
    };
  }

  /** The sound of a build as a loop, for the selection that is current NOW (it may have changed while the build and the download ran). */
  async function entryOf(result, signal, number) {
    loop.setLoading(true);
    try {
      const buffer = await context().decodeAudioData(await result.blob.arrayBuffer());
      if (!alive(signal)) return null;
      const render = { buffer, text: result.text, entry: result.entry, take: result.take, number };
      L.render = render;
      const entry = entryFor(render, currentJob());   // before it counts as the render that plays: a selection that does not fit leaves the last good one in place
      L.last = render;
      return entry;
    } finally { if (alive(signal)) loop.setLoading(false); }
  }

  const session = createListenSession({
    pause,
    build: (job, signal) => builder.build(job, signal),
    cancelBuild: () => builder.cancel(),
    play: (result, number, signal) => playLoaded({
      signal,
      load: (s) => entryOf(result, s, number),
      play: (entry, s) => loop.playEntry(entry, { timing: 'boundary', onStop: () => session.stop(), signal: s }),
    }),
    stopPlayback: () => { loop.setLoading(false); loop.stop({ quiet: true }); },
    onChange: () => paint(),
  });
  loop.onChange(() => paint());

  // ---- the marks: what plays now, on the text of the build that plays, moved to the text in the editor, and only in the file they belong to
  function updateMarks() {
    const st = session.state(), now = loop.state(), entry = now.entry;
    let marks = [];
    if (st.active && now.playing && entry && now.takeTime !== null && host.activePath() === entry.path) {
      const songBar = barAt(now.takeTime, entry.barSeconds), at = new Map(placedBlocks(entry.text).map((b) => [b.name, b.at]));
      const lit = playbackMarks({
        text: entry.text, target: null, songBar, active: activeBlocks(entry.text, Math.floor(songBar)), quartersPerBar: entry.quarters,
        barSeconds: entry.barSeconds, frame: () => ({ first: 1, offset: 0 }), clipFirstBar: (name) => at.get(name) || 1,
      });
      marks = moveMarks(lit, entry.text, host.getText());
    }
    const key = marks.map((m) => `${m.from}-${m.to}-${m.cls}`).join(',');
    if (key === L.lastKey) return;
    L.lastKey = key;
    host.setMarks(marks);
  }
  const startMarks = () => { if (!L.timer) L.timer = setInterval(updateMarks, 66); };
  const stopMarks = () => { clearInterval(L.timer); L.timer = 0; L.lastKey = ''; host.setMarks([]); };

  // ---- the bar
  function paint() {
    const bar = $('#lite-listen');
    if (!bar) return;
    const st = session.state(), song = host.hasSong();
    bar.hidden = !song;
    bar.dataset.status = st.status;   // what the session does and whether a sound really plays: for the page's own styling and for the tests
    bar.dataset.playing = String(loop.state().playing);
    const button = $('#lite-listen-play');
    button.textContent = st.active ? '■ Stop listening' : '▶ Listen';
    button.setAttribute('aria-pressed', String(st.active));
    $('#lite-listen-target').value = settings.target;
    $('#lite-listen-range').hidden = settings.target !== 'bars';
    $('#lite-listen-first').value = String(settings.first);
    $('#lite-listen-last').value = String(settings.last);
    const waitSeconds = st.active && st.status !== 'building' && st.pending ? Math.max(0, Math.ceil((L.nextAt - Date.now()) / 1000)) : 0;
    const text = statusText(st, { job: st.job || currentJob(), waitSeconds, loading: loop.state().loading });
    const line = $('#lite-listen-status');
    line.textContent = text;
    line.title = text;
    line.classList.toggle('bad', st.status === 'error');
  }

  function start() {
    if (!host.hasSong()) { host.toast('Open a song first.', 'error'); return Promise.resolve(); }
    host.pauseOutput();
    // L.nextAt stays: the distance between two builds counts from the last build, also over Stop and Listen again
    L.entryPath = host.entryPath();
    L.render = null; L.last = null;
    startMarks();
    paint();
    return session.start(currentJob()).catch((error) => host.toast(error?.message || String(error), 'error'));
  }
  function stop() { session.stop(); L.render = null; L.last = null; stopMarks(); paint(); }
  const toggle = () => (session.state().active ? stop() : start());

  /**
   * Another range or target: the sound that is there is cut again at once, nothing is built for it. The newest render goes first (a range that did not fit it
   * may fit now); the render that plays is the one to fall back on. A selection that fits neither is said.
   */
  async function retarget() {
    save(settings);
    const job = currentJob();
    session.retarget(job);
    if (!session.state().active || !loop.state().playing) { paint(); return; }
    const token = { get cancelled() { return !session.state().active; } };   // Stop during the resume of the audio context ends it
    let refused = null;
    for (const render of [...new Set([L.render, L.last])].filter(Boolean)) {
      let entry;
      try { entry = entryFor(render, job); } catch (error) { refused ??= error; continue; }
      if (!(await loop.playEntry(entry, { timing: 'now', onStop: () => session.stop(), signal: token }))) break;
      L.last = render;
      if (render.number !== undefined && render.number !== session.state().playing) session.recover(render.number);
      paint();
      return;
    }
    if (refused) host.toast(refused.message || String(refused), 'error');
    paint();
  }

  /** The selection changed (the controls call this): { target: 'song' | 'bars', first, last }. */
  function select(patch) {
    if (patch.target !== undefined) settings.target = patch.target === 'bars' ? 'bars' : 'song';
    for (const key of ['first', 'last']) if (patch[key] !== undefined) settings[key] = Math.max(1, Math.floor(Number(patch[key]) || 1));
    return retarget();
  }

  function wire() {
    $('#lite-listen-play').onclick = toggle;
    $('#lite-listen-target').onchange = (event) => { select({ target: event.target.value }); };
    const number = (id, key) => { $(id).onchange = (event) => { select({ [key]: event.target.value }); }; };
    number('#lite-listen-first', 'first');
    number('#lite-listen-last', 'last');
    document.addEventListener('keydown', (event) => {
      if (event.key === ' ' && event.ctrlKey && event.shiftKey && host.hasSong()) { event.preventDefault(); toggle(); }
    });
    paint();
  }

  return {
    wire,
    select,
    refreshMarks: updateMarks,
    paint,
    start,
    stop,
    /** Something of the project changed: build after the pause (only while listening). */
    changed() { if (session.state().active) session.changed(); },
    /** A file was opened in the editor: another song ends the listening (the loop belongs to the song that was built); a component or another text file does not. */
    fileOpened(path) {
      if (!session.state().active) { paint(); return; }
      if (host.isSong(path) && path !== L.entryPath) { stop(); host.toast('Listening stopped: another song was opened.'); return; }
      paint();
    },
    active: () => session.state().active,
    state: () => session.state(),
    player: () => loop.state(),
  };
}
