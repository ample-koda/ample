// The edits of the Playlist. Each one is the same command the other views use (setParts, connect, disconnect, addBlock, removeBlock, ...); an edit of several steps
// goes to the server as one list (editAll), which applies all of it or none of it, as one undo step. Nothing here changes seeds, plans or render inputs by itself.
// Plan: plan/playlist-view/ (PV04, PV05, PV06, PV10). @ample:playlist-actions
import { ask, toast } from './ui.js';
import { S, activeDoc, applyText, editAll, edit, flat, notify, readOnly, saveGroups, saveViewData, select, viewOf } from './store.js';
import { commitParts, writeParts } from './arrangement.js';
import { groupBlocks } from './graph.js';
import { formatSong } from '../lib/pattern-model.mjs';
import { blockLines, editPlaylist, parseSequencer } from '../lib/sequencer-lanes.mjs';
import { addPart, movePart, movePartsBy, removePart, repeatPart, resizePart, splitPart } from '../lib/part-editing.mjs';

const nodeOf = (id) => flat().nodes.find((n) => n.id === id);
const take = (result) => { if (result.problem) { toast(result.problem); return null; } return result.parts; };

export const blockNames = () => new Set([...flat().nodes.map((n) => n.id), ...Object.keys(S.groups || {})]);
export function uniqueName(base) {
  const taken = blockNames();
  if (!taken.has(base)) return base;
  for (let n = 2; ; n++) if (!taken.has(`${base}-${n}`)) return `${base}-${n}`;
}

/** The parts of a block as the Arrangement sees them: its own, or one that spans `bars` or the song when it has none. */
export function partsOf(block, songBars) {
  const n = nodeOf(block);
  if (!n) return [];
  if (n.parts.length) return n.parts.map((p) => ({ at: p.at, length: Number(p.length) > 0 ? Number(p.length) : songBars, name: p.name || '' }));
  const bars = Number(n.params.bars);
  return [{ at: 1, length: bars > 0 ? bars : songBars, name: '' }];
}

const guard = () => { if (readOnly()) { toast('The song has errors (or an old state is being looked at): it cannot be edited now.'); return false; } return true; };

// ------------------------------------------------------------------------------------------------ clips

export async function moveClip(clip, at, songBars) {
  if (!guard()) return false;
  const parts = take(movePart(partsOf(clip.block, songBars), clip.index, at));
  return parts ? writeParts(nodeOf(clip.block), parts, false) : false;
}

/** Moves several clips by the same number of bars (each block's parts together; one refused part refuses everything). */
export async function moveClips(clips, delta, songBars) {
  if (!guard() || !delta) return false;
  const byBlock = new Map();
  for (const c of clips) (byBlock.get(c.block) || byBlock.set(c.block, []).get(c.block)).push(c.index);
  const ops = [];
  for (const [block, indexes] of byBlock) {
    const parts = take(movePartsBy(partsOf(block, songBars), indexes, delta));
    if (!parts) return false;
    ops.push({ type: 'setParts', block, parts });
  }
  return editAll(ops);
}

export async function resizeClip(clip, length, songBars) {
  if (!guard()) return false;
  const before = partsOf(clip.block, songBars);
  const parts = take(resizePart(before, clip.index, length));
  return parts ? commitParts(nodeOf(clip.block), parts, before[clip.index].length, length) : false;
}

/** A new clip on a line: another part of the line's source (if it has several sources, the first one that is placed). */
export async function newClip(line, at, length, songBars) {
  if (!guard()) return false;
  const source = line.sources.find((s) => s.block)?.block;
  if (!source) { toast('This line has no source to place a clip of.'); return false; }
  const parts = take(addPart(partsOf(source, songBars), at, length));
  return parts ? writeParts(nodeOf(source), parts, false) : false;
}

export async function deleteClip(clip, line, songBars) {
  if (!guard()) return false;
  const before = partsOf(clip.block, songBars);
  const result = removePart(before, clip.index);
  if (result === null) { toast('This is the only clip of the block. Remove the line (or the block) to take it out of the song.'); return false; }
  const parts = take(result);
  return parts ? writeParts(nodeOf(clip.block), parts, false) : false;
}

/** Splits a clip at a bar. A second part of a block plays the take again from its start (local time), so the sound can change: the message says so. */
export async function splitClip(clip, bar, songBars) {
  if (!guard()) return false;
  const parts = take(splitPart(partsOf(clip.block, songBars), clip.index, bar));
  if (!parts) return false;
  const ok = await writeParts(nodeOf(clip.block), parts, false);
  if (ok) toast('Split in two parts. The second part plays the block from its start again; build and listen to check it.');
  return ok;
}

/** Paste: a copied clip becomes another part of the same block at a bar (the part keeps its length; it is refused when it would overlap or end after bar 256). */
export async function pasteClip(copied, bar, songBars) {
  if (!guard()) return false;
  if (!nodeOf(copied.block)) { toast('The block of the copied clip is not in the song any more.'); return false; }
  const parts = take(addPart(partsOf(copied.block, songBars), bar, copied.length));
  return parts ? writeParts(nodeOf(copied.block), parts, false) : false;
}

/** "Repeat": one more part right after this one. */
export async function repeatClip(clip, songBars) {
  if (!guard()) return false;
  const parts = take(repeatPart(partsOf(clip.block, songBars), clip.index));
  return parts ? writeParts(nodeOf(clip.block), parts, false) : false;
}

/** The free input of a bus (or mixer): the first `audioN` that nothing feeds; `inputs` grows when the lanes are all taken. */
export function freeInput(busName) {
  const bus = (activeDoc().blocks || []).find((b) => b.name === busName);
  if (!bus) return null;
  // the document names an input by its internal name (in1, in2, ...), the song text by its public one (audio1, ...): the number is the same
  const used = new Set(bus.froms.map((f) => Number(/(\d+)$/.exec(f.input)?.[1])).filter(Boolean));
  const declared = Math.max(4, Number((bus.parameters.find((p) => p.name === 'inputs') || {}).value) || 4);
  for (let n = 1; n <= 16; n++) if (!used.has(n)) return { input: `audio${n}`, grow: n > declared ? n : 0, name: busName };
  return null;
}

const connectOps = (from, output, bus) => {
  const free = freeInput(bus);
  if (!free) return null;
  return [...(free.grow ? [{ type: 'setParameter', block: bus, name: 'inputs', value: String(free.grow) }] : []), { type: 'connect', from, output, to: bus, input: free.input }];
};

/** "Copy as a new block": the block with its parameters, notes and inputs under a new name, put on the same bus as the clip's line. */
export async function copyClipAsBlock(clip, line) {
  if (!guard()) return false;
  const name = uniqueName(`${clip.block}-copy`);
  const ops = [{ type: 'copyBlock', block: clip.block, newName: name }];
  const bus = line.chain.filter((b) => ['bus', 'mixer'].includes(nodeOf(b)?.tpl)).at(-1);
  const connect = bus ? connectOps(name, 'audio', bus) : null;
  if (connect) ops.push(...connect);
  const ok = await editAll(ops);
  if (ok) toast(connect ? `Copied as ${name}, on the bus ${bus}.` : `Copied as ${name}. It is not connected yet.`);
  return ok;
}

/** Moves the source of a clip to another line (another bus): re-routes it when it is alone on its line, else takes the one clip into a copy. */
export async function moveClipToLine(clip, fromLine, toLine, songBars) {
  if (!guard() || fromLine === toLine) return false;
  const bus = toLine.chain.filter((b) => ['bus', 'mixer'].includes(nodeOf(b)?.tpl)).at(-1);
  if (!bus) { toast('The other line has no bus to connect to.'); return false; }
  const sources = fromLine.sources.filter((s) => s.block);
  if (sources.length === 1 && sources[0].block === clip.block && !sources[0].pre.length) {
    const edge = (activeDoc().blocks || []).flatMap((b) => b.froms.filter((f) => f.block === clip.block).map((f) => ({ to: b.name, input: f.input, output: f.output })))[0];
    if (!edge) return false;
    const connect = connectOps(clip.block, edge.output, bus);
    if (!connect) { toast('The bus of the other line is full (16 inputs).'); return false; }
    return editAll([{ type: 'disconnect', to: edge.to, input: edge.input }, ...connect]);
  }
  const parts = partsOf(clip.block, songBars);
  const rest = removePart(parts, clip.index);
  if (rest === null) { toast('That is the only clip of the block; move the whole line instead.'); return false; }
  const name = uniqueName(`${clip.block}-moved`);
  const connect = connectOps(name, 'audio', bus);
  if (!connect) { toast('The bus of the other line is full (16 inputs).'); return false; }
  const moved = [{ at: parts[clip.index].at, length: parts[clip.index].length, name: '' }];
  return editAll([
    { type: 'copyBlock', block: clip.block, newName: name },
    { type: 'setParts', block: name, parts: moved },
    { type: 'setParts', block: clip.block, parts: rest.parts },
    ...connect,
  ]);
}

// ------------------------------------------------------------------------------------------------ lines

export const SOURCE_TYPES = ['synth', 'drums', 'sampler', 'gm', 'bass', 'piano', 'guitar', 'import-audio'];

/** A new line: a source block, a bus for it, both connected to the terminal bus of the song. */
export async function newLine(type, terminal, songBars) {
  if (!guard()) return false;
  if (!terminal) { toast('The song has no bus to connect a new line to.'); return false; }
  const name = uniqueName(type === 'import-audio' ? 'audio' : type);
  const bus = uniqueName(`${name}-bus`);
  const connect = connectOps(bus, 'audio', terminal);
  if (!connect) { toast('The bus is full (16 inputs); make a second bus first.'); return false; }
  const ok = await editAll([
    { type: 'addBlock', blockType: type, name },
    { type: 'setParts', block: name, parts: [{ at: 1, length: songBars, name: '' }] },
    { type: 'addBlock', blockType: 'bus', name: bus },
    { type: 'connect', from: name, output: 'audio', to: bus, input: 'audio1' },
    ...connect,
  ]);
  if (ok) { select(name); notify(); }
  return ok;
}

/** The blocks that go when a line is removed: its chain, sources, lanes and note steps nothing else reads. */
export function lineBlocks(line, model) {
  const own = new Set([...line.chain, ...line.sources.map((s) => s.block), ...line.sources.flatMap((s) => s.pre), ...line.lanes].filter(Boolean));
  const shared = new Set(model.sharedNotes);
  for (const n of line.noteSides.flatMap((x) => x.steps)) if (!shared.has(n)) own.add(n);
  return [...own];
}

export async function removeLine(line, model) {
  if (!guard()) return false;
  const go = lineBlocks(line, model);
  const readersOutside = (name) => (activeDoc().blocks || []).some((b) => !go.includes(b.name) && b.froms.some((f) => f.block === name));
  const keep = go.filter((n) => readersOutside(n));
  const remove = go.filter((n) => !keep.includes(n));
  const ok = await ask(`Remove the line ${line.name}`, `${remove.length} block${remove.length === 1 ? '' : 's'} will be removed: ${remove.join(', ')}.${keep.length ? ` Kept because other blocks read them: ${keep.join(', ')}.` : ''}`,
    [{ label: 'Cancel', value: false }, { label: 'Remove', value: true, primary: true }]);
  if (!ok) return false;
  // consumer first: a block can only go when nothing reads it
  const order = [];
  const pending = new Set(remove);
  while (pending.size) {
    const next = [...pending].find((n) => (activeDoc().blocks || []).every((b) => !pending.has(b.name) || b.name === n || !b.froms.some((f) => f.block === n)));
    if (!next) { toast('The blocks of this line read each other in a way that cannot be removed in one step.'); return false; }
    order.push(next);
    pending.delete(next);
  }
  return editAll(order.map((block) => ({ type: 'removeBlock', block })));
}

/** The block a line is known by in the view block: the one nearest the bus. */
export const lineKey = (line) => line.chain.at(-1) || line.sources.find((s) => s.block)?.block || line.id;

export async function renameLine(line, name) {
  const key = lineKey(line);
  const lineNames = { ...viewOf().lineNames };
  if (name && name !== line.name) lineNames[key] = name; else delete lineNames[key];
  return saveViewData({ lineNames });
}

export async function renameBlock(block, newName) {
  if (!guard()) return false;
  return edit([{ type: 'rename', block, newName }]);
}

/** Writes the order of the lines (a block of each line) into the view block. */
export async function orderLines(lines) {
  return saveViewData({ order: lines.map(lineKey) });
}

export async function moveLine(lines, line, delta) {
  const i = lines.indexOf(line), j = i + delta;
  if (i < 0 || j < 0 || j >= lines.length) return false;
  const next = [...lines];
  [next[i], next[j]] = [next[j], next[i]];
  return orderLines(next);
}

/** Groups the blocks of these lines in one group of the view block (the same group Nodes shows). */
export function groupLines(lines, title) {
  if (!guard()) return false;
  const ids = lines.flatMap((l) => [...l.chain, ...l.sources.map((s) => s.block), ...l.sources.flatMap((s) => s.pre)]).filter(Boolean);
  const r = groupBlocks(flat(), S.groups, [...new Set(ids)]);
  if (r.error) { toast(r.error); return false; }
  S.groups = r.groups;
  if (title) S.groups[r.id].title = title;
  saveGroups();
  notify();
  toast('Grouped. The group shows in Nodes too.');
  return true;
}

export function renameGroup(id, title) {
  if (!S.groups[id]) return false;
  S.groups[id].title = title;
  saveGroups();
  notify();
  return true;
}

export function ungroup(id) {
  const rest = { ...S.groups };
  delete rest[id];
  S.groups = rest;
  saveGroups();
  notify();
}

// ------------------------------------------------------------------------------------------------ chains

export const effectTemplates = () => Object.values(S.tpl || {}).filter((t) => t.category === 'Effects' && (t.inputs || []).some((p) => p.type === 'audio') && (t.outputs || []).some((p) => p.type === 'audio'));

/** Puts an effect after a block of a line (everything that read the block reads the effect). */
export async function insertEffect(after, type) {
  if (!guard()) return false;
  const name = uniqueName(`${after}-${type}`);
  const ok = await editAll([{ type: 'insertEffect', block: after, blockType: type, name }]);
  if (ok) { select(name); notify(); }
  return ok;
}

/** Takes an effect out of its chain (what read it reads what it read). A sidechain key into it goes too. */
export async function bypassEffect(block) {
  if (!guard()) return false;
  const n = (activeDoc().blocks || []).find((b) => b.name === block);
  const key = n?.froms.find((f) => f.input === 'key');
  if (key && !(await ask(`Take out ${block}`, `${block} has a sidechain key from ${key.block}; that connection goes with it.`, [{ label: 'Cancel', value: false }, { label: 'Take it out', value: true, primary: true }]))) return false;
  return editAll([{ type: 'bypassEffect', block }]);
}

/** Moves an effect one step in its chain: out, and in again after its neighbour, with the same name and parameters. */
export async function moveEffect(block, neighbour) {
  if (!guard()) return false;
  const n = (activeDoc().blocks || []).find((b) => b.name === block);
  if (!n) return false;
  if ((n.flags || []).length || n.freeze) toast('Flags and a freeze of this block are not carried along; set them again.');
  const parameters = n.parameters.map((p) => ({ name: p.name, value: p.value }));
  const ops = [{ type: 'bypassEffect', block }, { type: 'insertEffect', block: neighbour, blockType: n.type, name: block, parameters }];
  return editAll(ops);
}

export async function setBlockDisabled(block, on) {
  if (!guard()) return false;
  return editAll([{ type: on ? 'addFlag' : 'removeFlag', block, flag: 'disabled' }]);
}

// ------------------------------------------------------------------------------------------------ routing

/** The connection that carries the line into its bus: { to, input, output } of the last chain block. */
export const headOf = (line) => line.chain.at(-1) || line.sources.find((s) => s.block)?.block || null;

export function routeOf(line) {
  const head = headOf(line);
  if (!head) return null;
  for (const b of activeDoc().blocks || []) for (const f of b.froms) if (f.block === head && !f.disabled && f.input !== 'key') return { from: head, output: f.output, to: b.name, input: f.input };
  return null;
}

/** Re-routes a line to another bus: the old connection goes and the new one comes, as one step (when the new one is refused, the old route stays). */
export async function rerouteLine(line, bus) {
  if (!guard()) return false;
  const route = routeOf(line);
  if (!route) { toast('This line is not connected to a bus yet.'); return false; }
  if (route.to === bus) return false;
  const connect = connectOps(route.from, route.output, bus);
  if (!connect) { toast(`${bus} is full (16 inputs).`); return false; }
  return editAll([{ type: 'disconnect', to: route.to, input: route.input }, ...connect]);
}

/** A send: the line also feeds another bus or send block. */
export async function addSend(line, target) {
  if (!guard()) return false;
  const head = headOf(line);
  const connect = head ? connectOps(head, 'audio', target) : null;
  if (!connect) { toast('That connection is not possible (no free input).'); return false; }
  return editAll(connect);
}

export async function removeConnection(to, input) {
  if (!guard()) return false;
  return editAll([{ type: 'disconnect', to, input }]);
}

/** Sidechain: the output of a line feeds the key input of a compressor (or clears it). */
export async function setKey(line, block) {
  if (!guard()) return false;
  const head = headOf(line);
  const ops = [];
  const present = (activeDoc().blocks || []).find((b) => b.name === block)?.froms.find((f) => f.input === 'key');
  if (present) ops.push({ type: 'disconnect', to: block, input: 'key' });
  ops.push({ type: 'connect', from: head, output: 'audio', to: block, input: 'key' });
  return editAll(ops);
}

export async function clearKey(block) { return removeConnection(block, 'key'); }


// ------------------------------------------------------------------------------------------------ lanes: automation, drum patterns, sequencer playlist

/** Writes the automation text of a parameter ('' puts it back to the plain default). */
export async function setAutomation(block, name, text) {
  if (!guard()) return false;
  return edit([text === '' ? { type: 'removeParameter', block, name } : { type: 'setParameter', block, name, value: text }]);
}

/** Writes the song of a drums block (its sections in order). */
export async function setDrumSong(block, entries) {
  if (!guard()) return false;
  const value = formatSong(entries);
  return edit([value === '' ? { type: 'removeParameter', block, name: 'song' } : { type: 'setParameter', block, name: 'song', value }]);
}

/**
 * Changes the playlist inside a sequencer block, written in the song text as one undo step: { move: { row, at, length } }, { pattern: { row, name } }, { remove: row } or
 * { add: { pattern, track, at, length } }. `line` is the first line of the block (0-based); a row counts from there; `at` counts from bar 1 of the block.
 */
export async function editSequencerPlaylist(line, change) {
  if (!guard()) return false;
  const doc = activeDoc();
  const lines = blockLines(doc.text, line);
  const next = editPlaylist(lines, parseSequencer(lines), change);
  if (!next) { toast('This sequencer has no playlist to put the clip in.'); return false; }
  const all = doc.text.split('\n');
  all.splice(line, lines.length, ...next);
  return !!(await applyText(all.join('\n')));
}
