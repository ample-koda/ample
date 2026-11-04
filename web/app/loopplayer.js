import { registerPlaybackAudio } from './playback-volume.js';
// Looped playback for the sequencer workspace (plan sequencer-live): a take (or a stretch of it) repeats, and a new take can be swapped in exactly when the loop
// comes round again, so a change that is built while the music plays is heard at the next return and never in the middle of a bar.
// It plays through a plain audio element (the same way the header Play does, which is known to reach the speakers) and jumps back at the end of the loop
// from a 10 ms timer, which leaves a gap of a few milliseconds at most. The arithmetic is in web/lib/loop-model.mjs. @ample:loop

import { audioDataId, setExternalPlayer, stop as stopPlayer } from './player.js';

const audio = registerPlaybackAudio(new Audio());
audio.preload = 'auto';
const L = { take: null, loop: null, looping: true, pending: null, version: 0, timer: 0, active: false };
const listeners = new Set();
const emit = () => listeners.forEach((f) => f(state()));
export const onLoopPlayer = (f) => { listeners.add(f); return () => listeners.delete(f); };

const urlOf = async (takeId) => `/api/data/${await audioDataId(takeId)}`;
const duration = () => (Number.isFinite(audio.duration) ? audio.duration : 0);
const clamp = (loop, total) => (loop && total && loop.start < total - 0.05 ? { start: Math.max(0, loop.start), end: Math.min(loop.end, total) } : null);
/** The loop that really plays: the chosen stretch, or the whole take. */
const effectiveLoop = () => (L.looping ? (clamp(L.loop, duration()) ?? { start: 0, end: duration() }) : null);

function loaded(url) {
  return new Promise((resolve, reject) => {
    const done = () => { audio.removeEventListener('loadedmetadata', done); audio.removeEventListener('error', fail); resolve(); };
    const fail = () => { audio.removeEventListener('loadedmetadata', done); audio.removeEventListener('error', fail); reject(new Error('The audio of this take could not be loaded.')); };
    audio.addEventListener('loadedmetadata', done); audio.addEventListener('error', fail);
    audio.src = url;
  });
}

/** Runs while it plays: wraps around at the end of the loop, and brings a waiting take in at that moment. */
function tick() {
  if (!L.active) return;
  const loop = effectiveLoop();
  if (loop && audio.currentTime >= loop.end - 0.012) {
    if (L.pending) swapNow();
    else audio.currentTime = loop.start;
  } else if (!loop && audio.ended) { L.active = false; clearInterval(L.timer); emit(); }
}

async function swapNow() {
  const next = L.pending; L.pending = null;
  const loop = effectiveLoop();
  audio.pause();
  await loaded(next.url);
  L.take = next.take;
  audio.currentTime = loop ? loop.start : 0;
  await audio.play().catch(() => {});
  emit();
}

/** The state for the user interface: whether it plays, where the playhead is (seconds into the take) and the loop. */
export function state() {
  return { playing: L.active && !audio.paused && !audio.ended, take: L.take, position: audio.currentTime || 0, duration: duration(), loop: L.loop, looping: L.looping, pending: !!L.pending };
}

/**
 * Starts playing a take.
 * @param {string} takeId
 * @param {{ from?: number, loop?: { start: number, end: number } | null, looping?: boolean }} options from: seconds into the take; loop: the stretch to repeat (null: all of it)
 */
export async function startLoopPlayer(takeId, { from = 0, loop = null, looping = true } = {}) {
  stopPlayer();   // the other player and this one: one sound at a time
  const version = ++L.version;
  const url = await urlOf(takeId);
  audio.pause();
  await loaded(url);
  if (version !== L.version) return;
  L.take = takeId; L.loop = loop; L.looping = looping; L.pending = null;
  const now = effectiveLoop();
  audio.currentTime = now && (from < now.start || from >= now.end) ? now.start : from;
  await audio.play();
  L.active = true;
  clearInterval(L.timer); L.timer = setInterval(tick, 10);
  emit();
}

export function stopLoopPlayer() {
  L.version++; L.active = false; L.pending = null; clearInterval(L.timer);
  audio.pause();
  emit();
}

/** Changes the repeated stretch (or turns the repeating on or off) while it plays: from where the playhead is now. */
export async function setLoopRange(loop, looping = L.looping) {
  L.loop = loop; L.looping = looping;
  const now = effectiveLoop();
  if (L.active && now && (audio.currentTime < now.start || audio.currentTime >= now.end)) audio.currentTime = now.start;
  emit();
}

/** Jumps to a place of the take (seconds) and goes on playing from there. */
export function seekLoopPlayer(seconds) {
  if (!L.active) return;
  const now = effectiveLoop();
  audio.currentTime = now && (seconds < now.start || seconds >= now.end) ? now.start : seconds;
  emit();
}

/** Brings a new take in while the old one plays: it waits for the next return of the loop (at once when nothing loops). */
export async function swapTake(takeId) {
  if (!L.active) return false;
  const url = await urlOf(takeId);
  if (!L.active) return false;
  await fetch(url).then((r) => r.arrayBuffer()).catch(() => {});   // warm the cache so the swap at the return does not wait
  L.pending = { take: takeId, url };
  if (!effectiveLoop()) await swapNow();
  emit();
  return true;
}

/** Kept for the Play click: nothing to unlock for an audio element, but the call stays so the click is what starts the sound. */
export const unlockAudio = () => {};

setExternalPlayer({ playing: () => state().playing, take: () => L.take, stop: () => { if (L.active) stopLoopPlayer(); } });
window.AMPLE_LOOP = { state, info: () => ({ muted: audio.muted, volume: audio.volume, paused: audio.paused, time: audio.currentTime, duration: audio.duration, ready: audio.readyState, src: audio.src.slice(-20), rate: audio.playbackRate, loop: L.loop, take: L.take }), context: () => (L.active ? 'audio-element' : 'none'), level: () => (L.active && !audio.paused ? 1 : 0) };
