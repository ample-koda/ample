// The Playlist model: how a parsed song becomes lines. A pure function of the parsed document (no DOM, no network, nothing from web/app/), so it is
// tested without a browser. Plan: plan/playlist-view/README.md section 4. @ample:playlist-model
//
// Input (what the page has, see web/app/graph.js and store.js):
//   doc.blocks   [{ name, type, froms: [{ block, output, input, disabled }], parts: [{ at, length, name }], parameters: [{ name, value }], flags: [], freeze, version }]
//   doc.uses     [{ instance, froms, inputs: [{ name, type }], outputs: [{ name, type }], parameters }]   a module instance; it is a block of type "module"
//   templates    { [type]: { category, inputs: [{ name, type }], outputs: [{ name, type }] } }   (the spec templates)
//   view         what parseView() of web/lib/view-lines.mjs gives: { groups, lineNames, order }  (optional)
//   options      { statusOf(blockName) -> string, songBars, beatsPerBar }  (all optional)
// If the page's nodes lack a field that is listed above, PV02 adds it there; the model never reads the DOM.
//
// Output: { lines, tail, terminals, links: { keys, sends, sides }, loose, groups, secondsPerBar(tempo) }.
// Every block of the song appears exactly once: in a line (chain, source, note side, lane), in the tail, or in `loose` with the reason.

/** What a block type is for the walk back. */
export function kindOf(type, template) {
  if (type === 'module') return template?.outputs?.length && !template.inputs?.length ? 'source' : 'chain';
  if (!template) return 'unknown';
  if (type === 'master') return 'tail';
  if (type === 'bus' || type === 'mixer' || type === 'send' || type === 'join') return 'merge';
  if (type === 'group' || type === 'variants') return 'chain';
  const cat = template.category;
  const ins = template.inputs || [], outs = template.outputs || [];
  const hasAudioOut = outs.some((p) => p.type === 'audio');
  if (cat === 'MIDI effects') return 'noteStep';
  if (cat === 'Control') return 'control';
  if (outs.length && outs.every((p) => p.type === 'midi' || p.type === 'midi-sequence') && !ins.some((p) => p.type === 'audio')) return ins.length ? 'noteStep' : 'noteSource';
  if (type === 'midi' || type === 'midi-clip') return 'noteSource';
  if (cat === 'Lyrics' || cat === 'Direction') return 'data';
  if (!hasAudioOut) return outs.some((p) => p.type === 'text' || p.type === 'data') ? 'data' : 'unknown';
  // an audio producer: a source when nothing audio comes in, an effect when something does
  return ins.some((p) => p.type === 'audio') ? 'chain' : 'source';
}

const portNumber = (name) => { const m = /(\d+)$/.exec(name || ''); return m ? Number(m[1]) : 0; };
const isKey = (input) => input === 'key';
const isMainAudio = (input, tpl) => {
  const port = (tpl?.inputs || []).find((p) => p.name === input);
  return !port || port.type === 'audio' || port.type === 'render' || port.type === 'region' ? !isKey(input) : false;
};
const STATUS_ORDER = ['failed', 'building', 'not built', 'stale', 'built', 'cache hit', 'frozen'];

/** Seconds per bar for a song: the beats of a bar follow the meter of the song, not a constant 4. */
export const secondsPerBar = (tempo, beatsPerBar = 4) => (beatsPerBar * 60) / (tempo || 120);

/** Derives the lines. */
export function derivePlaylist(doc, templates, view = {}, options = {}) {
  const blocks = new Map();
  for (const b of doc.blocks || []) blocks.set(b.name, { ...b, tpl: templates[b.type] });
  for (const u of doc.uses || []) blocks.set(u.instance, { name: u.instance, type: 'module', froms: u.froms || [], parts: [], parameters: u.parameters || [], flags: [], tpl: { category: 'Module', inputs: u.inputs || [], outputs: u.outputs || [] }, isModule: true });
  const kind = new Map([...blocks].map(([name, b]) => [name, kindOf(b.type, b.tpl)]));
  const disabledBlock = (b) => (b.flags || []).includes('disabled');

  // the edges that count: not disabled, and the source exists
  const edges = [];
  for (const b of blocks.values()) for (const f of b.froms || []) if (blocks.has(f.block) && !f.disabled) edges.push({ from: f.block, output: f.output, to: b.name, input: f.input, disabled: disabledBlock(blocks.get(f.block)) || disabledBlock(b) });
  const disabledEdges = [];
  for (const b of blocks.values()) for (const f of b.froms || []) if (blocks.has(f.block) && f.disabled) disabledEdges.push({ from: f.block, output: f.output, to: b.name, input: f.input });
  const inputsOf = (name) => edges.filter((e) => e.to === name);
  const consumersOf = (name) => edges.filter((e) => e.from === name);
  const mainIn = (name) => {
    const b = blocks.get(name);
    return inputsOf(name).filter((e) => isMainAudio(e.input, b.tpl)).sort((x, y) => (x.input === 'audio' ? -1 : 0) - (y.input === 'audio' ? -1 : 0) || portNumber(x.input) - portNumber(y.input));
  };

  // 1. terminals and tail: from every audio sink back through single-input steps to the first merge
  const audioProducers = [...blocks.keys()].filter((n) => ['source', 'chain', 'merge', 'tail'].includes(kind.get(n)));
  const sinks = audioProducers.filter((n) => !consumersOf(n).some((e) => ['source', 'chain', 'merge', 'tail'].includes(kind.get(e.to)) && isMainAudio(e.input, blocks.get(e.to).tpl)));
  const terminals = [];
  const tailOf = new Map();
  const claimed = new Set();
  for (const sink of sinks) {
    const tail = [];
    let cur = sink, guard = 0;
    while (cur && guard++ < 1000) {
      const inputs = mainIn(cur);
      if (kind.get(cur) === 'merge' && inputs.length >= 2) break;
      if (kind.get(cur) === 'source') { cur = null; break; }
      tail.unshift(cur);
      cur = inputs[0]?.from ?? null;
    }
    if (cur && kind.get(cur) === 'merge' && !terminals.includes(cur)) { terminals.push(cur); tailOf.set(cur, tail); }
    else if (!cur) { /* a chain without a merge: one line of its own, handled below */ }
  }
  // the final bus is the one with the most inputs when several qualify; keep the order of discovery otherwise
  terminals.sort((a, b) => inputsOf(b).length - inputsOf(a).length);
  const tail = [...new Set(terminals.flatMap((t) => tailOf.get(t)))];
  tail.forEach((n) => claimed.add(n));
  terminals.forEach((n) => claimed.add(n));

  // 2. walk back from every input of a terminal
  const lines = [];
  const lineOfBlock = new Map();
  const sends = [], sides = [], keys = [];
  const assign = (name, line, role) => { if (lineOfBlock.has(name)) return false; lineOfBlock.set(name, { line, role }); claimed.add(name); return true; };


  for (const terminal of terminals) {
    const inputs = inputsOf(terminal).filter((e) => isMainAudio(e.input, blocks.get(terminal).tpl)).sort((a, b) => portNumber(a.input) - portNumber(b.input));
    for (const e of inputs) {
      const line = { id: `line-${lines.length + 1}`, terminal, input: e.input, port: portNumber(e.input), blocks: [], sources: [], chain: [], noteSides: [], lanes: [] };
      const head = e.from;
      if (lineOfBlock.has(head)) { sends.push({ from: head, toLine: line, kind: 'send' }); continue; }
      collect(head, line);
      lines.push(line);
    }
  }

  // collect: a line is the chain from the block that feeds the terminal, back to its sources. chain lists blocks source side first.
  function collect(head, line) {
    const chain = [];
    const sources = [];
    const walk = (name, path) => {
      if (lineOfBlock.has(name)) { sends.push({ from: name, toLine: line, kind: 'send' }); return; }
      const k = kind.get(name);
      if (k === 'source') { assign(name, line, 'source'); sources.push({ block: name, pre: path }); return; }
      assign(name, line, k === 'merge' ? 'bus' : 'chain');
      const ins = mainIn(name).slice().sort((a, b) => portNumber(a.input) - portNumber(b.input));
      if (k === 'merge' || ins.length > 1) {
        // a merge inside a line (the bus of a kick with two halves): its inputs are sources of this line
        chain.unshift(name);
        ins.forEach((e, i) => { if (k === 'merge' || i === 0) walk(e.from, k === 'merge' ? [] : [name, ...path]); else sides.push({ from: e.from, to: name, input: e.input }); });
        return;
      }
      chain.unshift(name);
      if (ins[0]) walk(ins[0].from, path);
      else sources.push({ block: null, pre: path });
    };
    walk(head, []);
    line.chain = chain;
    line.sources = sources;
    line.blocks = [...chain, ...sources.map((s) => s.block).filter(Boolean)];
  }

  // a chain without a merge (a song with one output and no bus): the sink chain is a line of its own; with a bus in the song a sink that is not on it is loose
  for (const sink of terminals.length ? [] : sinks) {
    if (claimed.has(sink) || lineOfBlock.has(sink)) continue;
    const line = { id: `line-${lines.length + 1}`, terminal: null, input: null, port: 0, blocks: [], sources: [], chain: [], noteSides: [], lanes: [] };
    collect(sink, line);
    lines.push(line);
  }

  // 3. the note side of every instrument: its midi input, back to the note source
  const noteUsers = new Map();
  for (const line of lines) {
    for (const s of line.sources) {
      if (!s.block) continue;
      const b = blocks.get(s.block);
      const midiIn = (b.tpl?.inputs || []).filter((p) => p.type === 'midi' || p.type === 'midi-sequence').map((p) => p.name);
      for (const e of inputsOf(s.block).filter((x) => midiIn.includes(x.input))) {
        const steps = [];
        let cur = e.from;
        let guard = 0;
        while (cur && guard++ < 1000 && ['noteStep', 'noteSource', 'control'].includes(kind.get(cur))) {
          steps.unshift(cur);
          if (kind.get(cur) === 'noteSource') break;
          cur = inputsOf(cur)[0]?.from;
        }
        line.noteSides.push({ instrument: s.block, steps });
        for (const step of steps) { (noteUsers.get(step) || noteUsers.set(step, new Set()).get(step)).add(line.id); if (!lineOfBlock.has(step)) assign(step, line, 'note'); }
      }
    }
  }
  const sharedNotes = new Set([...noteUsers].filter(([, users]) => users.size > 1).map(([n]) => n));

  // 4. keys and side links: a key is not part of the chain, it links the line that holds its source to the line that is ducked
  for (const e of edges) {
    if (!isKey(e.input)) continue;
    keys.push({ fromBlock: e.from, toBlock: e.to });
  }

  // 5. control and data blocks are lanes of the line they steer or feed (the consumer first, else the block they read)
  const loose = [];
  const laneFor = (name) => {
    const own = lineOfBlock.get(name)?.line;
    if (own) return own;
    for (const c of consumersOf(name)) { const l = lineOfBlock.get(c.to)?.line; if (l) return l; }
    for (const i of inputsOf(name)) { const l = lineOfBlock.get(i.from)?.line; if (l) return l; }
    return null;
  };
  for (const [name, k] of kind) {
    if (lineOfBlock.has(name) || claimed.has(name)) continue;
    if (k === 'control' || k === 'data') {
      const line = laneFor(name);
      if (line) { line.lanes.push(name); assign(name, line, 'lane'); }
    }
  }

  // 6. everything else is loose, with the reason
  for (const name of blocks.keys()) {
    if (lineOfBlock.has(name) || claimed.has(name)) continue;
    const k = kind.get(name);
    const reason = k === 'unknown' ? 'the template of this block type is not known' : disabledBlock(blocks.get(name)) ? 'the block is disabled' : !consumersOf(name).length && !inputsOf(name).length ? 'it is not connected to anything' : 'it does not reach the mix';
    loose.push({ block: name, reason });
  }

  // 7. sends: an edge whose source belongs to a line, whose target is not on that line and is not the next step of its chain
  const sendLinks = [];
  for (const e of edges) {
    if (isKey(e.input)) continue;
    const from = lineOfBlock.get(e.from), to = lineOfBlock.get(e.to);
    if (from && to && from.line !== to.line && from.role !== 'note' && to.role !== 'lane' && !(['bus'].includes(to.role) && (blocks.get(e.to).type === 'bus' || blocks.get(e.to).type === 'mixer') && terminals.includes(e.to))) sendLinks.push({ from: e.from, fromLine: from.line.id, to: e.to, toLine: to.line.id, input: e.input });
  }
  const sendsOut = [...sendLinks, ...sends.filter((s) => s.from).map((s) => ({ from: s.from, fromLine: lineOfBlock.get(s.from)?.line.id ?? null, to: null, toLine: s.toLine?.id ?? null, input: null }))];

  // 8. clips: the parts of a source, or the whole song when it has none
  const songBars = options.songBars || Math.max(1, ...[...blocks.values()].flatMap((b) => (b.parts || []).map((p) => (p.at || 1) + (Number(p.length) || 0) - 1)));
  const clipsOf = (name) => {
    const b = blocks.get(name);
    if (b.parts && b.parts.length) return b.parts.map((p, index) => { const length = Number(p.length); return { block: name, index, at: p.at, length: Number.isFinite(length) && length > 0 ? length : null, auto: !(Number.isFinite(length) && length > 0), name: p.name || '' }; });
    const bars = Number((b.parameters || []).find((x) => x.name === 'bars')?.value);
    return [{ block: name, index: 0, at: 1, length: bars > 0 ? bars : songBars, auto: false, name: '', implicit: true }];
  };

  // 9. names, order, groups, status
  const viewNames = view.lineNames || {};
  const status = (line) => {
    if (!options.statusOf) return null;
    const all = [...line.chain, ...line.sources.map((s) => s.block).filter(Boolean)].map(options.statusOf).filter(Boolean);
    return STATUS_ORDER.find((s) => all.includes(s)) || all[0] || null;
  };
  for (const line of lines) {
    const firstSource = line.sources.find((s) => s.block)?.block;
    const root = [...line.chain].reverse().find((n) => blocks.get(n)?.type === 'bus' || blocks.get(n)?.type === 'mixer') || line.chain.at(-1) || firstSource;
    const computed = root ? String(root).replace(/-bus$/, '') : line.id;
    const custom = [...line.chain.slice().reverse(), ...line.sources.map((s) => s.block)].find((n) => n && viewNames[n] !== undefined);
    line.name = custom ? viewNames[custom] : computed;
    line.customName = !!custom;
    line.clips = line.sources.filter((s) => s.block).flatMap((s) => clipsOf(s.block).map((c) => ({ ...c, lineId: line.id })));
    line.status = status(line);
    line.sharedNotes = line.noteSides.filter((n) => n.steps.some((s) => sharedNotes.has(s))).map((n) => n.instrument);
  }

  const orderList = view.order || [];
  const orderIndex = (line) => { const idx = [...line.chain, ...line.sources.map((s) => s.block), ...line.noteSides.flatMap((n) => n.steps)].filter(Boolean).map((n) => orderList.indexOf(n)).filter((i) => i >= 0); return idx.length ? Math.min(...idx) : Infinity; };
  lines.sort((a, b) => (orderIndex(a) - orderIndex(b)) || (a.port - b.port) || String(a.name).localeCompare(String(b.name)));
  lines.forEach((line, i) => { line.row = i; });

  const groups = groupLines(lines, blocks, kind, view.groups || {}, terminals);

  return {
    lines,
    tail: tail.map((name) => ({ block: name })),
    terminals,
    links: {
      keys: keys.map((k) => ({ ...k, fromLine: lineOfBlock.get(k.fromBlock)?.line.id ?? null, toLine: lineOfBlock.get(k.toBlock)?.line.id ?? null })),
      sends: sendsOut,
      sides: sides.map((s) => ({ ...s, fromLine: lineOfBlock.get(s.from)?.line.id ?? null, toLine: lineOfBlock.get(s.to)?.line.id ?? null })),
      disabled: disabledEdges,
    },
    loose,
    groups,
    sharedNotes: [...sharedNotes],
    secondsPerBar: (tempo) => secondsPerBar(tempo, options.beatsPerBar || 4),
  };
}

const TYPE_GROUP = { drums: 'Drums', sampler: 'Samples', synth: 'Instruments', bass: 'Instruments', piano: 'Instruments', guitar: 'Instruments', gm: 'Instruments', 'midi-render': 'Instruments', 'import-audio': 'Audio', 'ai-voice': 'Voices', choir: 'Voices', 'ace-music': 'Voices' };

/** Groups: a group of the view block wins; otherwise nested buses, then the type of the source, then a shared name prefix, else "Lines". */
function groupLines(lines, blocks, kind, viewGroups, terminals) {
  const out = new Map();
  const put = (title, line, extra = {}) => { if (!out.has(title)) out.set(title, { title, custom: false, partial: false, lines: [], ...extra }); out.get(title).lines.push(line.id); line.group = title; };
  const customs = Object.values(viewGroups);
  const byPrefix = new Map();
  for (const line of lines) {
    const own = [...line.chain, ...line.sources.map((s) => s.block)].filter(Boolean);
    const feeder = line.chain.at(-1);
    let found = customs.find((g) => feeder && g.members.includes(feeder)) || customs.find((g) => own.some((n) => g.members.includes(n)));
    if (found) { put(found.title || found.id, line, { custom: true, id: found.id }); if (!(feeder && found.members.includes(feeder))) out.get(found.title || found.id).partial = true; continue; }
    const first = line.sources.find((s) => s.block)?.block;
    const type = first ? blocks.get(first).type : null;
    const prefix = String(line.name).split(/[-_ ]/)[0];
    line.defaultGroup = { type: TYPE_GROUP[type] || null, prefix };
    byPrefix.set(prefix, (byPrefix.get(prefix) || 0) + 1);
  }
  const typeCounts = new Map();
  for (const line of lines) if (!line.group && line.defaultGroup?.type) typeCounts.set(line.defaultGroup.type, (typeCounts.get(line.defaultGroup.type) || 0) + 1);
  for (const line of lines) {
    if (line.group) continue;
    const d = line.defaultGroup || {};
    if (d.type && typeCounts.get(d.type) >= 2) put(d.type, line);
    else if (d.prefix && byPrefix.get(d.prefix) >= 2) put(d.prefix, line);
    else put('Lines', line);
  }
  return [...out.values()];
}
