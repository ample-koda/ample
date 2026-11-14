// The client's state. The server owns the document; this only holds the last view of it plus view state
// (selection, zoom, groups). Every change of the song goes through edit(), which calls the edit API.

import { api, ApiError } from './api.js';
import { readNotes } from '../lib/notes-load.mjs';
import { stop } from './player.js';
import { abortable } from '../lib/settle.mjs';
import { containerView, flatGraph, pruneGroups, regionReveal } from './graph.js';
import { ask, toast } from './ui.js';
import { documentTarget, sameTarget, staleTargetMessage } from '../lib/edit-target.mjs';
import { signatureOf, staleBlocks, staleByFiles } from '../lib/build-stale.mjs';
import { buildSummary } from '../lib/transport-model.mjs';
import { collectBuildErrors } from '../lib/build-errors.mjs';
import { parseView, withoutViewText, writeView } from '../lib/view-lines.mjs';
import { sizes } from './geometry.js';
import { setBarBeats } from '../lib/meter.mjs';
import { consumeRecoveryAcceptance, decide, listWorkRecords, parseRecord, removeWorkRecord, workRecord } from '../lib/work-state.mjs';

export const S = {
  project: null,
  doc: null,
  songDiagnostics: [],
  sourceEditMessage: null,
  path: null,
  files: [],
  templates: [],
  tpl: {},
  plugins: [],
  groups: {},
  pins: new Set(),   // blocks whose place is fixed: they are not moved by hand or by Auto format (kept in the song as view lines)
  stack: ['root'],
  moduleStack: [],
  moduleDoc: null,
  modulePath: null,
  moduleFlat: null,
  view: { x: 20, y: 10, k: 0.8 },
  sel: null,
  selSet: new Set(),
  selLink: null,
  build: { running: false, status: {}, takes: {}, sig: '', log: [] },
  projectLog: [],
  history: null,
  peek: null,
  editPeek: null,
  selRev: null,
  adv: false,
  range: null,
  undo: [],
  redo: [],
  notes: {},
};

const listeners = new Set();
/** True while an edit that only moves blocks is applied: the canvas then keeps its blocks (and their waveforms) and only puts them in place. */
export const quiet = { layout: false, edits: 0 };   // edits: how many of our own edits are on their way (their doc.changed events need no reload: the answer of the edit is the new song)
let buildAbortController = null;
let buildCounter = 0;
const stoppedBuilds = new Set();   // the ids of builds this page stopped: the server may still send a few of their events, which belong to a song that is no longer the one on the screen
const stale = (e) => !!(e.buildId && stoppedBuilds.has(e.buildId));
function appendBuildLog(message) {
  S.build.log.push({ message, at: Date.now(), elapsedMs: S.build.startedAt ? Math.round(performance.now() - S.build.startedAt) : null });
}
export const subscribe = (f) => { listeners.add(f); return () => listeners.delete(f); };
export const notify = () => listeners.forEach((f) => f());
// A build reports every block (and every state change of it); redrawing the whole graph for each one froze the page with 200 blocks.
// The build events ask for one redraw per 200 ms instead: the last state always arrives.
let notifyTimer = 0;
const notifySoon = () => { if (notifyTimer) return; notifyTimer = setTimeout(() => { notifyTimer = 0; notify(); }, 200); };

// How far the blocks are while a build runs (event build.block): { state: 'queued' | 'running', fraction, steps: [{ name, fraction }] }. A bar that only moves is painted without redrawing the graph.
const progressListeners = new Set();
export const subscribeProgress = (f) => { progressListeners.add(f); return () => progressListeners.delete(f); };
export function onBlockProgress(e) {
  if (stale(e)) return;
  const block = e.block ?? e.Block;
  const live = S.build.live || (S.build.live = {});
  const before = live[block];
  live[block] = { state: e.state ?? e.State, fraction: e.fraction ?? e.Fraction ?? 0, steps: (e.steps ?? e.Steps ?? []).map((x) => ({ name: x.name ?? x.Name, fraction: x.fraction ?? x.Fraction ?? null })) };
  const step = live[block].steps.at(-1)?.name;
  if (S.projectLog.at(-1)?.block !== block || S.projectLog.at(-1)?.step !== step || S.projectLog.at(-1)?.state !== live[block].state) {
    S.projectLog.push({ at: Date.now(), block, step: step || live[block].state, state: live[block].state });
    if (S.projectLog.length > 5000) S.projectLog.splice(0, S.projectLog.length - 5000);
  }
  S.build.queued?.add(block);
  if (!before || before.state !== live[block].state) notifySoon(); else progressListeners.forEach((f) => f());
}

/** Adds a line to the project log (the Log tab), for things that are not a build step, like the client preview. */
export function logProject(block, state, step) {
  S.projectLog.push({ at: Date.now(), block, step, state });
  if (S.projectLog.length > 5000) S.projectLog.splice(0, S.projectLog.length - 5000);
}

// Selection changes must not rebuild the canvas (a drag or a double-click is in progress on the very element
// that would be replaced), so they have their own channel.
const selectionListeners = new Set();
export const subscribeSelection = (f) => { selectionListeners.add(f); return () => selectionListeners.delete(f); };
export const notifySelection = () => selectionListeners.forEach((f) => f());

let cache = { key: '', graph: { nodes: [], links: [] } };

/** The flat graph of the open song (every block). */
/**
 * Layout ops that write down where the blocks without a layout line are drawn now. A block that was never moved is placed by its depth in the graph,
 * so removing a connection or a block would make it jump; call this first in such an edit to keep every block where it is.
 * @param {string[]} [except] blocks that the edit removes
 */
export function pinUnplaced(except = []) {
  return flat().nodes.filter((n) => !n.placed && !n.isGroup && !n.proxy && !except.includes(n.id)).map((n) => ({ type: 'setLayout', block: n.id, x: n.x, y: n.y }));
}

function rootFlat() {
  if (!S.doc) return { nodes: [], links: [] };
  const key = `${S.doc.path}#${S.doc.version}#${S.peek ? S.peek.ref : ''}#${regionReveal.on ? 'r' : ''}${regionReveal.control ? 'c' : ''}#${S.tplRev || 0}`;
  if (cache.key !== key) cache = { key, graph: flatGraph(S.doc, S.tpl) };
  return cache.graph;
}

/** The document the views show: the module that is open, else the song. */
export const activeDoc = () => (S.moduleStack.length && S.moduleDoc ? S.moduleDoc : S.doc);
export function flat() { return S.moduleStack.length && S.moduleFlat ? S.moduleFlat : rootFlat(); }

export const container = () => S.stack[S.stack.length - 1];
export const current = () => S.moduleStack.length && S.moduleFlat ? containerView(S.moduleFlat, {}, 'root') : containerView(flat(), S.groups, container());
export const errors = () => ((S.moduleStack.length ? S.moduleDoc : S.doc)?.diagnostics.filter((d) => d.severity === 'error') || []);
export const unsupportedFeatures = () => (S.moduleStack.length ? S.moduleDoc?.unsupported : []) || [];
export const dirty = () => !!(S.moduleStack.length ? S.moduleDoc?.dirty || S.moduleStack[0]?.midiBlock && S.doc?.dirty : S.doc?.dirty);
/** True when the text cannot be read into blocks and links (a graph-breaking error): the Blocks view is out of use and the errors are fixed in the code view. */
export const graphBroken = () => !!(S.moduleStack.length ? S.moduleDoc : S.doc)?.graphBroken;
/** The problems (errors and warnings) that belong to one block, as the server attributed them by line. */
export const blockProblems = (name) => ((S.moduleStack.length ? S.moduleDoc : S.doc)?.diagnostics || []).filter((d) => d.block === name);
/** The error of the line of one parameter of a block, or null: the properties panel marks that field. */
export function paramProblem(name, param) {
  const doc = S.moduleStack.length ? S.moduleDoc : S.doc;
  const parameter = doc?.blocks?.find((b) => b.name === name || b.Name === name)?.parameters?.find((p) => (p.name ?? p.Name) === param);
  if (!parameter) return null;
  const line = (parameter.line ?? parameter.Line) + 1;   // a parameter counts lines from 0, a diagnostic from 1
  return (doc.diagnostics || []).find((d) => d.severity === 'error' && d.line === line) || null;
}
/** Read-only: only when the text cannot be read into blocks, or a state is only being looked at. One block with an invalid value no longer freezes the whole song. */
export const readOnly = () => graphBroken() || !!S.peek;

const groupsKey = () => `ample.groups:${S.project ? S.project.id : ''}:${S.path}:${S.moduleStack.length ? S.modulePath : ''}`;
const loadGroups = () => { try { return JSON.parse(localStorage.getItem(groupsKey()) || '{}'); } catch { return {}; } };
let viewTimer = 0;
let viewWrite = Promise.resolve();
async function persistView() {
  if (!S.doc || S.moduleStack.length || S.peek || readOnly()) return;
  const kept = parseView(S.doc.text);
  const wants = Object.keys(S.groups).length > 0 || S.pins.size > 0 || Object.values(sizes).some((scale) => scale && scale !== 1) || kept.sections.length > 0 || Object.keys(kept.lineNames).length > 0 || kept.order.length > 0 || kept.unread.length > 0;
  if (!wants && !kept.found) return;
  const next = writeView(S.doc.text, S.groups, sizes, [...S.pins], kept);
  if (next !== S.doc.text) await applyText(next);
}
/** Writes the groups and the block sizes into the song file (a step that can be undone), a moment after the last change. */
export function saveViewSoon() {
  clearTimeout(viewTimer);
  viewTimer = setTimeout(() => { viewTimer = 0; viewWrite = viewWrite.then(persistView); }, 150);
}
async function flushViewSave() { clearTimeout(viewTimer); viewTimer = 0; viewWrite = viewWrite.then(persistView); await viewWrite; }

/** The sections of the song, as the view block holds them. */
export const sectionsOf = () => (S.doc ? parseView(S.doc.text).sections : []);

/** What the view block holds besides the groups: { sections, lineNames, order }, read from the song text. */
export const viewOf = () => (S.doc ? parseView(S.doc.text) : { sections: [], lineNames: {}, order: [], groups: {}, unread: [] });

/** Writes new view data (any of sections, lineNames, order) into the view block as one undo step. Returns false when the song cannot be edited now. */
export async function saveViewData(patch) {
  if (!S.doc || S.moduleStack.length || S.peek || readOnly()) { toast('The song cannot be edited now.'); return false; }
  const kept = parseView(S.doc.text);
  const next = writeView(S.doc.text, S.groups, sizes, [...S.pins], { ...kept, ...patch });
  return next === S.doc.text ? true : !!(await applyText(next));
}
export const saveSections = (sections) => saveViewData({ sections });

/**
 * Applies a list of graph edits to the song, all of them or none (the server refuses the whole list when one edit is refused), as one undo step.
 * Inside a module the list goes one by one, as `edit` does.
 */
export function editAll(ops) {
  const p = chain.then(() => runAll(ops));
  chain = p.catch(() => {});
  return p;
}

async function runAll(ops) {
  if (!S.doc || readOnly()) { toast('The song has errors: it is read-only until they are fixed.'); return false; }
  if (S.moduleStack.length) return run(ops);
  stopBuild();
  quiet.edits++;
  const prev = S.doc.text;
  try {
    const doc = await api.post('/api/doc/edits', { path: S.path, baseVersion: S.doc.version, ops });
    if (doc.text !== prev) { S.undo.push(prev); editCount++; S.redo.length = 0; }
    setDoc(doc);
    return true;
  } catch (e) {
    fail(e);
    return false;
  } finally {
    quiet.edits--;
  }
}

/** Fixes the place of these blocks (or lets them move again). */
export function setPinned(ids, on) {
  for (const id of ids) { if (on) S.pins.add(id); else S.pins.delete(id); }
  saveViewSoon();
  notify();
}

export function saveGroups() {
  try { localStorage.setItem(groupsKey(), JSON.stringify(S.groups)); } catch { /* storage may be blocked */ }
  saveViewSoon();
}

const moduleNavigationFile = () => `notes/${encodeURIComponent(S.path)}.module-navigation.json`;
let navigationTimer = 0;
const documentHistory = new Map();
const historyKey = () => S.moduleStack.length && S.modulePath ? `module:${S.modulePath}` : `song:${S.path}`;
function activateHistory(reset) {
  const key = historyKey();
  if (reset) documentHistory.delete(key);
  let history = documentHistory.get(key);
  if (!history) { history = { undo: [], redo: [] }; documentHistory.set(key, history); }
  S.undo = history.undo;
  S.redo = history.redo;
}

function persistModuleNavigation() {
  clearTimeout(navigationTimer);
  if (!S.path) return;
  const path = moduleNavigationFile();
  const text = JSON.stringify({ stack: S.moduleStack, path: S.modulePath });
  navigationTimer = setTimeout(() => { api.put('/api/file', { path, text }).catch(() => {}); }, 200);
}

async function restoreModuleNavigation() {
  try {
    const saved = JSON.parse((await api.get(`/api/file?path=${encodeURIComponent(moduleNavigationFile())}&optional=1`)).text || '{}');
    S.moduleStack = Array.isArray(saved.stack) && saved.stack.every((x) => x && typeof x.instance === 'string' && x.instance.length > 0 && x.instance.length <= 128 && !x.instance.includes('/') && typeof x.path === 'string' && x.path.startsWith('modules/') && x.path.endsWith('.module') && (x.midiBlock === undefined || typeof x.midiBlock === 'string' && x.midiBlock === x.instance)) ? saved.stack : [];
    S.modulePath = typeof saved.path === 'string' && saved.path.startsWith('modules/') && saved.path.endsWith('.module') ? saved.path : null;
    if (S.moduleStack.length && S.moduleStack.at(-1).path !== S.modulePath) { S.moduleStack = []; S.modulePath = null; }
    const first = S.moduleStack[0];
    const useMatches = S.doc?.uses?.some((u) => u.instance === first?.instance && `modules/${u.module}.module` === first?.path);
    const midiBlock = first?.midiBlock && S.doc?.blocks?.find((block) => block.name === first.midiBlock && block.type === 'midi');
    const variantMatches = midiBlock?.parameters?.some((parameter) => parameter.name === 'variant-module' && `modules/${parameter.value}.module` === first?.path);
    if (!S.modulePath || !(useMatches || variantMatches)) { S.moduleStack = []; S.modulePath = null; }
  } catch { S.moduleStack = []; S.modulePath = null; }
}

async function refreshModuleGraph() {
  if (!S.moduleStack.length) { S.moduleFlat = null; return; }
  try {
    S.moduleDoc = await api.get(`/api/module/doc?path=${encodeURIComponent(S.modulePath)}`);
    if (!S.moduleDoc.blocks.length) throw new ApiError({ code: 'AMP5320', detail: 'The saved module navigation no longer exists.' });
    S.moduleFlat = flatGraph(S.moduleDoc, S.tpl);
  } catch (error) {
    S.moduleFlat = null;
    S.moduleDoc = null;
    if (error instanceof ApiError && (error.status === 404 || error.code === 'AMP5320' || error.code === 'AMP5326')) {
      S.moduleStack = S.moduleStack.slice(0, -1);
      S.modulePath = S.moduleStack.at(-1)?.path || null;
      S.moduleDoc = null;
      persistModuleNavigation();
      if (S.moduleStack.length) await refreshModuleGraph();
    } else toast(`Could not load the module graph: ${error.message || error}`);
  }
}

export async function refreshActiveModule() { await refreshModuleGraph(); notify(); }

export async function enterModule(node) {
  if (!node?.isModule) return;
  S.modulePath = `modules/${node.moduleName}.module`;
  S.moduleStack.push({ instance: node.id, path: S.modulePath });
  S.stack = ['root'];
  await refreshModuleGraph();
  activateHistory(false);
  await resumeModuleWork();
  persistModuleNavigation();
  S.sel = null;
  S.selSet.clear();
  notify();
}

export async function enterMidiVariant(node, moduleName) {
  if (!node || !/^[A-Za-z0-9_-]{1,64}$/.test(moduleName)) return;
  S.modulePath = `modules/${moduleName}.module`;
  S.moduleStack.push({ instance: node.id, path: S.modulePath, midiBlock: node.id });
  S.stack = ['root'];
  await refreshModuleGraph();
  activateHistory(false);
  await resumeModuleWork();
  persistModuleNavigation();
  S.sel = null;
  S.selSet.clear();
  notify();
}
export async function setModuleLevel(length) {
  if (length < 0 || length > S.moduleStack.length || length === S.moduleStack.length) return;
  S.moduleStack = S.moduleStack.slice(0, length);
  if (!S.moduleStack.length) { S.modulePath = null; S.moduleDoc = null; }
  else S.modulePath = S.moduleStack.at(-1).path;
  await refreshModuleGraph();
  activateHistory(false);
  if (S.modulePath) await resumeModuleWork();
  persistModuleNavigation();
  S.sel = null;
  S.selSet.clear();
  notify();
}

const notesFile = () => notesFileFor(S.path);
let notesTimer = 0;
/** Block notes live in the project (notes/<song>.notes.json, written through the command bus), so they travel with the project and its history. */
export function saveNotes() {
  clearTimeout(notesTimer);
  const path = notesFile();
  const text = JSON.stringify(S.notes, null, 2);
  notesTimer = setTimeout(() => { api.put('/api/file', { path, text }).catch(() => {}); }, 400);
}
const notesFileFor = (path) => `notes/${path.replace(/[\/]/g, '_')}.notes.json`;
/** Reads the notes of one song and returns them; the caller commits them only while that song is still the one being opened. */
const loadNotes = (path) => readNotes({ get: (url) => api.get(url), file: notesFileFor(path), legacyKey: `ample.notes:${S.project ? S.project.id : ''}:${path}`, storage: localStorage });

const workKey = (path) => `ample.work:${S.project ? S.project.id : ''}:${path}`;
let workTimer = 0, workHold = false;
/** Keeps the text and the undo/redo history of the song in this browser a moment after the last change, so a refresh or a restart of the server loses nothing. */
function persistWorkSoon() {
  clearTimeout(workTimer);
  if (workHold) return;
  workTimer = setTimeout(persistWork, 300);
}
function persistWork() {
  if (workHold || !S.doc || S.peek) return;
  const active = S.moduleStack.length && S.moduleDoc ? S.moduleDoc : S.doc;
  const path = active.path || (S.moduleStack.length ? S.modulePath : S.path);
  if (!path) return;
  const record = workRecord({ project: S.project ? S.project.id : '', path, text: active.text, undo: S.undo, redo: S.redo });
  try { if (record) localStorage.setItem(workKey(path), JSON.stringify(record)); } catch { /* storage may be full or blocked */ }
}
const readWork = (path) => { try { return parseRecord(localStorage.getItem(workKey(path)) || '', path); } catch { return null; } };
const dropWork = (path) => { try { localStorage.removeItem(workKey(path)); } catch { /* blocked */ } };
addEventListener('pagehide', () => persistWork());

export function setDoc(doc) {
  const previous = S.doc;
  // the components a song can see change with its import, component and embedded lines: read them again then (the palette and the blocks need them)
  const componentLines = (doc.text.match(/^(?:import|component|embedded)\b.*$/gm) || []).join('\n');
  const componentsChanged = previous?.path !== doc.path || S.componentLines !== componentLines;
  S.componentLines = componentLines;
  if (previous?.path !== doc.path) S.projectLog = [];
  S.doc = doc;
  setBarBeats(doc.project?.beatsPerBar);   // the roll and the grids draw bars of the song's meter
  S.songDiagnostics = doc.diagnostics || [];
  S.path = doc.path;
  // the groups and block sizes live in the song file (comment lines at the end); a text without them after one with them (Ctrl+Z) has none either
  const view = parseView(doc.text);
  if (view.found || (previous && previous.path === doc.path && parseView(previous.text).found)) {
    S.groups = view.groups;
    S.pins = new Set(view.pins);
    for (const key of Object.keys(sizes)) delete sizes[key];
    Object.assign(sizes, view.sizes);
  }
  const before = JSON.stringify(S.groups);
  S.groups = pruneGroups(S.groups, rootFlat());
  if (JSON.stringify(S.groups) !== before) saveGroups();
  if (S.stack.some((id) => id !== 'root' && !S.groups[id])) S.stack = ['root'];
  S.selSet = new Set([...S.selSet].filter((id) => current().nodes.some((n) => n.id === id) || S.groups[id]));
  if (S.sel && !S.selSet.has(S.sel)) S.sel = [...S.selSet].pop() || null;
  // Only the blocks that changed, and the blocks that read them, lose their build (the engine keys its takes the same way).
  const stale = previous && previous.path !== doc.path ? 'all' : staleBlocks(S.build.sig, previous, doc, S.build.follows || null);
  // a build only stops when what it builds changed: moving a block (a layout line) changes the text, not the sound
  if (S.build.running && previous && (previous.path !== doc.path || stale === 'all' || stale.size)) stopBuild();
  if (stale === 'all') { S.build.status = {}; S.build.takes = {}; } else for (const id of stale) { delete S.build.takes[id]; delete S.build.status[id]; }
  S.build.sig = signatureOf(doc);
  notify();
  refreshInputs().catch(() => {});
  refreshBuildStatus();
  persistWorkSoon();
  if (componentsChanged && !S.peek) import('./components.js').then((m) => m.loadComponents(doc.path)).catch(() => { /* the palette just has no components */ });
}

/** An audio component instance is built as the blocks "<instance>__<node>"; the instance itself plays with the take of the processor that gives its first output. */
export function aliasComponentTakes() {
  for (const b of S.doc?.blocks || []) {
    const t = S.tpl[b.type];
    if (!t?.component || t.kind === 'Notes') continue;
    const node = Object.values(t.outputNodes || {})[0];
    if (!node) continue;
    const part = `${b.name}__${node}`;
    if (S.build.takes[part]) S.build.takes[b.name] = S.build.takes[part]; else delete S.build.takes[b.name];
    if (S.build.status[part]) S.build.status[b.name] = S.build.status[part];
  }
}

let statusTimer = null, statusSeq = 0;
/**
 * Asks the server which blocks already have a take (a dry run of the build: nothing is rendered or stored) and shows those as built, so a song that was
 * built before, or a block that came with its take, is ready without pressing Build. Called after every change of the song, a little delayed.
 */
export function refreshBuildStatus() {
  clearTimeout(statusTimer);
  statusTimer = setTimeout(async () => {
    if (!S.path || S.peek || S.build.running) return;
    const seq = ++statusSeq, path = S.path;
    let report;
    try { report = await api.get(`/api/build/status?path=${encodeURIComponent(path)}`); } catch { return; }
    if (seq !== statusSeq || S.build.running || path !== S.path || !report.ok) return;
    const before = JSON.stringify([S.build.takes, S.build.status]);
    for (const b of report.blocks || []) {
      const name = b.block ?? b.Block, take = b.takeId ?? b.TakeId, st = b.status ?? b.Status;
      if (take) { if (S.build.takes[name] !== take || !S.build.status[name]) { S.build.takes[name] = take; S.build.status[name] = { status: st, message: b.message ?? b.Message }; } continue; }   // the same take keeps its status (rendered stays rendered)
      delete S.build.takes[name];
      if (st === 'disabled' || st === 'skipped') S.build.status[name] = { status: st, message: b.message ?? b.Message };   // blocked: say so before the first build
      else if (S.build.status[name]?.status === 'cached') delete S.build.status[name];   // it had a take and has none now; a failed block keeps its message
    }
    aliasComponentTakes();
    if (JSON.stringify([S.build.takes, S.build.status]) !== before) notify();   // nothing to redraw when the server agrees with what is shown
  }, 300);
}

/** A block is also out of date when a file it reads (a sample, a lyrics, MIDI or audio file, a SoundFont) changed while the song text did not: compare the stamps the server gives per block. */
export async function refreshInputs() {
  if (!S.path || S.peek) return;
  const buildWasRunning = S.build.running;
  const { hash, blocks = {}, followsMaster = null } = await api.get(`/api/doc/inputs?path=${encodeURIComponent(S.path)}`);
  if (S.build.running) { if (buildWasRunning && S.build.inputs && S.build.inputs !== hash) stopBuild(); return; }
  S.build.follows = followsMaster;   // which blocks a change of the master seed reaches
  if (S.build.inputs === hash) return;
  // only the blocks that read a changed file, and what follows them, lose their build
  const stale = [...staleByFiles(S.build.files, blocks, S.doc)].filter((id) => S.build.takes[id] || S.build.status[id]);
  for (const id of stale) { delete S.build.takes[id]; delete S.build.status[id]; }
  S.build.files = blocks;
  S.build.inputs = hash;
  if (stale.length) notify();   // redraw only when a block lost its build (the first answer after loading usually changes nothing on the board)
  refreshBuildStatus();
}

export async function loadProject() {
  const [project, tpl, files, plugins] = await Promise.all([api.get('/api/project'), api.get('/api/templates'), api.get('/api/docs'), api.get('/api/plugins')]);
  S.project = project;
  S.templates = tpl.templates;
  S.tpl = Object.fromEntries(tpl.templates.map((t) => [t.id, t]));
  S.files = files;
  S.plugins = plugins.plugins || [];
  if (files.length) await openDoc(S.path && files.includes(S.path) ? S.path : project.lastSong && files.includes(project.lastSong) ? project.lastSong : files[0]);
  else notify();
}

let openGeneration = 0;
/** Opens a song. Returns false when the open was refused or superseded by a newer open. */
export async function openDoc(path, stillWanted) {
  const leftPath = S.path, leftText = S.doc?.text;
  if (path !== S.path) { const { confirmSongSwitch } = await import('./source.js'); if (!(await confirmSongSwitch(path))) return false; }
  if (stillWanted && !stillWanted()) return false;
  const gen = ++openGeneration;
  const current = () => gen === openGeneration && (!stillWanted || stillWanted());
  if (path !== S.path) stopBuild();
  const doc = await api.get(`/api/doc?path=${encodeURIComponent(path)}`);
  if (!current()) return false;   // a newer open owns the editor now
  const loaded = await loadNotes(path).catch(() => ({ notes: {}, migrate: false }));
  if (!current()) return false;
  S.path = path;
  S.groups = loadGroups();
  S.notes = loaded.notes;
  if (loaded.migrate) saveNotes();   // notes an older version kept in this browser move into the project once
  S.stack = ['root'];
  S.moduleStack = [];
  S.modulePath = null;
  S.moduleDoc = null;
  S.moduleFlat = null;
  // the undo history of a song survives a switch to another tab and back, as long as the text is the one it was left with
  const leftHistory = leftPath && leftPath !== path ? documentHistory.get(`song:${leftPath}`) : null;
  if (leftHistory) leftHistory.text = leftText;
  const mine = documentHistory.get(`song:${path}`);
  const keepMine = mine && mine.text === doc.text ? mine : null;
  documentHistory.clear();
  if (leftHistory) documentHistory.set(`song:${leftPath}`, leftHistory);
  if (keepMine) documentHistory.set(`song:${path}`, keepMine);
  activateHistory(!keepMine);
  S.selSet.clear();
  S.sel = null;
  const stored = readWork(path);   // read before setDoc, which keeps the song as it is now
  workHold = true;
  setDoc(doc);
  const keepWorkRecord = await resumeWork(stored, doc);
  workHold = false;
  if (!current()) return false;
  if (keepWorkRecord) persistWorkSoon();
  await restoreModuleNavigation();
  await refreshModuleGraph();
  if (!current()) return false;
  activateHistory(false);
  notify();
  try { await api.post('/api/project/last-song', { path }); } catch (error) { toast(`Could not remember the last song: ${error.message}`); }
  if (!current()) return false;
  document.dispatchEvent(new CustomEvent('ample:song-opened'));
  // a song that opens with errors cannot be edited as blocks: it starts in the source editor, as at the start of the app
  if (graphBroken()) { const { setView } = await import('./views.js'); if (setView('src', true)) toast('This song opened with errors that stop the blocks from being read, so it starts in the code view. Fix them to use the Blocks view.'); }
  return true;
}

/** The browser may hold more than the saved song has after a server restart: restore undo history for identical text, otherwise require a saved-versus-recovered review. */
async function resumeWork(stored, doc) {
  const accepted = consumeRecoveryAcceptance(localStorage, S.project?.id || '', doc.path, 'work', stored?.updated, doc.text);
  const what = decide(stored, doc.text, doc.dirty === true);
  if (what === 'none') { if (stored) dropWork(doc.path); return true; }
  if (what === 'history') { S.undo.splice(0, S.undo.length, ...stored.undo); S.redo.splice(0, S.redo.length, ...stored.redo); return true; }
  if (accepted) {
    const applied = await applyText(stored.text);
    if (!applied) return false;
    S.undo.splice(0, S.undo.length, ...stored.undo); S.redo.splice(0, S.redo.length, ...stored.redo);
    return true;
  }
  // no question: the unsaved changes of this browser simply come back, and the person saves them or closes the tab and discards them
  const applied = await applyText(stored.text);
  if (!applied) return false;
  S.undo.splice(0, S.undo.length, ...stored.undo); S.redo.splice(0, S.redo.length, ...stored.redo);
  toast(`Your unsaved changes from this browser (${new Date(stored.updated).toLocaleString()}) are back. Save them, or close the tab to discard them.`);
  return true;
}

async function resumeModuleWork() {
  if (!S.moduleDoc || !S.modulePath) return;
  const stored = readWork(S.modulePath);
  const accepted = consumeRecoveryAcceptance(localStorage, S.project?.id || '', S.modulePath, 'work', stored?.updated, S.moduleDoc?.text);
  const what = decide(stored, S.moduleDoc.text, S.moduleDoc.dirty === true);
  if (what === 'none') { if (stored) dropWork(S.modulePath); return; }
  if (what === 'history') { S.undo.splice(0, S.undo.length, ...stored.undo); S.redo.splice(0, S.redo.length, ...stored.redo); return; }
  if (accepted) {
    S.moduleDoc = await api.put('/api/module/doc', { path: S.modulePath, text: stored.text, baseVersion: S.moduleDoc.version });
    S.moduleFlat = flatGraph(S.moduleDoc, S.tpl);
    S.undo.splice(0, S.undo.length, ...stored.undo); S.redo.splice(0, S.redo.length, ...stored.redo);
    return;
  }
  S.moduleDoc = await api.put('/api/module/doc', { path: S.modulePath, text: stored.text, baseVersion: S.moduleDoc.version });
  S.moduleFlat = flatGraph(S.moduleDoc, S.tpl);
  S.undo.splice(0, S.undo.length, ...stored.undo);
  S.redo.splice(0, S.redo.length, ...stored.redo);
}

/** Fetches the song again. With ifChanged (coming back to the tab, the event stream that reconnects) nothing is redrawn when the server still has the same text: the players and waveforms stay as they are. */
export async function reloadDoc({ ifChanged = false } = {}) {
  if (S.moduleStack.length && S.modulePath) { await refreshModuleGraph(); notify(); return; }
  if (S.path && !S.peek) {
    const doc = await api.get(`/api/doc?path=${encodeURIComponent(S.path)}`);
    if (ifChanged && S.doc && S.doc.path === doc.path && S.doc.version === doc.version && S.doc.text === doc.text) return;
    setDoc(doc);
  }
}

function fail(e) {
  if (e instanceof ApiError) {
    toast(`${e.code ? `${e.code}: ` : ''}${e.message}`);
    if (e.code === 'AMP5280') reloadDoc().catch(() => {});
    if (e.code === 'AMP5307') import('./diskconflict.js').then((m) => m.resolveDiskConflict()).catch((error) => toast(String(error.message || error)));
  } else {
    toast(String(e.message || e));
  }
}

let chain = Promise.resolve();

/** Applies edit operations one after another as one undo step (calls are queued). Returns true when all were applied. */
/** Resolves when the edits that were asked for so far are done (a field that is still being edited has asked for its edit when it loses focus). */
export const editsDone = () => chain;

export function edit(ops) {
  // the edit belongs to the document that is open now, not to the one that is open when its turn comes
  const target = documentTarget(S);
  const p = chain.then(() => run(ops, target));
  chain = p.catch(() => {});
  return p;
}

async function run(ops, target) {
  if (target && !sameTarget(target, documentTarget(S))) { toast(staleTargetMessage(target)); return false; }
  if (!S.doc || readOnly()) { toast('The song has errors: it is read-only until they are fixed.'); return false; }
  if (ops.some((op) => op.type !== 'setLayout')) stopBuild();
  quiet.edits++;
  try { return await runOps(ops, target); } finally { quiet.edits--; }
}

async function runOps(ops, target = documentTarget(S)) {
  const inModule = target.kind === 'module';
  const editPath = target.songPath;
  const editModulePath = target.path;
  const stillHere = () => sameTarget(target, documentTarget(S));
  const prev = inModule ? S.moduleDoc.text : S.doc.text;
  let doc = inModule ? S.moduleDoc : S.doc;
  let current = null;
  try {
    const layoutsOnly = !inModule && ops.length > 1 && ops.every((op) => op.type === 'setLayout');
    // Auto format, align, distribute: one request instead of one per block
    if (layoutsOnly) {
      const items = ops.map((op) => ({ block: op.block, x: op.x, y: op.y }));
      doc = await api.post('/api/doc/layouts', { path: editPath, baseVersion: doc.version, items });
    }
    else for (const op of ops) doc = (current = op, inModule)
      ? await api.post('/api/module/doc/edit', { path: editModulePath, baseVersion: doc.version, op })
      : await api.post('/api/doc/edit', { path: editPath, baseVersion: doc.version, op });
  } catch (e) {
    if (!stillHere()) { toast(staleTargetMessage(target)); return false; }
    if (inModule && doc !== S.moduleDoc) { S.undo.push(prev); editCount++; S.redo.length = 0; S.moduleDoc = doc; S.moduleFlat = flatGraph(doc, S.tpl); notify(); }
    else if (!inModule && doc !== S.doc) { S.undo.push(prev); editCount++; S.redo.length = 0; setDoc(doc); }
    if (current?.type === 'setParameter' && /AMP2081/.test(String(e.message || ''))) {
      // the property is driven by an expression: no control may overwrite it silently, the person chooses
      const mode = await ask(`${current.block}.${current.name} comes from an expression`, String(e.message).replace(/^AMP2081:\s*/, '') + '\n\nOverride keeps the expression in the source and uses your value on purpose; bake freezes what the expression gives now; a literal drops the expression.', [
        { label: `Override with ${current.value}`, value: 'override', primary: true },
        { label: 'Bake current value', value: 'bake' },
        { label: 'Replace with literal', value: 'literal' },
        { label: 'Edit expression in the source', value: 'source' },
        { label: 'Cancel', value: undefined },
      ]);
      if (mode === 'source') { toast('Edit the expression in the Source tab (the line of this property).'); return false; }
      if (mode && !stillHere()) { toast(staleTargetMessage(target)); return false; }
      const again = { type: 'setPropertyValue', block: current.block, name: current.name, value: String(current.value ?? ''), mode };
      if (mode) return runOps([again], target);
      return false;
    }
    fail(e);
    return false;
  }
  if (!stillHere()) { toast(staleTargetMessage(target)); return false; }
  if (doc.text !== prev) { S.undo.push(prev); editCount++; S.redo.length = 0; }
  if (inModule) { S.moduleDoc = doc; S.moduleFlat = flatGraph(doc, S.tpl); notify(); persistWorkSoon(); }
  else setDoc(doc);
  return true;
}

async function putText(text) {
  if (text !== (S.moduleStack.length ? S.moduleDoc?.text : S.doc?.text)) stopBuild();
  if (S.moduleStack.length && S.modulePath) {
    S.moduleDoc = await api.put('/api/module/doc', { path: S.modulePath, text, baseVersion: S.moduleDoc.version });
    S.moduleFlat = flatGraph(S.moduleDoc, S.tpl);
    notify();
    persistWorkSoon();
    return;
  }
  setDoc(await api.put('/api/doc', { path: S.path, text, baseVersion: S.doc.version }));
}

/** Applies new song text to the working copy (not saved), as one undo step. Returns the new view, or null when it failed. */
export async function applyText(text) {
  if (!S.doc || S.peek) { toast('Read-only: nothing can be applied while a state is only being looked at.'); return null; }
  const inModule = S.moduleStack.length > 0 && S.modulePath;
  const active = inModule ? S.moduleDoc : S.doc;
  const prev = active.text;
  if (text !== prev && withoutViewText(text) !== withoutViewText(prev)) stopBuild();   // a change of only the view block (sections, names, groups) changes no sound, so a running build goes on
  try {
    const doc = inModule
      ? await api.put('/api/module/doc', { path: S.modulePath, text, baseVersion: active.version })
      : await api.put('/api/doc', { path: S.path, text, baseVersion: active.version });
    if (doc.text !== prev) { S.undo.push(prev); editCount++; S.redo.length = 0; }
    if (inModule) { S.moduleDoc = doc; S.moduleFlat = flatGraph(doc, S.tpl); notify(); }
    else setDoc(doc);
    return doc;
  } catch (e) { fail(e); return null; }
}

/** Grouping lives in the browser, not in the song text, but it is a step like any other: Ctrl+Z takes it back and Ctrl+Y does it again. */
export function recordGroups() { /* the groups are in the song text now, so the text steps are the undo steps */ }

function swapGroups(entry, to) {
  to.push({ groups: JSON.stringify(S.groups) });
  S.groups = JSON.parse(entry.groups);
  S.stack = ['root']; S.selSet = new Set();
  saveGroups();
  notify();
}

// things that are not text edits but can still be undone (closing a tab): each hook gets 'undo' or 'redo' and returns true when it handled it
export const undoHooks = [];
let editCount = 0;
export const editSerial = () => editCount;

export async function undo() {
  if (S.peek) { toast('History previews are read-only. Return to the current work before undoing.'); return; }
  for (const hook of undoHooks) if (await hook('undo')) return;
  if (!S.undo.length) { toast('Nothing to undo.'); return; }
  const text = S.undo.pop();
  if (text && typeof text === 'object') { swapGroups(text, S.redo); return; }
  S.redo.push(S.moduleStack.length && S.moduleDoc ? S.moduleDoc.text : S.doc.text);
  try { await putText(text); } catch (e) { fail(e); }
}

export async function redo() {
  if (S.peek) { toast('History previews are read-only. Return to the current work before redoing.'); return; }
  for (const hook of undoHooks) if (await hook('redo')) return;
  if (!S.redo.length) { toast('Nothing to redo.'); return; }
  const text = S.redo.pop();
  if (text && typeof text === 'object') { swapGroups(text, S.undo); return; }
  S.undo.push(S.moduleStack.length && S.moduleDoc ? S.moduleDoc.text : S.doc.text);
  try { await putText(text); } catch (e) { fail(e); }
}

export function editHistory() {
  const text = S.peek ? S.moduleStack.length ? S.peek.returnState?.moduleDoc?.text : S.peek.returnState?.doc?.text : S.moduleStack.length && S.moduleDoc ? S.moduleDoc.text : S.doc?.text;
  const states = S.undo.map((entry, index) => ({ text: typeof entry === 'string' ? entry : text, kind: typeof entry === 'string' ? 'edit' : 'layout', label: typeof entry === 'string' ? `Undo step ${S.undo.length - index}` : `Undo layout ${S.undo.length - index}` }));
  states.push({ text, kind: 'current', label: 'Current work' });
  [...S.redo].reverse().forEach((entry, index) => states.push({ text: typeof entry === 'string' ? entry : text, kind: typeof entry === 'string' ? 'edit' : 'layout', label: typeof entry === 'string' ? `Redo step ${index + 1}` : `Redo layout ${index + 1}` }));
  return { states, current: S.undo.length };
}

export async function enterEditPeek(text, label) {
  if (!S.doc) return false;
  const path = S.moduleStack.length && S.modulePath ? S.modulePath : S.path;
  const doc = await api.post('/api/doc/parse', { text, path });
  const returnState = S.peek?.edit ? S.peek.returnState : peekReturnState();
  S.editPeek = { text, label };
  S.peek = { ref: label, edit: true, returnState };
  if (S.moduleStack.length) { S.moduleDoc = doc; S.moduleFlat = flatGraph(doc, S.tpl); }
  else { S.doc = doc; setBarBeats(doc.project?.beatsPerBar); }
  S.selSet.clear(); S.sel = null;
  notify();
  return true;
}

export async function restoreEditPeek() {
  if (!S.peek?.edit || !S.editPeek) return false;
  const text = S.editPeek.text;
  const current = S.moduleStack.length ? S.peek.returnState?.moduleDoc?.text : S.peek.returnState?.doc?.text;
  await leavePeek();
  S.editPeek = null;
  if (text === current) { toast('That is already the current state.'); return true; }
  return !!(await applyText(text));
}

let beforeSave = null;
/** The code view hands in what must happen before a save: its typed text is brought into the working copy, so a save never writes an older text than the editor shows. */
export const setBeforeSave = (fn) => { beforeSave = fn; };

export async function save() {
  try {
    if (beforeSave) await Promise.resolve(beforeSave()).catch(() => {});
    if (S.moduleStack.length && S.modulePath) {
      S.moduleDoc = await api.post('/api/module/doc/save', { path: S.modulePath });
      S.moduleFlat = flatGraph(S.moduleDoc, S.tpl);
      const midiBlock = S.moduleStack[0]?.midiBlock;
      if (midiBlock) {
        const activated = await api.activateMidiModule({ path: S.modulePath, songPath: S.path, block: midiBlock, baseVersion: S.doc.version });
        setDoc(activated.midi);
        if (S.doc.dirty) setDoc(await api.post('/api/doc/save', { path: S.path }));
        notify();
        toast('Saved and activated the MIDI variant. The original remains available from the MIDI block.');
      } else {
        notify();
        toast('Saved module.');
      }
      return;
    }
    setDoc(await api.post('/api/doc/save', { path: S.path }));
    toast('Saved.');
  } catch (e) { fail(e); }
}

/** A checkpoint stops when the recovery copy of a file in this browser differs from the file on the server: the person decides (see resolveCheckpointDraft). */
export class CheckpointDraftConflict extends Error {
  constructor(path, isModule) { super(`Checkpoint stopped: ${path} differs from its browser recovery buffer. Open and review that file first.`); this.path = path; this.isModule = isModule; }
}

/** The choice of the person for a recovery copy that stopped a checkpoint: 'keep' saves the browser copy as the file, 'discard' throws the browser copy away (the saved file stays). */
export async function resolveCheckpointDraft(path, choice) {
  const project = S.project?.id || '';
  const record = listWorkRecords(localStorage, project).find((r) => r.path === path);
  if (choice === 'keep' && record) {
    const current = await api.get(`/api/doc?path=${encodeURIComponent(path)}`);
    if (current.text !== record.text) {
      await api.put('/api/doc', { path, text: record.text, baseVersion: current.version });
      await api.post('/api/doc/save', { path, suppressCheckpoint: true });   // the checkpoint that goes on takes this saved text
    }
  }
  try { removeWorkRecord(localStorage, project, path); } catch { /* storage may be blocked */ }
}

/** Saves the current working documents without creating per-file auto-checkpoints; the caller creates one checkpoint after the whole batch succeeds. */
export async function saveForCheckpoint() {
  await editsDone();
  const records = listWorkRecords(localStorage, S.project?.id || '');
  for (const record of records) {
    if (record.path === S.path || record.path === S.modulePath) continue;
    const module = /^modules\/.*\.module$/i.test(record.path);
    let current;
    try {
      current = module
        ? await api.get(`/api/module/doc?path=${encodeURIComponent(record.path)}`)
        : await api.get(`/api/doc?path=${encodeURIComponent(record.path)}`);
    } catch (e) { if (/There is no file/.test(e.message)) continue; throw e; }   // a draft of a file that was moved or removed is not a reason to stop the checkpoint (the draft stays)
    if (record.text !== current.text) throw new CheckpointDraftConflict(record.path, module);
    if (!current.dirty) continue;
    if (module) await api.post('/api/module/doc/save', { path: record.path });
    else await api.post('/api/doc/save', { path: record.path, suppressCheckpoint: true });
  }
  if (S.moduleStack.length && S.modulePath) {
    if (S.moduleDoc?.dirty) {
      S.moduleDoc = await api.post('/api/module/doc/save', { path: S.modulePath });
      S.moduleFlat = flatGraph(S.moduleDoc, S.tpl);
      const midiBlock = S.moduleStack[0]?.midiBlock;
      if (midiBlock) {
        const activated = await api.activateMidiModule({ path: S.modulePath, songPath: S.path, block: midiBlock, baseVersion: S.doc.version });
        setDoc(activated.midi);
      }
    }
    if (S.doc.dirty) setDoc(await api.post('/api/doc/save', { path: S.path, suppressCheckpoint: true }));
    notify();
    return;
  }
  if (S.doc?.dirty) setDoc(await api.post('/api/doc/save', { path: S.path, suppressCheckpoint: true }));
}

/** Builds the song (or one block, or a bar range). Returns the report, or null when it failed or was refused. */
export async function build(only, range, intent, forceRender) {
  if (S.build.running) return null;
  await flushViewSave();
  if (S.build.running) return null;
  if (S.peek?.ai && document.body.classList.contains('ai-busy')) { toast('A new build of the proposal is in progress: wait until it is ready.'); return null; }
  if (S.peek?.ai) { import('./ai.js').then((m) => m.buildAndListen(S.peek.ai, only || undefined)); return null; }   // the proposal is built in its own temporary project, never in this one
  if (S.peek) { toast('Read-only: a state that is only being looked at cannot be built.'); return null; }
  buildAbortController = new AbortController();
  const buildId = `b${Date.now().toString(36)}-${++buildCounter}`;
  buildAbortController.buildId = buildId;
  S.build = { running: true, clockAt: S.build.clockAt, clockPath: S.build.clockPath, intent: intent || '', startedAt: performance.now(), only: only || null, status: only ? { ...S.build.status } : {}, takes: S.build.takes || {}, sig: S.build.sig, inputs: null, files: S.build.files, follows: S.build.follows, log: S.build.log, live: {}, queued: new Set(), completed: new Set() };
  S.buildErrors = null;   // the red marks in the code belong to the last build: they go when the next one starts
  appendBuildLog(`Build started${only ? ` (only ${only})` : ''}${range ? ` (bars ${range.firstBar} to ${range.firstBar + range.bars - 1})` : ''}.`);
  notify();
  let report = null;
  try {
    // the requests that prepare the build end at once when Cancel build is pressed, not when their answer arrives
    try {
      const request = api.get(`/api/doc/inputs?path=${encodeURIComponent(S.path)}`);
      const inputs = await abortable(request, buildAbortController.signal);
      S.build.inputs = inputs.hash;
      S.build.files = inputs.blocks || S.build.files;
      S.build.follows = inputs.followsMaster || S.build.follows;
    } catch { /* the build itself checks inputs */ }
    buildAbortController.signal.throwIfAborted();
    if (!only && !range) {
      stop();
      if (S.aiPlaying) (await import('./ai.js')).stopPreview();
    }
    // a full build says first how big and how long it will be (a guess that learns); a big one asks before it starts
    if (!only && !range && intent !== 'preview') {
      try {
        const e = await abortable(api.get(`/api/build/estimate?path=${encodeURIComponent(S.path)}`), buildAbortController.signal);
        if (e.ok && e.blocks > 0) {
          appendBuildLog(`Estimate: ${e.text}`);
          S.build.estimate = { blocks: e.blocks, seconds: e.seconds, startedAt: performance.now(), rendered: 0, nextReport: 0.25 };
          if (e.bytes > 2 * 1024 ** 3) {
            const { ask } = await import('./ui.js');
            const gb = (e.bytes / 1024 ** 3).toFixed(1);
            const choice = await ask('A big build', `This build needs about ${gb} GB (free: ${(e.freeBytes / 1024 ** 3).toFixed(1)} GB) and takes ${e.seconds < 90 ? 'about ' + Math.round(e.seconds) + ' s' : 'about ' + Math.round(e.seconds / 60) + ' min'}. ${e.basis === 'learned' ? 'This estimate rests on ' + e.builds + ' earlier build(s).' : 'This is a first guess; it gets better with every build.'}`, [{ label: 'Build', value: 'build', primary: true }, { label: 'Not now', value: 'no' }]);
            if (choice !== 'build') { S.build.running = false; S.build.estimate = null; appendBuildLog('Build cancelled before it started.'); notify(); return null; }
          }
        }
      } catch { /* the estimate is a help; the build itself checks the disk */ }
    }
    const body = { path: S.path, buildId };
    if (only) body.only = only;
    if (range) body.range = range;
    if (intent) body.intent = intent;
    if (forceRender === true) body.forceRender = true;
    buildAbortController.signal.throwIfAborted();
    report = await api.post('/api/build', body, buildAbortController.signal);
    if (report.clockAt) { S.build.clockAt = report.clockAt; S.build.clockPath = S.path; }   // the clock the build read: what the roll and the preview show must be made with the same one
    appendBuildLog(`Build done: ${report.hits ?? report.Hits} cached, ${report.misses ?? report.Misses} rendered.`);
    if (report.builtWithErrors) {
      const left = (report.skippedBlocks || []).map((b) => `${b.block ?? b.Block} (${b.code ?? b.Code}, line ${b.line ?? b.Line})`).join(', ');
      appendBuildLog(`ATTENTION: built WITH ERRORS. Left out of this build: ${left}. Fix the red blocks and build again.`);
      toast(`Built with errors: ${left} left out. Fix the red blocks.`);
    }
    for (const b of report.blocks || []) {
      const st = b.status ?? b.Status;
      if (st === 'failed' || st === 'skipped') appendBuildLog(`${b.block ?? b.Block}: ${st}: ${b.message ?? b.Message ?? ''}`);
    }
    for (const b of report.blocks || []) {
      const name = b.block ?? b.Block;
      S.build.status[name] = { status: b.status ?? b.Status, message: b.message ?? b.Message, at: Date.now() };
      const take = b.takeId ?? b.TakeId;
      if (take) S.build.takes[name] = take;
    }
    aliasComponentTakes();
    S.buildErrors = { path: S.path, items: collectBuildErrors(report, S.doc, (S.doc?.text ?? '').split('\n')) };
    const summary = buildSummary(report, false, 0);
    if (summary) { appendBuildLog(summary); toast(summary); }
  } catch (e) {
    if (e.name === 'AbortError') { const kept = Object.keys(S.build.takes || {}).length; appendBuildLog(buildSummary(null, true, kept)); toast(buildSummary(null, true, kept)); }
    else { appendBuildLog(`Build failed: ${e.message}`); fail(e); }
  }
  buildAbortController = null;
  S.build.running = false;
  S.build.live = {};
  notify();
  return report;
}

/** The instant the clock of the song is read at in the browser: the one of the last build of this song, else the one of the document, so the roll and the preview show the notes the build made. */
export function songClock() {
  const iso = (S.build.clockAt && S.build.clockPath === S.path ? S.build.clockAt : null) || S.doc?.clockAt;
  const at = iso ? new Date(iso) : null;
  return at && !Number.isNaN(at.getTime()) ? at : null;
}

/** Whether a block has something that reads the clock and can be baked (a build has to exist: its clock is what is baked). */
export function blockReadsClock(id, doc = S.doc) {
  return Object.keys(S.build.status || {}).length > 0 && (doc?.stabilityNotes || []).some((line) => line.startsWith(id + ': ') || line.startsWith(id + '.'));
}

/** After a build the person liked: bakes the values the clock gave in that build into the source (the whole song, or one block): in this song, or in a NEW song so the open one stays dynamic as a seed. */
export async function bakeDynamicHelpers(block) {
  if (!block && !S.doc?.bakeableClock) { toast('There is nothing that reads the clock and can be baked.'); return false; }
  const { ask, el } = await import('./ui.js');
  const scope = block ? `the block ${block}` : 'this song';
  const base = S.path.replace(/^.*\//, '').replace(/\.amplet$/, '');
  const input = el('input', { value: base + '-baked', 'aria-label': 'Name of the new song', style: 'width:100%;box-sizing:border-box' });
  const note = el('p', { className: 'note', textContent: 'Name for a new song (letters, digits, - and _):' });
  const choice = await ask('Bake clock values', `Writes the clock values the last build of ${scope} used into the source, so it stops following the clock.\n\nIn this song: Undo takes it back. In a new song: this song stays as it is (still dynamic), so it can be the seed of several baked songs.`, [
    { label: 'In this song', value: 'here', primary: true },
    { label: 'In a new song', value: 'new' },
    { label: 'Cancel', value: undefined },
  ], [note, input]);
  if (!choice) return false;
  if (choice === 'new') {
    try {
      const made = await api.post('/api/doc/bake-clock-as', { path: S.path, name: input.value.trim(), ...(block ? { block } : {}) });
      toast(`Baked into ${made.path}. ${made.summary || ''}`.trim());
      document.dispatchEvent(new CustomEvent('ample:open', { detail: made.path }));
      return true;
    } catch (e) { fail(e); return false; }
  }
  const before = (S.doc?.text.match(/\bbake\b/g) || []).length;
  const ok = await edit([{ type: 'bakeDynamicHelpers', path: S.path, ...(block ? { block } : {}) }]);
  if (ok) {
    const places = Math.max(0, (S.doc?.text.match(/\bbake\b/g) || []).length - before);
    appendBuildLog(`Baked the clock values of the last build (${places} place${places === 1 ? '' : 's'}).`);
    toast(`Baked ${places} place${places === 1 ? '' : 's'} with the values of the last build.${S.doc?.stability === 'dynamic' ? ' Something still reads the clock (see the status line).' : ''} Undo takes it back.`);
  }
  return ok;
}

export function stopBuild() {
  if (buildAbortController?.buildId) stoppedBuilds.add(buildAbortController.buildId);
  buildAbortController?.abort();
}

/* ---------- history and peek ---------- */
export async function loadHistory() {
  const graph = await api.get('/api/history/graph');
  if (S.history && JSON.stringify(S.history) === JSON.stringify(graph)) return;   // the same history: nothing to redraw (the players of the blocks would blink)
  S.history = graph;
  notify();
}

/** Looks at another state: the song of that state is parsed and shown read-only; nothing on the server changes. */
function peekReturnState() {
  return { doc: S.doc, sel: S.sel, selSet: new Set(S.selSet), selLink: S.selLink, selRev: S.selRev, groups: { ...S.groups }, pins: new Set(S.pins), stack: [...S.stack], moduleStack: [...S.moduleStack], modulePath: S.modulePath, moduleDoc: S.moduleDoc, moduleFlat: S.moduleFlat, songDiagnostics: S.songDiagnostics, projectLog: [...S.projectLog], build: { ...S.build, status: { ...S.build.status }, takes: { ...S.build.takes }, log: [...S.build.log], queued: S.build.queued ? new Set(S.build.queued) : undefined, completed: S.build.completed ? new Set(S.build.completed) : undefined, live: S.build.live ? { ...S.build.live } : undefined } };
}

export async function enterPeek(ref) {
  const files = await api.get(`/api/history/peek?ref=${encodeURIComponent(ref)}`);
  const text = files[S.path];
  if (text === undefined) { toast(`That state has no ${S.path}.`); return false; }
  const doc = await api.post('/api/doc/parse', { text, path: S.path });
  S.peek = { ref, returnState: S.peek?.returnState || peekReturnState() };
  S.doc = doc;
  setBarBeats(doc.project?.beatsPerBar);
  S.selSet.clear();
  S.sel = null;
  notify();
  return true;
}

/** Looks at the song of an AI proposal in the real views, read-only: the proposed text is parsed and shown, nothing on the server changes and nothing is saved. */
export async function enterAiPeek(taskId, path) {
  const { text } = await api.get(`/api/ai/tasks/${taskId}/text?path=${encodeURIComponent(path)}`);
  const doc = await api.post('/api/doc/parse', { text, path });
  S.peek = { ref: 'AI proposal', ai: taskId, targetPath: path, audio: new Set(), returnState: S.peek?.returnState || peekReturnState() };
  S.build = { ...S.build, running: false, only: null, status: {}, takes: {}, log: S.build.log || [] };   // what is shown as built belongs to the real project, not to the proposal
  S.doc = doc;
  S.selSet.clear();
  S.sel = null;
  await applyAiBuild(await api.get(`/api/ai/tasks/${taskId}`).catch(() => null));
  notify();
  return true;
}

/** Shows what the build check of a proposal found per block (built, failed, with audio to play) on the blocks of the read-only preview. */
export async function applyAiBuild(task) {
  if (!S.peek?.ai || !task?.build) return;
  S.peek.audio = new Set((task.build.blocks || []).filter((b) => b.audio).map((b) => b.block));
  for (const b of task.build.blocks || []) {
    const detail = task.build.details?.find((item) => item.block === b.block);
    S.build.status[b.block] = { status: b.status, message: detail?.message };
    if (['failed', 'skipped'].includes(b.status) && S.peek.buildLogVersion !== task.build.version) appendBuildLog(`${b.block}: ${b.status}: ${detail?.message || task.build.message || ''}`);
  }
  S.peek.buildLogVersion = task.build.version;
  notify();
}

export async function leavePeek() {
  if (!S.peek) return;
  const previous = S.peek.returnState;
  S.peek = null;
  if (previous) { Object.assign(S, previous); }
  else await reloadDoc();
  S.editPeek = null;
  notify();
}

/** Switches the working files to a line; unsaved work in the session is saved or dropped first, on the user's say. */
export async function goLine(line) {
  S.peek = null;
  await api.post('/api/history/go', { line });
  documentHistory.clear();
  activateHistory(true);
  await Promise.all([reloadDoc(), loadHistory()]);
}

export function onBuildProgress(e) {
  if (stale(e)) return;
  const block = e.block ?? e.Block;
  S.build.status[block] = { status: e.status ?? e.Status };
  S.build.queued?.add(block);
  S.build.completed?.add(block);
  if (S.build.live) delete S.build.live[block];
  appendBuildLog(`${block}: ${e.status ?? e.Status}`);
  const est = S.build.estimate;
  if (est && (e.status ?? e.Status) === 'rendered') {
    est.rendered += 1;
    const share = est.rendered / Math.max(1, est.blocks);
    if (share >= est.nextReport && share < 1) {
      est.nextReport = Math.min(1, share + 0.25);
      const elapsed = (performance.now() - est.startedAt) / 1000;
      const left = (elapsed / share) * (1 - share);
      appendBuildLog(`About ${Math.round(share * 100)}% of expected renders done; about ${left < 90 ? Math.max(1, Math.round(left)) + ' s' : Math.round(left / 60) + ' min'} to go.`);
    }
  }
  notifySoon();
}

export function select(id, additive = false) {
  if (additive) {
    if (S.selSet.has(id)) S.selSet.delete(id); else S.selSet.add(id);
    S.sel = S.selSet.has(id) ? id : ([...S.selSet].pop() || null);
  } else {
    S.selSet = new Set(id ? [id] : []);
    S.sel = id;
  }
  S.selLink = null;
}
