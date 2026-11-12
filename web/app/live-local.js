// Blocks of the live chain that the browser makes itself instead of looping a built take: the built-in drums, the sampler (its notes pass the five MIDI transforms, which are plain
// functions of the note list) and the buffer operations cut, fade, shift, reverse and chop. They run on the same libraries as the "Build and play preview" (the exact seeded drums,
// the sampler worker), so what you hear is that preview's sound; when a knob moves the block is made again and swapped in at the same place of the loop (live-chain.js).
// Advisory: nothing is written or built here. @ample:live-local
import { getToken } from './api.js';
import { flat, S } from './store.js';
import { cutAudio, fadeAudio, reverseAudio, shiftAudio } from '../lib/clientside-sample-ops.mjs';
import { chopAudio } from '../lib/clientside-chop.mjs';
import { rewindAudio } from '../lib/clientside-rewind.mjs';
import { processScratchChannels } from '../lib/clientside-scratch.mjs';
import { builtinDrumSongSupported, drumWorkingSetBytes, renderBuiltinDrumChannels } from '../lib/clientside-drums.mjs';
import { renderSamplerChannels, samplerWorkingSetBytes } from '../lib/clientside-sampler.mjs';
import { transposeMidiEvents } from '../lib/clientside-midi-transpose.mjs';
import { chordMidiEvents } from '../lib/clientside-midi-chord.mjs';
import { quantizeMidiEvents } from '../lib/clientside-midi-quantize.mjs';
import { humanizeMidiEvents } from '../lib/clientside-midi-humanize.mjs';
import { arpeggiateMidiEvents } from '../lib/clientside-midi-arpeggiator.mjs';
import { parseEffectiveGranularSeed } from '../lib/clientside-granular.mjs';
import { barBeats } from '../lib/meter.mjs';

const RATE = 44100;
const cache = { seeds: new Map(), clips: new Map(), samples: new Map() };

export const toAudio = (buffer) => ({ sampleRate: buffer.sampleRate, channels: Array.from({ length: buffer.numberOfChannels }, (_, c) => buffer.getChannelData(c).slice()) });
export function toBuffer(context, audio) {
  const length = audio.channels[0]?.length || 1, buffer = context.createBuffer(audio.channels.length || 1, length, audio.sampleRate);
  audio.channels.forEach((channel, c) => buffer.copyToChannel(channel, c));
  return buffer;
}

const tempo = () => Number(S.doc?.project?.tempo) || 120;
const samplesPerBar = (rate) => Math.floor(barBeats() * 60 * rate * 1000 / Math.round(tempo() * 1000));
/** The bars of the song (the longest block), the same count the "Build and play preview" uses. */
export const songBars = () => Math.max(0, ...flat().nodes.flatMap((node) => (node.parts?.length ? node.parts.map((part) => part.at + part.length - 1) : [Number(node.params?.bars) || 0]))) || 8;

async function effectiveSeed(block) {
  const key = `${S.path}#${block}`;
  if (!cache.seeds.has(key)) {
    const response = await fetch(`/api/control/seed?${new URLSearchParams({ path: S.path, block })}`, { headers: { 'X-Ample-Token': getToken() } });
    const seed = response.ok ? parseEffectiveGranularSeed(await response.text()) : null;
    if (seed === null) throw new Error(`AMPLE did not return an exact seed for ${block}.`);
    cache.seeds.set(key, seed);
  }
  return cache.seeds.get(key);
}

async function clipNotes(clip) {
  // the notes of a clip only change when its text does: the song text is the key
  const key = `${S.path}#${clip}#${JSON.stringify(flat().nodes.find((n) => n.id === clip)?.params)}`;
  if (!cache.clips.has(key)) {
    const response = await fetch('/api/midi/clip-notes', { method: 'POST', headers: { 'X-Ample-Token': getToken(), 'Content-Type': 'application/json' }, body: JSON.stringify({ path: S.path, block: clip }) });
    if (!response.ok) throw new Error(`The notes of ${clip} could not be read.`);
    const notes = String((await response.json()).text || '').trim().split(/\s+/).filter(Boolean).map((entry) => { const [bar, beat, beats, pitch, velocity] = entry.split(':').map(Number); return { bar, beat, beats, pitch, velocity, pan: 0 }; });
    if (!notes.every((note) => Object.values(note).every(Number.isFinite))) throw new Error(`The notes of ${clip} are not plain notes the browser can play.`);
    cache.clips.set(key, notes);
  }
  return cache.clips.get(key);
}

/** The notes after the transforms, worked out now (synchronously apart from a seed that is asked once). */
export async function notesOf(chain, paramsOf) {
  let notes = await clipNotes(chain.clip);
  for (const { id, tpl } of chain.transforms) {
    const params = paramsOf(id), off = params.enabled === 'off';
    if (tpl === 'transpose') notes = transposeMidiEvents({ notes }, params).notes;
    else if (tpl === 'chord') notes = chordMidiEvents({ notes }, params).notes;
    else if (tpl === 'quantize') notes = quantizeMidiEvents({ notes }, params).notes;
    else if (tpl === 'midi-humanize') notes = humanizeMidiEvents({ notes }, params, off ? '0' : await effectiveSeed(id)).notes;
    else if (tpl === 'arpeggiator') notes = arpeggiateMidiEvents({ notes }, params, off || params.pattern !== 'random' ? '0' : await effectiveSeed(id)).notes;
  }
  return notes;
}

async function sampleOf(file, context) {
  if (!cache.samples.has(file)) {
    const response = await fetch(`/api/project/audio?path=${encodeURIComponent(file)}`, { headers: { 'X-Ample-Token': getToken() } });
    if (!response.ok) throw new Error(`The sample ${file} could not be loaded.`);
    const bytes = await response.arrayBuffer();
    if (bytes.byteLength > 32 * 1024 * 1024) throw new Error(`The sample ${file} is too large for the live preview.`);
    cache.samples.set(file, context.decodeAudioData(bytes));
  }
  return cache.samples.get(file);
}

/**
 * Makes one local block. item: a plan item; paramsOf(id): the parameters now (song plus the preview of a knob); inputs: the audio of the blocks it works on ({ sampleRate, channels } by id).
 * Returns { sampleRate, channels }.
 */
export async function renderLocal(item, { paramsOf, inputs, context, signal }) {
  const params = paramsOf(item.id), bpm = tempo();
  if (item.tpl === 'drums') {
    const full = { ...params, tempo: bpm }, bars = songBars(), node = flat().nodes.find((n) => n.id === item.id);
    if (!builtinDrumSongSupported(full)) throw new Error('The live drums play beat-A and beat-B (and their song sequence); other patterns are built by the server.');
    if (drumWorkingSetBytes(bars * samplesPerBar(RATE)) > 128 * 1024 * 1024) throw new Error('The drums are too long for the live preview.');
    const activeBars = (node?.parts?.length ? node.parts.flatMap((part) => Array.from({ length: part.length }, (_, k) => part.at + k)) : Array.from({ length: bars }, (_, k) => 1 + k)).filter((bar) => bar >= 1 && bar <= bars);
    const channels = await renderBuiltinDrumChannels(full, await effectiveSeed(item.id), RATE, 1, bars, activeBars, signal);
    return { sampleRate: RATE, channels };
  }
  if (item.tpl === 'sampler') {
    const bars = songBars(), per = samplesPerBar(RATE), file = String(params.file || '').replace(/^"|"$/g, '').trim();
    if (params.enabled === 'off') return { sampleRate: RATE, channels: [new Float32Array(bars * per)] };
    const notes = await notesOf(item.midi, paramsOf), decoded = await sampleOf(file, context);
    if (samplerWorkingSetBytes(bars * per, decoded.numberOfChannels, decoded.length, decoded.numberOfChannels) > 128 * 1024 * 1024) throw new Error('The sampler is too large for the live preview.');
    const sampleChannels = Array.from({ length: decoded.numberOfChannels }, (_, c) => decoded.getChannelData(c).slice());
    const channels = await renderSamplerChannels(sampleChannels, decoded.sampleRate, notes, params, RATE, 1, bars, per, signal);
    return { sampleRate: RATE, channels: params.channels === 'mono' && channels.length > 1 ? [Float32Array.from(channels[0], (v, i) => (v + channels[1][i]) * 0.5)] : channels };
  }
  const input = inputs.get(item.inputs[0].from);
  if (!input) throw new Error(`${item.id} has no sound to work on.`);
  const bars = Math.max(1, Math.round((input.channels[0].length / input.sampleRate) / (barBeats() * 60 / bpm)));
  if (item.tpl === 'reverse') return params.enabled === 'off' ? input : reverseAudio(input, signal);
  if (item.tpl === 'cut') return cutAudio(input, params, signal);
  if (item.tpl === 'fade') return fadeAudio(input, params, bpm, 1, signal);
  if (item.tpl === 'shift') return shiftAudio(input, params, bpm, bars, signal);
  if (item.tpl === 'chop') return chopAudio(input, params, bpm, signal);
  if (item.tpl === 'rewind') {
    if (String(params.sound || '').replace(/^"|"$/g, '') === 'fx') throw new Error('A rewind block with sound fx needs a sample on its fx input; the live preview cannot do that yet.');
    return rewindAudio(input, params, samplesPerBar(input.sampleRate), null, signal).audio;
  }
  if (item.tpl === 'scratch') return { sampleRate: input.sampleRate, channels: await processScratchChannels(input.channels, params, input.sampleRate, samplesPerBar(input.sampleRate), 1, signal) };
  throw new Error(`${item.tpl} cannot be made live.`);
}

/** Forgets what was fetched for the open song (a different song, or the samples changed). */
export function forgetLocal() { cache.seeds.clear(); cache.clips.clear(); cache.samples.clear(); }
document.addEventListener('ample:open', forgetLocal);
