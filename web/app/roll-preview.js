import { getToken } from './api.js';
import { parseEffectiveGranularSeed } from '../lib/clientside-granular.mjs';
import { S, flat } from './store.js';
import { resolveRollInstrument, rollConsumers, rollVoiceKey } from '../lib/roll-instrument.mjs';
import { renderSynthChannels } from '../lib/clientside-synth.mjs';
import { renderPianoChannels } from '../lib/clientside-piano.mjs';
import { renderBassChannels } from '../lib/clientside-bass.mjs';
import { renderSamplerChannels } from '../lib/clientside-sampler.mjs';
import { transposeMidiEvents } from '../lib/clientside-midi-transpose.mjs';
import { chordMidiEvents } from '../lib/clientside-midi-chord.mjs';
import { quantizeMidiEvents } from '../lib/clientside-midi-quantize.mjs';
import { humanizeMidiEvents } from '../lib/clientside-midi-humanize.mjs';
import { arpeggiateMidiEvents } from '../lib/clientside-midi-arpeggiator.mjs';
import { createNoteCache, selectPreviewVoice } from '../lib/clientside-note-preview.mjs';
import { previewGuitar, previewPitchedSampleSet } from './clientside-preview.js';
import { liveAudioContext } from './live-play.js';
import { llamaLive, llamaTweaks, onLlamaTweak } from './llama-live.js';
import { llamaSettings } from '../lib/clientside-delay-llama.mjs';
import { absolute, parse } from '../lib/notes-model.mjs';
import { barBeats } from '../lib/meter.mjs';

/** One roll owns its bounded, cancellable voice cache. Cold voices use the audible sketch fallback. */
export function createRollPreview(clipId, ppq, onStatus) {
  const controller = new AbortController(), cache = createNoteCache(), pending = new Map(), failures = new Set(), waiting = [];
  // the block that sounds this clip while it is worked on (when it feeds more than one): picked in the window, remembered per clip in this browser
  const pickKey = 'ample.roll.voice:' + (S.project?.id || '') + ':' + clipId;
  let pick = null;
  try { pick = localStorage.getItem(pickKey) || null; } catch { /* storage can be blocked */ }
  // a knob of the llama window turned while it plays: the live voice takes it at once
  let lastLlama = null;
  const stopTweaks = onLlamaTweak((block, patch) => { if (lastLlama && route?.node.id === block) lastLlama.set(llamaSettings({ ...route.node.params, ...patch })); });
  let route = null, seeds = {}, signature = '', generation = 0, active = 0, disposed = false;
  let preparing = null, lastChoice = { kind: 'fallback', local: false, reason: 'no-instrument' };
  const announce = (ready) => onStatus(ready ? `${route.node.title} · instrument preview (approximate)` : `${route?.node.title || 'No connected instrument'} · sketch preview (approximate)`);
  async function prepare() {
    const next = resolveRollInstrument(flat(), clipId, pick);
    const seeded = next?.chain.some((effect) => effect.params.enabled !== 'off' && (effect.tpl === 'midi-humanize' || effect.tpl === 'arpeggiator' && effect.params.pattern === 'random'));
    const key = JSON.stringify({ route: next, source: seeded ? S.doc?.text : null });
    if (key === signature) { if (preparing) { try { await preparing; } catch { return; } } failures.clear(); return; }
    route = next; signature = key; seeds = {}; generation++;
    cache.clear(); failures.clear(); announce(false);
    const mine = generation;
    preparing = (async () => {
      for (const effect of next?.chain || []) {
        if (effect.params.enabled === 'off' || effect.tpl !== 'midi-humanize' && !(effect.tpl === 'arpeggiator' && effect.params.pattern === 'random')) continue;
        const result = await fetch(`/api/control/seed?${new URLSearchParams({ path: S.path, block: effect.id })}`, { headers: { 'X-Ample-Token': getToken() }, signal: controller.signal });
        const seed = result.ok ? parseEffectiveGranularSeed(await result.text()) : null;
        if (seed === null) throw new Error('Effective preview seed is unavailable.');
        if (mine === generation) seeds[effect.id] = seed;
      }
      // a Delay Llama is sung live by its worklet: it is loaded here, nothing is rendered in advance
      if (next?.node.tpl === 'delay-llama') await llamaLive(liveAudioContext());
    })();
    try { await preparing; } catch { if (mine === generation) { route = null; signature = ''; announce(false); } }
    finally { if (mine === generation) preparing = null; }
  }
  function transform(rows) {
    let notes = rows.map((note) => ({ ...note, bar: Math.floor(note.at / (barBeats() * ppq)) + 1, beat: (note.at % (barBeats() * ppq)) / ppq, beats: note.length / ppq }));
    for (const effect of route?.chain || []) {
      const input = { notes }, params = effect.params;
      if (effect.tpl === 'transpose') notes = transposeMidiEvents(input, params).notes;
      else if (effect.tpl === 'chord') notes = chordMidiEvents(input, params).notes;
      else if (effect.tpl === 'quantize') notes = quantizeMidiEvents(input, params).notes;
      else if (effect.tpl === 'midi-humanize') notes = humanizeMidiEvents(input, params, seeds[effect.id] || '0').notes;
      else if (effect.tpl === 'arpeggiator') notes = arpeggiateMidiEvents(input, params, seeds[effect.id] || '0').notes;
    }
    return notes.map((note) => ({ ...note, at: ((note.bar - 1) * barBeats() + note.beat) * ppq, length: Math.max(1, note.beats * ppq) }));
  }
  async function render(ctx, note, bpm, target) {
    const params = target.params, sampleRate = ctx.sampleRate, samplesPerBar = Math.floor(barBeats() * 60 * sampleRate * 1000 / Math.round(bpm * 1000));
    const beats = note.length / ppq, bars = Math.ceil((beats + Math.max(2, Math.min(5, Number(params.release || 0) / 1000)) * bpm / 60) / 4);
    if (bars * samplesPerBar * 16 > 128 * 1024 * 1024) throw new Error('Preview voice exceeds its memory budget.');
    const notes = [{ ...note, bar: 1, beat: 0, beats }];
    const choice = selectPreviewVoice({ node: target }, note);
    if (choice.kind === 'fallback') throw new Error(`This voice needs the full renderer (${choice.reason}).`);
    let channels;
    if (choice.kind === 'gm') {
      // The bounded audition endpoint renders at 120 BPM; convert duration instead of changing playback rate/pitch.
      const ticks = Math.round(beats * 120 / bpm * 1_000_000);
      if (ticks < 1 || ticks > 4_000_000) throw new Error('This SoundFont sustain exceeds the bounded audition window.');
      const body = { file: String(params.file || 'pack:musescore-general').replace(/^"|"$/g, ''), bank: note.bank ?? Number(params.bank || 0), program: note.program ?? Number(params.program || 0), channel: note.channel ?? Number(params.channel || 0), gain: Number(params.gain || 0), limiter: params.limiter || 'soft', notes: [{ bar: 1, tick: 0, ticks, ppq: 1_000_000, pitch: note.pitch, velocity: note.velocity }] };
      const result = await fetch('/api/gm/audition', { method: 'POST', headers: { 'X-Ample-Token': getToken(), 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal: controller.signal });
      if (!result.ok) throw new Error('The configured SoundFont could not be auditioned.');
      return ctx.decodeAudioData(await result.arrayBuffer());
    }
    if (choice.kind === 'llama') {
      // the Delay Llama sings the note in the browser with the voice of the engine
      const { llamaNotes, llamaSettings, renderLlama } = await import('../lib/clientside-delay-llama.mjs');
      const sung = renderLlama(llamaNotes(notes, 1, samplesPerBar), llamaSettings(params), sampleRate, bars * samplesPerBar, 1);
      channels = [sung.left, sung.right];
    } else if (choice.kind === 'synth') channels = await renderSynthChannels(notes, params, sampleRate, 1, bars, samplesPerBar, controller.signal);
    else if (choice.kind === 'piano') channels = [await renderPianoChannels(notes, sampleRate, 1, bars, samplesPerBar, controller.signal)];
    else if (choice.kind === 'bass') channels = [await renderBassChannels(notes, sampleRate, 1, bars, samplesPerBar, controller.signal)];
    else if (choice.kind === 'sampler') {
      const response = await fetch(`/api/project/audio?path=${encodeURIComponent(String(params.file || ''))}`, { headers: { 'X-Ample-Token': getToken() }, signal: controller.signal });
      if (!response.ok || Number(response.headers.get('content-length')) > 32 * 1024 * 1024) throw new Error('Sample is unavailable or exceeds the preview budget.');
      const data = await response.arrayBuffer();
      if (data.byteLength > 32 * 1024 * 1024) throw new Error('Sample exceeds the preview budget.');
      const decoded = await ctx.decodeAudioData(data);
      channels = await renderSamplerChannels(Array.from({ length: decoded.numberOfChannels }, (_, i) => decoded.getChannelData(i).slice()), decoded.sampleRate, notes, params, sampleRate, 1, bars, samplesPerBar, controller.signal);
    } else if (choice.kind === 'guitar' || choice.kind === 'pitched-set') {
      const renderSet = choice.kind === 'guitar' ? previewGuitar : previewPitchedSampleSet;
      const result = await renderSet({ ...target, parts: [] }, ctx, controller.signal, { firstBar: 1, bars }, bars, async () => notes);
      channels = result.channels;
    } else throw new Error('No local instrument renderer.');
    if (params.channels === 'mono' && channels.length > 1) channels = [Float32Array.from(channels[0], (v, i) => (v + channels[1][i]) * 0.5)];
    if (params.channels === 'stereo' && channels.length === 1) channels = [channels[0], channels[0]];
    const buffer = ctx.createBuffer(channels.length, channels[0].length, sampleRate);
    channels.forEach((channel, i) => buffer.copyToChannel(channel, i));
    return buffer;
  }
  async function warm(ctx, note, bpm) {
    if (!route || disposed) return null;
    note = voiceNote(note);
    const key = rollVoiceKey(route, seeds, note, bpm, ctx.sampleRate);
    if (cache.has(key)) return cache.get(key);
    if (failures.has(key)) return null;
    if (pending.has(key)) return pending.get(key);
    const mine = generation, target = route.node;
    // two renders run at a time and the others wait their turn (they used to be dropped: a clip with many different notes then kept playing the sketch voice for most of them)
    const job = (async () => {
      while (active >= 2) { await new Promise((resolve) => waiting.push(resolve)); if (disposed || generation !== mine) return null; }
      active++;
      try {
        const buffer = await render(ctx, note, bpm, target);
        if (disposed || generation !== mine) return null;
        return cache.set(key, buffer) ? buffer : null;
      } catch { if (mine === generation) failures.add(key); return null; } finally { active--; waiting.shift()?.(); }
    })().finally(() => { pending.delete(key); });
    pending.set(key, job); return job;
  }
  const voiceNote = (note) => ({ pitch: note.pitch, velocity: note.velocity, length: Math.max(1, Math.round(note.length)), channel: note.channel, bank: note.bank, program: note.program, pan: note.pan, fine: note.fine, rel: note.rel, cut: note.cut, slide: note.slide, port: note.port });
  function schedule(ctx, output, event, at, bpm) {
    if (route?.node.tpl === 'delay-llama' && route.node.params.enabled !== 'off') {
      // the llama sings the note live: its start and its end are sent to the worklet on the clock of the audio context
      lastChoice = { kind: 'llama', local: true };
      llamaLive(ctx).then((voice) => { lastLlama = voice; voice.connect(output); voice.set(llamaSettings({ ...route.node.params, ...llamaTweaks(route.node.id) })); voice.noteOn(event.pitch, event.velocity ?? 96, at, event.fine || 0); voice.noteOff(event.pitch, at + event.duration); });
      announce(true);
      return true;
    }
    const note = voiceNote({ ...event, length: event.duration * bpm / 60 * ppq });
    const key = route && rollVoiceKey(route, seeds, note, bpm, ctx.sampleRate), buffer = key && cache.get(key);
    lastChoice = selectPreviewVoice(route, note);
    if (!buffer) { warm(ctx, note, bpm); announce(false); return false; }
    const voice = ctx.createBufferSource(); voice.buffer = buffer; voice.connect(output); voice.start(at); announce(true); return true;
  }
  const consumers = () => rollConsumers(flat(), clipId).map((c) => ({ id: c.node.id, title: c.node.title || c.node.id, tpl: c.node.tpl }));
  const choose = (id) => { pick = id || null; try { if (pick) localStorage.setItem(pickKey, pick); else localStorage.removeItem(pickKey); } catch { /* storage can be blocked */ } };
  const chosen = () => (resolveRollInstrument(flat(), clipId, pick)?.node.id ?? null);
  return { prepare, transform, schedule, warm, consumers, choose, chosen, get choice() { return lastChoice; }, cached: (ctx, note, bpm) => !!route && cache.has(rollVoiceKey(route, seeds, voiceNote(note), bpm, ctx.sampleRate)), get revision() { return generation; }, dispose() {
    stopTweaks(); disposed = true; controller.abort(); cache.clear(); } };
}
