import { fieldFor } from '../fields.js';
import { rememberedSize, resizeGrip } from '../window.js';
import { el } from '../ui.js';
import { whyNotLive, liveClassOf } from '../../lib/live-class.mjs';
import { subscribe } from '../store.js';
import { knob } from './knob.js';
import { led } from './led.js';
import { selector } from './selector.js';
import { renderStudioPanel } from './panels/registry.js';
import './panels/eq-filter.js';
import './panels/peq.js';
import './panels/compressor-duck.js';
import './panels/time-effects.js';
import './panels/mixer.js';
import './panels/volume.js';
import './panels/denoise.js';
import './panels/rewind.js';
import './panels/autotune.js';
import './panels/vocoder.js';
import './panels/chop.js';
import './panels/delay-llama.js';
import './panels/limiter-pan-width.js';
import './panels/voice-effects.js';
import './panels/master.js';
import { masterAudioBuffer } from '../../lib/clientside-mix.mjs';
import { meterAudio } from '../../lib/clientside-meter.mjs';
import { onPlayer, peaksOf, play, playTransient, playLivePreview, updateLivePreview, replaceLivePreviewImpulse, setAuditionBlock, playerState, seekFraction, setLoop, setLoopRegion, stop } from '../player.js';
import { scrubBar } from './scrub.js';
const PARITY_TYPES = new Set(['reverb', 'gate', 'limiter', 'deesser', 'eq', 'peq', 'filter', 'compressor', 'delay', 'distortion', 'chorus', 'flanger', 'phaser', 'pan', 'width', 'volume', 'tremolo']);   // the blocks live-parity.js can compare
import { LIVE_BADGES, parameterLive } from '../../lib/live-parameters.mjs';
import { decodePreviewBlob, decodedInput, encodeWav, fetchTakeAudio, liveEffectGraph } from './preview/audio.js';
import { renderDenoisePreview } from './preview/denoise.js';
import { meter } from './meter.js';

let active = null;   // the window that was used last
const windows = new Map();   // block id -> window: several panels can be open at once, as floating windows
let topZ = 40, cascade = 0;

function numericValue(value, unit) {
  const text = String(value).trim().replace(unit || '\u0000', '').trim();
  return /^[-+]?(?:\d+\.?\d*|\.\d+)$/.test(text) ? Number(text) : null;
}

function panelState(state) {
  const panel = state;
  const takeId = typeof panel.getTakeId === 'function' ? panel.getTakeId() : panel.takeId;
  const preview = (name, value) => {
    panel.preview?.(name, value);
    if (state.previewMode !== 'live') return;
    state.liveParams[name] = value;
    if (panel.type === 'master') {
      if (name === 'target') return;
      clearTimeout(state.localRefreshTimer); state.denoiseAbort?.abort();
      state.localRefreshTimer = setTimeout(() => playLocalPreview(state, { ...state.liveParams, enabled: state.ab.getAttribute('aria-pressed') === 'true' ? 'off' : 'on' }, true).catch((error) => { if (error.name !== 'AbortError') state.status.textContent = error.message; }), 180);
    } else updateLivePreview({ ...state.liveParams, ...(state.ab.getAttribute('aria-pressed') === 'true' ? { enabled: 'off' } : {}) });
  };
  return panel === state ? { ...state, takeId, preview } : { ...panel, takeId, preview, commit: state.wrapCommit(panel.commit), commitMany: state.wrapCommitMany(panel.commitMany) };
}

const LOCAL_PREVIEW_TYPES = new Set(['eq', 'filter', 'compressor', 'duck', 'distortion', 'delay', 'reverb', 'volume', 'convolution', 'denoise', 'chorus', 'tremolo', 'pan', 'width', 'phaser', 'flanger', 'gate', 'limiter', 'deesser', 'master']);
function canRenderedPreview(panel) { return Boolean(panel.takeId && typeof panel.realPreview === 'function'); }
function canLocalPreview(panel) { return Boolean(panel.takeId && LOCAL_PREVIEW_TYPES.has(panel.type)); }

function applyPreviewLoop(state) { if (state.previewLoop && state.previewLoopRegion) setLoopRegion(state.previewLoopRegion); else setLoop(state.previewLoop); }

async function loadSelectedInput(state) {
  const panel = panelState(state);
  if (!panel.loadInput) return;
  try {
    await panel.loadInput();
    if (!state.dialog.open || panelState(state).block !== panel.block) return;
    renderBody(state); renderTransport(state);
  } catch (error) { if (state.dialog.open && state.status) state.status.textContent = error.message; }
}

function renderHeader(state) {
  const panel = panelState(state);
  state.titleElement.textContent = (panel.title || panel.type).toUpperCase();
  state.blockElement.textContent = `block ${panel.block}`;
  state.bypassHost.replaceChildren();
  state.ab.hidden = typeof panel.realPreview !== 'function' && panel.type !== 'master';
  state.ab.disabled = !canRenderedPreview(panel) && !(panel.type === 'master' && canLocalPreview(panel));
  state.ab.title = canRenderedPreview(panel) ? `Compare ${panel.title} with its bypassed input` : 'Select an effect and build its upstream input before comparing';
  if (panel.definitions.some((definition) => definition.name === 'enabled')) {
    state.bypassHost.append(led({ label: 'Effect enabled', title: 'Effect enabled: the light is lit while this block is on; press it to switch the block off (it passes the sound through)', value: (panel.getParams().enabled ?? 'on') === 'on', disabled: panel.readOnly, onCommit: (value) => panel.commit('enabled', value ? 'on' : 'off') }));
  }
}

// a stable key for the focused control: its place in the body plus its label, so focus survives the body being drawn again
function focusKey(body) {
  const focused = document.activeElement;
  if (!focused || focused === body || !body.contains(focused)) return null;
  const path = [];
  for (let node = focused; node && node !== body; node = node.parentNode) path.unshift(Array.prototype.indexOf.call(node.parentNode.children, node));
  const input = focused instanceof HTMLInputElement || focused instanceof HTMLTextAreaElement;
  return { path, label: focused.getAttribute('aria-label') || '', selection: input ? [focused.selectionStart, focused.selectionEnd] : null };
}
function restoreFocus(body, key) {
  if (!key) return;
  let node = body;
  for (const index of key.path) { node = node?.children?.[index]; if (!node) return; }
  if ((node.getAttribute('aria-label') || '') !== key.label || typeof node.focus !== 'function') return;
  node.focus({ preventScroll: true });
  if (key.selection && key.selection[0] != null) { try { node.setSelectionRange(key.selection[0], key.selection[1]); } catch { /* not a text field */ } }
}
const signatureOf = (state) => { const panel = panelState(state); return JSON.stringify([panel.getParams(), panel.takeId ?? null, panel.readOnly ?? false, panel.tempo ?? null, panel.title ?? '', panel.definitions.map((d) => d.name)]); };
const textFieldFocused = (state) => { const focused = document.activeElement; return Boolean(focused && state.body.contains(focused) && (focused instanceof HTMLTextAreaElement || (focused instanceof HTMLInputElement && ['text', 'number', 'search'].includes(focused.type)))); };

function renderBody(state) {
  const key = focusKey(state.body);
  renderBodyNow(state);
  state.signature = signatureOf(state);
  restoreFocus(state.body, key);
}

function renderBodyNow(state) {
  const body = state.body;
  body.querySelectorAll('.studio-eq-panel, .studio-live-panel').forEach((panel) => panel.dispose?.());
  body.replaceChildren();
  const panel = panelState(state);
  renderHeader(state);
  const params = panel.getParams();
  state.displayedParams = { ...params }; state.localChanges.clear();
  const definitions = panel.definitions;
  const bespoke = renderStudioPanel(panel.type, { type: panel.type, params, definitions, preview: panel.preview, commit: panel.commit, commitMany: panel.commitMany, block: panel.block, readOnly: panel.readOnly, tempo: panel.tempo, inputBuffer: panel.takeId ? () => decodedInput(`${panel.block}:${panel.takeId}`, () => fetchTakeAudio(panel.takeId)) : null, renderedBuffer: panel.takeId && panel.realPreview ? async (values) => decodePreviewBlob(await panel.realPreview(values, panel.takeId)) : null });
  if (bespoke) { body.append(bespoke); if (panel.type === 'master' && state.masterLevels) bespoke.setMasterLevels(state.masterLevels); }
  else definitions.forEach((definition) => {
    if (definition.name === 'enabled') return; // the header already shows the "Effect enabled" switch
    const value = params[definition.name] ?? definition.default ?? '';
    const controlOptions = { label: definition.label || definition.name, disabled: panel.readOnly, onPreview: (preview) => { if (panel.preview) panel.preview(definition.name, preview); }, onCommit: (next) => panel.commit(definition.name, next) };
    let control;
    if (definition.kind === 'choice' && definition.options?.length) {
      control = selector({ ...controlOptions, value, choices: definition.options.map((option) => ({ value: option, label: option })) });
    } else if (definition.kind === 'number' && numericValue(value, definition.unit) !== null) {
      const number = numericValue(value, definition.unit);
      const min = Number.isFinite(definition.min) ? definition.min : 0;
      const max = Number.isFinite(definition.max) && definition.max > min ? definition.max : Math.max(min + 1, number * 2);
      control = knob({ ...controlOptions, onCommit: (next) => panel.commit(definition.name, definition.unit ? `${next} ${definition.unit}` : String(next)), min, max, value: number, defaultValue: numericValue(definition.default, definition.unit) ?? number, step: definition.step, unit: definition.unit || '', scale: definition.scale || 'linear' });
    } else {
      control = fieldFor(definition.name, definition, String(value), { disabled: panel.readOnly, title: definition.doc || '', save: (next) => panel.commit(definition.name, next) });
      if (!control) { const input = el('input', { type: 'text', value: String(value), disabled: panel.readOnly, 'aria-label': definition.label || definition.name }); input.onchange = () => panel.commit(definition.name, input.value); control = input; }
    }
    const badge = LIVE_BADGES[parameterLive(panel.type, definition.name)];
    body.append(el('label', { className: 'studio-panel-control' }, [el('span', { textContent: definition.label || definition.name }), control, el('small', { className: `live-badge live-${parameterLive(panel.type, definition.name)}`, textContent: badge.text, title: badge.title })]));
  });
  addLiveBadges(body, panel);
  if (!definitions.length && !bespoke) body.append(el('p', { className: 'note', textContent: 'This block has no editable parameters.' }));
}

/** Under every control of the window that matches a parameter: whether it moves live, re-renders or needs a build (the controls of the bespoke panels are found by their label). */
function addLiveBadges(body, panel) {
  const byLabel = new Map(panel.definitions.flatMap((definition) => [[(definition.label || definition.name).toLowerCase(), definition.name], [definition.name.toLowerCase(), definition.name]]));
  body.querySelectorAll('.studio-panel-control').forEach((row) => {
    if (row.querySelector('.live-badge')) return;
    const name = byLabel.get(String(row.querySelector('span')?.textContent || '').replace(/s*(automated)$/, '').trim().toLowerCase());
    if (!name) return;
    const kind = parameterLive(panel.type, name), badge = LIVE_BADGES[kind];
    row.append(el('small', { className: `live-badge live-${kind}`, textContent: badge.text, title: badge.title }));
  });
}

// The input take is immutable and independent of this effect's controls, so download and decode it once per take for local preview.
function cachedTakeInput(state, panel) {
  if (state.takeInputCache?.takeId !== panel.takeId) {
    const blob = fetchTakeAudio(panel.takeId).catch((error) => { if (state.takeInputCache?.takeId === panel.takeId) state.takeInputCache = null; throw error; });
    state.takeInputCache = { takeId: panel.takeId, blob, decoded: null };
  }
  return state.takeInputCache;
}

async function playLocalPreview(state, params, preservePosition) {
  const generation = ++state.localGeneration;
  const panel = panelState(state);
  state.denoiseAbort?.abort();
  const denoiseAbort = ['denoise', 'master'].includes(panel.type) ? new AbortController() : null;
  state.denoiseAbort = denoiseAbort;
  state.liveParams = { ...params };
  state.status.textContent = 'Loading input for live local preview…';
  const cache = cachedTakeInput(state, panel);
  const [audio, impulse] = await Promise.all([cache.blob, panel.type === 'convolution' ? panel.loadImpulse(params).then(decodePreviewBlob) : null]);
  if (generation !== state.localGeneration || !state.dialog.open) return;
  state.auditioning = true;
  if (panel.type === 'master') {
    state.status.textContent = 'Preparing master preview on this device…';
    if (!cache.decoded) cache.decoded = decodePreviewBlob(audio);
    const decoded = await cache.decoded;
    const source = { sampleRate: decoded.sampleRate, channels: Array.from({ length: decoded.numberOfChannels }, (_, channel) => decoded.getChannelData(channel)) };
    const result = await masterAudioBuffer(source, params, Number(panel.tempo) || 120, denoiseAbort.signal);
    if (!result.channels[0].length) throw new Error('The master slice contains no input audio.');
    const input = cache.masterLevels || await meterAudio(source, {}, denoiseAbort.signal);
    cache.masterLevels = input;
    const output = await meterAudio(result, {}, denoiseAbort.signal);
    denoiseAbort.signal.throwIfAborted();
    if (generation !== state.localGeneration || !state.dialog.open) return;
    state.masterLevels = { input, output };
    state.body.querySelector('.studio-master-panel')?.setMasterLevels(state.masterLevels);
    const previous = playerState(); applyPreviewLoop(state);
    await playTransient(encodeWav({ numberOfChannels: result.channels.length, sampleRate: result.sampleRate, length: result.channels[0].length, getChannelData: (channel) => result.channels[channel] }));
    setAuditionBlock(panel.block);
    if (preservePosition && previous.duration > 0) seekFraction(previous.time / previous.duration);
    state.status.textContent = 'Client-side master preview · approximate · target is a reference.';
    return;
  }
  if (panel.type === 'denoise') {
    state.status.textContent = 'Filtering the preview on this device…';
    let filtered = audio;
    if (state.liveParams.enabled !== 'off') {
      if (!cache.decoded) cache.decoded = decodePreviewBlob(audio);
      filtered = await renderDenoisePreview(await cache.decoded, state.liveParams, denoiseAbort.signal);
    }
    if (state.denoiseAbort === denoiseAbort) state.denoiseAbort = null;
    if (generation !== state.localGeneration || !state.dialog.open) return;
    // captured only now, right before the swap: the previous preview kept playing during the fetch/decode/FFT above, so capturing it earlier
    // (before that wait) restored a stale, now-passed position and made the preview visibly jump backward on every change.
    const previous = playerState();
    applyPreviewLoop(state);
    await playTransient(filtered);
    setAuditionBlock(panel.block);
    if (generation !== state.localGeneration || !state.dialog.open) return;
    if (preservePosition && previous.duration > 0) seekFraction(previous.time / previous.duration);
    state.status.textContent = 'Client-side denoise preview · approximate.';
    return;
  }

  const previous = playerState();
  applyPreviewLoop(state);
  await playLivePreview(audio, (context, source, output) => liveEffectGraph(context, source, output, panel.type, state.liveParams, panel.tempo, impulse));
  setAuditionBlock(panel.block);
  if (generation !== state.localGeneration || !state.dialog.open) return;
  if (preservePosition && previous.duration > 0) seekFraction(previous.time / previous.duration);
  state.status.textContent = `Live local preview · ${panel.title} · approximate`;
}

async function playRenderedPreview(state, params, preservePosition) {
  const generation = ++state.localGeneration;
  const panel = panelState(state);
  state.status.textContent = `Rendering ${panel.title} on input from ${panel.inputTitle || 'the upstream block'}…`;
  const audio = await panel.realPreview(params, panel.takeId);
  if (generation !== state.localGeneration || !state.dialog.open) return;
  state.auditioning = true;
  const previous = playerState();   // captured after the render, not before: the previous preview kept playing during it
  applyPreviewLoop(state);
  await playTransient(audio);
  setAuditionBlock(panel.block);
  if (generation !== state.localGeneration || !state.dialog.open) return;
  if (preservePosition && previous.duration > 0) seekFraction(previous.time / previous.duration);
  state.status.textContent = `Rendered preview · ${panel.title}. Knob changes replay this preview after release.`;
}

function renderTransport(state) {
  state.unsubscribePlayer?.(); state.unsubscribePlayer = null; state.scrub = null;
  const panel = panelState(state);
  state.inputTakeId = panel.takeId;
  const footer = state.footer;
  footer.replaceChildren();
  // the rewind and autotune panels have their own play bar (Play, Loop, A/B, scrub) on top, worked out in the browser
  footer.hidden = panel.type === 'rewind' || panel.type === 'autotune' || panel.type === 'delay-llama' || panel.type === 'vocoder' || panel.type === 'chop';
  // these two windows are resized with a grip, like Jam: their graph grows with the window
  if (footer.hidden && !state.dialog.querySelector('.aw-grip')) {
    state.dialog.classList.add('studio-resizable');
    resizeGrip(state.dialog, `studio-${panel.type}`);
    const size = rememberedSize(`studio-${panel.type}`);
    if (size) { state.dialog.style.width = `${size.width}px`; state.dialog.style.height = `${size.height}px`; }
  }
  // their A/B is in that play bar too: the A/B of the header (which needs a rendered preview) is not shown for them
  state.ab.hidden = footer.hidden;
  if (footer.hidden) { state.status = el('span'); return; }
  const renderedAvailable = canRenderedPreview(panel), localAvailable = canLocalPreview(panel);
  const effectControls = typeof panel.realPreview === 'function' || panel.type === 'master';
  const previewKind = panel.type === 'denoise' ? 'client-side preview is approximate' : 'live preview is approximate';
  const noAudioOfItsOwn = ['visual', 'data'].includes(liveClassOf(panel.type));
  const statusText = noAudioOfItsOwn ? `No audio of its own: ${whyNotLive(panel.type)}` : panel.takeId ? localAvailable ? `Input: ${panel.inputTitle || 'upstream audio'} · ${previewKind}` : `Ready to play${whyNotLive(panel.type) ? ` · ${whyNotLive(panel.type)}` : ''}` : panel.buildInput ? 'Build audio to play' : 'Connect an audio input';
  const status = el('span', { className: 'note', textContent: statusText });
  state.status = status;
  if (!panel.takeId) {
    const inputPlay = el('button', { type: 'button', textContent: panel.playLabel || 'Play input', disabled: true, 'aria-label': 'Play the selected audio take', title: panel.buildLabel === 'Build block' ? 'Build this block first' : 'Build the upstream input first' });
    const local = el('button', { type: 'button', textContent: 'Live local preview', disabled: true, title: panel.buildLabel === 'Build block' ? 'Build this block first' : 'Build the upstream input first' });
    const rendered = el('button', { type: 'button', textContent: 'Play rendered effect', disabled: true, title: panel.buildLabel === 'Build block' ? 'Build this block first' : 'Build the upstream input first' });
    if (panel.buildInput) {
      const buildInput = el('button', { type: 'button', className: 'studio-build-input', textContent: panel.buildLabel || 'Build input', disabled: panel.readOnly });
      buildInput.onclick = async () => {
        buildInput.disabled = true; status.textContent = panel.buildLabel === 'Build block' ? 'Building this block…' : 'Building the upstream input…';
        // the input is built when the window opens: Cancel stops waiting for it (the build itself goes on in the background and is used the next time)
        state.buildCancelled = false;
        const cancel = el('button', { type: 'button', className: 'studio-cancel-build', textContent: 'Cancel', title: 'Stop waiting for the build', onclick: () => { state.buildCancelled = true; cancel.remove(); status.textContent = 'Cancelled. Press Build to use the input when it is ready.'; buildInput.disabled = false; } });
        status.after(cancel);
        try { const take = await panel.buildInput(); if (state.buildCancelled) return; cancel.remove(); state.takeId = take || state.takeId; renderBody(state); renderTransport(state); }
        catch (error) { cancel.remove(); if (state.buildCancelled) return; status.textContent = error.message; buildInput.disabled = false; }
      };
      if (!effectControls) footer.append(inputPlay); footer.append(buildInput, status);
    } else {
      if (!effectControls) footer.append(inputPlay); footer.append(status);
    }
    return;
  }
  const inputPlay = el('button', { type: 'button', textContent: panel.playLabel || 'Play input', 'aria-label': 'Play the selected audio take' });
  const local = el('button', { type: 'button', textContent: 'Live local preview', 'aria-label': panel.type === 'denoise' ? 'Client-side denoise preview' : 'Live local preview', disabled: !localAvailable, title: panel.type === 'denoise' ? 'Denoise the bounded preview audio on this device; approximate, not bit-exact' : localAvailable ? 'Play this input through a live local effect; knobs change the sound immediately (approximate model)' : 'Select an effect and build its upstream input first' });
  const rendered = el('button', { type: 'button', textContent: 'Play rendered effect', disabled: !renderedAvailable, title: renderedAvailable ? `Play ${panel.title} rendered by AMPLE's integer DSP on its upstream input` : 'Select an effect and build its upstream input first' });
  const levelMeter = meter({ label: 'Live output level', min: 0, max: 1, value: 0, peak: 0 });
  const scrubHost = el('span', { className: 'studio-scrub-placeholder', textContent: 'Loading waveform…', 'aria-live': 'polite' });
  const loop = el('button', { type: 'button', textContent: '↻ Loop', title: 'Repeat the preview sample; Shift-drag the waveform to choose a loop range', 'aria-pressed': String(state.previewLoop), className: state.previewLoop ? 'on' : '' });
  loop.onclick = () => { state.previewLoop = !state.previewLoop; state.loopChanged = true; if (!state.previewLoop) state.previewLoopRegion = null; applyPreviewLoop(state); state.scrub?.setScrubLoop(state.previewLoopRegion); loop.setAttribute('aria-pressed', String(state.previewLoop)); loop.classList.toggle('on', state.previewLoop); };
  inputPlay.onclick = () => { clearTimeout(state.localRefreshTimer); state.denoiseAbort?.abort(); state.denoiseAbort = null; state.localGeneration++; state.previewMode = null; state.localPreviewActive = false; if (playerState().playing && playerState().take === panel.takeId) { stop(); return; } applyPreviewLoop(state); state.startedPlayback = true; play(panel.takeId).catch((error) => { status.textContent = error.message; }); };
  local.onclick = async () => {
    if (state.previewMode === 'live' && playerState().playing) { state.denoiseAbort?.abort(); state.denoiseAbort = null; stop(); state.previewMode = null; state.auditioning = false; status.textContent = statusText; return; }
    local.disabled = true; state.localPreviewActive = true; state.previewMode = 'live';
    try {
      const params = { ...panel.getParams() }; if (state.ab.getAttribute('aria-pressed') === 'true') params.enabled = 'off';
      await playLocalPreview(state, params, false);
    } catch (error) { if (error.name !== 'AbortError') status.textContent = error.message; }
    finally { local.disabled = !canLocalPreview(panel); }
  };
  rendered.onclick = async () => {
    state.denoiseAbort?.abort(); state.denoiseAbort = null; rendered.disabled = true; state.previewMode = 'rendered'; state.localPreviewActive = false;
    try { const params = { ...panel.getParams() }; if (state.ab.getAttribute('aria-pressed') === 'true') params.enabled = 'off'; await playRenderedPreview(state, params, false); }
    catch (error) { state.auditioning = false; status.textContent = error.message; }
    finally { rendered.disabled = !canRenderedPreview(panel); }
  };
  // an effect without a live model: the main Play button plays the input take (it stayed greyed out while the status said "Ready to play")
  if (effectControls && !localAvailable && panel.takeId) {
    local.disabled = false;
    local.title = 'Play the input of this block';
    local.setAttribute('aria-label', 'Play the input of this block');
    local.onclick = inputPlay.onclick;
  }
  if (effectControls) {
    const more = el('details', { className: 'studio-preview-more' });
    const chain = el('button', { type: 'button', textContent: 'Live chain', 'aria-label': 'Live chain from the built sources to this block', title: 'Loop the built sounds that lead to this block through every effect and mixer on the way, live: tune several blocks while it plays and bypass them one by one' });
    chain.onclick = () => import('../live-chain.js').then((m) => m.startChain(panel.block));
    const parity = el('button', { type: 'button', textContent: 'Check live vs render', title: 'Plays a test sound through the live version of this block and through the browser render, and says how far apart they are', hidden: !PARITY_TYPES.has(panel.type) });
    const parityBadge = el('span', { className: 'parity-badge', role: 'status', hidden: true });
    parity.onclick = async () => {
      parity.disabled = true;
      try {
        const { parityCheck } = await import('../live-parity.js'), result = await parityCheck(panel.type, { ...panel.getParams() }, { tempo: Number(panel.tempo) || 120 });
        parityBadge.hidden = !result; parityBadge.className = `parity-badge parity-${result?.level || 'unknown'}`;
        if (result) { parityBadge.textContent = `${result.word} · ${result.deviationDb.toFixed(0)} dB`; parityBadge.title = `Live against the browser render on a test sound: the difference is ${result.deviationDb.toFixed(1)} dB below the render (${result.word})`; }
      } catch (error) { parityBadge.hidden = false; parityBadge.textContent = error.message; } finally { parity.disabled = false; }
    };
    more.append(el('summary', { textContent: 'Compare / render' }), inputPlay, rendered, chain, parity, parityBadge);
    if (panel.type === 'master') {
      rendered.hidden = true;
      if (panel.buildInput) more.append(el('button', { type: 'button', textContent: 'Rebuild input', disabled: panel.readOnly, onclick: async () => { try { state.takeId = await panel.buildInput(); renderBody(state); renderTransport(state); } catch (error) { state.status.textContent = error.message; } } }));
    }
    footer.append(local, loop, scrubHost, levelMeter, more, status);
  } else footer.append(inputPlay, loop, scrubHost, levelMeter, status);
  const syncScrub = (player) => {
    levelMeter.setMeterValue(player.level, player.peak);
    local.textContent = state.previewMode === 'live' && player.playing ? '■ Stop' : panel.type === 'denoise' ? '▶ Preview' : '▶ Play';
    local.setAttribute('aria-label', panel.type === 'denoise' ? 'Client-side denoise preview' : 'Live local preview');
    if (!state.scrub || (player.take !== panel.takeId && player.take !== 'audition')) return;
    if (player.duration > 0) state.scrub.setScrubPosition(player.time / player.duration);
  };
  state.unsubscribePlayer = onPlayer(syncScrub);
  peaksOf(panel.takeId, 400).then((summary) => {
    if (!scrubHost.isConnected) return;
    const scrub = scrubBar({
      label: 'Preview waveform and position', position: 0, peaks: summary.peaks, peaksArePairs: true,
      loop: playerState().loopRegion || null,
      onPreview: seekFraction, onCommit: seekFraction,
      onLoopPreview: (range) => scrub.setScrubLoop(range),
      onLoopCommit: (range) => { state.loopChanged = true; state.previewLoop = true; state.previewLoopRegion = range; setLoopRegion(range); loop.setAttribute('aria-pressed', 'true'); loop.classList.add('on'); status.textContent = `Loop ${Math.round(range[0] * 100)}–${Math.round(range[1] * 100)}% of the input.`; },
    });
    state.scrub = scrub; scrubHost.replaceWith(scrub); syncScrub(playerState());
  }).catch((error) => { if (scrubHost.isConnected) scrubHost.textContent = `Waveform unavailable: ${error.message}`; });
}

/** Brings a window to the front and makes it the one that Esc and the song redraw talk to. */
function raise(state) { active = state; state.dialog.style.zIndex = String(++topZ); }

/** A panel is a floating window (not a modal dialog): placed next to the others, draggable by its header, and it remembers its place per block. */
function floatWindow(state) {
  const dialog = state.dialog;
  if (!matchMedia('(min-width:701px)').matches) return;   // a phone keeps the full-screen panel
  const key = `ample.studio.pos.${state.block}`;
  let saved = null;
  try { saved = JSON.parse(localStorage.getItem(key) || 'null'); } catch { /* storage may be blocked */ }
  const rect = dialog.getBoundingClientRect(), n = Math.max(0, windows.size - 1);
  const place = (left, top) => { dialog.style.left = `${Math.min(Math.max(0, left), Math.max(0, innerWidth - 140))}px`; dialog.style.top = `${Math.min(Math.max(0, top), Math.max(0, innerHeight - 60))}px`; };
  dialog.style.inset = 'auto'; dialog.style.position = 'fixed'; dialog.style.margin = '0';
  place(saved ? saved.left : Math.round((innerWidth - rect.width) / 2 + n * 28), saved ? saved.top : Math.round(Math.max(16, (innerHeight - rect.height) / 2 + n * 28)));
  const header = dialog.querySelector('.studio-dialog-header');
  if (header) {
    header.style.cursor = 'grab';
    header.addEventListener('pointerdown', (event) => {
      if (event.button !== 0 || event.target.closest('button, input, select, textarea')) return;
      const start = dialog.getBoundingClientRect(), dx = event.clientX - start.left, dy = event.clientY - start.top;
      header.setPointerCapture(event.pointerId);
      const move = (e) => place(e.clientX - dx, e.clientY - dy);
      const up = () => {
        header.removeEventListener('pointermove', move); header.removeEventListener('pointerup', up);
        try { localStorage.setItem(key, JSON.stringify({ left: parseInt(dialog.style.left, 10), top: parseInt(dialog.style.top, 10) })); } catch { /* storage may be blocked */ }
      };
      header.addEventListener('pointermove', move); header.addEventListener('pointerup', up);
    });
  }
  dialog.addEventListener('pointerdown', () => raise(state), true);
  dialog.addEventListener('keydown', (event) => { if (event.key === 'Escape' && !event.defaultPrevented) { event.stopPropagation(); dialog.close(); } });   // Esc closes the window that has the focus
  dialog.addEventListener('focusin', () => { active = state; });
}

/** A panel with an upstream input that is not built yet builds it when it opens (not the block itself, which can be an expensive voice). Cancel stops waiting. */
function autoBuild(state) {
  if (!state.dialog.open || state.autoBuilt) return;
  const panel = panelState(state);
  if (panel.takeId || !panel.buildInput || panel.readOnly || panel.buildLabel === 'Build block') return;
  const button = state.footer.querySelector('.studio-build-input');
  if (!button || button.disabled) return;
  state.autoBuilt = true;
  button.click();
}

export function openStudioPanel(options) {
  const existing = windows.get(options.block);
  if (existing?.dialog.open) { raise(existing); return existing.dialog; }   // the same block again: its window comes to the front
  const suffix = [...windows.values()].some((w) => w.dialog.open) ? `-${++cascade}` : '';   // the first window keeps the id studio (a window that was just closed is still in the map until its close event has come)
  const dialog = el('dialog', { id: `studio${suffix}`, className: 'studio-dialog studio-floating', 'aria-labelledby': `studio-title${suffix}` });
  const title = el('strong', { id: `studio-title${suffix}`, textContent: (options.title || options.type).toUpperCase() });
  const block = el('span', { className: 'note', textContent: `block ${options.block}` });
  const close = el('button', { type: 'button', textContent: '×', 'aria-label': 'Close advanced controls' });
  const bypassHost = el('span', { className: 'studio-dialog-bypass' });
  const header = el('header', { className: 'studio-dialog-header' }, [title, block, bypassHost]);
  const ab = el('button', { type: 'button', className: 'studio-ab', textContent: 'A/B', 'aria-label': 'A/B bypass', 'aria-pressed': 'false', title: 'Build an audio input before comparing', disabled: true });
  ab.onclick = async () => {
    const on = ab.getAttribute('aria-pressed') !== 'true';
    const panel = panelState(state);
    if (panel.type === 'master' && canLocalPreview(panel)) {
      ab.setAttribute('aria-pressed', String(on)); ab.classList.toggle('on', on); state.previewMode = 'live'; state.localPreviewActive = true;
      try { await playLocalPreview(state, { ...panel.getParams(), enabled: on ? 'off' : 'on' }, true); } catch (error) { if (error.name !== 'AbortError') state.status.textContent = error.message; }
      return;
    }
    if (!canRenderedPreview(panel)) return;
    ab.setAttribute('aria-pressed', String(on)); ab.classList.toggle('on', on);
    try { const params = { ...panel.getParams(), enabled: on ? 'off' : (panel.getParams().enabled || 'on') }; if (state.previewMode === 'live' && state.type === 'denoise') { state.liveParams = { ...state.liveParams, enabled: params.enabled }; await playLocalPreview(state, state.liveParams, true); } else if (state.previewMode === 'live') updateLivePreview({ ...state.liveParams, enabled: params.enabled }); else { state.previewMode = 'rendered'; state.localPreviewActive = false; await playRenderedPreview(state, params, false); } }
    catch (error) { if (state.status) state.status.textContent = error.message; }
  };
  header.append(ab, close);
  const body = el('div', { className: 'studio-dialog-body' });
  const footer = el('footer', { className: 'studio-preview-slot', 'aria-label': 'Effect preview controls' });
  dialog.append(header, body, footer); document.body.append(dialog);
  const previousPlayer = playerState();
  const state = { ...options, dialog, body, footer, titleElement: title, blockElement: block, bypassHost, ab, type: options.type, title: options.title, block: options.block, tempo: options.tempo, definitions: options.definitions, readOnly: options.readOnly, getParams: options.getParams, preview: options.preview, commit: options.commit, commitMany: options.commitMany, effectChain: options.effectChain || null, takeId: options.takeId, buildInput: options.buildInput, realPreview: options.realPreview, status: null, scrub: null, unsubscribePlayer: null, auditioning: false, localPreviewActive: false, localGeneration: 0, localRefreshTimer: 0, takeInputCache: null, localChanges: new Set(), loopChanged: false, originalLoop: previousPlayer.loop && !previousPlayer.loopRegion, originalLoopRegion: previousPlayer.loopRegion };
  state.previewLoop = previousPlayer.loop;
  state.previewLoopRegion = previousPlayer.loopRegion;
  const scheduleLocalUpdate = (changed) => {
    if (!state.previewMode) return;
    if (state.type === 'master' && (Array.isArray(changed) ? changed : [changed]).every((name) => name === 'target')) return;
    if (state.previewMode === 'live') {
      state.liveParams = { ...panelState(state).getParams() };
      if (state.type === 'denoise' || state.type === 'master') {
        if (state.ab.getAttribute('aria-pressed') === 'true') state.liveParams.enabled = 'off';
        clearTimeout(state.localRefreshTimer);
    state.localRefreshTimer = setTimeout(() => playLocalPreview(state, state.liveParams, true).catch((error) => { if (error.name !== 'AbortError' && state.status) state.status.textContent = error.message; }), 180);
        return;
      }

      if (state.type === 'convolution' && (Array.isArray(changed) ? changed : [changed]).some((name) => name === 'file' || name === 'ir')) {
        const generation = ++state.localGeneration, panel = panelState(state);
        panel.loadImpulse(state.liveParams).then(decodePreviewBlob).then((impulse) => {
          if (generation === state.localGeneration && state.dialog.open && state.previewMode === 'live') replaceLivePreviewImpulse(impulse, state.liveParams);
        }).catch((error) => { if (state.status) state.status.textContent = error.message; });
      } else updateLivePreview({ ...state.liveParams, ...(state.ab.getAttribute('aria-pressed') === 'true' ? { enabled: 'off' } : {}) });
      return;
    }
    clearTimeout(state.localRefreshTimer);
    state.localRefreshTimer = setTimeout(() => {
      const panel = panelState(state);
      const params = { ...panel.getParams() }; if (state.ab.getAttribute('aria-pressed') === 'true') params.enabled = 'off';
      const replay = state.previewMode === 'rendered' ? playRenderedPreview : playLocalPreview;
      replay(state, params, true).catch((error) => { if (state.status) state.status.textContent = error.message; });
    }, 180);
  };
  const commit = options.commit;
  state.commit = (name, value) => { if (state.type === 'convolution' && state.previewMode === 'live') state.localChanges.add(name); const result = commit(name, value); Promise.resolve(result).then(() => scheduleLocalUpdate(name)).catch((error) => { if (state.status) state.status.textContent = error.message; }); return result; };
  const commitMany = options.commitMany;
  if (commitMany) state.commitMany = (values) => { const result = commitMany(values); Promise.resolve(result).then(() => scheduleLocalUpdate(values.map((item) => item.name))).catch((error) => { if (state.status) state.status.textContent = error.message; }); return result; };
  state.wrapCommit = (commitEffect) => (name, value) => { const result = commitEffect(name, value); Promise.resolve(result).then(() => scheduleLocalUpdate(name)).catch((error) => { if (state.status) state.status.textContent = error.message; }); return result; };
  state.wrapCommitMany = (commitEffects) => (values) => { const result = commitEffects(values); Promise.resolve(result).then(() => scheduleLocalUpdate(values.map((item) => item.name))).catch((error) => { if (state.status) state.status.textContent = error.message; }); return result; };
  active = state; windows.set(options.block, state); renderBody(state);
  renderTransport(state);
  close.onclick = () => dialog.close();
  dialog.onclose = () => {
    clearTimeout(state.localRefreshTimer); state.denoiseAbort?.abort(); state.localGeneration++;
    const panel = panelState(state);
    body.querySelectorAll('.studio-eq-panel, .studio-live-panel').forEach((panel) => panel.dispose?.());
    state.unsubscribePlayer?.();
    const current = true;
    if (current && (state.auditioning || (state.startedPlayback && playerState().take === panel.takeId))) stop();
    if (current && state.loopChanged) { if (state.originalLoopRegion) setLoopRegion(state.originalLoopRegion); else setLoop(state.originalLoop); }
    dialog.remove(); windows.delete(options.block); if (active === state) active = [...windows.values()].pop() ?? null;
  };
  dialog.show();
  floatWindow(state); raise(state);
  loadSelectedInput(state).then(() => autoBuild(state));
  return dialog;
}

let redrawAfterDrag = false;
const redraw = () => { for (const w of [...windows.values()]) redrawOne(w); };
const redrawOne = (active) => {
  if (!active.dialog.open) return;
  if (active.type === 'convolution' && active.previewMode === 'live' && active.inputTakeId === panelState(active).takeId) {
    const params = panelState(active).getParams(), changed = new Set([...Object.keys(params), ...Object.keys(active.displayedParams)].filter((name) => String(params[name]) !== String(active.displayedParams[name])));
    if ([...changed].every((name) => active.localChanges.has(name))) return;
  }
  if (active.inputTakeId !== panelState(active).takeId) {
    clearTimeout(active.localRefreshTimer); active.localGeneration++;
    if (active.auditioning) stop();
    active.previewMode = null; active.localPreviewActive = false; active.auditioning = false; active.takeInputCache = null;
  }
  if (active.signature === signatureOf(active)) { // nothing the panel shows has changed (a source that settles a moment later is looked at once more)
    const state = active; clearTimeout(state.recheckTimer);
    state.recheckTimer = setTimeout(() => { if (active === state && state.dialog.open && state.signature !== signatureOf(state)) redraw(); }, 150);
    return;
  }
  if (textFieldFocused(active)) { // never destroy a field that is being typed in; draw again once it is left
    if (!active.redrawOnBlur) { const state = active; state.redrawOnBlur = true; state.body.addEventListener('focusout', () => setTimeout(() => { state.redrawOnBlur = false; if (!textFieldFocused(state)) redraw(); }, 0), { once: true }); }
    return;
  }
  renderBody(active); renderTransport(active);
};
// the body is drawn again when the song changes, but never under a knob that is being dragged (the knob would be replaced and the gesture lost): it waits for the end of the gesture
subscribe(() => {
  if (document.body.classList.contains('studio-dragging')) {
    if (!redrawAfterDrag) {
      redrawAfterDrag = true;
      addEventListener('pointerup', () => setTimeout(() => { redrawAfterDrag = false; redraw(); }, 0), { once: true, capture: true });
    }

    return;
  }

  redraw();
});
// Esc closes the window that was used last (a modal dialog on top of it, such as a question, takes the Esc first)
// (the window in front is found from the z-order, not from `active`, which is only updated when the close event of the last window has come)
const topWindow = () => [...windows.values()].filter((w) => w.dialog.open).sort((a, b) => Number(a.dialog.style.zIndex || 0) - Number(b.dialog.style.zIndex || 0)).pop() ?? null;
document.addEventListener('keydown', (event) => { if (event.key !== 'Escape' || event.defaultPrevented || document.querySelector('dialog:modal')) return; topWindow()?.dialog.close(); });
