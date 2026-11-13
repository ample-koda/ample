// The live chain of a song: which blocks lie between the built sounds and a chosen block, in the order they must be built into one Web Audio graph. Pure (no audio), so the
// walk is tested in node. Effects with a live graph run live; mixers and buses sum; any other block with audio out must have a built take, which is looped as a source.
// The song text stays the truth: the plan is made again from the flat graph whenever blocks or links change.
import { selectPreviewVoice } from './clientside-note-preview.mjs';
import { sequencePreviewPlan } from './clientside-sequencer.mjs';

/** Whether the browser can play the lanes of a sequencer block itself: an inline sequence of plain tone channels (square, saw, triangle), as the build preview. */
export function sequencerLocal(params = {}) {
  const plan = String(params.sequence || '').trim() ? sequencePreviewPlan(params.sequence, params.lanes, 1, 1) : null;
  return Boolean(plan && plan.channels.every(({ channel }) => ['square', 'saw', 'triangle'].includes(channel.settings.wave || (channel.type === 'bass' ? 'saw' : channel.type === 'piano' ? 'triangle' : 'square'))));
}

/** Block types that have a persistent live graph (studio/preview/audio.js liveEffectGraph) and can sit in a chain (convolution loads its impulse response in the chain, plan 2.3). */
export const CHAIN_EFFECTS = new Set(['volume', 'eq', 'peq', 'filter', 'compressor', 'duck', 'distortion', 'delay', 'reverb', 'convolution', 'chorus', 'tremolo', 'pan', 'width', 'phaser', 'flanger', 'gate', 'limiter', 'deesser', 'master']);
/** Instruments that play the notes of a midi-clip (through the five note transforms) and that the browser renders itself: synth, piano, bass, guitar and the pitched sample sets (plan 3.4, 3.5). */
export const NOTE_INSTRUMENTS = new Set(['synth', 'piano', 'bass', 'guitar']);
export const SUM_TYPES = new Set(['mixer', 'bus', 'send']);   // a send has two outputs: `through` (its own id) and `send` (id:send)
/** Buffer operations on a sound that is a buffer already (a built take or another local block): re-made in the browser when a knob moves, swapped in without a gap. */
export const LOCAL_OPS = new Set(['cut', 'fade', 'shift', 'reverse', 'chop', 'rewind', 'scratch', 'normalize', 'pitch', 'stretch', 'granular', 'denoise', 'trim']);
/** Buffer blocks with several inputs or none (plan local-preview 2.5): made in the browser from the sounds they work on, or from a file of the project. Splitter is one item per used output (id `splitter:left`). */
export const LOCAL_BUFFERS = new Set(['click', 'import-audio', 'ambience', 'join', 'crossfade', 'splitter']);
/** The id of the block behind a plan id (a splitter output `s:left` is the block `s`). */
export const baseId = (id) => String(id).split(':')[0];
/** The five note transforms between a midi-clip and an instrument; they are plain functions of the note list. */
export const MIDI_TRANSFORMS = new Set(['transpose', 'chord', 'quantize', 'midi-humanize', 'arpeggiator']);

/** The notes feeding an instrument: { clip, transforms: [{ id, tpl }] } (transforms from the clip towards the instrument), or null when the walk ends anywhere but a midi-clip. */
export function midiChain(id, byId, links) {
  const transforms = [];
  let current = id;
  for (let guard = 0; guard < 16; guard++) {
    const link = links.find((l) => l.to === current && l.type === 'midi' && l.inName === 'notes' && !l.disabled);
    const from = link && byId.get(link.from);
    if (!from) return null;
    if (from.tpl === 'midi-clip') return { clip: from.id, transforms: transforms.reverse() };
    if (from.tpl === 'fire') {   // notes made from a control (a follow of a sound, or a curve): plan 2.8
      const control = links.find((l) => l.to === from.id && l.type === 'control' && l.inName === 'in' && !l.disabled);
      const source = control && byId.get(control.from);
      if (!source || !['follow', 'curve'].includes(source.tpl)) return null;
      return { clip: from.id, fire: { source: source.id, tpl: source.tpl }, transforms: transforms.reverse() };
    }
    if (!MIDI_TRANSFORMS.has(from.tpl)) return null;
    transforms.push({ id: from.id, tpl: from.tpl });
    current = from.id;
  }
  return null;
}

const laneOf = (name) => { const m = /^in(\d+)$/.exec(name || ''); return m ? Number(m[1]) - 1 : -1; };

/**
 * planChain({ nodes, links, target, takeOf })
 *  nodes: [{ id, tpl, params }], links: flat().links, takeOf(id): the built take of a block or a falsy value.
 * Returns { ok: true, items, output, sources } or { ok: false, reason }.
 *  items (dependencies first): { id, kind: 'source' | 'effect' | 'sum', tpl, take?, inputs: [{ from, lane }] } with lane = the mixer lane, or 0 for an effect.
 */
export function planChain({ nodes, links, target, takeOf }) {
  const byId = new Map(nodes.map((node) => [node.id, node]));
  if (!byId.has(target)) return { ok: false, reason: 'This block is not on the board.' };
  const items = [], seen = new Set(), visiting = new Set(), notes = [];
  // what a link carries away from a block: a splitter has two outputs, each its own item
  const sourceOf = (link) => { const tpl = byId.get(link.from)?.tpl; return tpl === 'splitter' ? `${link.from}:${link.outName === 'right' ? 'right' : 'left'}` : tpl === 'sequencer' ? `${link.from}:${link.outName || 'mix'}` : tpl === 'send' && link.outName === 'send' ? `${link.from}:send` : link.from; };
  const audioIn = (id) => links.filter((link) => link.to === id && link.type === 'audio' && !link.disabled && !link.expression);
  const BUFFER_KINDS = new Set(['source', 'local']);
  // a block the browser can make itself returns a function that adds it: it answers undefined when done, a text when something upstream is wrong, or null to use the built take
  const localItem = (node, planId = node.id) => {
    const id = node.id, params = node.params || {};
    if (node.tpl === 'silence') return () => { seen.add(id); items.push({ id, kind: 'local', tpl: 'silence', inputs: [] }); };
    if (node.tpl === 'click') return () => { seen.add(id); items.push({ id, kind: 'local', tpl: 'click', inputs: [] }); };
    if (node.tpl === 'import-audio' || node.tpl === 'ambience') {
      if (!String(params.file || '').replace(/^"|"$/g, '').trim()) return null;
      return () => { seen.add(id); items.push({ id, kind: 'local', tpl: node.tpl, inputs: [] }); };
    }
    if (node.tpl === 'join' || node.tpl === 'crossfade' || node.tpl === 'splitter' || node.tpl === 'vocoder') {
      const links = audioIn(id).filter((l) => (node.tpl === 'splitter' ? (l.inName === 'in' || l.inName === 'audio') : node.tpl === 'crossfade' ? l.inName === 'a' || l.inName === 'b' : node.tpl === 'vocoder' ? l.inName === 'modulator' || l.inName === 'carrier' : /^(in|audio)\d+$/.test(l.inName)));
      if (!links.length) return null;
      if (links.some((l) => { const up = byId.get(l.from); return !up || CHAIN_EFFECTS.has(up.tpl) || SUM_TYPES.has(up.tpl); })) return null;   // after an effect it plays its built take
      return () => {
        const inputs = [];
        for (const l of links) {
          const problem = visit(sourceOf(l));
          if (problem) return problem;
          inputs.push({ from: sourceOf(l), lane: node.tpl === 'vocoder' ? (l.inName === 'carrier' ? 1 : 0) : node.tpl === 'crossfade' ? (l.inName === 'b' ? 1 : 0) : node.tpl === 'join' ? Number(/\d+/.exec(l.inName)[0]) - 1 : 0 });
        }
        seen.add(planId); items.push({ id: planId, base: id, kind: 'local', tpl: node.tpl, inputs, ...(node.tpl === 'splitter' ? { port: planId.split(':')[1] } : {}) });
      };
    }
    if (node.tpl === 'drums') {
      if (String(params.kit || '').replace(/^"|"$/g, '').trim() !== 'builtin') return null;   // a sample kit is the server's
      return () => { seen.add(id); items.push({ id, kind: 'local', tpl: 'drums', inputs: [] }); };
    }
    if (node.tpl === 'sequencer') {
      if (!sequencerLocal(params)) return null;   // a sequence file, samples or other voices: the built take
      return () => { seen.add(planId); items.push({ id: planId, base: id, kind: 'local', tpl: 'sequencer', inputs: [], port: planId.split(':')[1] || 'mix' }); };
    }
    if (NOTE_INSTRUMENTS.has(node.tpl)) {
      const voice = selectPreviewVoice({ node }, {});
      const chain = midiChain(id, byId, links);
      if (!chain || !voice.local || voice.kind === 'fallback' || params.enabled === 'off') return null;   // humanize, a server SoundFont or a missing clip: the built take
      return () => { seen.add(id); items.push({ id, kind: 'local', tpl: node.tpl, inputs: [], midi: chain, voice: voice.kind }); };
    }
    if (node.tpl === 'midi') {
      // the direct audio of a midi file: the whole file through the pack General MIDI SoundFont in the browser (4.8); a project SoundFont or no file: the built take
      if (!String(params.file || '').replace(/^"|"$/g, '').trim() || audioIn(id).length) return null;
      return () => { seen.add(id); items.push({ id, kind: 'local', tpl: 'midi', inputs: [] }); };
    }
    if (node.tpl === 'sampler') {
      const chain = midiChain(id, byId, links);
      if (!chain || !String(params.file || '').replace(/^"|"$/g, '').trim()) return null;
      return () => { seen.add(id); items.push({ id, kind: 'local', tpl: 'sampler', inputs: [], midi: chain }); };
    }
    if (LOCAL_OPS.has(node.tpl)) {
      const link = audioIn(id).find((l) => l.inName === 'in');
      if (!link) return null;
      const upstream = byId.get(link.from);
      if (!upstream || CHAIN_EFFECTS.has(upstream.tpl) || SUM_TYPES.has(upstream.tpl)) return null;   // after an effect it plays its built take
      return () => {
        const problem = visit(sourceOf(link));
        if (problem) return problem;
        seen.add(id); items.push({ id, kind: 'local', tpl: node.tpl, inputs: [{ from: sourceOf(link), lane: 0 }] });
      };
    }
    return null;
  };
  const visit = (requested) => {
    const id = byId.get(baseId(requested))?.tpl === 'send' ? baseId(requested) : requested;   // both outputs of a send come from the one block
    if (seen.has(id)) return null;
    if (visiting.has(id)) return 'The blocks are connected in a loop.';
    const node = byId.get(baseId(id));
    if (!node) return `Unknown block ${id}.`;
    const effect = CHAIN_EFFECTS.has(node.tpl), sum = SUM_TYPES.has(node.tpl);
    if (!effect && !sum) {
      const local = localItem(node, id);
      if (local) { const problem = local(); return problem || null; }
      const take = takeOf(baseId(id));
      if (!take) return `Build ${baseId(id)} first: the live chain loops the built sound of the blocks it starts from and never builds by itself.`;
      // a lane of a sequencer is its own audio of the take (role lane2); the plain take is the mix of all lanes
      const lane = node.tpl === 'sequencer' && /^lane\d+$/.test(id.split(':')[1] || '') ? id.split(':')[1] : '';
      seen.add(id); items.push({ id, kind: 'source', tpl: node.tpl, take, ...(lane ? { role: lane } : {}), inputs: [] });
      return null;
    }
    visiting.add(id);
    const incoming = audioIn(id), inputs = [];
    let key = null;
    for (const link of incoming) {
      if (node.tpl === 'compressor' && link.inName === 'key') {   // the sidechain: the key take is built into the chain like a source and only drives the detector
        const problem = visit(sourceOf(link));
        if (problem) return problem;
        key = sourceOf(link); continue;
      }
      if (effect && link.inName !== (node.tpl === 'master' ? 'mix' : 'in')) { notes.push(`${id}: the ${link.inName} input is not heard in the live chain`); continue; }
      if (sum && laneOf(link.inName) < 0) continue;
      const problem = visit(sourceOf(link));
      if (problem) return problem;
      inputs.push({ from: sourceOf(link), lane: effect ? 0 : laneOf(link.inName) });
    }
    visiting.delete(id);
    if (!inputs.length) return `${id} has no sound coming in: connect a block to its audio input first.`;
    seen.add(id); items.push({ id, kind: effect ? 'effect' : 'sum', tpl: node.tpl, inputs, ...(key ? { key } : {}) });
    return null;
  };
  const problem = visit(target);
  if (problem) return { ok: false, reason: problem };
  return { ok: true, items, output: target, sources: items.filter((item) => item.kind === 'source' || item.kind === 'local').map((item) => item.id), notes };
}

/** The blocks of a plan that can be bypassed or tuned: every effect and sum, in order. */
export const tunable = (plan) => plan.items.filter((item) => item.kind !== 'source' && item.kind !== 'local');
