// Live Preview in the code editor (plan/live-preview/PLAN.md, WP2): a small Web Audio player that loops the segments of one take (a loop with skipped silences),
// one lap at a time with a short look-ahead, so a new take can come in exactly at the loop boundary (or at once) and a cut never clicks (5 ms fades).
// It plays through playbackOutput (the listening volume and the safety stage of every browser sound) and is registered with the header player, so one
// Stop stops everything and only one thing plays. The arithmetic (segments, preview time and take time) is web/lib/listen-segments.mjs. @ample:listen-preview
import { getToken } from './api.js';
import { liveAudioContext } from './live-play.js';
import { playbackOutput } from './playback-volume.js';
import { setExternalPlayer, stop as stopOtherPlayers } from './player.js';
import { heardLength, takeTimeAt, wavSlice } from '../lib/listen-segments.mjs';

const FADE = 0.005, LOOKAHEAD = 0.12, TICK = 30;
const P = { entry: null, pending: null, playing: false, lapStart: 0, scheduled: [], timer: 0, loading: false, version: 0, onStop: null };
const buffers = new Map();   // take:role -> AudioBuffer (the last few)
const listeners = new Set();
const emit = () => listeners.forEach((f) => f(listenPlayerState()));
export const onListenPlayer = (f) => { listeners.add(f); return () => listeners.delete(f); };

/** Fetches and decodes one output of a take (cached for the last six). */
export async function loadTake(takeId, role = '') {
  const key = `${takeId}:${role}`;
  if (buffers.has(key)) return buffers.get(key);
  const response = await fetch(`/api/takes/${encodeURIComponent(takeId)}/download?format=wav${role ? `&role=${encodeURIComponent(role)}` : ''}`, { headers: { 'X-Ample-Token': getToken() } });
  if (!response.ok) throw new Error(`The sound of this build could not be read (${response.status}).`);
  const buffer = await liveAudioContext().decodeAudioData(await response.arrayBuffer());
  buffers.set(key, buffer);
  while (buffers.size > 6) buffers.delete(buffers.keys().next().value);
  return buffer;
}

/**
 * Fetches and decodes only a stretch of a take (fromSec to toSec), with HTTP range requests on the WAV download: the header first, then the bytes of the
 * stretch (plan/live-preview 6.4: the bars before a range are rendered, never sent). Returns { buffer, offset } (offset: take seconds where the buffer starts).
 * When the server does not answer a range (or the header is unusual), the whole take is read and the stretch is used from it.
 */
export async function loadTakeSlice(takeId, role, fromSec, toSec) {
  const key = `${takeId}:${role}:${fromSec.toFixed(4)}-${toSec.toFixed(4)}`;
  if (buffers.has(key)) return buffers.get(key);
  const url = `/api/takes/${encodeURIComponent(takeId)}/download?format=wav${role ? `&role=${encodeURIComponent(role)}` : ''}`;
  const read = (range) => fetch(url, { headers: { 'X-Ample-Token': getToken(), Range: range } });
  const head = await read('bytes=0-65535');
  if (!head.ok) throw new Error(`The sound of this build could not be read (${head.status}).`);
  const total = Number(/\/(\d+)$/.exec(head.headers.get('Content-Range') || '')?.[1]);
  const slice = head.status === 206 && total > 0 ? wavSlice(await head.arrayBuffer(), total, fromSec, toSec) : null;
  let result;
  if (slice) {
    const body = await read(`bytes=${slice.from}-${slice.to}`);
    if (body.status !== 206) throw new Error(`The sound of this build could not be read (${body.status}).`);
    result = { buffer: await liveAudioContext().decodeAudioData(slice.wav(await body.arrayBuffer())), offset: slice.offset, sliced: true };
  } else {
    result = { buffer: await loadTake(takeId, role), offset: 0, sliced: false };   // no range answer: the whole take, the loop picks the stretch
  }
  buffers.set(key, result);
  while (buffers.size > 6) buffers.delete(buffers.keys().next().value);
  return result;
}

/** Where the playhead is: seconds into the take (null when nothing plays), and whether a new take waits for the loop boundary. */
export function listenPlayerState() {
  const entry = P.entry;
  if (!P.playing || !entry) return { playing: false, loading: P.loading, takeTime: null, pending: !!P.pending, remaining: 0, entry };
  const context = liveAudioContext(), length = heardLength(entry.segments);
  const into = Math.max(0, context.currentTime - P.lapStart);
  return {
    playing: true, loading: P.loading, takeTime: takeTimeAt(entry.segments, into), previewTime: into % Math.max(length, 1e-6),
    pending: !!P.pending, remaining: Math.max(0, length - into), entry,
  };
}

/** One lap of an entry from `at` (context time), starting `offset` seconds into the heard loop. */
function scheduleLap(entry, at, offset = 0) {
  const context = liveAudioContext(), out = playbackOutput(context);
  let time = at, skip = offset;
  for (const segment of entry.segments) {
    const length = segment.to - segment.from;
    if (skip >= length) { skip -= length; continue; }
    const source = context.createBufferSource(), gain = context.createGain();
    source.buffer = entry.buffer;
    source.connect(gain).connect(out);
    const play = length - skip;
    gain.gain.setValueAtTime(0, time);
    gain.gain.linearRampToValueAtTime(1, time + Math.min(FADE, play / 4));
    gain.gain.setValueAtTime(1, Math.max(time + Math.min(FADE, play / 4), time + play - Math.min(FADE, play / 4)));
    gain.gain.linearRampToValueAtTime(0, time + play);
    source.start(time, segment.from + skip, play);
    P.scheduled.push({ source, end: time + play });
    time += play; skip = 0;
  }
  return time;
}

function tick() {
  if (!P.playing) return;
  const context = liveAudioContext();
  P.scheduled = P.scheduled.filter((s) => s.end > context.currentTime - 0.5);
  const lapEnd = P.lapStart + heardLength(P.entry.segments);
  if (lapEnd - context.currentTime < LOOKAHEAD + TICK / 1000) {
    // the next lap: the waiting take (swap at the boundary), else the same one again
    if (P.pending) { P.entry = P.pending; P.pending = null; emit(); }
    P.lapStart = lapEnd;
    scheduleLap(P.entry, lapEnd);
  }
}

function silenceAll() {
  const context = liveAudioContext();
  for (const s of P.scheduled) { try { s.source.stop(context.currentTime + FADE); } catch { /* already ended */ } }
  P.scheduled = [];
}

/**
 * Plays an entry { buffer, segments }. When something plays: timing 'boundary' waits for the end of the current loop, 'now' swaps at once at the same
 * place in the loop.
 */
export async function playEntry(entry, { timing = 'boundary', onStop = null } = {}) {
  const context = liveAudioContext();
  if (context.state === 'suspended') await context.resume().catch(() => {});
  if (P.playing && timing === 'boundary') { P.pending = entry; emit(); return; }
  const into = P.playing ? Math.max(0, context.currentTime - P.lapStart) : 0;
  if (!P.playing) { P.version++; stopOtherPlayers(); }   // one sound at a time (the header player, the loop player of the sequencer)
  silenceAll();
  P.entry = entry; P.pending = null; P.playing = true; P.onStop = onStop;
  const offset = into % Math.max(heardLength(entry.segments), 1e-6), start = context.currentTime + 0.03;
  P.lapStart = start - offset;
  scheduleLap(entry, start, offset);
  clearInterval(P.timer); P.timer = setInterval(tick, TICK);
  emit();
}

/** Brings a waiting take in at once (Apply now). */
export function applyPendingNow() { if (P.pending) { const next = P.pending; P.pending = null; return playEntry(next, { timing: 'now', onStop: P.onStop }); } return Promise.resolve(); }

export function setLoading(on) { P.loading = on; emit(); }

/** Stops the sound (and tells the owner of the session, unless it asked for the stop itself). */
export function stopListenPlayer({ quiet = false } = {}) {
  if (!P.playing && !P.pending) return;
  clearInterval(P.timer); P.timer = 0;
  silenceAll();
  P.playing = false; P.pending = null;
  const onStop = P.onStop; P.onStop = null;
  emit();
  if (!quiet) onStop?.();
}

setExternalPlayer({ playing: () => P.playing, take: () => P.entry?.take ?? null, stop: () => stopListenPlayer() });
