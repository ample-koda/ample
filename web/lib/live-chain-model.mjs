// The live chain of a song: which blocks lie between the built sounds and a chosen block, in the order they must be built into one Web Audio graph. Pure (no audio), so the
// walk is tested in node. Effects with a live graph run live; mixers and buses sum; any other block with audio out must have a built take, which is looped as a source.
// The song text stays the truth: the plan is made again from the flat graph whenever blocks or links change.

/** Block types that have a persistent live graph (studio/preview/audio.js liveEffectGraph) and can sit in a chain (convolution needs its impulse, so it is left out here). */
export const CHAIN_EFFECTS = new Set(['volume', 'eq', 'peq', 'filter', 'compressor', 'duck', 'distortion', 'delay', 'reverb', 'chorus', 'tremolo', 'pan', 'width', 'phaser', 'flanger', 'gate', 'limiter', 'deesser']);
export const SUM_TYPES = new Set(['mixer', 'bus']);
/** Buffer operations on a sound that is a buffer already (a built take or another local block): re-made in the browser when a knob moves, swapped in without a gap. */
export const LOCAL_OPS = new Set(['cut', 'fade', 'shift', 'reverse', 'chop', 'rewind', 'scratch']);
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
  const audioIn = (id) => links.filter((link) => link.to === id && link.type === 'audio' && !link.disabled && !link.expression);
  const BUFFER_KINDS = new Set(['source', 'local']);
  // a block the browser can make itself returns a function that adds it: it answers undefined when done, a text when something upstream is wrong, or null to use the built take
  const localItem = (node) => {
    const id = node.id, params = node.params || {};
    if (node.tpl === 'drums') {
      if (String(params.kit || '').replace(/^"|"$/g, '').trim() !== 'builtin') return null;   // a sample kit is the server's
      return () => { seen.add(id); items.push({ id, kind: 'local', tpl: 'drums', inputs: [] }); };
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
        const problem = visit(link.from);
        if (problem) return problem;
        seen.add(id); items.push({ id, kind: 'local', tpl: node.tpl, inputs: [{ from: link.from, lane: 0 }] });
      };
    }
    return null;
  };
  const visit = (id) => {
    if (seen.has(id)) return null;
    if (visiting.has(id)) return 'The blocks are connected in a loop.';
    const node = byId.get(id);
    if (!node) return `Unknown block ${id}.`;
    const effect = CHAIN_EFFECTS.has(node.tpl), sum = SUM_TYPES.has(node.tpl);
    if (!effect && !sum) {
      const local = localItem(node);
      if (local) { const problem = local(); return problem || null; }
      const take = takeOf(id);
      if (!take) return `Build ${id} first: the live chain loops the built sound of the blocks it starts from and never builds by itself.`;
      seen.add(id); items.push({ id, kind: 'source', tpl: node.tpl, take, inputs: [] });
      return null;
    }
    visiting.add(id);
    const incoming = audioIn(id), inputs = [];
    let key = null;
    for (const link of incoming) {
      if (node.tpl === 'compressor' && link.inName === 'key') {   // the sidechain: the key take is built into the chain like a source and only drives the detector
        const problem = visit(link.from);
        if (problem) return problem;
        key = link.from; continue;
      }
      if (effect && link.inName !== 'in') { notes.push(`${id}: the ${link.inName} input is not heard in the live chain`); continue; }
      if (sum && laneOf(link.inName) < 0) continue;
      const problem = visit(link.from);
      if (problem) return problem;
      inputs.push({ from: link.from, lane: effect ? 0 : laneOf(link.inName) });
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
