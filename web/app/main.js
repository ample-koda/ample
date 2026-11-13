// Starts the app: token, project, panels, header buttons, event stream. @ample:layout

import { $, showMenu, toast } from './ui.js';
import { api, connectEvents, initToken } from './api.js';
import { S, bakeDynamicHelpers, quiet, redo, build, dirty, flat, graphBroken, loadHistory, loadProject, notify, onBlockProgress, onBuildProgress, openDoc, readOnly, refreshInputs, reloadDoc, save, select, stopBuild, subscribe, subscribeSelection, undo, errors } from './store.js';
import { applyView, initCanvas, paintSelection, renderGraph } from './canvas.js';
import { initKeymap } from './keymap.js';
import { initMixerView } from './mixer-view.js';
import { initBuildProgress } from './build-progress.js';
import { refreshMounts, renderBottom, renderFiles, renderLib, renderPlugins, renderProps, renderStructure, setBottomTab } from './panels.js';
import { initLayout } from './layout.js';
import { initHistory, renderBranches, renderDetail, renderEditHistory, renderHistory, renderHistoryHeader } from './history.js';
import { renderVariants } from './variants.js';
import { initArrangement, previewSong, renderArrangement } from './arrangement.js';
import { initPlayLights } from './play-fx.js';
import { stopTranscription } from './transcription.js';
import { V, initViews, setView, updateBanner } from './views.js';
import { initTabs } from './tabs.js';
import { initSource, openProjectTextFile, resetSourceFile, confirmLeaveTextFile } from './source.js';
import { initProjectPanel, refreshProjectPanel, revealInProject } from './projectpanel.js';
import { createSaveControl } from './savecontrol.js';
import { mountStudioGallery } from './studio/index.js';
import { initLyrics, openLyrics } from './lyrics.js';
import { initPluginUi } from './pluginui.js';
import { initPatternView } from './patternview.js';
import { initSequencer } from './sequencer.js';
import { initLibraryUi, refreshLibrary } from './libraryui.js';
import { initBranchManager, renderBranchManager } from './branchmgr.js';
import { initProjects } from './projects.js';
import { onPlayer, play, playerState, playProjectFile, setLoop, stop } from './player.js';
import { initVolumeUi } from './volume-ui.js';
import { initHelpMenu } from './about.js';
import { initSelectionGuard } from './selection-guard.js';
import { initMobile, initMobileCanvas } from './mobile.js';
import { renderMidiPropertyTools } from './midi-editors.js';
import { renderTemplates } from './templates.js';
import { initKoda, toggleKoda } from './koda.js';
import { initKeysDock } from './keys-dock.js';
import { buildButton, failedBadge, masterTakeOf, playButton, playingLabel, runningText, songState, stopButton } from '../lib/transport-model.mjs';
import { liveActive, onLiveChange, stopLive } from './component-live.js';
import './live-chain-ui.js';   // the bar of the live chain (the chain itself starts from the Studio window of an effect)
import { buildAndPlayClientPreview, cancelClientPreview, clientPreviewProgress, clientPreviewRunning } from './clientside-preview.js';

const masterTake = () => masterTakeOf(S.build.takes || {}, flat().nodes.map((n) => n.id), flat().nodes);

const songStatus = () => songState(flat().nodes, S.build.takes || {}, S.build.status || {});
const transportContext = () => ({ hasDoc: !!S.doc, peek: !!S.peek && !S.peek.ai, errors: S.doc ? S.doc.diagnostics.filter((d) => d.severity === 'error').length : 0, building: !!S.build.running || clientPreviewRunning() });

// the one Stop button (cancels a build, otherwise stops the audio), the Play button and the line that says what plays
function renderPlaybackState(state = playerState()) {
  const button = $('#btn-stop');
  if (!button) return;
  const hearing = state.playing || !!state.loading || !!S.aiPlaying || liveActive();   // a live preview of a component is something Stop ends too   // pending playback can be cancelled with Stop too
  const clientBuilding = clientPreviewRunning() && !S.build.running;
  const stopper = stopButton({ building: !!S.build.running || clientPreviewRunning(), playing: hearing });
  button.textContent = clientBuilding ? '■ Cancel preview' : stopper.label === '■ Stop' ? '■' : stopper.label;
  button.setAttribute('aria-label', clientBuilding ? 'Cancel client preview' : stopper.label.replace('■ ', ''));
  button.title = clientBuilding ? 'Cancel the client-side preview currently being prepared.' : stopper.title;
  button.disabled = stopper.disabled;
  button.dataset.mode = stopper.mode;
  button.classList.toggle('playing', hearing);
  button.setAttribute('aria-pressed', String(hearing));
  const loop = $('#btn-loop');
  loop.classList.toggle('on', state.loop);
  loop.setAttribute('aria-pressed', String(state.loop));
  loop.setAttribute('aria-checked', String(state.loop));
  loop.textContent = `${state.loop ? '✓ ' : ''}⟳ Loop`;
  const text = playingLabel(state, S.build.takes || {}, masterTake());
  const label = $('#b-playing');
  const shown = text || (S.aiPlaying ? '▶ the AI proposal' : '');
  label.style.display = shown ? '' : 'none';
  label.textContent = shown;
}

function renderTransport() {
  const ctx = transportContext(), state = songStatus();
  const b = buildButton(ctx);
  $('#btn-build').disabled = b.disabled;
  $('#btn-build').title = `${b.title} (Ctrl+B)`;
  const p = playButton(ctx, state, masterTake());
  const play = $('#btn-preview');
  play.textContent = p.label === '▶ Play' ? '▶' : p.label;
  play.setAttribute('aria-label', p.label.replace('▶ ', ''));
  play.title = `${p.title} (Space)`;
  play.disabled = p.disabled;
  play.dataset.action = p.action;
  const bake = $('#btn-bake-dyn');
  if (bake) bake.style.display = !S.peek && !S.build.running && Object.keys(S.build.status || {}).length && S.doc?.bakeableClock ? '' : 'none';
  const clientPreview = $('#btn-build-play-preview');
  if (clientPreview) clientPreview.disabled = p.disabled || clientPreviewRunning() || !!S.peek;
  const range = $('#btn-play-range');
  range.style.display = S.range ? '' : 'none';
  if (S.range) {
    const last = S.range.firstBar + S.range.bars - 1;
    range.textContent = `▶ bars ${S.range.firstBar}–${last}`;
    range.title = `Build and play only bars ${S.range.firstBar} to ${last} (the bars chosen in the arrangement ruler).`;
    range.disabled = p.disabled;
  }
  const badge = failedBadge(state);
  const failed = $('#b-failed');
  failed.style.display = badge && !S.build.running ? '' : 'none';
  if (badge) { failed.textContent = badge.text; failed.title = badge.title; }
}

/** Play the built song, or build what is out of date first and then play it (the Play button and the space bar). */
async function playSong() {
  const p = playButton(transportContext(), songStatus(), masterTake());
  if (p.disabled) { toast(p.title); return; }
  if (p.action === 'play') { try { await play(masterTake()); } catch (e) { if (!e.toasted) toast(e.message); } return; }
  await previewSong();
}

/** Stop: cancel the build when one runs, otherwise stop the audio. */
function stopSomething() {
  stopLive();   // a live preview of a component (it has no take in the player)
  import('./ai.js').then((m) => m.stopPreview()).catch(() => {});   // the audio of an AI proposal comes through its own player
  cancelClientPreview();
  if (S.build.running) { stopBuild(); return; }
  stop(); stopTranscription();
}

function renderHeader() {
  const noSong = !S.path;   // no song loaded: the source editor is closed and the empty canvas gets its own pattern
  $('#vs-source').disabled = noSong;
  $('#vs-source').title = noSong ? 'Open a song first: there is no source to edit yet' : '';
  $('#canvaswrap').classList.toggle('nosong', noSong);
  if (noSong && V.mode === 'src') setView('blocks', true);
  $('#b-dirty').style.display = dirty() && !S.peek ? '' : 'none';
  $('#btn-undo').disabled = !S.undo.length || !!S.peek;
  $('#btn-redo').disabled = !S.redo.length || !!S.peek;
  $('#btn-save').disabled = !dirty() || !!S.peek;
  renderTransport();
  renderPlaybackState();
  ['btn-cp', 'btn-promote', 'btn-fork'].forEach((id) => { $(`#${id}`).disabled = !!S.peek || !S.history; });
  $('#ro-file').textContent = S.peek?.ai ? `Proposal · ${S.peek.targetPath || S.path || 'The song'}` : S.modulePath || S.path || 'The song';
  updateBanner();
  const done = S.build.completed?.size || 0;
  const total = S.build.queued?.size || 0;
  const preview = clientPreviewRunning() ? clientPreviewProgress() : null;   // the preview shows its own progress also while it waits for a server render
  const server = S.build.running ? total ? `· server render ${done}/${total}${runningText(S.build.live)}`.replace(/^/, ' ') : ' · server render' : '';
  $('#bfill').style.width = preview ? `${Math.round(Math.min(1, preview.done.size / Math.max(preview.total, 1)) * (preview.phase ? 1 : 0.95) * 100)}%` : S.build.running ? `${Math.round((done / Math.max(total, 1)) * 100)}%` : '0';
  $('#btxt').textContent = preview ? (preview.phase ? `preview · ${preview.phase}` : `preview ${Math.min(preview.done.size, preview.total)}/${preview.total}${preview.active.length ? ` · ${preview.active.at(-1)}` : ''}${server}`) : S.build.running ? `building ${done}/${total || '?'}${runningText(S.build.live)}` : clientPreviewRunning() ? 'preparing client preview' : 'idle';
  $('#btxt').title = preview ? (preview.phase ? `Preview: ${preview.phase}` : `Preview: ${preview.done.size} of ${preview.total} blocks ready. Working on: ${preview.active.join(', ') || 'the next block'}${server ? '. A block is being rendered on the server.' : ''}`) : '';
  renderHistoryHeader();
}

function renderAll() {
  renderHeader();
  renderGraph();
  renderProps();
  renderMidiPropertyTools();
  renderBottom();
  renderFiles();
  renderPlugins();
  renderHistory();
  renderEditHistory();
  renderDetail();
  renderBranches();
  renderVariants();
  renderArrangement();
  // the Playlist is a view behind a setting: while it is off nothing of it is loaded
  if (localStorage.getItem('ample.playlist.on') === '1') import('./playlist.js').then((m) => { m.syncTab(); m.renderPlaylist(); }).catch((e) => console.error('playlist', e));
  if (V.mode === 'bm') renderBranchManager();
  if ($('#pane-struct').style.display !== 'none') renderStructure();
}

async function openProjectFile(path) {
  const extension = path.slice(path.lastIndexOf('.')).toLowerCase();
  const node = flat().nodes.find((item) => item.params?.file === path);
  if (['.wav', '.flac', '.mp3', '.ogg', '.m4a', '.aif', '.aiff'].includes(extension)) { await playProjectFile(path); return true; }
  if (['.mid', '.midi'].includes(extension)) {
    const details = await api.get(`/api/midi/info?file=${encodeURIComponent(path)}`);
    if (node) select(node.id);
    toast(`${details.tracks} MIDI tracks · ${Math.round(60_000_000 / details.microsecondsPerQuarter)} BPM · ${details.bars} bars · ${details.lengthTicks} ticks`);
    return true;
  }
  // a sequence file: the sequencer of the block that uses it, or the text of the file when no block uses it yet
  if (extension === '.seq' || extension === '.ampleseq') { const { getRecoveryDraft } = await import('../lib/recovery-drafts.mjs'); const draft = getRecoveryDraft(localStorage, S.project?.id || '', path); if (draft) return openProjectTextFile(path);   // unsaved text (from Koda, or typed) is shown as text, not hidden behind the sequencer
    // in the Source view a tab of a sequence file shows its text there (switching between the song and its sequence file); elsewhere it opens the sequencer
    if (node?.tpl === 'sequencer' && V.mode !== 'src') { (await import('./sequencer.js')).openSequencer(node.id); return true; } return openProjectTextFile(path); }
  if (['.lyr', '.lrc', '.amplelrc'].includes(extension) && node?.tpl === 'lyrics') { await openLyrics(node); return true; }
  if (['.lyr', '.lrc', '.amplelrc', '.srt', '.txt', '.chords', '.md', '.csv', '.json', '.amplec'].includes(extension)) return openProjectTextFile(path);
  toast(`${path} is selected in the Project panel; no editor is registered for this file type.`);
  return false;
}

/** Every open request (an ample:open or ample:open-file event) gets one answer, ample:open-result { path, ok, request }, so the one who asked keeps or takes back its own request (tabs.js). */
const answerOpen = (request, ok) => document.dispatchEvent(new CustomEvent('ample:open-result', { detail: { path: request.detail, ok, request } }));

function wire() {
  $('#btn-save').onclick = save;
  $('#save-ctl-top')?.append(createSaveControl({ compact: true }));
  $('#btn-undo').onclick = undo;
  $('#btn-redo').onclick = redo;
  $('#btn-build').onclick = () => build();
  $('#btn-bake-dyn').onclick = () => bakeDynamicHelpers();
  $('#btn-stop').onclick = stopSomething;
  $('#btn-preview').onclick = () => playSong();
  $('#btn-loop').onclick = () => { const on = !playerState().loop; setLoop(on); import('./ai.js').then((m) => m.setPreviewLoop(on)); };
  const popup = (button, menu) => {
    const close = () => { menu.hidden = true; button.setAttribute('aria-expanded', 'false'); };
    button.onclick = () => { menu.hidden = !menu.hidden; button.setAttribute('aria-expanded', String(!menu.hidden)); };
    document.addEventListener('pointerdown', (event) => { if (!button.parentElement.contains(event.target)) close(); });
    button.parentElement.addEventListener('keydown', (event) => { if (event.key === 'Escape') { close(); button.focus(); } });
  };
  popup($('#btn-play-options'), $('#play-options'));
  $('#btn-build-play-preview').onclick = () => { $('#play-options').hidden = true; $('#btn-play-options').setAttribute('aria-expanded', 'false'); buildAndPlayClientPreview(); };
  popup($('#btn-volume'), $('#play-volume'));
  initVolumeUi();
  $('#btn-play-range').onclick = () => { if (S.range) previewSong(S.range); };
  const openLog = () => $('#btabs button[data-t="log"]')?.click();
  $('#b-failed').onclick = openLog;
  $('#b-failed').onkeydown = (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openLog(); } };
  // Space plays or stops, Ctrl+B builds (Ctrl+Enter already applies the source editor); never while typing or in a dialog
  document.addEventListener('keydown', (e) => {
    if (e.defaultPrevented || e.altKey || document.querySelector('dialog[open]')) return;
    const target = e.target, typing = target instanceof HTMLElement && (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT', 'BUTTON', 'A'].includes(target.tagName) || !!target.closest?.('.cm-editor, [role="slider"]'));
    if (e.key === ' ' && !e.ctrlKey && !e.metaKey && !typing) { e.preventDefault(); if (playerState().playing) stop(); else playSong(); }
    else if ((e.key === 'b' || e.key === 'B') && e.ctrlKey && !e.shiftKey) { e.preventDefault(); if (!$('#btn-build').disabled) build(); }
  });
  initHistory();
  initKeymap();
  initMixerView();
  initBuildProgress();
  initHelpMenu();
  initSelectionGuard();
  initMobile();
  initViews();
  initSource();
  initLyrics();
  initPluginUi();
  initPatternView();
  initSequencer();
  initLibraryUi();
  initBranchManager();
  initProjects();
  onPlayer(renderPlaybackState);
  onLiveChange(() => renderPlaybackState());
  document.addEventListener('ample:open', async (e) => {
    const path = e.detail;
    try {
      if (!(await confirmLeaveTextFile(path))) { answerOpen(e, false); return; }   // unapplied text of a Source file is asked about before anything is reset
      resetSourceFile();
      await openDoc(path);
      answerOpen(e, S.path === path);   // a refused or overtaken open leaves another song in the editor
    } catch (x) { toast(x.message); answerOpen(e, false); }
  });
  document.addEventListener('ample:open-file', (e) => openProjectFile(e.detail).then((opened) => answerOpen(e, opened !== false), (x) => { toast(x.message); answerOpen(e, false); }));
  document.addEventListener('ample:reveal-file', (e) => revealInProject(e.detail));
  $('#lib-q').oninput = renderLib;
  document.addEventListener('ample:components-changed', () => { renderLib(); import('./component-library.js').then((m) => m.refreshComponentLibraryIfOpen()).catch(() => {}); });
  document.querySelectorAll('#ltabs button').forEach((b) => {
    b.onclick = () => {
      document.querySelectorAll('#ltabs button').forEach((x) => x.classList.toggle('on', x === b));
      $('#pane-lib').style.display = b.dataset.l === 'lib' ? '' : 'none';
      $('#pane-struct').style.display = b.dataset.l === 'struct' ? '' : 'none';
      $('#pane-tpl').style.display = b.dataset.l === 'tpl' ? '' : 'none';
      $('#pane-clib').style.display = b.dataset.l === 'clib' ? '' : 'none';
      document.querySelectorAll('#ltabs button').forEach((x) => x.setAttribute('aria-selected', String(x === b)));
      if (b.dataset.l === 'struct') renderStructure();
      if (b.dataset.l === 'tpl') renderTemplates();
      if (b.dataset.l === 'clib') import('./component-library.js').then((m) => m.renderComponentLibrary()).catch((x) => toast(x.message));
    };
  });
  document.querySelectorAll('#btabs button[data-t]').forEach((b) => { b.onclick = () => {
    if (b.classList.contains('on') && !$('#bottom').classList.contains('min')) { $('#b-min').click(); return; }
    if ($('#bottom').classList.contains('min')) $('#b-min').click();
    setBottomTab(b.dataset.t);
    if (b.dataset.t === 'assistant') import('./ai.js').then((m) => m.renderAssistantPanel());
  }; });
  document.querySelectorAll('[data-later]').forEach((b) => { b.title = `Arrives with ${b.dataset.later}`; });
}

async function start() {
  window.AMPLE_STATE = S;
  window.AMPLE_API = api;
  initToken();
  import('./ai.js').then((m) => m.aiStatus()).catch(() => {});
  mountStudioGallery();
  initLayout();
  initKoda();
  $('#btn-koda').onclick = () => toggleKoda();
  initKeysDock();
  initCanvas();
  initMobileCanvas();
  initArrangement();
  initPlayLights();
  wire();
  subscribe(renderAll);
  subscribeSelection(() => { paintSelection(); renderProps(); renderMidiPropertyTools(); if ($('#pane-struct').style.display !== 'none') renderStructure(); });
  connectEvents({
    'build.progress': onBuildProgress,
    'build.block': onBlockProgress,
    'doc.changed': (e) => { const version = e.Version ?? e.version; if (S.doc && !S.peek && !quiet.edits && version > S.doc.version) reloadDoc().catch(() => {}); refreshProjectPanel().catch(() => {}); },
    'files.changed': () => { refreshProjectPanel().catch(() => {}); refreshInputs().catch(() => {}); },
    'history.changed': () => { loadHistory().catch(() => {}); },
    'library.changed': () => { refreshLibrary().catch(() => {}); refreshMounts(); },
    'plugins.changed': () => { api.get('/api/plugins').then((p) => { S.plugins = p.plugins || []; notify(); }).catch(() => {}); },
    open: () => { if (S.project) { reloadDoc({ ifChanged: true }).catch(() => {}); loadHistory().catch(() => {}); } },
  });
  try {
    loadHistory().catch(() => {});   // the history graph is the slowest answer of the start (seconds on a big project) and nothing on the first screen needs it: it fills in when it arrives
    await loadProject();
    initProjectPanel();
    initTabs();
    renderLib();
    refreshMounts();
    if (graphBroken()) { setView('src', true); toast('This project opened with errors that stop the blocks from being read, so it starts in the code view. Fix them to use the Blocks view.'); }
    setTimeout(() => import('./storage.js').then((m) => m.storageHint()), 4000);
    setTimeout(() => import('./browser-view.js').then((m) => m.watchBrowserView()), 3000);   // the browser of the assistant shows here when it waits for a person (sign-in, a human check)
    api.get('/api/health').then((h) => { if ((h.pendingRegens || []).length || h.pendingRegens > 0) toast('A regeneration is waiting for your decision: select the block and look at its controls. The previous take is still the one in use.'); }).catch(() => {});
  } catch (e) {
    if (e.status !== 401 && e.code !== 'AMP5284') toast(`${e.code ? `${e.code}: ` : ''}${e.message}. Open the address that "ample serve" printed.`);   // a lost session has its own page or box: no second message
  }
  applyView();
  notify();
  document.body.dataset.ready = '1';
  hideBoot();
  await runFirstTimeWizard();
}

/** The loading overlay (plain HTML at the top of the page) fades away once the studio is ready for use; a failed start never leaves it standing. */
function hideBoot() {
  const boot = document.getElementById('boot');
  if (!boot || boot.classList.contains('done')) return;
  boot.classList.add('done');
  setTimeout(() => boot.remove(), 500);
}
setTimeout(hideBoot, 20000);

/** The first-time setup wizard (plan/first-run-wizard.md): while the project still has to do it, nothing opens by itself; at Finish a new project opens the demo song, builds it and plays it. */
async function runFirstTimeWizard() {
  try {
    const { firstRunState, runFirstRunWizard } = await import('./first-run.js');
    const state = await firstRunState();
    if (!state?.pending) return;
    const result = await runFirstRunWizard(state, S.project?.name || '');
    if (result.open) await openDoc(result.open);
    if (result.play) playSong().catch((e) => { if (!e.toasted) toast(e.message); });
  } catch (e) { console.error('first-run', e); }
}

start();
