// The graph the canvas draws, derived from the server's document view. Pure functions, no DOM.
// @ample:groups @ample:ports-links

import { autoLayout } from './geometry.js';
import { tieGroups } from '../lib/midi-events.js';

const BUILTIN_EFFECT_TYPES = new Set(['eq', 'filter', 'compressor', 'duck', 'distortion', 'delay', 'reverb', 'volume']);

/**
 * Builds the flat graph (every block, every connection) from a document view and the templates.
 * Link: { from, out (index), to, inp (index), type, outName, inName }.
 */
/** The index of an output by name; "out" is the one name for the sound of a block, so it finds the first audio output when the block has no port of that name. */
function outIndex(block, name) {
  let i = block.outs.findIndex((q) => q[0] === name);
  if (i < 0 && name === 'out') i = block.outs.findIndex((q) => q[1] === 'audio');
  return i;
}

/** While a region line is being dragged, the effects show their region input too (otherwise it shows only when a region is connected). */
export const regionReveal = { on: false, control: false };   // on: a region is dragged (the loop seam ports show); control: a control is dragged (the control inputs of the parameters show)

export function flatGraph(doc, templates) {
  const blocks = doc.blocks;
  const disabled = new Map(blocks.filter((b) => b.flags.includes('disabled')).map((b) => [b.name, b.name]));
  for (let changed = true; changed;) {
    changed = false;
    for (const b of blocks) {
      if (disabled.has(b.name)) continue;
      const template = templates[b.type];
      if ((b.type === 'mixer' || b.type === 'bus') && b.froms.every((f) => f.disabled || disabled.has(f.block))) { disabled.set(b.name, b.froms[0]?.block || b.name); changed = true; continue; }
      const input = b.froms.find((f) => (f.disabled || disabled.has(f.block)) && (template?.inputs.some((p) => p.name === f.input && p.required) || BUILTIN_EFFECT_TYPES.has(b.type) && ['in', 'render_in'].includes(f.input)));
      if (input) { disabled.set(b.name, input.block); changed = true; }
    }
  }
  // a rewind whose Scratches output nothing uses yet: the effects show their region input at once, so there is a place to drop it
  const freeScratches = blocks.some((r) => r.type === 'rewind' && !blocks.some((x) => x.froms.some((f) => f.block === r.name && f.output === 'region')));
  const auto = autoLayout(blocks, templates);
  const nodes = blocks.map((b) => {
    const t = templates[b.type];
    const p = b.layout || auto.get(b.name) || { x: 20, y: 20 };
    return {
      id: b.name,
      title: b.name,
      tpl: b.type,
      cls: b.class || (t ? t.class : 'stable'),
      ver: b.version || (t ? t.version : ''),
      dyn: !!(t && t.dynamic),
      plugin: t ? t.plugin : null,
      params: Object.fromEntries(b.parameters.map((q) => [q.name, q.value])),
      flags: b.flags,
      disabledBy: disabled.get(b.name) || null,
      freeze: b.freeze,
      line: b.line,
      parts: b.parts,
      placed: !!b.layout,
      known: !!t,
      cardHeight: t?.cardHeight || 0,   // the controls a component shows on its card (its card layout)
      x: p.x,
      y: p.y,
      // an effect shows its loop seam ports only when they are in use (a region comes in, or its region goes on); a dragged region line reveals the inputs
      ins: t ? visibleInputs(b, t).filter((q) => q.name !== 'region_in' || regionReveal.on || freeScratches || b.froms.some((f) => f.input === 'region_in')).map((q) => [q.name, q.type]) : [],
      outs: t ? t.outputs.filter((q) => (b.type !== 'volume' || q.name !== 'out') && (q.type !== 'region' || !t.inputs.some((i) => i.name === 'region_in') || b.froms.some((f) => f.input === 'region_in') || blocks.some((x) => x.froms.some((f) => f.block === b.name && f.output === q.name)))).map((q) => [q.name, q.type]) : [],
    };
  });
  const modules = (doc.uses || []).map((u, i) => ({
    id: u.instance, title: u.instance, tpl: 'module', cls: 'stable', ver: u.version || '', dyn: false, plugin: null,
    params: Object.fromEntries((u.parameters || []).map((p) => [p.name, p.value])), flags: [], freeze: null, line: u.line, isMidiModule: !!u.isMidi,
    parts: [], placed: true, known: true, isModule: true, moduleName: u.module,
    x: 500, y: 100 + i * 170, ins: (u.inputs || []).map((p) => [p.name, p.type]), outs: (u.outputs || []).map((p) => [p.name, p.type]),
  }));
  nodes.push(...modules);
  const eventCounts = new Map();
  for (const line of (doc.text || '').split('\n')) {
    const event = /^\s*# source-event track=(\d+)\s/.exec(line);
    if (event) eventCounts.set(Number(event[1]), (eventCounts.get(Number(event[1])) || 0) + 1);
  }
  blocks.filter((block) => block.type === 'midi-clip').forEach((block, index) => {
    const namedTrack = /^track-(\d+)$/i.exec(block.name);
    const track = namedTrack ? Number(namedTrack[1]) - 1 : index;
    const count = eventCounts.get(track) || 0;
    const clip = nodes.find((node) => node.id === block.name);
    const ties = tieGroups(clip?.params?.notes).length;
    if (clip && (count || ties)) clip.midiEvents = { track, count, ties };   // shown in the properties of the clip (button MIDI events), not as a second card on the canvas
  });
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const links = [];
  for (const b of blocks) {
    const to = byId.get(b.name);
    for (const f of b.froms) {
      const from = byId.get(f.block);
      if (!from || !to) continue;
      const out = outIndex(from, f.output);
      const inp = to.ins.findIndex((q) => q[0] === f.input);
      if (out < 0 || inp < 0) continue;
      links.push({ from: from.id, out, to: to.id, inp, type: from.outs[out][1], outName: f.output, inName: f.input, disabled: !!f.disabled });
    }
  }
  // the controls that a parameter reads in its text (cutoff 600 + 3000 * traffic.level) are links to the port of that parameter too
  for (const b of blocks) {
    const to = byId.get(b.name);
    for (const c of b.controlRefs || []) {
      const from = byId.get(c.block);
      if (!from || !to) continue;
      const out = outIndex(from, c.output);
      const inp = to.ins.findIndex((q) => q[0] === c.parameter);
      if (out < 0 || inp < 0) continue;
      links.push({ from: from.id, out, to: to.id, inp, type: from.outs[out][1], outName: c.output, inName: c.parameter, disabled: false, expression: true });
    }
  }
  for (const u of doc.uses || []) {
    const target = byId.get(u.instance);
    for (const f of u.froms || []) {
      const from = byId.get(f.block);
      const out = from ? outIndex(from, f.output) : -1;
      const inp = target?.ins.findIndex((q) => q[0] === f.input) ?? -1;
      if (from && target && out >= 0 && inp >= 0) links.push({ from: from.id, out, to: target.id, inp, type: from.outs[out][1], outName: f.output, inName: f.input });
    }
  }
  const effectTypes = new Set(['eq', 'filter', 'compressor', 'duck', 'distortion', 'delay', 'reverb', 'volume']);
  for (const node of nodes) {
    if (!node.outs.some((port) => port[1] === 'audio')) continue;
    node.effectChain = connectedEffectChain(node.id, nodes, links, BUILTIN_EFFECT_TYPES);
    node.renderChain = connectedEffectChain(node.id, nodes, links, BUILTIN_EFFECT_TYPES, 'render');
  }
  return { nodes, links };
}

/** Lists the ordered effect nodes reached from an audio block through audio outputs. */
export function connectedEffectChain(block, nodes, links, effectTypes, streamType = 'audio') {
  const byId = new Map(nodes.map((node) => [node.id, node]));
  const chain = [];
  const seen = new Set([block]);
  const visit = (source) => {
    for (const link of links) {
      if (link.disabled || link.from !== source || link.type !== streamType) continue;
      const target = byId.get(link.to);
      if (!target || !effectTypes.has(target.tpl) || seen.has(target.id)) continue;
      seen.add(target.id);
      chain.push(target);
      visit(target.id);
    }
  };
  visit(block);
  return chain;
}

/**
 * The nodes and links of one container: 'root' or a group id. A link is [a, outIndex, b, inIndex, flatLink?];
 * flatLink is present only when both ends are real blocks (those links can be deleted one by one).
 * Groups only exist in the view (the language has no inner graphs yet): `groups` maps
 * id to { id, title, members, x, y, expanded }.
 */
export function containerView(flat, groups, cid = 'root') {
  const groupOf = new Map();
  for (const g of Object.values(groups)) for (const m of g.members) groupOf.set(m, g.id);
  const ports = new Map(Object.values(groups).map((g) => [g.id, { ins: [], outs: [], inner: [] }]));
  for (const g of Object.values(groups)) {
    const P = ports.get(g.id);
    for (const e of g.exposedIns || []) P.ins.push({ key: `bind:${e.block}:${e.input}`, name: e.input, type: e.type, bound: e });
    for (const e of g.exposedOuts || []) P.outs.push({ key: `${e.block}:${e.out}`, name: e.name, type: e.type, bound: e });
  }
  const rootLinks = [];
  const seen = new Set();
  for (const l of flat.links) {
    const ga = groupOf.get(l.from), gb = groupOf.get(l.to);
    const real = [l.from, l.out, l.to, l.inp, l];
    if (ga && ga === gb) { ports.get(ga).inner.push(real); continue; }
    if (!ga && !gb) { rootLinks.push(real); continue; }
    const key = `${l.from}:${l.out}`;
    let a = l.from, ap = l.out, b = l.to, bp = l.inp;
    if (ga) {
      const P = ports.get(ga);
      let k = P.outs.findIndex((p) => p.key === key);
      if (k < 0) { P.outs.push({ key, name: l.outName, type: l.type }); k = P.outs.length - 1; }
      P.inner.push([l.from, l.out, 'out', k]);
      a = ga; ap = k;
    }
    if (gb) {
      const P = ports.get(gb);
      // a line that was added by hand to this input of the block inside takes the link
      let k = P.ins.findIndex((p) => p.bound && p.bound.block === l.to && p.bound.input === l.inName);
      if (k < 0) k = P.ins.findIndex((p) => p.key === key);
      if (k < 0) { P.ins.push({ key, name: l.inName, type: l.type }); k = P.ins.length - 1; }
      P.inner.push(['in', k, l.to, l.inp]);
      b = gb; bp = k;
    }
    const sig = `${a}|${ap}|${b}|${bp}`;
    if (!seen.has(sig)) { seen.add(sig); rootLinks.push([a, ap, b, bp]); }
  }
  // the owner can give the lines of a group a name (a hint of what comes out of it): g.labels maps the line to its name
  // without a name of their own the lines show the name of the block behind them (and the line too when one block has several lines on that side)
  const labelOf = (g, p, side) => {
    if (g.labels?.[p.key]) return g.labels[p.key];
    const block = p.bound?.block;
    if (!block) return p.name;
    return ports.get(g.id)[side].filter((q) => q.bound?.block === block).length > 1 ? `${block} · ${p.name}` : block;
  };
  const groupNode = (g) => ({
    id: g.id, title: g.title, tpl: 'group', cls: 'stable', ver: '1.0', dyn: false, params: {}, flags: [], isGroup: true, expanded: !!g.expanded,
    x: g.x, y: g.y, ins: ports.get(g.id).ins.map((p) => [labelOf(g, p, 'ins'), p.type]), outs: ports.get(g.id).outs.map((p) => [labelOf(g, p, 'outs'), p.type]),
    outKeys: ports.get(g.id).outs.map((p) => p.key), inKeys: ports.get(g.id).ins.map((p) => p.key), inBound: ports.get(g.id).ins.map((p) => p.bound || null),
  });
  if (cid === 'root') {
    return { nodes: [...flat.nodes.filter((n) => !groupOf.has(n.id)), ...Object.values(groups).map(groupNode)], links: rootLinks };
  }
  const g = groups[cid];
  if (!g) return { nodes: [], links: [] };
  const members = flat.nodes.filter((n) => g.members.includes(n.id));
  const minX = Math.min(...members.map((n) => n.x), 0), maxX = Math.max(...members.map((n) => n.x), 0), minY = Math.min(...members.map((n) => n.y), 0);
  const P = ports.get(cid);
  return {
    nodes: [
      { id: 'in', proxy: true, groupId: cid, flags: [], title: 'inputs', x: g.proxyPos?.in?.x ?? minX - 240, y: g.proxyPos?.in?.y ?? minY, ins: [], outs: P.ins.map((p) => [labelOf(g, p, 'ins'), p.type]), inKeys: P.ins.map((p) => p.key), bound: P.ins.map((p) => p.bound || null) },
      { id: 'out', proxy: true, groupId: cid, flags: [], title: 'outputs', x: g.proxyPos?.out?.x ?? maxX + 240, y: g.proxyPos?.out?.y ?? minY, ins: P.outs.map((p) => [labelOf(g, p, 'outs'), p.type]), outs: [], outKeys: P.outs.map((p) => p.key), bound: P.outs.map((p) => p.bound || null) },
      ...members,
    ],
    links: P.inner,
  };
}

let counter = 1;

/** Makes a group of the given blocks (all at the top level). Returns the new groups map, or an error text. */
export function groupBlocks(flat, groups, ids) {
  const taken = new Set(Object.values(groups).flatMap((g) => g.members));
  const members = flat.nodes.filter((n) => ids.includes(n.id) && !taken.has(n.id));
  if (members.length < 2) return { error: 'Select two or more blocks that are not in a group yet.' };
  while (groups[`group-${counter}`]) counter++;
  const id = `group-${counter}`;
  const g = {
    id,
    title: `Group ${counter}`,
    members: members.map((n) => n.id),
    expanded: true,
    x: Math.round((Math.min(...members.map((n) => n.x)) + Math.max(...members.map((n) => n.x))) / 2),
    y: Math.round((Math.min(...members.map((n) => n.y)) + Math.max(...members.map((n) => n.y))) / 2),
  };
  counter++;
  return { groups: { ...groups, [id]: g }, id };
}

export function ungroup(groups, id) {
  const rest = { ...groups };
  delete rest[id];
  return rest;
}

/** Drops members that no longer exist (a block was deleted or renamed in the source) and groups with fewer than two members. */
export function pruneGroups(groups, flat) {
  const names = new Set(flat.nodes.map((n) => n.id));
  const out = {};
  for (const g of Object.values(groups)) {
    const members = g.members.filter((m) => names.has(m));
    if (members.length >= 2) out[g.id] = { ...g, members };
  }
  return out;
}

/** The inputs a block shows: all of them, except a mixer, which shows as many lanes as its `inputs` says (4 to 16), and never fewer than the highest one in use. */
export function visibleInputs(block, template) {
  if (block.type !== 'mixer' && block.type !== 'bus') {
    const steered = (template.controlInputs || []).filter((q) => regionReveal.control || (block.froms || []).some((f) => f.input === q.name) || (block.controlRefs || []).some((c) => c.parameter === q.name));
    return steered.length ? [...template.inputs, ...steered] : template.inputs;
  }
  const used = Math.max(0, ...(block.froms || []).map((f) => Number(/^in(\d+)$/.exec(f.input)?.[1]) || 0));
  const asked = Number((block.parameters || []).find((p) => p.name === 'inputs')?.value) || 4;
  return template.inputs.slice(0, Math.max(4, Math.min(16, Math.max(asked, used))));
}
