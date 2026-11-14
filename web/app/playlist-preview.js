// Mute and solo of the Playlist: a PREVIEW only. They never touch the song text, a plan, a take or a build. The takes of the lines that are heard are mixed in the
// browser (Web Audio) for a range of bars, which is approximate: the bus and master processing of the real build is not applied. Plan: PV07. @ample:playlist-preview
import { getContext } from '../lib/audio-engine.mjs';
import { createAuditionGuard } from '../lib/audition-guard.mjs';
import { S } from './store.js';
import { toast } from './ui.js';
import { takeCache } from '../lib/take-cache.mjs';
import { playbackOutput } from './playback-volume.js';
import { audioDataId, notifyPlayer, setExternalPlayer, stop as stopPlayer } from './player.js';

export const preview = { muted: new Set(), solo: new Set() };
export const previewActive = () => preview.muted.size > 0 || preview.solo.size > 0;
export const clearPreview = () => { preview.muted.clear(); preview.solo.clear(); stopMix(); };

/** Toggles mute (or solo) of a line; returns whether the set now has it. */
export function toggle(kind, id) {
  const set = kind === 'solo' ? preview.solo : preview.muted;
  if (set.has(id)) set.delete(id); else set.add(id);
  return set.has(id);
}

/** The lines that are heard: the soloed ones if any, else all that are not muted. */
export const heard = (lines) => (preview.solo.size ? lines.filter((l) => preview.solo.has(l.id)) : lines.filter((l) => !preview.muted.has(l.id)));

/** The block whose take stands for the line: the last one in its chain (its bus), else its source. */
export const takeBlockOf = (line) => line.chain.at(-1) || line.sources.find((s) => s.block)?.block || null;

// `generation` names the newest audition: Stop, Clear, a newer Hear it and a take played elsewhere raise it, and an audition whose takes are still loading checks it after
// every wait, so a superseded one never starts a source (#103). `pending` is the audition that is preparing: the transport shows it as loading, so Stop can cancel it.
let context = null, playing = null, pending = null;
const guard = createAuditionGuard(() => S.path);

export function stopMix() {
  guard.invalidate();
  const had = playing !== null || pending !== null;
  try { playing?.stop(); } catch { /* already stopped */ }
  playing = null; pending = null;
  if (had) notifyPlayer();
}

// Hear it takes part in the transport: it shows as loading and then playing (the Mixer meter follows the shared output), and the main Stop ends or cancels it.
setExternalPlayer({ playing: () => playing !== null, loading: () => pending, take: () => 'playlist', stop: () => stopMix() });
// a song really opened (not a mere request: a declined confirm keeps the audition) ends it; the check before the source starts covers a song changed in another way
document.addEventListener('ample:song-opened', () => stopMix());

// decoded takes live in the shared byte-budgeted cache (evicts the least recently used), under the key the live chain uses for the same take (data ids are content hashes, so no project id is needed), so one take is decoded once
const bufferOf = (takeId) => takeCache.getDecoded(`chain:${takeId}`, async () => {
  context ||= getContext();
  const dataId = await audioDataId(takeId);
  const response = await fetch(`/api/data/${dataId}`);
  if (!response.ok) throw new Error('A take could not be loaded.');
  return context.decodeAudioData(await response.arrayBuffer());
});

/**
 * Plays the mix of the heard lines for `bars` ({ firstBar, bars }) at `secondsPerBar`. A line without a take is named and the preview asks for a build.
 * Returns false when nothing could be played.
 */
export async function playMix(lines, bars, secondsPerBar, maxSeconds = 90) {
  stopMix();
  stopPlayer();
  const current = guard.begin();   // false once stopped, replaced, or the song changed while the takes loaded
  const chosen = heard(lines);
  if (!chosen.length) { toast('Every line is muted: nothing to hear.'); return false; }
  const missing = chosen.filter((l) => !S.build.takes?.[takeBlockOf(l)]);
  if (missing.length) { toast(`Build first: no take yet for ${missing.map((l) => l.name).join(', ')}.`); return false; }
  const from = (bars?.firstBar || 1) - 1, count = bars?.bars || Math.max(1, Math.floor(maxSeconds / secondsPerBar));
  const seconds = Math.min(count * secondsPerBar, maxSeconds);
  pending = 'playlist';
  notifyPlayer();
  let resumed = null;
  try { context ||= getContext(); if (context.state !== 'running') resumed = context.resume().catch(() => {}); } catch { /* no audio: the decode below reports it */ }   // started inside the click, awaited below
  try {
    const decoded = await Promise.all(chosen.map((l) => bufferOf(S.build.takes[takeBlockOf(l)])));
    await resumed;   // a suspended shared context would play nothing while Stop is enabled
    if (!current()) return false;   // stopped, cleared or replaced while the takes loaded
    context ||= getContext();
    const rate = decoded[0].sampleRate, length = Math.max(1, Math.floor(seconds * rate)), start = Math.floor(from * secondsPerBar * rate);
    const mix = context.createBuffer(2, length, rate);
    for (const b of decoded) for (let ch = 0; ch < 2; ch++) {
      const out = mix.getChannelData(ch), src = b.getChannelData(Math.min(ch, b.numberOfChannels - 1));
      for (let i = 0; i < length && start + i < src.length; i++) out[i] += src[start + i];
    }
    const gain = 1 / Math.max(1, Math.sqrt(decoded.length));   // a rough guard against clipping, not the master's limiter
    const node = context.createBufferSource();
    node.buffer = mix;
    const g = context.createGain();
    g.gain.value = gain;
    node.connect(g).connect(playbackOutput(context));
    node.onended = () => { if (playing === node) { playing = null; notifyPlayer(); } };
    node.start();
    playing = node; pending = null;
    notifyPlayer();
    return true;
  } catch (e) {
    if (!current()) return false;   // a superseded audition says nothing
    toast(e.message || String(e));
    return false;
  } finally {
    if (current.latest() && pending !== null) { pending = null; notifyPlayer(); }   // it failed or its song was left: no longer preparing
  }
}
