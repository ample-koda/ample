// Live Preview in the code editor (plan/live-preview/PLAN.md, WP2): the loop player of Studio. The engine (a loop with skipped silences, one lap at a time with a
// short look-ahead, a new take at the loop boundary or at once, 5 ms fades) is web/lib/listen-loop.mjs, shared with Remix; this file gives it Studio's audio
// context and output (playbackOutput: the listening volume and the safety stage of every browser sound), registers it with the header player so one Stop stops
// everything and only one thing plays, and downloads the takes. The arithmetic is web/lib/listen-segments.mjs. @ample:listen-preview
import { getToken } from './api.js';
import { liveAudioContext } from './live-play.js';
import { playbackOutput } from './playback-volume.js';
import { setExternalPlayer, stop as stopOtherPlayers } from './player.js';
import { wavSlice } from '../lib/listen-segments.mjs';
import { createLoopPlayer } from '../lib/listen-loop.mjs';

// one sound at a time: when this one starts from silence the other players (the header player, the loop player of the sequencer) stop
const loop = createLoopPlayer({ context: liveAudioContext, output: playbackOutput, beforeStart: () => stopOtherPlayers() });
const buffers = new Map();   // take:role -> AudioBuffer (the last few)
export const onListenPlayer = (f) => loop.onChange(f);

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
export const listenPlayerState = () => loop.state();

/** Plays an entry { buffer, segments } (see createLoopPlayer: timing, onStop and the token of the session). */
export const playEntry = (entry, options) => loop.playEntry(entry, options);

/** Brings a waiting take in at once (Apply now). */
export const applyPendingNow = () => loop.applyPendingNow();

export const setLoading = (on) => loop.setLoading(on);

/** Stops the sound (and tells the owner of the session, unless it asked for the stop itself). */
export const stopListenPlayer = (options) => loop.stop(options);

setExternalPlayer({ playing: () => loop.isPlaying(), take: () => loop.currentTake(), stop: () => stopListenPlayer() });
