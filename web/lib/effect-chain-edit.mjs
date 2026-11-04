const audioPort = (template, direction) => {
  const port = template?.[direction]?.find((item) => (Array.isArray(item) ? item[1] : item.type) === 'audio');
  return port ? (Array.isArray(port) ? port[0] : port.name) : null;
};

/** Builds one undoable edit sequence for adding, removing or reordering a linear effect chain. */
export function effectChainEdit(source, entries, links, nodes, templates, action) {
  const byId = new Map(entries.map((entry) => [entry.id, entry]));
  const allNodes = new Map(nodes.map((node) => [node.id, node]));
  const chainIds = new Set(byId.keys());
  const incoming = entries.map((entry) => links.find((link) => link.to === entry.id && link.type === 'audio' && (link.from === source.id || chainIds.has(link.from))));
  if (incoming.some((link) => !link)) return { error: 'Manage effects only when they form one connected audio chain.' };
  const ordered = [];
  let previous = source.id;
  while (ordered.length < entries.length) {
    const next = entries.find((entry) => incoming.find((link) => link.to === entry.id)?.from === previous);
    if (!next || ordered.includes(next)) return { error: 'The connected effects do not form a single linear chain.' };
    ordered.push(next); previous = next.id;
  }
  const pathLinks = incoming;
  const tail = ordered.at(-1);
  const terminal = links.filter((link) => link.from === (tail?.id || source.id) && link.type === 'audio' && !chainIds.has(link.to));
  const disconnect = [...pathLinks, ...terminal].map((link) => ({ type: 'disconnect', to: link.to, input: link.inName }));
  const connectOrder = (nodes, downstream) => {
    const ops = [];
    let from = source;
    for (const node of nodes) {
      const output = audioPort(templates[from.tpl], 'outputs');
      const input = audioPort(templates[node.tpl], 'inputs');
      if (!output || !input) return null;
      ops.push({ type: 'connect', from: from.id, output, to: node.id, input });
      from = node;
    }
    const output = audioPort(templates[from.tpl], 'outputs');
    if (!output) return null;
    if (downstream.some((link) => !audioPort(templates[allNodes.get(link.to)?.tpl], 'inputs'))) return null;
    for (const link of downstream) ops.push({ type: 'connect', from: from.id, output, to: link.to, input: link.inName });
    return ops;
  };
  if (action.kind === 'add') {
    const template = templates[action.type];
    if (!template || !audioPort(template, 'inputs') || !audioPort(template, 'outputs')) return { error: 'Choose an effect with audio input and output.' };
    const occupied = new Set(allNodes.keys());
    let name = `${source.id}-${action.type}`.replace(/[^A-Za-z0-9_-]/g, '-').slice(0, 54);
    const root = name;
    for (let suffix = 2; occupied.has(name); suffix++) name = `${root.slice(0, 60 - String(suffix).length)}-${suffix}`;
    const created = { id: name, tpl: action.type };
    const effects = [...ordered, created];
    const wiring = connectOrder(effects, terminal);
    if (!wiring) return { error: 'The selected effect chain cannot be rewired safely.' };
    const neutral = { eq: { low: '0 dB', mid: '0 dB', high: '0 dB' }, compressor: { ratio: '1', makeup: '0 dB' }, duck: { depth: '0 dB' }, distortion: { mix: '0 %' }, delay: { mix: '0 %' }, reverb: { mix: '0 %' }, filter: { enabled: 'off' } }[action.type] || {};
    const parameters = Object.entries(neutral).map(([key, value]) => ({ type: 'setParameter', block: name, name: key, value }));
    return { ops: [{ type: 'addBlock', blockType: action.type, name }, ...parameters, ...disconnect, ...wiring] };
  }
  if (action.kind !== 'remove' && action.kind !== 'move') return { error: 'Unknown effect-chain action.' };
  const targetIndex = ordered.findIndex((entry) => entry.id === action.id);
  if (targetIndex < 0) return { error: 'The effect is no longer in this chain.' };
  const desired = [...ordered];
  if (action.kind === 'remove') desired.splice(targetIndex, 1);
  else {
    const other = targetIndex + action.delta;
    if (other < 0 || other >= desired.length) return { error: 'The effect is already at that end of the chain.' };
    [desired[targetIndex], desired[other]] = [desired[other], desired[targetIndex]];
  }
  const outside = links.filter((link) => chainIds.has(link.from) && !chainIds.has(link.to));
  if (outside.some((link) => link.from !== tail?.id)) return { error: 'Reordering is unavailable while an effect has a separate audio consumer.' };
  const wiring = connectOrder(desired, terminal);
  if (!wiring) return { error: 'The selected effect chain cannot be rewired safely.' };
  const removeOps = action.kind === 'remove' ? [{ type: 'removeBlock', block: action.id }] : [];
  return { ops: [...disconnect, ...wiring, ...removeOps] };
}
