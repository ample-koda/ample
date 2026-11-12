// Mute and solo of the Playlist: a PREVIEW only. They never touch the song text, a plan, a take or a build. The takes of the lines that are heard are mixed in the
// browser (Web Audio) for a range of bars, which is approximate: the bus and master processing of the real build is not applied. Plan: PV07. @ample:playlist-preview
import { getContext } from '../lib/audio-engine.mjs';
import { S } from './store.js';
import { toast } from './ui.js';
import { audioDataId, stop as stopPlayer } from './player.js';

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

let context = null, playing = null;
const buffers = new Map();   // dataId -> AudioBuffer

export function stopMix() { try { playing?.stop(); } catch { /* already stopped */ } playing = null; }

async function bufferOf(dataId) {
  if (buffers.has(dataId)) return buffers.get(dataId);
  context ||= getContext();
  const response = await fetch(`/api/data/${dataId}`);
  if (!response.ok) throw new Error('A take could not be loaded.');
  const buffer = await context.decodeAudioData(await response.arrayBuffer());
  buffers.set(dataId, buffer);
  return buffer;
}

/**
 * Plays the mix of the heard lines for `bars` ({ firstBar, bars }) at `secondsPerBar`. A line without a take is named and the preview asks for a build.
 * Returns false when nothing could be played.
 */
export async function playMix(lines, bars, secondsPerBar, maxSeconds = 90) {
  stopMix();
  stopPlayer();
  const chosen = heard(lines);
  if (!chosen.length) { toast('Every line is muted: nothing to hear.'); return false; }
  const missing = chosen.filter((l) => !S.build.takes?.[takeBlockOf(l)]);
  if (missing.length) { toast(`Build first: no take yet for ${missing.map((l) => l.name).join(', ')}.`); return false; }
  const from = (bars?.firstBar || 1) - 1, count = bars?.bars || Math.max(1, Math.floor(maxSeconds / secondsPerBar));
  const seconds = Math.min(count * secondsPerBar, maxSeconds);
  try {
    const decoded = await Promise.all(chosen.map(async (l) => bufferOf(await audioDataId(S.build.takes[takeBlockOf(l)]))));
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
    node.connect(g).connect(context.destination);
    node.onended = () => { if (playing === node) playing = null; };
    node.start();
    playing = node;
    return true;
  } catch (e) {
    toast(e.message || String(e));
    return false;
  }
}
