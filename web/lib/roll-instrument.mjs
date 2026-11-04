const effects = new Set(['transpose', 'chord', 'quantize', 'midi-humanize', 'arpeggiator']);
const instruments = new Set(['gm', 'synth', 'piano', 'bass', 'sampler', 'guitar', 'delay-llama']);
const disabled = (node) => !node || node.disabledBy || node.flags?.includes('disabled');

/** Every instrument the clip's active MIDI routing reaches (each with the midi effects in front of it, in order): the blocks that can sound this clip. */
export function rollConsumers(graph, clipId) {
  const found = [];
  const visit = (id, chain, seen) => {
    if (seen.has(id)) return;
    const nextSeen = new Set([...seen, id]);
    for (const link of graph.links.filter((item) => item.from === id && item.type === 'midi' && !item.disabled)) {
      const node = graph.nodes.find((item) => item.id === link.to);
      if (disabled(node)) continue;
      if (instruments.has(node.tpl)) { if (!found.some((f) => f.node.id === node.id)) found.push({ node, chain }); }
      else if (effects.has(node.tpl)) visit(node.id, [...chain, node], nextSeen);
    }
  };
  visit(clipId, [], new Set());
  return found;
}

/** The instrument that sounds the clip while it is worked on: the one that was picked when the clip still reaches it, else the first. */
export function resolveRollInstrument(graph, clipId, preferred = null) {
  const all = rollConsumers(graph, clipId);
  return all.find((c) => c.node.id === preferred) || all[0] || null;
}

/** Follow the clip's active MIDI routing to its first instrument, retaining the effect order (the old way). */
export function firstRollInstrument(graph, clipId) {
  const visit = (id, chain, seen) => {
    if (seen.has(id)) return null;
    const nextSeen = new Set([...seen, id]);
    for (const link of graph.links.filter((item) => item.from === id && item.type === 'midi' && !item.disabled)) {
      const node = graph.nodes.find((item) => item.id === link.to);
      if (disabled(node)) continue;
      if (instruments.has(node.tpl)) return { node, chain };
      if (effects.has(node.tpl)) { const found = visit(node.id, [...chain, node], nextSeen); if (found) return found; }
    }
    return null;
  };
  return visit(clipId, [], new Set());
}

/** Include effective seeds and every instrument setting in preview cache identity. */
export function rollVoiceKey(route, seeds, note, bpm, sampleRate) {
  return JSON.stringify({ instrument: route.node, effects: route.chain, seeds, note, bpm, sampleRate });
}
