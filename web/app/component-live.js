// The live preview of an audio component (docs/language/19-components.md, "Layout and live preview"): while a knob moves, the sound changes at once, in the browser.
// The take of the block that feeds the component is looped through a real-time chain made of the same live effects the built-in blocks use
// (studio/preview/audio.js liveEffectGraph); only the values of the knobs travel to the server (POST /api/components/graph-values), which stays the only
// evaluator of the process (units, beats to milliseconds, arithmetic over the properties). Advisory: nothing is built or written, the committed build is the truth.
// @ample:component-live
import { api, getToken } from './api.js';
import { liveEffectGraph } from './studio/preview/audio.js';
import { onPlayer, playLivePreview, playerState, setAuditionBlock, setLoop, stop as stopPlayer, updateLivePreview } from './player.js';
import { flat, notify, S } from './store.js';
import { liveAudioContext } from './live-play.js';
import { llamaLive } from './llama-live.js';
import { llamaSettings } from '../lib/clientside-delay-llama.mjs';
import { toast } from './ui.js';
import { barBeats } from '../lib/meter.mjs';

let active = null;   // { block, graph, pending, busy, dirty, meterFrame }
const listeners = new Set();
const emit = () => { listeners.forEach((f) => f()); notify(); };   // the switches of the panel and the card are drawn again

/** Told when a live preview starts or stops (the switches of the panel and the card follow it). */
export const onLiveChange = (f) => { listeners.add(f); return () => listeners.delete(f); };

/** Whether any live preview is going (the Stop of the header then stops it). */
export const liveActive = () => !!active;

/** Whether this block is being previewed live now. */
export const isLive = (block) => active?.block === block;

/** What the Live switch of a component should be: shown at all, usable, and why not. */
export function liveControl(template) {
  const asked = template?.ui?.preview === 'live';
  const available = Boolean(template?.livePreview);
  const reason = available ? 'Loops the sound that feeds this block and changes it while you move a knob (approximate: the build is the truth)' : `No live preview: ${(template?.noLive || []).join(', ') || 'a processor'} cannot run in real time in the browser`;
  return { asked, available, reason };
}

const valueOf = (node, name) => node.parameters.find((p) => p.name === name)?.value;
const paramsOf = (node) => Object.fromEntries(node.parameters.map((p) => [p.name, p.value]));

/** The real-time chain of a graph: one live effect per node, a mix is a sum with its gains. Returns what playLivePreview wants: update and dispose. */
function chainFor(graph, tempo, block) {
  return (context, source, destination) => {
    const outputs = new Map(), effects = new Map(), made = [];
    const feed = (reference) => (reference.startsWith('node:') ? outputs.get(reference.slice(5)) : source);
    for (const node of graph.nodes) {
      if (node.id === graph.liveSource) { outputs.set(node.id, source); continue; }   // the instrument: its rendered sound is the source
      const out = context.createGain();
      made.push(out);
      if (node.template === 'bus') {
        const gains = String(valueOf(node, 'gains') || '').trim().split(' ').filter(Boolean);
        node.inputs.forEach((input, k) => {
          const gain = context.createGain();
          gain.gain.value = 10 ** ((Number.parseFloat(gains[k]) || 0) / 20);
          made.push(gain);
          feed(input.source)?.connect(gain).connect(out);
        });
      } else {
        const input = context.createGain();
        made.push(input);
        for (const link of node.inputs.filter((i) => i.port === 'in')) feed(link.source)?.connect(input);
        effects.set(node.id, liveEffectGraph(context, input, out, node.template, paramsOf(node), tempo));
      }

      outputs.set(node.id, out);
    }

    const reference = Object.values(graph.outputs)[0] || 'in:audio';
    const last = feed(reference) || source;
    const analyser = context.createAnalyser();
    analyser.fftSize = 1024;
    last.connect(destination);
    last.connect(analyser);
    const samples = new Float32Array(analyser.fftSize);
    const level = () => {
      analyser.getFloatTimeDomainData(samples);
      const rms = Math.sqrt(samples.reduce((sum, x) => sum + x * x, 0) / samples.length);
      const db = Math.max(-60, 20 * Math.log10(rms || 1e-6));
      document.querySelectorAll(`.node[data-id="${CSS.escape(block)}"] canvas[data-output], .cui-popup canvas[data-output], #props canvas[data-output]`).forEach((canvas) => canvas.setMeterValue?.(db));
      if (active?.block === block) active.meterFrame = requestAnimationFrame(level);
    };
    level();
    return {
      update: (byNode) => { for (const [id, params] of Object.entries(byNode)) effects.get(id)?.update(params); },
      dispose: () => {
        effects.forEach((e) => e.dispose());
        made.forEach((n) => n.disconnect());
        analyser.disconnect();
        try { last.disconnect(destination); } catch { /* the player already took the source apart */ }
      },
    };
  };
}

/**
 * The Delay Llama sings live: its notes are the notes of the clip that feeds the component, scheduled in a loop on the clock of the audio context, the settings of the
 * llama follow the knobs, and the effects after it run in the same chain. Nothing is rendered by the server, so the voice itself changes while a knob moves.
 */
async function startLlama(block, graph, instrument) {
  const link = flat().links.find((l) => l.to === block && l.type === 'midi');
  const clip = link ? flat().nodes.find((n) => n.id === link.from) : null;
  if (!clip || clip.tpl !== 'midi-clip') throw new Error('The live llama sings the notes of a midi-clip: connect one to the notes input of this block first.');
  const response = await fetch('/api/midi/clip-notes', { method: 'POST', headers: { 'X-Ample-Token': getToken(), 'Content-Type': 'application/json' }, body: JSON.stringify({ path: S.path, block: clip.id }) });
  if (!response.ok) throw new Error('The notes of the clip could not be read.');
  const tempo = Number(S.doc?.project?.tempo) || 120, beat = 60 / tempo;
  const notes = String((await response.json()).text || '').trim().split(/\s+/).filter(Boolean).map((entry) => {
    const [bar, at, length, pitch, velocity] = entry.split(':').map(Number);
    return { start: ((bar - 1) * barBeats() + at) * beat, length: length * beat, pitch, velocity };
  }).filter((n) => [n.start, n.length, n.pitch, n.velocity].every(Number.isFinite));
  if (!notes.length) throw new Error('The clip has no notes to sing.');
  const loop = Math.max(1, Math.ceil(Math.max(...notes.map((n) => n.start + n.length)) / (beat * 4))) * beat * 4;
  const context = liveAudioContext();
  const voice = await llamaLive(context);
  voice.set(llamaSettings(paramsOf(instrument)));
  const source = context.createGain();
  voice.connect(source);
  const chain = chainFor(graph, tempo, block)(context, source, context.destination);
  const mine = { block, graph, pending: {}, busy: false, dirty: false, meterFrame: 0, llama: { voice, source, chain, timer: 0 }, instrument: instrument.id };
  active = mine;
  // a loop is scheduled a little before it starts; the next one when this one is nearly over
  let next = context.currentTime + 0.1;
  const schedule = () => {
    if (active !== mine) return;
    for (const n of notes) { voice.noteOn(n.pitch, n.velocity, next + n.start, 0); voice.noteOff(n.pitch, next + n.start + Math.max(0.05, n.length * 0.95)); }
    next += loop;
    mine.llama.timer = setTimeout(schedule, Math.max(50, (next - context.currentTime - 0.3) * 1000));
  };
  schedule();
  emit();
}

/** Starts the live preview of an instance of an audio component. */
export async function startLive(block) {
  if (active?.block === block) return;
  stopLive();
  try {
    const graph = await api.get(`/api/components/graph?path=${encodeURIComponent(S.path)}&block=${encodeURIComponent(block)}`);
    if (!graph.livePreview) throw new Error(`No live preview: ${(graph.noLive || []).join(', ') || 'a processor'} cannot run in real time in the browser.`);
    const node = flat().nodes.find((n) => n.id === block);
    if (!node) throw new Error('This block is not on the board.');
    const instrument = graph.nodes.find((n) => n.id === graph.liveSource);
    if (instrument?.template === 'delay-llama') { await startLlama(block, graph, instrument); return; }   // a voice the browser can sing itself: no build needed
    // a component with an instrument inside loops the sound of that instrument (the server renders it, the effects after it run live); else the sound that feeds the block
    const feeding = graph.liveSource ? { from: `${block}__${graph.liveSource}` } : flat().links.find((l) => l.to === block && l.type === 'audio');
    if (!feeding) throw new Error('Connect a sound to the audio input of this block first.');
    const take = S.build.takes[feeding.from];
    if (!take) throw new Error(`Build ${graph.liveSource ? block : feeding.from} first: the live preview loops ${graph.liveSource ? 'the instrument of this block' : `the sound that feeds ${block}`} and never builds by itself.`);
    const response = await fetch(`/api/takes/${encodeURIComponent(take)}/download?format=wav`, { headers: { 'X-Ample-Token': getToken() } });
    if (!response.ok) throw new Error('The sound that feeds this block could not be loaded.');
    const blob = await response.blob();
    setLoop(true);
    active = { block, graph, pending: {}, busy: false, dirty: false, meterFrame: 0, starting: true };
    await playLivePreview(blob, chainFor(graph, S.doc?.project?.tempo, block));
    active.starting = false;
    setAuditionBlock(block);
    emit();
  } catch (error) {
    active = null;
    emit();
    toast(error.message || String(error));
  }
}

/** Stops the live preview (the sound stops with it). */
export function stopLive({ player = true } = {}) {
  if (!active) return;
  cancelAnimationFrame(active.meterFrame);
  const llama = active.llama;
  active = null;
  if (llama) {
    clearTimeout(llama.timer);
    llama.voice.allOff();
    llama.chain.dispose();
    try { llama.source.disconnect(); } catch { /* already apart */ }
  } else if (player) stopPlayer();
  emit();
}

async function flush() {
  const current = active;
  if (!current) return;
  current.busy = true;
  try {
    const answer = await api.post('/api/components/graph-values', { path: S.path, block: current.block, values: current.pending });
    if (active === current) {
      const byNode = Object.fromEntries(answer.nodes.map((n) => [n.id, paramsOf(n)]));
      if (current.llama) {
        current.llama.chain.update(byNode);
        if (byNode[current.instrument]) current.llama.voice.set(llamaSettings(byNode[current.instrument]));
      } else updateLivePreview(byNode);
    }
  } catch (error) {
    // a value the song cannot take (a half-typed number) must not end the preview: the next move tries again
    if (active === current && error?.message) console.debug('live preview', error.message);
  } finally {
    current.busy = false;
    if (current.dirty && active === current) { current.dirty = false; setTimeout(flush, 30); }
  }
}

/** A widget is being moved: the new values go to the chain (at most about 30 times a second, the last one always arrives). */
export function liveChange(block, changes) {
  if (!active || active.block !== block) return;
  Object.assign(active.pending, changes);
  if (active.busy) active.dirty = true; else flush();
}

// the player was stopped from outside (Stop, another take): the preview is over, and the switches say so
onPlayer(() => {
  if (!active || active.starting) return;
  if (active.llama) { if (playerState().playing) stopLive({ player: false }); return; }   // the llama has no take in the player: it ends with its switch, or when something else plays
  const state = playerState();
  if (!state.playing && !state.loading && state.take !== 'audition') stopLive({ player: false });
});

// leaving the song ends it too
document.addEventListener('ample:open', () => stopLive());
