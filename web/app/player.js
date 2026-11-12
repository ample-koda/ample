// Playback: one audio element streams the take with range requests (the page never holds a whole take in memory),
// peaks for the waveform come from the server. @ample:preview-playback

import { api, getToken } from './api.js';
import { toast } from './ui.js';
import { playbackOutput } from './playback-volume.js';
import { getContext } from '../lib/audio-engine.mjs';
import { prepareWorklets } from './studio/preview/audio.js';

const audio = new Audio();
audio.preload = 'metadata';
let current = null;
const listeners = new Set();
const peakCache = new Map();
let loadVersion = 0;
let loadingTake = null;
let cancelTakeLoad = null;
let playVersion = 0;
let pendingPlay = null;
let transientUrl = null;
let loopRegion = null;
let meterAnalyser = null;
let meterData = null;
let meterSpectrum = null;
let meterSampleRate = 0;
let meterReady = false;
let meterContext = null;
let meterSource = null;
let channelAnalysers = [];
let channelData = [];
let liveEffect = null;
let auditionBlock = null;
const externals = new Set();   // other players that share the Stop button (the looping player of the sequencer, the live chain): { playing(), take(), stop() }
const externalPlaying = () => [...externals].find((player) => player.playing());

/** Lets a second player (gapless looping with Web Audio) take part: it shows in playerState, stops with Stop and is stopped when a take plays here. */
export const setExternalPlayer = (player) => { externals.add(player); };

async function prepareMeterAudio() {
  if (meterReady || (!window.AudioContext && !window.webkitAudioContext)) return;
  meterReady = true;
  try {
    const context = getContext();
    const source = context.createMediaElementSource(audio);
    meterContext = context; meterSource = source;
    meterAnalyser = context.createAnalyser(); meterAnalyser.fftSize = 2048; meterAnalyser.channelCount = 2; meterAnalyser.channelCountMode = 'explicit';
    meterSampleRate = context.sampleRate; meterSpectrum = new Float32Array(meterAnalyser.frequencyBinCount);
    meterData = new Float32Array(meterAnalyser.fftSize);
    source.connect(meterAnalyser).connect(playbackOutput(context));
    const splitter = context.createChannelSplitter(2);
    meterAnalyser.connect(splitter);
    channelAnalysers = Array.from({ length: 2 }, (_, channel) => { const analyser = context.createAnalyser(); analyser.fftSize = 2048; splitter.connect(analyser, channel); return analyser; });
    channelData = channelAnalysers.map((analyser) => new Float32Array(analyser.fftSize));
    if (context.state === 'suspended') await context.resume();
  } catch {
    meterAnalyser = null; meterData = null; meterSpectrum = null; meterSampleRate = 0;
  }
}

function meterLevel() {
  if (!meterAnalyser || !meterData) return { level: 0, peak: 0 };
  meterAnalyser.getFloatTimeDomainData(meterData);
  let sum = 0, peak = 0;
  for (const sample of meterData) { sum += sample * sample; peak = Math.max(peak, Math.abs(sample)); }
  return { level: Math.sqrt(sum / meterData.length), peak };
}

function stereoMeterLevel() {
  const levels = channelAnalysers.map((analyser, channel) => {
    const data = channelData[channel]; analyser.getFloatTimeDomainData(data);
    let sum = 0, peak = 0;
    for (const sample of data) { sum += sample * sample; peak = Math.max(peak, Math.abs(sample)); }
    return { level: Math.sqrt(sum / data.length), peak };
  });
  return { leftLevel: levels[0]?.level || 0, leftPeak: levels[0]?.peak || 0, rightLevel: levels[1]?.level || 0, rightPeak: levels[1]?.peak || 0 };
}

export const onPlayer = (f) => { listeners.add(f); return () => listeners.delete(f); };
export function playerSpectrum(count) {
  if (!meterAnalyser || !meterSpectrum || !(meterSampleRate > 0)) return [];
  meterAnalyser.getFloatFrequencyData(meterSpectrum);
  return Array.from({ length: count }, (_, index) => {
    const frequency = 20 * (1000 ** (index / Math.max(1, count - 1))), bin = Math.min(meterSpectrum.length - 1, Math.max(0, Math.round(frequency / meterSampleRate * meterAnalyser.fftSize)));
    return { frequency, db: Math.max(-72, Math.min(6, meterSpectrum[bin])) };
  });
}
export const playerState = () => ({
  take: externalPlaying() ? externalPlaying().take() : current ? current.takeId : null,
  auditionBlock,
  source: current?.filePath || null,
  playing: (!audio.paused && !audio.ended) || !!externalPlaying(),
  loading: pendingPlay?.takeId || null,
  time: audio.currentTime || 0,
  duration: Number.isFinite(audio.duration) ? audio.duration : 0,
  loop: audio.loop || Boolean(loopRegion), loopRegion: loopRegion ? [...loopRegion] : null,
  ...meterLevel(),
  ...stereoMeterLevel(),
});
const emit = () => { const st = playerState(); listeners.forEach((f) => f(st)); };
let meterFrame = 0;
const animateMeters = () => { emit(); meterFrame = !audio.paused && !audio.ended ? requestAnimationFrame(animateMeters) : 0; };
audio.addEventListener('play', () => { if (!meterFrame) meterFrame = requestAnimationFrame(animateMeters); });
['pause', 'ended'].forEach((event) => audio.addEventListener(event, () => { if (meterFrame) cancelAnimationFrame(meterFrame); meterFrame = 0; }));
['play', 'pause', 'ended', 'loadedmetadata', 'seeked'].forEach((e) => audio.addEventListener(e, emit));
audio.addEventListener('timeupdate', () => {
  if (loopRegion && Number.isFinite(audio.duration) && audio.currentTime >= loopRegion[1] * audio.duration) audio.currentTime = loopRegion[0] * audio.duration;
  emit();
});

export async function audioDataId(takeId) {
  const r = await api.get(`/api/takes/${encodeURIComponent(takeId)}`);
  if (!r.audioDataId) throw new Error('This take has no audio.');
  return r.audioDataId;
}

export async function peaksOf(takeId, points = 600, role = '') {
  const key = `${takeId}:${points}:${role}`;
  if (!peakCache.has(key)) peakCache.set(key, api.get(`/api/takes/${encodeURIComponent(takeId)}/peaks?points=${points}${role ? `&role=${encodeURIComponent(role)}` : ''}`).then((r) => r.peaks));
  return peakCache.get(key);
}

async function selectTake(takeId) {
  if (loadingTake?.takeId === takeId) return loadingTake.promise;
  if (current?.takeId === takeId && audio.readyState >= 1) return true;
  cancelTakeLoad?.();
  const version = ++loadVersion;
  const promise = (async () => {
    const dataId = await audioDataId(takeId);
    if (version !== loadVersion) return false;
    audio.pause();
    current = { takeId, dataId };
    const ready = new Promise((resolve, reject) => {
      const cleanup = () => { clearTimeout(timeout); if (cancelTakeLoad === cancelled) cancelTakeLoad = null; audio.removeEventListener('loadedmetadata', loaded); audio.removeEventListener('error', failed); };
      const loaded = () => { cleanup(); resolve(true); };
      const failed = () => { cleanup(); reject(new Error('The audio take could not be loaded.')); };
      const cancelled = () => { cleanup(); resolve(false); };
      const timeout = setTimeout(() => { cleanup(); reject(new Error('Loading this audio took too long. Try Play again.')); }, 15000);
      cancelTakeLoad = cancelled;
      audio.addEventListener('loadedmetadata', loaded, { once: true });
      audio.addEventListener('error', failed, { once: true });
      audio.src = `/api/data/${dataId}`;
      if (transientUrl) { URL.revokeObjectURL(transientUrl); transientUrl = null; }
      if (audio.readyState >= 1) loaded();
    });
    await ready;
    return version === loadVersion;
  })();
  loadingTake = { takeId, promise };
  try { return await promise; } finally { if (loadingTake?.promise === promise) loadingTake = null; }
}

export async function play(takeId, from = 0) {
  const version = ++playVersion;
  pendingPlay = { takeId, version };
  emit();
  externals.forEach((player) => player.stop());
  auditionBlock = null;
  clearLivePreview();
  try {
    if (!await selectTake(takeId) || current?.takeId !== takeId || version !== playVersion) return;
    audio.currentTime = Math.max(0, from);
    await prepareMeterAudio();
    if (meterContext?.state === 'suspended') await meterContext.resume();
    if (version !== playVersion || current?.takeId !== takeId) return;
    await audio.play();
  } catch (error) { if (version === playVersion) { toast(`Playback failed: ${error.message || error}`); try { error.toasted = true; } catch {} throw error; } }
  finally { if (pendingPlay?.version === version) { pendingPlay = null; emit(); } }
}

export async function playProjectFile(path) {
  stop();
  const version = ++loadVersion;
  current = { takeId: null, dataId: null, filePath: path };
  await new Promise((resolve, reject) => {
    const loaded = () => { audio.removeEventListener('error', failed); resolve(); };
    const failed = () => { audio.removeEventListener('loadedmetadata', loaded); if (version === loadVersion) current = null; reject(new Error('The project audio file could not be played.')); };
    audio.addEventListener('loadedmetadata', loaded, { once: true });
    audio.addEventListener('error', failed, { once: true });
    audio.src = `/api/project/audio?path=${encodeURIComponent(path)}`;
    if (audio.readyState >= 1) loaded();
  });
  if (version !== loadVersion) return;
  await prepareMeterAudio();
  if (version !== loadVersion) return;
  try { await audio.play(); } catch (error) { toast(`Playback failed: ${error.message || error}`); try { error.toasted = true; } catch {} throw error; }
}

export async function playTransient(blob) {
  const activeLoopRegion = loopRegion;
  const activeLoop = audio.loop;
  stop();
  loopRegion = activeLoopRegion;
  audio.loop = activeLoop;
  const version = ++loadVersion;
  if (transientUrl) URL.revokeObjectURL(transientUrl);   // each local preview re-render makes a new blob; without this the old one leaked every time a slider changed
  transientUrl = URL.createObjectURL(blob);
  current = { takeId: 'audition', dataId: null };
  await new Promise((resolve, reject) => {
    const cleanup = () => { audio.removeEventListener('loadedmetadata', loaded); audio.removeEventListener('error', failed); };
    const loaded = () => { cleanup(); resolve(); }, failed = () => { cleanup(); reject(new Error('The preview audio could not be loaded.')); };
    audio.addEventListener('loadedmetadata', loaded, { once: true }); audio.addEventListener('error', failed, { once: true });
    audio.src = transientUrl;
  });
  if (version !== loadVersion) return false;
  await prepareMeterAudio();
  if (version !== loadVersion) return false;
  await audio.play();
  return true;
}

export async function playLivePreview(blob, createEffect) {
  if (!await playTransient(blob)) return;
  if (!meterContext || !meterSource || !meterAnalyser) throw new Error('Live effects are unavailable in this browser.');
  const version = playVersion;
  await prepareWorklets(meterContext);   // the exact reverb is a worklet: ready before the chain is built
  if (version !== playVersion) return;   // stopped meanwhile
  meterSource.disconnect();
  liveEffect = createEffect(meterContext, meterSource, meterAnalyser);
}

export function updateLivePreview(params) { liveEffect?.update(params); }
export function replaceLivePreviewImpulse(impulse, params) { liveEffect?.replaceImpulse?.(impulse, params); }
export function setAuditionBlock(block) { auditionBlock = block; emit(); }

function clearLivePreview() {
  auditionBlock = null;
  if (!liveEffect) return;
  meterSource.disconnect(); liveEffect.dispose(); liveEffect = null;
  meterSource.connect(meterAnalyser);
}

export async function seekTake(takeId, fraction) {
  if (!await selectTake(takeId) || current?.takeId !== takeId || !audio.duration) return;
  seekFraction(fraction);
}

export function toggle(takeId) {
  if (pendingPlay?.takeId === takeId) { stop(); return Promise.resolve(); }
  if (current && current.takeId === takeId && !audio.paused) { audio.pause(); return Promise.resolve(); }
  const resume = current && current.takeId === takeId && audio.currentTime < (audio.duration || 0) - 0.05 ? audio.currentTime : 0;
  return play(takeId, resume);
}

export const pause = () => audio.pause();
export const stop = () => { externals.forEach((player) => player.stop()); clearLivePreview(); playVersion++; pendingPlay = null; loadVersion++; cancelTakeLoad?.(); loadingTake = null; loopRegion = null; audio.loop = false; audio.pause(); audio.removeAttribute('src'); audio.load(); current = null; if (transientUrl) { URL.revokeObjectURL(transientUrl); transientUrl = null; } emit(); };
export const seekFraction = (f) => { if (audio.duration) audio.currentTime = Math.max(0, Math.min(1, f)) * audio.duration; };
export const setLoop = (on) => { loopRegion = null; audio.loop = on; emit(); };
export const setLoopRegion = (region) => {
  loopRegion = region && region.length === 2 ? [...region].sort((a, b) => a - b).map((value) => Math.max(0, Math.min(1, Number(value)))) : null;
  if (loopRegion && loopRegion[1] - loopRegion[0] < .005) loopRegion = null;
  audio.loop = false; emit();
};
export const currentTake = () => (current ? current.takeId : null);

/** Downloads a take. A fetch (not a plain link) so that a refusal, like a missing ffmpeg for MP3, is shown as a message instead of the browser's "no file". */
export async function download(takeId, format) {
  try {
    const r = await fetch(`/api/takes/${encodeURIComponent(takeId)}/download?format=${encodeURIComponent(format)}`, { headers: { 'X-Ample-Token': getToken() } });
    if (!r.ok) {
      let p = {};
      try { p = await r.json(); } catch { /* not JSON */ }
      toast(p.detail || p.title || `The download failed (${r.status}).`);
      return;
    }
    const url = URL.createObjectURL(await r.blob());
    const link = document.createElement('a');
    link.href = url;
    link.download = `${takeId}.${format}`;
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
  } catch (e) {
    toast(`The download failed: ${e.message}`);
  }
}

// For the browser tests.
window.AMPLE_PLAYER = { state: playerState };

/** Draws min/max pairs into a canvas (values -1 to 1). */
export function drawPeaks(canvas, peaks, color, overlay = false) {
  const ctx = canvas.getContext('2d');
  const w = canvas.width, h = canvas.height;
  if (!overlay) ctx.clearRect(0, 0, w, h);   // overlay: drawn on top of what is there, and silence is left out
  ctx.fillStyle = color;
  const n = peaks.length / 2;
  for (let x = 0; x < w; x++) {
    const i = Math.min(n - 1, Math.floor((x / w) * n));
    const lo = peaks[i * 2], hi = peaks[i * 2 + 1];
    if (overlay && Math.abs(lo) < 1e-4 && Math.abs(hi) < 1e-4) continue;
    const y0 = h / 2 - hi * (h / 2), y1 = h / 2 - lo * (h / 2);
    ctx.fillRect(x, y0, 1, Math.max(1, y1 - y0));
  }
}
