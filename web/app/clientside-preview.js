// Hybrid preview: evaluate mix-family blocks locally and ask the server only for upstream blocks without a local renderer.
import { api, getToken } from './api.js';
import { soloInPlan } from '../lib/live-chain-model.mjs';
import { flat, S, build, notify, stopBuild, logProject } from './store.js';
import { evaluate as evaluateNotePipeline, isExpression as isNotePipeline } from '../lib/note-pipeline.mjs';
import { playTransient } from './player.js';
import { toast } from './ui.js';
import { masterAudioBuffer, mixAudioBuffers, sendAudioBuffers } from '../lib/clientside-mix.mjs';
import { placeImportedAudio } from '../lib/clientside-import-audio.mjs';
import { crossfadeAudio, cutAudio, fadeAudio, joinAudio, reverseAudio, shiftAudio, splitAudio } from '../lib/clientside-sample-ops.mjs';
import { panAudio } from '../lib/clientside-pan.mjs';
import { widthAudio } from '../lib/clientside-width.mjs';
import { tremoloAudio } from '../lib/clientside-tremolo.mjs';
import { clickAudio } from '../lib/clientside-click.mjs';
import { peqAudio } from '../lib/clientside-peq.mjs';
import { volumeAudio } from '../lib/clientside-volume.mjs';
import { normalizeAudio } from '../lib/clientside-normalize.mjs';
import { delayAudio } from '../lib/clientside-delay.mjs';
import { deesserAudio } from '../lib/clientside-deesser.mjs';
import { vocodeAudio } from '../lib/clientside-vocoder.mjs';
import { chopAudio } from '../lib/clientside-chop.mjs';
import { distortionAudio } from '../lib/clientside-distortion.mjs';
import { duckAudio } from '../lib/clientside-duck.mjs';
import { eqAudio } from '../lib/clientside-eq.mjs';
import { filterAudio } from '../lib/clientside-filter.mjs';
import { bitcrusherAudio } from '../lib/clientside-bitcrusher.mjs';
import { chorusAudio } from '../lib/clientside-chorus.mjs';
import { flangerAudio } from '../lib/clientside-flanger.mjs';
import { phaserAudio } from '../lib/clientside-phaser.mjs';
import { meterAudio } from '../lib/clientside-meter.mjs';
import { limiterAudio } from '../lib/clientside-limiter.mjs';
import { compressorAudio } from '../lib/clientside-compressor.mjs';
import { gateAudio } from '../lib/clientside-gate.mjs';
import { denoiseWorkingSetBytes, processDenoiseChannels } from './studio/preview/denoise.js';
import { granularWorkingSetBytes, parseEffectiveGranularSeed, processGranularChannels } from '../lib/clientside-granular.mjs';
import { convolutionWorkingSetBytes, mapConvolutionImpulse, processConvolutionChannels } from '../lib/clientside-convolution.mjs';
import { processScratchChannels, scratchWorkingSetBytes } from '../lib/clientside-scratch.mjs';
import { rewindAudio } from '../lib/clientside-rewind.mjs';
import { applyCorrections, autotuneSettings, corrections, detectPitch, parseFixes } from '../lib/clientside-autotune.mjs';
import { clipBendCurve, pitchWorkingSetBytes, processPitchChannels } from '../lib/clientside-pitch.mjs';
import { bendChannels } from '../lib/clientside-autotune.mjs';
import { llamaCurve, llamaNotes, llamaSettings, renderLlama } from '../lib/clientside-delay-llama.mjs';
import { processStretchChannels, stretchWorkingSetBytes } from '../lib/clientside-stretch.mjs';
import { processReverbChannels, reverbWorkingSetBytes } from '../lib/clientside-reverb.mjs';
import { renderSynthChannels, synthWorkingSetBytes } from '../lib/clientside-synth.mjs';
import { bassWorkingSetBytes, renderBassChannels } from '../lib/clientside-bass.mjs';
import { renderSamplerChannels, samplerWorkingSetBytes } from '../lib/clientside-sampler.mjs';
import { renderSequenceChannels, sequencePreviewPlan, sequencerWorkingSetBytes } from '../lib/clientside-sequencer.mjs';
import { pianoWorkingSetBytes, renderPianoChannels } from '../lib/clientside-piano.mjs';
import { transposeMidiEvents } from '../lib/clientside-midi-transpose.mjs';
import { chordMidiEvents } from '../lib/clientside-midi-chord.mjs';
import { quantizeMidiEvents } from '../lib/clientside-midi-quantize.mjs';
import { humanizeMidiEvents } from '../lib/clientside-midi-humanize.mjs';
import { arpeggiateMidiEvents } from '../lib/clientside-midi-arpeggiator.mjs';
import { midiSourceEvents } from '../lib/clientside-midi-source.mjs';
import { ambienceWorkingSetBytes, processAmbienceChannels } from '../lib/clientside-ambience.mjs';
import { builtinDrumSongSupported, drumWorkingSetBytes, renderBuiltinDrumChannels } from '../lib/clientside-drums.mjs';
import { guitarSampleNotes, renderGuitarChannels } from '../lib/clientside-guitar.mjs';
import { pitchedSampleNotes, pitchedSampleWorkingSetBytes, renderPitchedSamples } from '../lib/clientside-pitched-samples.mjs';
import { barBeats } from '../lib/meter.mjs';
import { PREVIEW_RATE } from '../lib/preview-rate.mjs';

let controller = null;
let running = false;
const decodedMemoryLimit = 512 * 1024 * 1024;
export const clientPreviewRunning = () => running;
/** What the preview is busy with: how many of the blocks it needs are done, the block it works on now, and the phase after the last block. Null when no preview is being prepared. */
let progress = null;
export const clientPreviewProgress = () => progress;
export function cancelClientPreview() { controller?.abort(); if (running && S.build.running) stopBuild(); }

function encodeWav(audio) {
  const channels = audio.channels.length, frames = audio.channels[0].length, bytes = new ArrayBuffer(44 + frames * channels * 2), view = new DataView(bytes);
  const write = (at, text) => [...text].forEach((char, index) => view.setUint8(at + index, char.charCodeAt(0)));
  write(0, 'RIFF'); view.setUint32(4, bytes.byteLength - 8, true); write(8, 'WAVE'); write(12, 'fmt ');
  view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, channels, true);
  view.setUint32(24, audio.sampleRate, true); view.setUint32(28, audio.sampleRate * channels * 2, true);
  view.setUint16(32, channels * 2, true); view.setUint16(34, 16, true); write(36, 'data'); view.setUint32(40, frames * channels * 2, true);
  let offset = 44;
  for (let frame = 0; frame < frames; frame++) for (let channel = 0; channel < channels; channel++) {
    const value = Math.max(-1, Math.min(1, audio.channels[channel][frame]));
    view.setInt16(offset, Math.round(value * (value < 0 ? 32768 : 32767)), true); offset += 2;
  }
  return new Blob([bytes], { type: 'audio/wav' });
}

function cacheAudio(id, audio, cache) {
  const size = audio.channels.reduce((sum, channel) => sum + channel.byteLength, 0);
  const used = [...cache.values()].reduce((total, item) => total + item.channels.reduce((sum, channel) => sum + channel.byteLength, 0), 0);
  if (used + size > decodedMemoryLimit) throw new Error('This full-length client preview exceeds the 512 MiB audio buffer limit. Choose a shorter arrangement range or build and play the server render.');
  cache.set(id, audio);
  return audio;
}

// block -> take id for the blocks that already have a preview take. One dry-run request tells which they are, so only the blocks that really need rendering start a build (each build is a line block in the build log and costs about half a second).
// the last finished preview: pressing play again with nothing changed (same song text, same takes) plays it at once instead of preparing it again
let lastPreview = null;
let plannedTakes = null;
async function planTakes(signal) {
  if (S.range) return null;   // the dry run says nothing about a range of bars
  try {
    const plan = await api.get(`/api/build/status?path=${encodeURIComponent(S.path)}&intent=preview`, signal);
    return plan.ok ? { takes: new Map((plan.blocks || []).filter((block) => block.status === 'cached' && block.takeId).map((block) => [block.block, block.takeId])), signature: JSON.stringify((plan.blocks || []).map((block) => [block.block, block.status, block.takeId])) } : null;
  } catch { return null; }
}

async function buildAudio(node, context, signal, cache, serverSources) {
  if (cache.has(node.id)) return cache.get(node.id);
  signal.throwIfAborted();
  let takeId = plannedTakes?.get(node.id);
  if (!takeId) {
    const report = await build(node.id, S.range || null, 'preview');
    signal.throwIfAborted();
    if (!report) throw new Error(`Could not prepare server audio for “${node.id}”. Check the build log.`);
    const item = (report.blocks || []).find((block) => (block.block ?? block.Block) === node.id);
    const status = item?.status ?? item?.Status;
    if (status === 'failed' || status === 'skipped' || status === 'waiting') throw new Error(`Server audio for “${node.id}” is ${status}.`);
    takeId = item?.takeId ?? item?.TakeId ?? S.build.takes[node.id];
  }
  if (!takeId) throw new Error(`The server did not return audio for “${node.id}”.`);
  const response = await fetch(`/api/takes/${encodeURIComponent(takeId)}/download?format=wav`, { headers: { 'X-Ample-Token': getToken() }, signal });
  if (!response.ok) throw new Error(`Could not read the stored audio for “${node.id}” (${response.status}).`);
  const decoded = await context.decodeAudioData(await response.arrayBuffer());
  const audio = { sampleRate: decoded.sampleRate, channels: Array.from({ length: decoded.numberOfChannels }, (_, channel) => decoded.getChannelData(channel).slice()) };
  serverSources.add(node.id);
  return cacheAudio(node.id, audio, cache);
}

async function effectiveSeed(block, signal) {
  const query = new URLSearchParams({ path: S.path, block });
  const response = await fetch(`/api/control/seed?${query}`, { headers: { 'X-Ample-Token': getToken() }, signal });
  if (!response.ok) throw new Error(`Could not read the effective seed for “${block}” (${response.status}).`);
  const seed = parseEffectiveGranularSeed(await response.text());
  if (seed === null) throw new Error(`AMPLE did not return an exact seed for “${block}”.`);
  return seed;
}

async function fetchConvolutionImpulse(block, params, context, sampleRate, signal) {
  const response = await fetch('/api/effects/impulse', { method: 'POST', headers: { 'X-Ample-Token': getToken(), 'Content-Type': 'application/json' }, body: JSON.stringify({ path: S.path, block, params }), signal });
  if (!response.ok || Number(response.headers.get('content-length')) > 8 * 1024 * 1024) throw new Error(`The selected impulse for “${block}” is unavailable for local preview.`);
  const bytes = await response.arrayBuffer();
  if (bytes.byteLength > 8 * 1024 * 1024) throw new Error(`The selected impulse for “${block}” is too large for local preview.`);
  const decoded = await context.decodeAudioData(bytes);
  return mapConvolutionImpulse(Array.from({ length: decoded.numberOfChannels }, (_, channel) => decoded.getChannelData(channel)), decoded.sampleRate, sampleRate, params);
}

async function fetchClipNotes(node, signal, cache) {
  if (cache.has(node.id)) return cache.get(node.id);
  const source = String(node.params.notes || '').trim();
  if (source && isNotePipeline(source)) {
    // a note pipeline: worked out here when it is plain (the same exact maths as the server); anything else is left to the server render
    const { pipelineSources, masterSeedOfSong, streamsOf } = await import('./roll-views.js');
    const made = evaluateNotePipeline(source, { sources: pipelineSources(), tempo: Number(S.doc?.project?.tempo) || 120, beatsPerBar: Number(S.doc?.project?.beatsPerBar) || 4, limit: 20000, master: masterSeedOfSong(), streams: streamsOf(node) });
    const plain = made.notes !== null && made.notes.split(/\s+/).filter(Boolean).every((entry) => /^\d+:\d+(?:\.\d{1,6})?:\d+(?:\.\d{1,6})?:\d+:\d+$/.test(entry));
    const notes = plain ? made.notes.split(/\s+/).filter(Boolean).map((entry) => { const [bar, beat, beats, pitch, velocity] = entry.split(':').map(Number); return { bar, beat, beats, pitch, velocity, pan: 0 }; }) : null;
    cache.set(node.id, notes);
    return notes;
  }
  if (source && !source.split(/\s+/).every((entry) => /^\d+:\d+(?:\.\d{1,6})?:\d+(?:\.\d{1,6})?:\d+:\d+$/.test(entry))) { cache.set(node.id, null); return null; }
  const response = await fetch('/api/midi/clip-notes', { method: 'POST', headers: { 'X-Ample-Token': getToken(), 'Content-Type': 'application/json' }, body: JSON.stringify({ path: S.path, block: node.id }), signal });
  if (!response.ok) { cache.set(node.id, null); return null; }
  const data = await response.json();
  const notes = String(data.text || '').trim().split(/\s+/).filter(Boolean).map((entry) => {
    const [bar, beat, beats, pitch, velocity] = entry.split(':').map(Number);
    return { bar, beat, beats, pitch, velocity, pan: 0 };
  });
  const result = notes.every((note) => Object.values(note).every(Number.isFinite)) ? notes : null;
  cache.set(node.id, result);
  return result;
}

async function fetchMidiSourceNotes(node, signal, cache) {
  if (cache.has(node.id)) return cache.get(node.id);
  const file = String(node.params.file || '').replace(/^"|"$/g, ''), track = String(node.params.track || 'all').replace(/^"|"$/g, '');
  if (!file) { cache.set(node.id, null); return null; }
  const query = new URLSearchParams({ file, track });
  const response = await fetch(`/api/midi/notes?${query}`, { headers: { 'X-Ample-Token': getToken() }, signal });
  if (!response.ok) { cache.set(node.id, null); return null; }
  const data = await response.json();
  const events = midiSourceEvents(data, node.parts, Number(node.params.bars) || 8);
  const result = events && events.length <= 20000 ? events : null;
  cache.set(node.id, result);
  return result;
}

export async function importAudio(node, context, signal, cache, range, totalBars) {
  if (cache.has(node.id)) return cache.get(node.id);
  const path = String(node.params.file || '');
  const response = await fetch(`/api/project/audio?path=${encodeURIComponent(path)}`, { headers: { 'X-Ample-Token': getToken() }, signal });
  if (!response.ok) throw new Error(`Could not read project audio for “${node.id}” (${response.status}).`);
  const bytes = await response.arrayBuffer();
  if (bytes.byteLength > decodedMemoryLimit) throw new Error('This project audio is too large for a local preview.');
  const decoded = await context.decodeAudioData(bytes);
  const sampleRate = PREVIEW_RATE, Offline = window.OfflineAudioContext || window.webkitOfflineAudioContext;
  const projectedFrames = Math.ceil(decoded.duration * sampleRate), projectedBytes = projectedFrames * Float32Array.BYTES_PER_ELEMENT;
  const used = [...cache.values()].reduce((total, item) => total + item.channels.reduce((sum, channel) => sum + channel.byteLength, 0), 0);
  if (used + projectedBytes > decodedMemoryLimit) throw new Error('This project audio is too large for a local preview.');
  let mono = decoded;
  if (decoded.numberOfChannels > 1) {
    const inputs = Array.from({ length: decoded.numberOfChannels }, (_, channel) => decoded.getChannelData(channel)), output = context.createBuffer(1, decoded.length, decoded.sampleRate), monoChannel = output.getChannelData(0);
    for (let frame = 0; frame < decoded.length; frame++) for (const input of inputs) monoChannel[frame] += input[frame] / inputs.length;
    mono = output;
  }
  let source = mono;
  if (mono.sampleRate !== sampleRate) {
    if (!Offline) throw new Error('Local sample-rate conversion is unavailable in this browser.');
    const resampler = new Offline(1, projectedFrames, sampleRate), player = resampler.createBufferSource();
    player.buffer = mono; player.connect(resampler.destination); player.start(); source = await resampler.startRendering();
  }
  const audio = { sampleRate, channels: Array.from({ length: source.numberOfChannels }, (_, channel) => source.getChannelData(channel).slice()) };
  const tempoMilli = Math.round((Number(S.doc?.project?.tempo) || 92) * 1000), samplesPerBar = Math.floor(barBeats() * 60 * sampleRate * 1000 / tempoMilli), partAt = node.parts[0]?.at || 1;
  return cacheAudio(node.id, placeImportedAudio(audio, node.params, samplesPerBar, totalBars, range, partAt, sampleRate), cache);
}

export async function previewAmbience(node, context, signal, range, totalBars) {
  if (String(node.params.enabled || 'on') === 'off') {
    const firstBar = range?.firstBar || 1, bars = range?.bars || totalBars, tempoMilli = Math.round((Number(S.doc?.project?.tempo) || 92) * 1000), samplesPerBar = Math.floor(barBeats() * 60 * PREVIEW_RATE * 1000 / tempoMilli);
    return { sampleRate: PREVIEW_RATE, channels: [new Float32Array(bars * samplesPerBar)] };
  }
  if (node.parts.length !== 1) throw new Error('Local ambience preview currently needs one continuous block placement.');
  const file = String(node.params.file || '').replace(/^"|"$/g, ''), response = await fetch(`/api/project/audio?path=${encodeURIComponent(file)}`, { headers: { 'X-Ample-Token': getToken() }, signal });
  if (!response.ok) throw new Error(`Could not read ambience source for “${node.id}” (${response.status}).`);
  const bytes = await response.arrayBuffer();
  if (bytes.byteLength > decodedMemoryLimit) throw new Error('The ambience source is too large for local preview.');
  const decoded = await context.decodeAudioData(bytes), sampleRate = PREVIEW_RATE, Offline = window.OfflineAudioContext || window.webkitOfflineAudioContext;
  if (decoded.numberOfChannels > 2) throw new Error('Local ambience preview supports mono or stereo sources.');
  const channelsAtTarget = decoded.sampleRate === sampleRate ? decoded.numberOfChannels : 1, projectedFrames = Math.ceil(decoded.duration * sampleRate);
  let input = decoded;
  if (channelsAtTarget === 1 && decoded.numberOfChannels > 1) {
    input = context.createBuffer(1, decoded.length, decoded.sampleRate);
    const mono = input.getChannelData(0);
    for (let frame = 0; frame < decoded.length; frame++) for (let channel = 0; channel < decoded.numberOfChannels; channel++) mono[frame] += decoded.getChannelData(channel)[frame] / decoded.numberOfChannels;
  }
  let source = input;
  if (input.sampleRate !== sampleRate) {
    if (!Offline) throw new Error('Local ambience sample-rate conversion is unavailable in this browser.');
    const resampler = new Offline(channelsAtTarget, projectedFrames, sampleRate), player = resampler.createBufferSource();
    player.buffer = input; player.connect(resampler.destination); player.start(); source = await resampler.startRendering();
  }
  const firstBar = range?.firstBar || 1, bars = range?.bars || totalBars, tempoMilli = Math.round((Number(S.doc?.project?.tempo) || 92) * 1000), samplesPerBar = Math.floor(barBeats() * 60 * sampleRate * 1000 / tempoMilli), outputFrames = bars * samplesPerBar;
  const part = node.parts[0], offset = (firstBar - part.at) * samplesPerBar, inputChannels = Array.from({ length: source.numberOfChannels }, (_, channel) => source.getChannelData(channel).slice());
  if (ambienceWorkingSetBytes(inputChannels[0].length, outputFrames, inputChannels.length, offset) > 128 * 1024 * 1024) throw new Error('This ambience preview exceeds its 128 MiB working-set limit.');
  const seed = await effectiveSeed(node.id, signal), channels = await processAmbienceChannels(inputChannels, node.params, seed, sampleRate, outputFrames, offset, signal);
  const activeStart = Math.min(outputFrames, Math.max(0, -offset)), activeEnd = Math.min(outputFrames, Math.max(0, part.length * samplesPerBar - offset));
  for (const channel of channels) { channel.fill(0, 0, activeStart); channel.fill(0, Math.max(activeStart, activeEnd)); }
  return { sampleRate, channels };
}

export async function previewGuitar(node, context, signal, range, totalBars, resolveMidiNotes) {
  const folder = String(node.params.set || '').trim().replace(/^"|"$/g, '');
  if (!folder || folder.startsWith('pack:') || folder.toLowerCase().endsWith('.sfz')) throw new Error('Local guitar preview currently needs a project folder of note-named mono WAV samples.');
  const listing = await fetch(`/api/project/tree?path=${encodeURIComponent(folder)}&depth=0`, { headers: { 'X-Ample-Token': getToken() }, signal });
  if (!listing.ok) throw new Error(`Could not list the guitar sample folder (${listing.status}).`);
  const children = (await listing.json()).children || [];
  if (children.some((file) => file.name.toLowerCase() === 'set.json')) throw new Error('Local guitar preview currently needs a sample folder without set.json tuning, loops or envelope settings.');
  const files = children.filter((file) => !file.isDir && file.name.toLowerCase().endsWith('.wav'));
  const sampleMap = guitarSampleNotes(files);
  if (!sampleMap) throw new Error('Local guitar preview needs one plain note-named WAV per note, without velocity layers or round robins.');
  if (files.some((file) => file.sizeBytes > 16 * 1024 * 1024) || files.reduce((sum, file) => sum + file.sizeBytes, 0) > 32 * 1024 * 1024) throw new Error('The guitar sample set is too large for local preview.');
  const samples = await Promise.all([...sampleMap].map(async ([pitch, file]) => {
    const response = await fetch(`/api/project/audio?path=${encodeURIComponent(file.path)}`, { headers: { 'X-Ample-Token': getToken() }, signal });
    if (!response.ok) throw new Error(`Could not read guitar sample “${file.name}” (${response.status}).`);
    const decoded = await context.decodeAudioData(await response.arrayBuffer());
    if (decoded.numberOfChannels !== 1) throw new Error(`Guitar sample “${file.name}” must be mono for local preview.`);
    return { pitch, sourceRate: decoded.sampleRate, channels: [decoded.getChannelData(0).slice()] };
  }));
  const graph = flat(), link = graph.links.find((item) => item.to === node.id && item.type === 'midi' && item.inName === 'notes' && !item.disabled);
  const source = link ? graph.nodes.find((item) => item.id === link.from) : null;
  const notes = await resolveMidiNotes(source);
  if (!notes || notes.length > 20000) throw new Error('Local guitar preview needs at most 20,000 supported MIDI notes.');
  const firstBar = range?.firstBar || 1, bars = range?.bars || totalBars, sampleRate = 44100;
  const samplesPerBar = Math.floor(barBeats() * 60 * sampleRate * 1000 / Math.round((Number(S.doc?.project?.tempo) || 120) * 1000));
  const activeBars = node.parts.length ? node.parts.flatMap((part) => Array.from({ length: part.length }, (_, index) => part.at + index).filter((bar) => bar >= firstBar && bar < firstBar + bars)) : Array.from({ length: bars }, (_, index) => firstBar + index);
  const channels = await renderGuitarChannels(samples, notes, node.params, sampleRate, firstBar, bars, samplesPerBar, activeBars, signal);
  return { sampleRate, channels: node.params.channels === 'stereo' ? [channels[0], channels[0].slice()] : channels };
}

export async function previewPitchedSampleSet(node, context, signal, range, totalBars, resolveMidiNotes) {
  const mode = node.tpl, folder = String(node.params.set || '').trim().replace(/^"|"$/g, '');
  if (!folder || folder.startsWith('pack:') || folder.toLowerCase().endsWith('.sfz')) throw new Error(`Local ${mode} preview needs a project folder of note-named mono WAV samples.`);
  const listing = await fetch(`/api/project/tree?path=${encodeURIComponent(folder)}&depth=0`, { headers: { 'X-Ample-Token': getToken() }, signal });
  if (!listing.ok) throw new Error(`Could not list the ${mode} sample folder (${listing.status}).`);
  const children = (await listing.json()).children || [];
  if (children.some((file) => ['set.json'].includes(file.name.toLowerCase()) || file.name.toLowerCase().endsWith('.sfz'))) throw new Error(`Local ${mode} preview currently needs a plain WAV sample folder without set.json or SFZ mappings.`);
  const files = children.filter((file) => !file.isDir && file.name.toLowerCase().endsWith('.wav'));
  const sampleMap = pitchedSampleNotes(files);
  if (!sampleMap) throw new Error(`Local ${mode} preview needs one plain note-named WAV per pitch, without layers or round robins.`);
  if (files.some((file) => file.sizeBytes > 16 * 1024 * 1024) || files.reduce((sum, file) => sum + file.sizeBytes, 0) > 32 * 1024 * 1024) throw new Error(`The ${mode} sample set is too large for local preview.`);
  const samples = await Promise.all([...sampleMap].map(async ([pitch, file]) => {
    const response = await fetch(`/api/project/audio?path=${encodeURIComponent(file.path)}`, { headers: { 'X-Ample-Token': getToken() }, signal });
    if (!response.ok) throw new Error(`Could not read ${mode} sample “${file.name}” (${response.status}).`);
    const decoded = await context.decodeAudioData(await response.arrayBuffer());
    if (decoded.numberOfChannels !== 1) throw new Error(`${mode} sample “${file.name}” must be mono for local preview.`);
    return { pitch, sourceRate: decoded.sampleRate, channels: [decoded.getChannelData(0).slice()] };
  }));
  const graph = flat(), link = graph.links.find((item) => item.to === node.id && item.type === 'midi' && item.inName === 'notes' && !item.disabled);
  const source = link ? graph.nodes.find((item) => item.id === link.from) : null, notes = await resolveMidiNotes(source);
  if (!notes || notes.length > 20000) throw new Error(`Local ${mode} preview needs at most 20,000 supported MIDI notes.`);
  const firstBar = range?.firstBar || 1, bars = range?.bars || totalBars, sampleRate = 44100;
  const samplesPerBar = Math.floor(barBeats() * 60 * sampleRate * 1000 / Math.round((Number(S.doc?.project?.tempo) || 120) * 1000));
  if (pitchedSampleWorkingSetBytes(bars * samplesPerBar, samples.reduce((sum, sample) => sum + sample.channels[0].length, 0)) > 128 * 1024 * 1024) throw new Error(`The ${mode} sample set exceeds the local preview memory limit.`);
  const activeBars = node.parts.length ? node.parts.flatMap((part) => Array.from({ length: part.length }, (_, index) => part.at + index)) : Array.from({ length: bars }, (_, index) => firstBar + index);
  const activeNotes = notes.filter((note) => activeBars.includes(note.bar));
  const channel = await renderPitchedSamples(samples, activeNotes, mode, node.params.gain, sampleRate, firstBar, bars, samplesPerBar, signal);
  return { sampleRate, channels: node.params.channels === 'stereo' ? [channel, channel.slice()] : [channel] };
}

export async function buildAndPlayClientPreview() {
  if (running || S.build.running || !S.doc || S.peek || S.doc.diagnostics?.some((item) => item.severity === 'error')) return;
  const initialPath = S.path, initialText = S.doc.text, initialRange = S.range ? { ...S.range } : null;
  const graph = flat(), nodes = new Map(graph.nodes.map((node) => [node.id, node])), memo = new Map(), audioCache = new Map();
  for (const link of graph.links) if (link.type === 'any') {
    const from = nodes.get(link.from), to = nodes.get(link.to);
    if (from?.tpl === 'group' && to?.ins.some((port) => port[0] === link.inName && port[1] === 'audio') || to?.tpl === 'group' && from?.outs.some((port) => port[0] === link.outName && port[1] === 'audio')) link.type = 'audio';
  }
  const serverSources = new Set(), localApproximationSources = new Set(), localEffectApproximations = new Set(), localExactSources = new Set(), localMidiTransforms = new Set(), localInstrumentFallbackReasons = new Set(), midiCache = new Map(), midiResults = new Map(), meterReports = [], totalBars = Math.max(0, ...graph.nodes.flatMap((node) => node.parts.length ? node.parts.map((part) => part.at + part.length - 1) : [Number(node.params.bars) || 0])) || 8;
  const isDisabled = (node) => !!node.disabledBy || node.flags?.includes('disabled');
  const target = [...graph.nodes].reverse().find((node) => node.tpl === 'master' && !isDisabled(node)) || [...graph.nodes].reverse().find((node) => ['mixer', 'bus', 'send'].includes(node.tpl) && !isDisabled(node));
  if (!target) { toast('This song has no enabled master or mix block to preview.'); return; }
  controller = new AbortController(); running = true;
  const needed = new Set([target.id]);   // the blocks this preview needs: everything that leads into the target
  {
    const queue = [target.id];
    while (queue.length) { const id = queue.pop(); for (const link of graph.links) if (link.to === id && !link.disabled && !needed.has(link.from) && nodes.get(link.from) && !isDisabled(nodes.get(link.from))) { needed.add(link.from); queue.push(link.from); } }
    progress = { total: needed.size, done: new Set(), active: [], phase: '' };
  }
  let touchQueued = false;
  const touch = () => { if (touchQueued) return; touchQueued = true; requestAnimationFrame(() => { touchQueued = false; notify(); }); };
  notify();
  let context;
  const active = new Set();
  const soloBus = soloInPlan(graph.nodes.filter((node) => !isDisabled(node)), needed);   // a solo bus that does not lead into the target cannot silence it
  try {
    context = new AudioContext();
    const plan = await planTakes(controller.signal);
    plannedTakes = plan?.takes ?? null;
    const planKey = plan ? JSON.stringify([S.path, initialText, plan.signature]) : null;   // without a plan (a range of bars is chosen) nothing is reused
    if (planKey && lastPreview?.key === planKey) {
      progress.phase = 'starting playback'; touch();
      await playTransient(lastPreview.blob);
      logProject('client preview', 'preview', 'Played the previous preview again: the song and its takes did not change.');
      toast('Played the previous preview again: nothing changed.');
      return;
    }
    const resolveMidiNotes = async (node) => {
      if (!node || isDisabled(node)) return null;
      if (midiResults.has(node.id)) return midiResults.get(node.id);
      if (node.tpl === 'midi-clip') return fetchClipNotes(node, controller.signal, midiCache);
      if (node.tpl === 'midi') {
        const notes = await fetchMidiSourceNotes(node, controller.signal, midiCache);
        if (notes) localExactSources.add(node.id);
        else localInstrumentFallbackReasons.add(`${node.id}: its MIDI note data is unavailable or exceeds the local preview limit`);
        midiResults.set(node.id, notes);
        return notes;
      }
      const link = graph.links.find((item) => item.to === node.id && item.type === 'midi' && item.inName === 'notes' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
      const upstream = link ? nodes.get(link.from) : null;
      let notes = await resolveMidiNotes(upstream);
      if (!notes || notes.length > 20000) { midiResults.set(node.id, null); return null; }
      if (node.tpl === 'transpose') {
        notes = transposeMidiEvents({ notes }, node.params).notes;
        localMidiTransforms.add(node.id);
      } else if (node.tpl === 'chord') {
        notes = chordMidiEvents({ notes }, node.params).notes;
        localMidiTransforms.add(node.id);
      } else if (node.tpl === 'quantize') {
        notes = quantizeMidiEvents({ notes }, node.params).notes;
        localMidiTransforms.add(node.id);
      } else if (node.tpl === 'midi-humanize') {
        const seed = node.params.enabled === 'off' ? '0' : await effectiveSeed(node.id, controller.signal);
        notes = humanizeMidiEvents({ notes }, node.params, seed).notes;
        localMidiTransforms.add(node.id);
      } else if (node.tpl === 'arpeggiator') {
        const seed = node.params.enabled === 'off' || node.params.pattern !== 'random' ? '0' : await effectiveSeed(node.id, controller.signal);
        notes = arpeggiateMidiEvents({ notes }, node.params, seed).notes;
        localMidiTransforms.add(node.id);
      } else if (node.tpl !== 'midi-clip') { midiResults.set(node.id, null); return null; }
      midiResults.set(node.id, notes);
      return notes;
    };
    // a block that plays the notes of a bent midi-clip (its bend curve, the ;bend= of notes) is heard bent, like the engine does it
    const bent = new Map();
    const withBend = (id, output, result) => {
      if (!result?.channels?.length) return result;
      const link = flat().links.find((item) => item.to === id && item.type === 'midi' && !item.disabled), clip = link && nodes.get(link.from);
      if (!clip || clip.tpl !== 'midi-clip' || !/auto\(|;bend=/.test(`${clip.params.bend ?? ''} ${clip.params.notes ?? ''}`)) return result;
      const key = `${id}:${output || ''}`;
      if (bent.get(key)?.from === result) return bent.get(key).audio;
      const curve = clipBendCurve(clip.params, result.sampleRate, Number(S.doc?.project?.tempo) || 92, S.range?.firstBar || 1, result.channels[0].length);
      const audio = curve ? { ...result, channels: bendChannels(result.channels, result.sampleRate, (position) => curve[Math.min(curve.length - 1, Math.max(0, Math.round(position)))], false) } : result;
      bent.set(key, { from: result, audio });
      return audio;
    };
    const resolve = async (id, output) => {
      const node = nodes.get(id);
      const fresh = !!node && !isDisabled(node) && !memo.has(`${id}:${output || ''}`) && !progress.done.has(id);
      if (fresh) { progress.active.push(id); touch(); }
      try { return withBend(id, output, await resolveNode(id, output)); }
      finally { if (fresh) { progress.active.splice(progress.active.lastIndexOf(id), 1); progress.done.add(id); touch(); } }
    };
    const resolveNode = async (id, output) => {
      controller.signal.throwIfAborted();
      const node = nodes.get(id);
      if (!node || isDisabled(node)) return null;
      const key = `${id}:${output || ''}`;
      if (memo.has(key)) return memo.get(key);
      if (active.has(key)) throw new Error(`The client preview found a cycle at “${id}”.`);
      active.add(key);
      try {
      if (node.tpl === 'crossfade') {
        const sources = {};
        for (const link of graph.links.filter((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}))) sources[link.inName] = await resolve(link.from, link.outName);
        const sourceRate = sources.a?.sampleRate || sources.b?.sampleRate || PREVIEW_RATE;
        const result = await crossfadeAudio(sources.a, sources.b, node.params, Number(S.doc?.project?.tempo) || 120, S.range?.firstBar || 1, totalBars, sourceRate, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'join') {
        const inputs = [];
        for (const link of graph.links.filter((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {})).sort((a, b) => a.inp - b.inp)) {
          const audio = await resolve(link.from, link.outName);
          if (audio) inputs.push({ audio, index: Number(/^in(\d+)$/.exec(link.inName)?.[1] || inputs.length + 1) });
        }
        const result = await joinAudio(inputs, node.params, Number(S.doc?.project?.tempo) || 120, totalBars, inputs[0]?.audio.sampleRate || PREVIEW_RATE, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'splitter') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = splitAudio(source);
        for (const [port, audio] of Object.entries(result)) memo.set(`${id}:${port}`, audio);
        return result[output];
      }
      if (node.tpl === 'pan' || node.tpl === 'width') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = node.tpl === 'pan' ? await panAudio(source, node.params, controller.signal) : await widthAudio(source, node.params, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'peq') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = await peqAudio(source, node.params, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'tremolo') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = await tremoloAudio(source, node.params, Number(S.doc?.project?.tempo) || 120, S.range?.firstBar || 1, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'volume' && !/^(?:auto|fadein|fadeout)\s*\(/i.test(String(node.params.gain || ''))) {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = volumeAudio(source, node.params);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'normalize' && (node.params.mode || 'peak') === 'peak') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = await normalizeAudio(source, node.params, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'delay' && !/^(?:auto|lfo|env)\s*\(/i.test(String(node.params.mix || ''))) {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = await delayAudio(source, node.params, controller.signal, Number(S.doc?.project?.tempo) || 120);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'chop') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        try {
          const result = await chopAudio(source, node.params, Number(S.doc?.project?.tempo) || 120, controller.signal);
          memo.set(key, result);
          return result;
        } catch (error) {
          // a pattern or a list of cuts the browser cannot read: the build says why
          controller.signal.throwIfAborted();
          const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
          memo.set(key, audio);
          return audio;
        }
      }
      if (node.tpl === 'vocoder') {
        // two inputs: the voice (modulator) and the sound that speaks (carrier), both worked out in the browser
        const find = (name) => graph.links.find((item) => item.to === id && item.inName === name && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const voiceLink = find('modulator'), carrierLink = find('carrier');
        const voice = voiceLink ? await resolve(voiceLink.from, voiceLink.outName) : null, sound = carrierLink ? await resolve(carrierLink.from, carrierLink.outName) : null;
        if (!voice || !sound || voice.sampleRate !== sound.sampleRate) { memo.set(key, null); return null; }
        const result = await vocodeAudio(voice, sound, node.params, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'deesser') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = await deesserAudio(source, node.params, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'distortion' && !/^(?:auto|lfo|env)\s*\(/i.test(String(node.params.mix || ''))) {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = await distortionAudio(source, node.params, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'duck') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = await duckAudio(source, node.params, Number(S.doc?.project?.tempo) || 120, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'eq') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = await eqAudio(source, node.params, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'filter' && !/^(?:auto|lfo|env)\s*\(/i.test(String(node.params.cutoff || '')) && !/^(?:auto|lfo|env)\s*\(/i.test(String(node.params.resonance || ''))) {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = await filterAudio(source, node.params, Number(S.doc?.project?.tempo) || 120, S.range?.firstBar || 1, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'bitcrusher' && !/^(?:auto|lfo|env)\s*\(/i.test(String(node.params.mix || ''))) {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = await bitcrusherAudio(source, node.params, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'chorus' && !/^(?:auto|lfo|env)\s*\(/i.test(String(node.params.mix || ''))) {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = await chorusAudio(source, node.params, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'flanger' && !/^(?:auto|lfo|env)\s*\(/i.test(String(node.params.mix || ''))) {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = await flangerAudio(source, node.params, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'phaser' && !/^(?:auto|lfo|env)\s*\(/i.test(String(node.params.mix || ''))) {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = await phaserAudio(source, node.params, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'meter') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const measured = await meterAudio(source, node.params, controller.signal);
        if (measured.report) meterReports.push(`${node.id}: ${measured.report}`);
        memo.set(key, measured.audio);
        return measured.audio;
      }
      if (node.tpl === 'limiter') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = await limiterAudio(source, node.params, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'compressor' && !/^(?:auto|lfo|env)\s*\(/i.test(String(node.params.threshold || ''))) {
        const audioLink = graph.links.find((item) => item.to === id && item.type === 'audio' && item.inName === 'in' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const keyLink = graph.links.find((item) => item.to === id && item.type === 'audio' && item.inName === 'key' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = audioLink ? await resolve(audioLink.from, audioLink.outName) : null;
        const keyAudio = keyLink ? await resolve(keyLink.from, keyLink.outName) : null;
        if (!source || (keyLink && !keyAudio)) { memo.set(key, null); return null; }
        const result = await compressorAudio(source, node.params, keyAudio, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'gate') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = await gateAudio(source, node.params, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'denoise') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        if (node.params.enabled === 'off') { memo.set(key, source); return source; }
        if (denoiseWorkingSetBytes(source.channels) > 128 * 1024 * 1024) {
          const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
          memo.set(key, audio);
          return audio;
        }
        const result = { sampleRate: source.sampleRate, channels: await processDenoiseChannels(source.channels, source.sampleRate, node.params, controller.signal) };
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'reverb') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        if (node.params.enabled === 'off') { memo.set(key, source); return source; }
        if (/^(?:auto|lfo|env)\s*\(/i.test(String(node.params.mix || '')) || reverbWorkingSetBytes(source.channels[0].length, source.channels.length) > 128 * 1024 * 1024) {
          const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
          memo.set(key, audio);
          return audio;
        }
        try {
          const result = { sampleRate: source.sampleRate, channels: await processReverbChannels(source.channels, node.params, source.sampleRate, controller.signal) };
          localEffectApproximations.add(node.id);
          memo.set(key, result);
          return result;
        } catch (error) {
          controller.signal.throwIfAborted();
          const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
          memo.set(key, audio);
          return audio;
        }
      }
      if (node.tpl === 'granular') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        if (node.params.enabled === 'off') { memo.set(key, source); return source; }
        if (granularWorkingSetBytes(source.channels) > 128 * 1024 * 1024) {
          const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
          memo.set(key, audio);
          return audio;
        }
        let seed;
        try { seed = await effectiveSeed(node.id, controller.signal); }
        catch {
          controller.signal.throwIfAborted();
          const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
          memo.set(key, audio);
          return audio;
        }
        const result = { sampleRate: source.sampleRate, channels: await processGranularChannels(source.channels, source.sampleRate, node.params, seed, controller.signal) };
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'convolution') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        if (node.params.enabled === 'off') { memo.set(key, source); return source; }
        try {
          const impulse = await fetchConvolutionImpulse(node.id, node.params, context, source.sampleRate, controller.signal);
          if (convolutionWorkingSetBytes(source.channels[0].length, source.channels.length, impulse.length) > 128 * 1024 * 1024) throw new Error('local convolution exceeds the worker memory budget');
          const channels = await processConvolutionChannels(source.channels, impulse, node.params, source.sampleRate, controller.signal);
          const result = { sampleRate: source.sampleRate, channels };
          memo.set(key, result);
          return result;
        } catch (error) {
          controller.signal.throwIfAborted();
          const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
          memo.set(key, audio);
          return audio;
        }
      }
      if (node.tpl === 'scratch') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        if (node.params.enabled === 'off') { memo.set(key, source); return source; }
        if (scratchWorkingSetBytes(source.channels[0].length, source.channels.length) > 128 * 1024 * 1024) {
          const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
          memo.set(key, audio);
          return audio;
        }
        const tempoMilli = Math.round((Number(S.doc?.project?.tempo) || 92) * 1000), samplesPerBar = Math.floor(barBeats() * 60 * source.sampleRate * 1000 / tempoMilli);
        try {
          const channels = await processScratchChannels(source.channels, node.params, source.sampleRate, samplesPerBar, S.range?.firstBar || 1, controller.signal);
          const result = { sampleRate: source.sampleRate, channels };
          memo.set(key, result);
          return result;
        } catch (error) {
          controller.signal.throwIfAborted();
          const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
          memo.set(key, audio);
          return audio;
        }
      }
      if (node.tpl === 'rewind') {
        const live = (item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {});
        const link = graph.links.find((item) => live(item) && item.inName !== 'fx'), fxLink = graph.links.find((item) => live(item) && item.inName === 'fx');
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const fx = fxLink ? await resolve(fxLink.from, fxLink.outName) : null;
        const tempoMilli = Math.round((Number(S.doc?.project?.tempo) || 92) * 1000), samplesPerBar = Math.floor(barBeats() * 60 * source.sampleRate * 1000 / tempoMilli);
        try {
          const done = rewindAudio(source, node.params, samplesPerBar, fx, controller.signal);
          for (const port of ['out', 'audio']) memo.set(`${id}:${port}`, done.audio);
          memo.set(`${id}:region`, done.region);
          return output === 'region' ? done.region : done.audio;
        } catch (error) {
          controller.signal.throwIfAborted();
          const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
          memo.set(key, audio);
          return audio;
        }
      }
      if (node.tpl === 'autotune') {
        // the steps of the engine with the key and the fixes (a melody on the Melody input is left to the build)
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        try {
          const channels = source.channels.map((c) => Float32Array.from(c));
          const mono = channels.length > 1 ? channels[0].map((v, i) => (v + channels[1][i]) / 2) : channels[0];
          const track = detectPitch(mono, source.sampleRate);
          const tempoMilli = Math.round((Number(S.doc?.project?.tempo) || 92) * 1000), samplesPerBar = Math.floor(barBeats() * 60 * source.sampleRate * 1000 / tempoMilli);
          const fixes = parseFixes(String(node.params.fixes ?? '').replace(/^"|"$/g, ''), source.sampleRate, samplesPerBar);
          const { correction } = corrections(track, autotuneSettings(node.params, track), [], fixes);
          const result = { sampleRate: source.sampleRate, channels: applyCorrections(channels, source.sampleRate, track, correction, String(node.params.method ?? '').replace(/^"|"$/g, '') || 'spectral') };
          for (const port of ['out', 'audio']) memo.set(`${id}:${port}`, result);
          return result;
        } catch (error) {
          controller.signal.throwIfAborted();
          const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
          memo.set(key, audio);
          return audio;
        }
      }
      if (node.tpl === 'pitch') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        if (node.params.enabled === 'off') { memo.set(key, source); return source; }
        try {
          const method = String(node.params.method || 'wsola').toLowerCase();
          if (pitchWorkingSetBytes(source.channels[0].length, source.channels.length, method) > 128 * 1024 * 1024) throw new Error('local pitch preview exceeds the worker memory budget');
          const channels = await processPitchChannels(source.channels, source.sampleRate, node.params, controller.signal, { tempo: Number(S.doc?.project?.tempo) || 92, firstBar: S.range?.firstBar || 1 });
          const result = { sampleRate: source.sampleRate, channels };
          memo.set(key, result);
          return result;
        } catch (error) {
          controller.signal.throwIfAborted();
          const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
          memo.set(key, audio);
          return audio;
        }
      }
      if (node.tpl === 'stretch') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        if (node.params.enabled === 'off') { memo.set(key, source); return source; }
        try {
          if (stretchWorkingSetBytes(source.channels[0].length, source.channels.length) > 128 * 1024 * 1024) throw new Error('local stretch preview exceeds the worker memory budget');
          const channels = await processStretchChannels(source.channels, source.sampleRate, node.params, controller.signal);
          const result = { sampleRate: source.sampleRate, channels };
          memo.set(key, result);
          return result;
        } catch (error) {
          controller.signal.throwIfAborted();
          const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
          memo.set(key, audio);
          return audio;
        }
      }
      if (node.tpl === 'reverse' || node.tpl === 'cut' || node.tpl === 'fade' || node.tpl === 'shift') {
        const input = graph.links.find((link) => link.to === id && link.type === 'audio' && !link.disabled && !isDisabled(nodes.get(link.from) || {}));
        const source = input ? await resolve(input.from, input.outName) : null;
        if (!source) { memo.set(key, null); return null; }
        const result = node.tpl === 'reverse' ? node.params.enabled === 'off' ? source : await reverseAudio(source, controller.signal)
          : node.tpl === 'cut' ? await cutAudio(source, node.params, controller.signal)
            : node.tpl === 'fade' ? await fadeAudio(source, node.params, Number(S.doc?.project?.tempo) || 120, S.range?.firstBar || 1, controller.signal)
              : await shiftAudio(source, node.params, Number(S.doc?.project?.tempo) || 120, totalBars, controller.signal);
        memo.set(key, result);
        return result;
      }
      if (node.tpl === 'sequencer') {
        const firstBar = S.range?.firstBar || 1, bars = S.range?.bars || totalBars;
        const plan = String(node.params.sequence || '').trim() ? sequencePreviewPlan(node.params.sequence, node.params.lanes, firstBar, bars) : null;
        const sampleRate = 44100, samplesPerBar = Math.floor(barBeats() * 60 * sampleRate * 1000 / Math.round((Number(S.doc?.project?.tempo) || 120) * 1000));
        if (plan && plan.channels.every(({ channel }) => ['square', 'saw', 'triangle'].includes(channel.settings.wave || (channel.type === 'bass' ? 'saw' : channel.type === 'piano' ? 'triangle' : 'square'))) && sequencerWorkingSetBytes(bars * samplesPerBar, plan.lanes.length) <= 128 * 1024 * 1024) {
          try {
            const result = await renderSequenceChannels(plan, node.params, renderSynthChannels, sampleRate, firstBar, bars, samplesPerBar, controller.signal);
            for (const [port, audio] of Object.entries(result)) memo.set(`${id}:${port}`, audio);
            localApproximationSources.add(node.id);
            return result[output] || null;
          } catch (error) { controller.signal.throwIfAborted(); localInstrumentFallbackReasons.add(`${node.id}: ${/working-set limit/.test(error.message) ? "too long for the browser preview, so the built take is used" : error.message}`); }
        }
        const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
        memo.set(key, audio);
        return audio;
      }
      if (node.tpl === 'group') {
        const link = graph.links.find((item) => item.to === id && item.type === 'audio' && item.inName === 'in' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? await resolve(link.from, link.outName) : null;
        memo.set(key, source);
        return source;
      }
      if (node.tpl === 'delay-llama') {
        // the same voice as the engine, sung in the browser (the bend of the notes is added by the bend step after it)
        const link = graph.links.find((item) => item.to === id && item.type === 'midi' && item.inName === 'notes' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? nodes.get(link.from) : null;
        if (source && node.params.enabled !== 'off') {
          try {
            const notes = await resolveMidiNotes(source);
            if (notes) {
              const firstBar = S.range?.firstBar || 1, bars = S.range?.bars || totalBars, sampleRate = 44100;
              const samplesPerBar = Math.floor(barBeats() * 60 * sampleRate * 1000 / Math.round((Number(S.doc?.project?.tempo) || 120) * 1000)), length = bars * samplesPerBar;
              const curves = { vowel: llamaCurve(node.params.vowel, firstBar, samplesPerBar, length), voice: llamaCurve(node.params.voice, firstBar, samplesPerBar, length), mouth: llamaCurve(node.params.mouth, firstBar, samplesPerBar, length) };
              const sung = renderLlama(llamaNotes(notes.filter((n) => n.bar >= firstBar && n.bar < firstBar + bars), firstBar, samplesPerBar), llamaSettings(node.params), sampleRate, length, 1, curves);
              const audio = { sampleRate, channels: [sung.left, sung.right] };
              localApproximationSources.add(node.id);
              memo.set(key, audio);
              return audio;
            }
          } catch (error) { controller.signal.throwIfAborted(); localInstrumentFallbackReasons.add(`${node.id}: ${/working-set limit/.test(error.message) ? "too long for the browser preview, so the built take is used" : error.message}`); }
        }
        const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
        memo.set(key, audio);
        return audio;
      }
      if (node.tpl === 'synth') {
        const link = graph.links.find((item) => item.to === id && item.type === 'midi' && item.inName === 'notes' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? nodes.get(link.from) : null;
        if (source) {
          try {
            const notes = await resolveMidiNotes(source);
            if (notes && notes.length <= 20000) {
              const firstBar = S.range?.firstBar || 1, bars = S.range?.bars || totalBars;
              const sampleRate = 44100, samplesPerBar = Math.floor(barBeats() * 60 * sampleRate * 1000 / Math.round((Number(S.doc?.project?.tempo) || 120) * 1000));
              if (synthWorkingSetBytes(bars * samplesPerBar, 2) <= 128 * 1024 * 1024) {
                const channels = await renderSynthChannels(notes, node.params, sampleRate, firstBar, bars, samplesPerBar, controller.signal);
                const audio = node.params.channels === 'mono' ? { sampleRate, channels: [Float32Array.from(channels[0], (value, index) => (value + channels[1][index]) * 0.5)] } : { sampleRate, channels };
                localApproximationSources.add(node.id);
                memo.set(key, audio);
                return audio;
              }
            }
          } catch (error) { controller.signal.throwIfAborted(); localInstrumentFallbackReasons.add(`${node.id}: ${/working-set limit/.test(error.message) ? "too long for the browser preview, so the built take is used" : error.message}`); }
        }
        const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
        memo.set(key, audio);
        return audio;
      }
      if (node.tpl === 'guitar') {
        try {
          const audio = await previewGuitar(node, context, controller.signal, S.range || null, totalBars, resolveMidiNotes);
          localApproximationSources.add(node.id);
          memo.set(key, audio);
          return audio;
        } catch (error) { controller.signal.throwIfAborted(); localInstrumentFallbackReasons.add(`${node.id}: ${/working-set limit/.test(error.message) ? "too long for the browser preview, so the built take is used" : error.message}`); }
        const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
        memo.set(key, audio);
        return audio;
      }
      if (node.tpl === 'bass') {
        const link = graph.links.find((item) => item.to === id && item.type === 'midi' && item.inName === 'notes' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? nodes.get(link.from) : null;
        const set = String(node.params.set || 'analog-808'), supported = source && set === 'analog-808' && Number(node.params.humanize || 0) === 0 && String(node.params.pattern || 'bass-A') === 'bass-A';
        if (supported) {
          try {
            const notes = await resolveMidiNotes(source);
            const firstBar = S.range?.firstBar || 1, bars = S.range?.bars || totalBars;
            const sampleRate = 44100, samplesPerBar = Math.floor(barBeats() * 60 * sampleRate * 1000 / Math.round((Number(S.doc?.project?.tempo) || 120) * 1000));
            if (notes && notes.length <= 20000 && bassWorkingSetBytes(bars * samplesPerBar) <= 128 * 1024 * 1024) {
              const channel = await renderBassChannels(notes, sampleRate, firstBar, bars, samplesPerBar, controller.signal);
              localApproximationSources.add(node.id);
              const audio = { sampleRate, channels: [channel] };
              memo.set(key, audio);
              return audio;
            }
          } catch (error) { controller.signal.throwIfAborted(); localInstrumentFallbackReasons.add(`${node.id}: ${/working-set limit/.test(error.message) ? "too long for the browser preview, so the built take is used" : error.message}`); }
        }
        if (source && set !== 'analog-808' && Number(node.params.humanize || 0) === 0 && String(node.params.pattern || 'bass-A') === 'bass-A') {
          try { const audio = await previewPitchedSampleSet(node, context, controller.signal, S.range || null, totalBars, resolveMidiNotes); localApproximationSources.add(node.id); memo.set(key, audio); return audio; }
          catch (error) { controller.signal.throwIfAborted(); localInstrumentFallbackReasons.add(`${node.id}: ${/working-set limit/.test(error.message) ? "too long for the browser preview, so the built take is used" : error.message}`); }
        }
        const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
        memo.set(key, audio);
        return audio;
      }
      if (node.tpl === 'sampler') {
        const firstBar = S.range?.firstBar || 1, bars = S.range?.bars || totalBars, sampleRate = 44100;
        if (node.params.enabled === 'off') {
          const audio = { sampleRate, channels: [new Float32Array(bars * Math.floor(barBeats() * 60 * sampleRate * 1000 / Math.round((Number(S.doc?.project?.tempo) || 120) * 1000)))] };
          memo.set(key, audio);
          return audio;
        }
        const link = graph.links.find((item) => item.to === id && item.type === 'midi' && item.inName === 'notes' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? nodes.get(link.from) : null;
        if (source) {
          try {
            const notes = await resolveMidiNotes(source), file = String(node.params.file || '').trim();
            if (notes && notes.length <= 20000 && file) {
              const response = await fetch(`/api/project/audio?path=${encodeURIComponent(file)}`, { headers: { 'X-Ample-Token': getToken() }, signal: controller.signal });
              if (response.ok && Number(response.headers.get('content-length')) <= 32 * 1024 * 1024) {
                const bytes = await response.arrayBuffer();
                if (bytes.byteLength <= 32 * 1024 * 1024) {
                  const decoded = await context.decodeAudioData(bytes);
                  const samplesPerBar = Math.floor(barBeats() * 60 * sampleRate * 1000 / Math.round((Number(S.doc?.project?.tempo) || 120) * 1000));
                  if (samplerWorkingSetBytes(bars * samplesPerBar, decoded.numberOfChannels, decoded.length, decoded.numberOfChannels) <= 128 * 1024 * 1024) {
                    const sampleChannels = Array.from({ length: decoded.numberOfChannels }, (_, channel) => decoded.getChannelData(channel).slice());
                    const channels = await renderSamplerChannels(sampleChannels, decoded.sampleRate, notes, node.params, sampleRate, firstBar, bars, samplesPerBar, controller.signal);
                    const audio = node.params.channels === 'mono' && channels.length > 1 ? { sampleRate, channels: [Float32Array.from(channels[0], (value, index) => (value + channels[1][index]) * 0.5)] }
                      : node.params.channels === 'stereo' && channels.length === 1 ? { sampleRate, channels: [channels[0], channels[0].slice()] } : { sampleRate, channels };
                    localApproximationSources.add(node.id);
                    memo.set(key, audio);
                    return audio;
                  }
                }
              }
            }
          } catch (error) { controller.signal.throwIfAborted(); localInstrumentFallbackReasons.add(`${node.id}: ${/working-set limit/.test(error.message) ? "too long for the browser preview, so the built take is used" : error.message}`); }
        }
        const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
        memo.set(key, audio);
        return audio;
      }
      if (node.tpl === 'piano') {
        const link = graph.links.find((item) => item.to === id && item.type === 'midi' && item.inName === 'notes' && !item.disabled && !isDisabled(nodes.get(item.from) || {}));
        const source = link ? nodes.get(link.from) : null;
        const supported = source && String(node.params.set || 'felt-piano') === 'felt-piano' && Number(node.params.humanize || 0) === 0;
        if (supported) {
          try {
            const notes = await resolveMidiNotes(source), firstBar = S.range?.firstBar || 1, bars = S.range?.bars || totalBars;
            const sampleRate = 44100, samplesPerBar = Math.floor(barBeats() * 60 * sampleRate * 1000 / Math.round((Number(S.doc?.project?.tempo) || 120) * 1000));
            if (notes && notes.length <= 20000 && pianoWorkingSetBytes(bars * samplesPerBar) <= 128 * 1024 * 1024) {
              const channel = await renderPianoChannels(notes, sampleRate, firstBar, bars, samplesPerBar, controller.signal);
              localApproximationSources.add(node.id);
              const audio = { sampleRate, channels: [channel] };
              memo.set(key, audio);
              return audio;
            }
          } catch (error) { controller.signal.throwIfAborted(); localInstrumentFallbackReasons.add(`${node.id}: ${/working-set limit/.test(error.message) ? "too long for the browser preview, so the built take is used" : error.message}`); }
        }
        if (source && String(node.params.set || 'felt-piano') !== 'felt-piano' && Number(node.params.humanize || 0) === 0) {
          try { const audio = await previewPitchedSampleSet(node, context, controller.signal, S.range || null, totalBars, resolveMidiNotes); localApproximationSources.add(node.id); memo.set(key, audio); return audio; }
          catch (error) { controller.signal.throwIfAborted(); localInstrumentFallbackReasons.add(`${node.id}: ${/working-set limit/.test(error.message) ? "too long for the browser preview, so the built take is used" : error.message}`); }
        }
        const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
        memo.set(key, audio);
        return audio;
      }
      if (node.tpl === 'drums') {
        const params = { ...node.params, tempo: S.doc?.project?.tempo || 120 }, firstBar = S.range?.firstBar || 1, bars = S.range?.bars || totalBars;
        const supported = String(node.params.kit || 'kit/tape').trim() === 'builtin' && builtinDrumSongSupported(params);
        const activeBars = (node.parts?.length ? node.parts.flatMap((part) => Array.from({ length: part.length }, (_, index) => part.at + index)) : Array.from({ length: bars }, (_, index) => firstBar + index)).filter((bar) => bar >= firstBar && bar < firstBar + bars);
        const sampleRate = 44100, frames = bars * Math.floor((barBeats() * 60 * sampleRate * 1000) / Math.round(Number(params.tempo) * 1000));
        if (supported && drumWorkingSetBytes(frames) <= 128 * 1024 * 1024) {
          try {
            const seed = await effectiveSeed(node.id, controller.signal);
            const channels = await renderBuiltinDrumChannels(params, seed, sampleRate, firstBar, bars, activeBars, controller.signal);
            const audio = { sampleRate, channels };
            localExactSources.add(node.id);
            memo.set(key, audio);
            return audio;
          } catch (error) { controller.signal.throwIfAborted(); localInstrumentFallbackReasons.add(`${node.id}: ${/working-set limit/.test(error.message) ? "too long for the browser preview, so the built take is used" : error.message}`); }
        }
        const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
        memo.set(key, audio);
        return audio;
      }
      const local = ['mixer', 'bus', 'send', 'master'].includes(node.tpl);
      if (node.tpl === 'import-audio') {
        try {
          const audio = await importAudio(node, context, controller.signal, audioCache, S.range || null, totalBars);
          memo.set(key, audio);
          return audio;
        } catch (error) {
          controller.signal.throwIfAborted();
        }
      }
      if (node.tpl === 'click') {
        // a source with no input and no file: the same integer bursts as the server (ExtraClick.cs), computed here
        const tempoMilli = Math.round((Number(S.doc?.project?.tempo) || 92) * 1000), samplesPerBar = Math.floor(barBeats() * 60 * PREVIEW_RATE * 1000 / tempoMilli);
        const audio = clickAudio(node.params, node.parts, samplesPerBar, PREVIEW_RATE, S.range?.firstBar || 1, S.range?.bars || totalBars);
        memo.set(`${id}:audio`, audio); memo.set(`${id}:out`, audio);
        return audio;
      }
      if (node.tpl === 'ambience') {
        try {
          const audio = await previewAmbience(node, context, controller.signal, S.range || null, totalBars);
          localEffectApproximations.add(node.id);
          memo.set(`${id}:audio`, audio); memo.set(`${id}:out`, audio);
          return audio;
        } catch (error) { controller.signal.throwIfAborted(); }
      }
      if (!local) {
        const audio = await buildAudio(node, context, controller.signal, audioCache, serverSources);
        memo.set(key, audio);
        return audio;
      }
      const links = graph.links.filter((link) => link.to === id && link.type === 'audio' && !link.disabled && !isDisabled(nodes.get(link.from) || {})).sort((a, b) => a.inp - b.inp);
      const inputs = [];
      for (const link of links) {
        const audio = await resolve(link.from, link.outName);
        if (audio) inputs.push({ audio, index: Number(/^in(\d+)$/.exec(link.inName)?.[1] || inputs.length + 1) - 1 });
      }
      if (!inputs.length && node.tpl !== 'master') { memo.set(key, null); return null; }
      let result;
      if (node.tpl === 'send') {
        const pair = await sendAudioBuffers(inputs, node.params, controller.signal);
        result = { through: pair.through, send: pair.send };
      } else if (node.tpl === 'mixer' || node.tpl === 'bus') {
        const muted = node.tpl === 'bus' && (node.params.mute === 'on' || soloBus && node.params.solo !== 'on');
        result = { mix: await mixAudioBuffers(inputs, { ...node.params, mute: muted, busGain: node.tpl === 'bus' ? node.params.gain : '0' }, controller.signal) };
      } else {
        const source = inputs.find((item) => item.index === 0)?.audio;
        if (!source) throw new Error(`Master “${id}” has no enabled mix input.`);
        const tempo = Number(S.doc?.project?.tempo) || 120;
        const params = S.range ? { ...node.params, slice_from: '', slice_to: '' } : node.params;
        result = { master: await masterAudioBuffer(source, params, tempo, controller.signal) };
      }
      for (const [port, audio] of Object.entries(result)) memo.set(`${id}:${port}`, audio);
      return result[output] || result.mix || result.master || result.through || null;
      } finally {
        active.delete(key);
      }
    };
    const outputName = target.tpl === 'master' ? 'master' : target.tpl === 'send' ? 'through' : 'mix';
    const result = await resolve(target.id, outputName);
    progress.phase = 'mixing down'; touch();
    if (!result?.channels?.[0]?.length) throw new Error('The client preview produced no audio. Check the enabled inputs.');
    controller.signal.throwIfAborted();
    if (S.path !== initialPath || S.doc?.text !== initialText || JSON.stringify(S.range || null) !== JSON.stringify(initialRange)) throw new Error('The song or preview range changed while audio was preparing. Build the preview again.');
    progress.phase = 'starting playback'; touch();
    const wav = encodeWav(result);
    if (planKey) lastPreview = { key: planKey, blob: wav };
    await playTransient(wav);
    const sources = [...serverSources];
    const base = sources.length ? `Client mix preview is playing; unsupported audio came from server blocks: ${sources.join(', ')}.` : 'Client mix preview is playing; the audio graph was processed locally.';
    const approximation = localApproximationSources.size ? ` Local instrument approximation: ${[...localApproximationSources].join(', ')}.` : '';
    const effectApproximation = localEffectApproximations.size ? ` Local effect approximation: ${[...localEffectApproximations].join(', ')}.` : '';
    const exactInstruments = localExactSources.size ? ` Local exact instruments: ${[...localExactSources].join(', ')}.` : '';
    const midiTransforms = localMidiTransforms.size ? ` Local MIDI transforms: ${[...localMidiTransforms].join(', ')}.` : '';
    const instrumentFallback = localInstrumentFallbackReasons.size ? ` Local instrument fallback: ${[...localInstrumentFallbackReasons].join('; ')}.` : '';
    const lines = [base, approximation, effectApproximation, exactInstruments, midiTransforms, instrumentFallback, ...meterReports.map((report) => `Meter ${report}`)].map((line) => line.trim()).filter(Boolean);
    for (const line of lines) logProject('client preview', 'preview', line);   // the toast is gone in a few seconds: the Log tab keeps it
    toast(lines.join(' '));
  } catch (error) {
    if (error.name !== 'AbortError') { logProject('client preview', 'failed', error.message || String(error)); toast(error.message || String(error)); } else logProject('client preview', 'cancelled', 'The preview was cancelled.');
  } finally {
    await context?.close().catch(() => {});
    controller = null; running = false; progress = null; plannedTakes = null; notify();
  }
}
