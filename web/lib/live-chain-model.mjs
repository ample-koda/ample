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
export const CHAIN_EFFECTS = new Set(['volume', 'eq', 'peq', 'filter', 'compressor', 'duck', 'distortion', 'delay', 'reverb', 'convolution', 'chorus', 'tremolo', 'pan', 'width', 'phaser', 'flanger', 'gate', 'limiter', 'deesser', 'bitcrusher', 'master']);
/** Instruments that play the notes of a midi-clip (through the five note transforms) and that the browser renders itself: synth, piano, bass, guitar and the pitched sample sets (plan 3.4, 3.5). */
export const NOTE_INSTRUMENTS = new Set(['synth', 'piano', 'bass', 'guitar']);
export const SUM_TYPES = new Set(['mixer', 'bus', 'send', 'region-merge']);   // a send has two outputs: `through` (its own id) and `send` (id:send); a region merge adds the body of a loop and its processed seam (issue 79)
/** Buffer operations on a sound that is a buffer already (a built take or another local block): re-made in the browser when a knob moves, swapped in without a gap. */
export const LOCAL_OPS = new Set(['cut', 'fade', 'shift', 'reverse', 'chop', 'rewind', 'scratch', 'normalize', 'pitch', 'stretch', 'granular', 'denoise', 'trim', 'autotune']);
/** Buffer blocks with several inputs or none (plan local-preview 2.5): made in the browser from the sounds they work on, or from a file of the project. Splitter is one item per used output (id `splitter:left`). */
export const LOCAL_BUFFERS = new Set(['click', 'import-audio', 'ambience', 'join', 'crossfade', 'splitter']);
/** Blocks that pass the sound on unchanged (a Meter measures and reports, its audio out is its audio in): the plan looks through them, so adding one changes nothing of the audio graph. */
export const PASS_THROUGH = new Set(['meter']);
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

const laneOf = (name) => { if (name === 'body') return 0; if (name === 'region') return 1; const m = /^in(\d+)$/.exec(name || ''); return m ? Number(m[1]) - 1 : -1; };

/**
 * planChain({ nodes, links, target, takeOf })
 *  nodes: [{ id, tpl, params }], links: flat().links, takeOf(id): the built take of a block or a falsy value.
 * Returns { ok: true, items, output, sources } or { ok: false, reason }.
 *  items (dependencies first): { id, kind: 'source' | 'effect' | 'sum', tpl, take?, inputs: [{ from, lane }] } with lane = the mixer lane, or 0 for an effect.
 */
export function planChain({ nodes, links, target: requested, takeOf }) {
  const byId = new Map(nodes.map((node) => [node.id, node]));
  if (!byId.has(requested)) return { ok: false, reason: 'This block is not on the board.' };
  let target = requested;
  const items = [], seen = new Set(), visiting = new Set(), notes = [];
  // what a link carries away from a block: a splitter has two outputs, each its own item
  const audioIn0 = (id) => links.filter((link) => link.to === id && link.type === 'audio' && !link.disabled && !link.expression);
  // a link that comes from a pass-through block is the link that block reads from, followed back until a real block is reached
  const resolveLink = (link) => {
    let current = link;
    for (let guard = 0; guard < 16; guard++) {
      const from = byId.get(current.from);
      if (!from || !PASS_THROUGH.has(from.tpl)) return current;
      const inner = audioIn0(from.id).find((l) => l.inName === 'in' || l.inName === 'audio');
      if (!inner) return current;
      current = inner;
    }
    return current;
  };
  const sourceOf = (raw) => sourceOfDirect(resolveLink(raw));
  const sourceOfDirect = (link) => {
    const tpl = byId.get(link.from)?.tpl;
    if (tpl === 'splitter') return `${link.from}:${link.outName === 'right' ? 'right' : 'left'}`;
    if (tpl === 'import-audio' && link.outName === 'body') return `${link.from}:body`;
    if (tpl === 'import-audio' && link.outName === 'loop-region') return `${link.from}:region`;
    if (tpl === 'sequencer') return `${link.from}:${link.outName || 'mix'}`;
    if (tpl === 'send' && link.outName === 'send') return `${link.from}:send`;
    return link.from;
  };
  const audioIn = (id) => links.filter((link) => link.to === id && link.type === 'audio' && !link.disabled && !link.expression);
  // a region (the seam of a loop) goes through an effect as the sound it is, and only a region merge turns it back into audio (issue 79)
  const regionIn = (id) => links.filter((link) => link.to === id && link.type === 'region' && !link.disabled && !link.expression);
  const BUFFER_KINDS = new Set(['source', 'local']);
  // a block the browser can make itself returns a function that adds it: it answers undefined when done, a text when something upstream is wrong, or null to use the built take
  const localItem = (node, planId = node.id) => {
    const id = node.id, params = node.params || {};
    if (node.tpl === 'silence') return () => { seen.add(id); items.push({ id, kind: 'local', tpl: 'silence', inputs: [] }); };
    if (node.tpl === 'click') return () => { seen.add(id); items.push({ id, kind: 'local', tpl: 'click', inputs: [] }); };
    if (node.tpl === 'import-audio' || node.tpl === 'ambience') {
      if (!String(params.file || '').replace(/^"|"$/g, '').trim()) return null;
      const port = node.tpl === 'import-audio' ? planId.split(':')[1] : '';   // the whole loop, its body or its seam: each is its own sound
      return () => { seen.add(planId); items.push({ id: planId, kind: 'local', tpl: node.tpl, inputs: [], ...(port ? { base: id, port } : {}) }); };
    }
    if (node.tpl === 'join' || node.tpl === 'crossfade' || node.tpl === 'splitter' || node.tpl === 'vocoder') {
      const links = audioIn(id).filter((l) => (node.tpl === 'splitter' ? (l.inName === 'in' || l.inName === 'audio') : node.tpl === 'crossfade' ? l.inName === 'a' || l.inName === 'b' : node.tpl === 'vocoder' ? l.inName === 'modulator' || l.inName === 'carrier' : /^(in|audio)\d+$/.test(l.inName)));
      if (!links.length) return null;
      if (links.some((l) => !byId.get(resolveLink(l).from))) return null;   // after an effect or a mixer its input is the sound of the chain up to there, rendered when it is needed
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
      if (!byId.get(resolveLink(link).from)) return null;
      // an Autotune with a Melody input follows notes the live chain does not resolve: it plays its built take (the song preview and the build follow the notes)
      if (node.tpl === 'autotune' && links.some((l) => l.to === id && l.type === 'midi' && l.inName === 'target' && !l.disabled)) return null;
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
      if (node.tpl === 'import-audio' && id !== baseId(id)) return `${baseId(id)} has no file the browser can read, so the ${id.split(':')[1]} of its loop is not heard in the live chain.`;
      const take = takeOf(baseId(id));
      if (!take) return `Build ${baseId(id)} first: the live chain loops the built sound of the blocks it starts from and never builds by itself.`;
      // a lane of a sequencer is its own audio of the take (role lane2); the plain take is the mix of all lanes
      const lane = node.tpl === 'sequencer' && /^lane\d+$/.test(id.split(':')[1] || '') ? id.split(':')[1] : '';
      seen.add(id); items.push({ id, kind: 'source', tpl: node.tpl, take, ...(lane ? { role: lane } : {}), inputs: [] });
      return null;
    }
    visiting.add(id);
    const regions = node.tpl === 'region-merge' ? regionIn(id) : effect && !audioIn(id).some((l) => l.inName === 'in') ? regionIn(id).map((l) => ({ ...l, inName: 'in' })) : [];
    const incoming = [...audioIn(id), ...regions], inputs = [];
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
  // starting the chain at a Meter starts it at what the Meter is fed by
  if (PASS_THROUGH.has(byId.get(requested).tpl)) {
    const fed = links.find((l) => l.to === requested && l.type === 'audio' && !l.disabled && !l.expression && (l.inName === 'in' || l.inName === 'audio'));
    if (fed) target = sourceOf(fed);
  }
  const problem = visit(target);
  if (problem) return { ok: false, reason: problem };
  return { ok: true, items, output: target, sources: items.filter((item) => item.kind === 'source' || item.kind === 'local').map((item) => item.id), notes };
}

/**
 * The effects and mixers whose sound a block of the plan works on: everything between it and the sources and local blocks before it, dependencies first, and the ids of those
 * sounds at the edge (`leaves`). A buffer block after an effect is made from the rendered sound of exactly these (issue 73).
 * @param {{ items: Array<object> }} plan
 * @param {Array<{ from: string }>} inputs the inputs of the block
 */
export function upstreamOf(plan, inputs) {
  const byId = new Map(plan.items.map((item) => [item.id, item]));
  const chain = [], seen = new Set(), leaves = [];
  const walk = (id) => {
    const item = byId.get(id) ?? byId.get(baseId(id));
    if (!item || item.kind === 'source' || item.kind === 'local') { if (!leaves.includes(id)) leaves.push(id); return; }
    if (seen.has(item.id)) return;
    seen.add(item.id);
    for (const link of item.inputs || []) walk(link.from);
    if (item.key) walk(item.key);
    chain.push(item);
  };
  for (const link of inputs) walk(link.from);
  return { chain, leaves };
}

/** The structure of a plan without the takes of its sources: two plans with the same shape are the same graph, only a rebuilt sound differs (issue 275). */
export const planShape = (plan) => JSON.stringify(plan.items.map((i) => [i.id, i.kind, i.tpl, i.role || '', i.inputs, i.midi || null, i.key || null]));

/** The sources whose take is another one in the new plan (same graph): their new sound can replace the old one without starting the chain again. */
export function changedSources(before, after) {
  if (planShape(before) !== planShape(after)) return null;
  const was = new Map(before.items.map((i) => [i.id, i.take]));
  return after.items.filter((i) => i.kind === 'source' && was.get(i.id) !== i.take);
}

/** The blocks of a plan that can be bypassed or tuned: every effect and sum, in order. */
export const tunable = (plan) => plan.items.filter((item) => item.kind !== 'source' && item.kind !== 'local');

/** Whether a solo bus silences the others: only a bus that is part of the audition (`planIds`: the ids of the plan) counts, so a solo elsewhere (another branch, downstream of the target, removed from the chain) never mutes it. */
export const soloInPlan = (nodes, planIds) => nodes.some((n) => n.tpl === 'bus' && n.params?.solo === 'on' && planIds.has(n.id));
