// Blocks of the live chain that the browser makes itself instead of looping a built take: the built-in drums, the sampler (its notes pass the five MIDI transforms, which are plain
// functions of the note list) and the buffer operations cut, fade, shift, reverse and chop. They run on the same libraries as the "Build and play preview" (the exact seeded drums,
// the sampler worker), so what you hear is that preview's sound; when a knob moves the block is made again and swapped in at the same place of the loop (live-chain.js).
// Advisory: nothing is written or built here. @ample:live-local
import { getToken } from './api.js';
import { flat, S } from './store.js';
import { crossfadeAudio, cutAudio, fadeAudio, joinAudio, reverseAudio, shiftAudio, splitAudio } from '../lib/clientside-sample-ops.mjs';
import { clickAudio } from '../lib/clientside-click.mjs';
import { vocodeAudio } from '../lib/clientside-vocoder.mjs';
import { silenceAudio, trimAudio } from '../lib/clientside-trim.mjs';
import { normalizeAudio, normalizeGain } from '../lib/clientside-normalize.mjs';
import { pitchWorkingSetBytes, processPitchChannels } from '../lib/clientside-pitch.mjs';
import { processStretchChannels, stretchWorkingSetBytes } from '../lib/clientside-stretch.mjs';
import { granularWorkingSetBytes, processGranularChannels } from '../lib/clientside-granular.mjs';
import { denoiseWorkingSetBytes, processDenoiseChannels } from './studio/preview/denoise.js';
import { importAudio, previewAmbience, previewGuitar, previewPitchedSampleSet } from './clientside-preview.js';
import { renderSynthChannels } from '../lib/clientside-synth.mjs';
import { renderPianoChannels } from '../lib/clientside-piano.mjs';
import { renderBassChannels } from '../lib/clientside-bass.mjs';
import { renderSequenceChannels, sequencePreviewPlan } from '../lib/clientside-sequencer.mjs';
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
import { fireNotes, followSeries } from '../lib/control-follow.mjs';
import { curveLevel } from '../lib/control-live.mjs';
import { decodedInput, fetchTakeAudio } from './studio/preview/audio.js';
import { drumHits, hitAudio, noiseStream, renderHit } from '../lib/drum-core.mjs';

const RATE = 44100;
const infos = new Map();
/** A short note about a local block after it was made (normalize: the gain it applies), or ''. */
export const localInfo = (id) => infos.get(id) || '';
const cache = { seeds: new Map(), clips: new Map(), samples: new Map(), sequences: new Map() };

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

/** The notes a fire block makes from its control: a follow of a built sound (its take) or a curve, per millisecond. */
async function fireOf(chain, paramsOf) {
  const bars = songBars(), per = samplesPerBar(RATE), source = chain.fire.source;
  let values;
  if (chain.fire.tpl === 'curve') {
    const params = paramsOf(source), count = Math.floor(bars * per * 1000 / RATE) + 1;
    values = Float32Array.from({ length: count }, (_, k) => curveLevel(params, 1 + (k / 1000) * RATE / per) ?? 0);
  } else {
    const link = flat().links.find((l) => l.to === source && l.type === 'audio' && l.inName === 'in' && !l.disabled), take = link && S.build?.takes?.[link.from];
    if (!take) throw new Error(`Build ${link?.from || source} first: the fire block listens to its sound.`);
    const buffer = await decodedInput(`chain:${take}`, () => fetchTakeAudio(take)), data = followSeries(toAudio(buffer), paramsOf(source));
    if (!data) throw new Error('The browser cannot follow a sound with auto settings; build it.');
    values = data.values;
  }
  return fireNotes(values, paramsOf(chain.clip), { samplesPerBar: per, rate: RATE, bars });
}

/** The notes after the transforms, worked out now (synchronously apart from a seed that is asked once). */
export async function notesOf(chain, paramsOf) {
  let notes = chain.fire ? await fireOf(chain, paramsOf) : await clipNotes(chain.clip);
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

/** The bars the drums block plays: its placements, or the whole song. */
const drumBars = (node, bars) => (node?.parts?.length ? node.parts.flatMap((part) => Array.from({ length: part.length }, (_, k) => part.at + k)) : Array.from({ length: bars }, (_, k) => 1 + k)).filter((bar) => bar >= 1 && bar <= bars);
const hitBuffers = new Map(), noises = new Map();   // every hit is rendered once: the key is the sound (seed, kind, length, place in the noise), so a pattern change reuses the buffers it shares
/**
 * The built-in drums as single hits for the live chain: { rate, loop (seconds), hits: [{ at (seconds), kind, samples }] }. The hits are the exact ones of the offline render
 * (drum-core.mjs), so a pattern or song knob only changes which hits play and when; nothing is rendered again for the hits that were heard before.
 */
export async function drumVoicePlan(item, { paramsOf }) {
  const full = { ...paramsOf(item.id), tempo: tempo() }, bars = songBars(), node = flat().nodes.find((n) => n.id === item.id);
  if (!builtinDrumSongSupported(full)) throw new Error('The live drums play beat-A and beat-B (and their song sequence); other patterns are built by the server.');
  if (drumWorkingSetBytes(bars * samplesPerBar(RATE)) > 128 * 1024 * 1024) throw new Error('The drums are too long for the live preview.');
  const seed = String(await effectiveSeed(item.id));
  if (!noises.has(seed)) { if (noises.size > 4) noises.clear(); noises.set(seed, noiseStream(seed)); }
  const noise = noises.get(seed), plan = drumHits({ params: full, sampleRate: RATE, firstBar: 1, bars, activeBars: drumBars(node, bars), beatsPerBar: barBeats() });
  if (hitBuffers.size > 4000) hitBuffers.clear();
  const hits = plan.hits.map((hit) => {
    const key = [seed, hit.kind, hit.length, hit.fullLength, hit.tonal ? 0 : hit.noiseAt].join(':');
    if (!hitBuffers.has(key)) hitBuffers.set(key, hitAudio(renderHit(hit, noise)));
    return { at: hit.start / RATE, kind: hit.kind, samples: hitBuffers.get(key) };
  });
  return { rate: RATE, loop: plan.total / RATE, hits };
}

/**
 * Makes one local block. item: a plan item; paramsOf(id): the parameters now (song plus the preview of a knob); inputs: the audio of the blocks it works on ({ sampleRate, channels } by id).
 * Returns { sampleRate, channels }.
 */
export async function renderLocal(item, { paramsOf, inputs, context, signal }) {
  const params = paramsOf(item.id), bpm = tempo();
  if (item.tpl === 'midi') {
    // the whole midi file through the pack SoundFont in the browser (the server's built-in GM render of a midi block, played here); levels as the server: loudest sample at -6 dBFS
    const file = String(params.file || '').replace(/^"|"$/g, ''), response = await fetch(`/api/midi/notes?${new URLSearchParams({ file, track: String(params.track || 'all').replace(/^"|"$/g, '') })}`, { headers: { 'X-Ample-Token': getToken() }, signal });
    if (!response.ok) throw new Error(`The notes of ${file} could not be read.`);
    const data = await response.json();
    if (data.truncated || !data.ppq) throw new Error('This MIDI file is too large for the browser to play.');
    const seconds = (ticks) => ticks / data.ppq * (data.microsecondsPerQuarter / 1e6);
    const notes = data.notes.map((n) => ({ start: seconds(n.start), end: seconds(n.end), pitch: n.pitch, velocity: n.velocity, channel: n.channel, bank: n.bank, program: n.program })).sort((a, b) => a.start - b.start);
    const { localSf2 } = await import('../lib/sf2-local.js');
    const sf = await localSf2(RATE, getToken()), length = notes.reduce((m, n) => Math.max(m, n.end), 0);
    const [l, r] = sf.renderNotes(notes, length, { tail: 1.5 });
    return { sampleRate: RATE, channels: [l, r] };
  }
  if (item.tpl === 'sequencer') {
    // the lanes of an inline sequence, played by the tone renderer of the build preview; every output (mix, lane1..8) comes from one render, kept while the sequence and the song do not change
    const bars = songBars(), per = samplesPerBar(RATE), key = JSON.stringify([item.base, params.sequence, params.lanes, params.gain, params.channels, bars, per]);
    if (!cache.sequences.has(key)) {
      cache.sequences.clear();
      const plan = sequencePreviewPlan(params.sequence, params.lanes, 1, bars);
      if (!plan) throw new Error('The browser cannot play this sequence; it is built by the server.');
      cache.sequences.set(key, renderSequenceChannels(plan, params, renderSynthChannels, RATE, 1, bars, per, signal || new AbortController().signal));
    }
    const all = await cache.sequences.get(key);
    return all[item.port] || { sampleRate: RATE, channels: [new Float32Array(bars * per)] };
  }
  if (item.voice) {
    // an instrument that plays the notes of a clip: the same renderers as the "Build and play preview" (and the piano roll), made again when a knob or a note changes
    const bars = songBars(), per = samplesPerBar(RATE), node = flat().nodes.find((n) => n.id === item.id), notes = await notesOf(item.midi, paramsOf);
    let channels;
    if (item.voice === 'synth') channels = await renderSynthChannels(notes, params, RATE, 1, bars, per, signal);
    else if (item.voice === 'piano') channels = [await renderPianoChannels(notes, RATE, 1, bars, per, signal)];
    else if (item.voice === 'bass') channels = [await renderBassChannels(notes, RATE, 1, bars, per, signal)];
    else {
      const render = item.tpl === 'guitar' ? previewGuitar : previewPitchedSampleSet;
      return render({ ...node, params, parts: [] }, context, signal, { firstBar: 1, bars }, bars, async () => notes);
    }
    if (params.channels === 'mono' && channels.length > 1) channels = [Float32Array.from(channels[0], (v, i) => (v + channels[1][i]) * 0.5)];
    if (params.channels === 'stereo' && channels.length === 1) channels = [channels[0], channels[0]];
    return { sampleRate: RATE, channels };
  }
  if (item.tpl === 'silence') return silenceAudio(params, Math.round(songBars() * samplesPerBar(RATE) * (context?.sampleRate || RATE) / RATE), context?.sampleRate || RATE);
  if (item.tpl === 'click' || item.tpl === 'import-audio' || item.tpl === 'ambience') {
    // sources from nothing or from a file of the project, made by the same functions as the "Build and play preview"
    const node = flat().nodes.find((n) => n.id === item.id), bars = songBars();
    if (item.tpl === 'click') return clickAudio(params, node?.parts || [], samplesPerBar(RATE), RATE, 1, bars);
    const audio = item.tpl === 'import-audio' ? await importAudio(node, context, signal, new Map(), null, bars) : await previewAmbience(node, context, signal, null, bars);
    if (!audio) throw new Error(`${item.id} has no sound the browser can make.`);
    return audio;
  }
  if (item.tpl === 'vocoder') {
    // the voice (modulator) shapes the carrier; both come from the sounds the block works on, made again when a knob moves (2.4's second input idea, as two local inputs)
    const voice = inputs.get(item.inputs.find((l) => l.lane === 0)?.from), sound = inputs.get(item.inputs.find((l) => l.lane === 1)?.from);
    if (!voice || !sound) throw new Error('The vocoder needs a sound on both its modulator and its carrier.');
    return vocodeAudio(voice, sound, params, signal || new AbortController().signal);
  }
  if (item.tpl === 'join' || item.tpl === 'crossfade' || item.tpl === 'splitter') {
    const sounds = item.inputs.map((link) => ({ audio: inputs.get(link.from), lane: link.lane })).filter((entry) => entry.audio);
    if (!sounds.length) throw new Error(`${item.id} has no sound to work on.`);
    const rate = sounds[0].audio.sampleRate, bars = songBars();
    if (item.tpl === 'splitter') return splitAudio(sounds[0].audio)[item.port];
    if (item.tpl === 'crossfade') return crossfadeAudio(sounds.find((s) => s.lane === 0)?.audio, sounds.find((s) => s.lane === 1)?.audio, params, bpm, 1, bars, rate, signal);
    return joinAudio(sounds.map((s) => ({ audio: s.audio, index: s.lane + 1 })), params, bpm, bars, rate, signal);
  }
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
  if (item.tpl === 'stretch' || item.tpl === 'granular' || item.tpl === 'denoise') {
    // the browser libraries of the build preview, made again from the input when a knob moves (streaming versions are not built, see plan/local-preview/streaming-decision.md); a stretch changes the length of the loop
    if (params.enabled === 'off') return input;
    const over = (bytes) => { if (bytes > 128 * 1024 * 1024) throw new Error(`The ${item.tpl} block is too long for the live preview.`); };
    const stop = signal || new AbortController().signal;
    if (item.tpl === 'stretch') { over(stretchWorkingSetBytes(input.channels[0].length, input.channels.length)); return { sampleRate: input.sampleRate, channels: await processStretchChannels(input.channels, input.sampleRate, params, stop) }; }
    if (item.tpl === 'granular') { over(granularWorkingSetBytes(input.channels)); return { sampleRate: input.sampleRate, channels: await processGranularChannels(input.channels, input.sampleRate, params, await effectiveSeed(item.id), stop) }; }
    over(denoiseWorkingSetBytes(input.channels)); return { sampleRate: input.sampleRate, channels: await processDenoiseChannels(input.channels, input.sampleRate, params, stop) };
  }
  if (item.tpl === 'trim') { const cut = trimAudio(input, params); infos.set(item.id, cut.report.replace(/^trim: /, '').slice(0, 60)); return cut.audio; }
  if (item.tpl === 'pitch') {
    // the pitch of the build preview (granular or WSOLA in a worker), made again from its input when a knob moves; method voice and bend curves work as in the preview
    if (params.enabled === 'off') return input;
    if (pitchWorkingSetBytes(input.channels[0].length, input.channels.length, String(params.method || 'wsola').toLowerCase()) > 128 * 1024 * 1024) throw new Error('The pitch block is too long for the live preview.');
    return { sampleRate: input.sampleRate, channels: await processPitchChannels(input.channels, input.sampleRate, params, signal || new AbortController().signal, { tempo: bpm, firstBar: 1 }) };
  }
  if (item.tpl === 'normalize') {
    // the gain needs the whole sound, so the block is made again from its input when a knob moves (the measurement is the server's: peak, or BS.1770 loudness)
    const { gainDb } = params.enabled === 'off' ? { gainDb: 0 } : await normalizeGain(input, params, signal);
    infos.set(item.id, `${gainDb >= 0 ? '+' : ''}${gainDb.toFixed(1)} dB`);
    return normalizeAudio(input, params, signal);
  }
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
export function forgetLocal() { cache.seeds.clear(); cache.clips.clear(); cache.samples.clear(); cache.sequences.clear(); }
document.addEventListener('ample:open', forgetLocal);
