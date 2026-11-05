// The Playlist: an alternative, advanced view of the same song. Lines (a chain of blocks from its sources to its bus) with clips, sections, groups, the tail, the
// master, a routing gutter, lanes and the transport. It reads the parsed song through web/lib/playlist-model.mjs and writes only through the commands every view uses
// (playlist-actions.js). Switching it off, or never opening it, changes nothing. Plan: plan/playlist-view/. @ample:playlist
import { $, el, showMenu, toast } from './ui.js';
import { S, activeDoc, enterModule, flat, notify, readOnly, select, setModuleLevel, viewOf, sectionsOf } from './store.js';
import { statOf, focusNode } from './canvas.js';
import { gotoSource } from './panels.js';
import { openMidiNotesForBlock } from './midi-editors.js';
import { download, drawPeaks, onPlayer, peaksOf, playerState, seekFraction, setLoopRegion, stop } from './player.js';
import { previewSong } from './arrangement.js';
import { drawSections, sectionMenu } from './view-sections.js';
import { sectionAt } from '../lib/view-sections.mjs';
import { derivePlaylist } from '../lib/playlist-model.mjs';
import { barAt, snapBar } from '../lib/part-editing.mjs';
import { lineGraph } from '../lib/playlist-graph.mjs';
import { blockLines, laneClips, parseSequencer } from '../lib/sequencer-lanes.mjs';
import { addSection, moveSection, parsePatterns, parseSong, removeSection, sectionNames, setBars } from '../lib/pattern-model.mjs';
import { colourOf, dropIndex } from '../lib/section-timeline-model.mjs';
import { lastChange } from '../lib/playlist-diff.mjs';
import { addableParams, automatedParams, curvePath } from '../lib/playlist-automation.mjs';
import { openAutomationEditor } from './automation-editor.js';
import { barBeats } from '../lib/meter.mjs';
import * as act from './playlist-actions.js';
import * as gs from './playlist-states.js';
import { clearPreview, playMix, preview, previewActive, stopMix, toggle as togglePreview } from './playlist-preview.js';

const KEY = 'ample.playlist.on';
export const playlistEnabled = () => { try { return localStorage.getItem(KEY) === '1'; } catch { return false; } };
export function setPlaylistEnabled(on) {
  try { localStorage.setItem(KEY, on ? '1' : '0'); } catch { /* storage may be blocked */ }
  syncTab();
  if (on) renderPlaylist();
}

const view = { bw: 24, open: new Set(), folded: new Set(), routing: false, snap: 1, picked: new Set(), focus: null };
let model = null, songBars = 16, spb = 2, built = false, unlisten = null;
const phone = () => matchMedia('(max-width: 700px)').matches;
const editable = () => !readOnly() && !phone();
const clipKey = (c) => `${c.block}:${c.index}`;
const nodeOf = (id) => flat().nodes.find((n) => n.id === id);
const lineOfId = (id) => model?.lines.find((l) => l.id === id);

/** The tab exists only while the setting is on; off means no button, no section and no code running. */
export function syncTab() {
  const on = playlistEnabled();
  let btn = $('#btabs button[data-t="playlist"]');
  let sec = $('#b-playlist');
  if (on && !btn) {
    btn = el('button', { textContent: 'Playlist' });
    btn.dataset.t = 'playlist';
    $('#btabs button[data-t="arr"]').after(btn);
    btn.onclick = async () => { const { setBottomTab } = await import('./panels.js'); if (btn.classList.contains('on') && !$('#bottom').classList.contains('min')) { $('#b-min').click(); return; } if ($('#bottom').classList.contains('min')) $('#b-min').click(); setBottomTab('playlist'); renderPlaylist(); };
  }
  if (on && !sec) { sec = el('div', { className: 'bsec', id: 'b-playlist' }); $('#bbody').append(sec); }
  if (!on) {
    if (btn?.classList.contains('on')) $('#btabs button[data-t="arr"]')?.click();
    btn?.remove();
    sec?.remove();
    unlisten?.();
    unlisten = null;
    clearPreview();
  }
}

const mapStatus = (n) => {
  if (!n) return null;
  const s = statOf(n);
  if (s.cls === 'invalid' || s.cls === 'blocked') return 'failed';
  if (s.cls === 'run') return 'building';
  if (s.txt === 'not built') return 'not built';
  if (s.cls === 'ok') return 'built';
  if (s.cls === 'hit') return 'cache hit';
  return null;
};

const masterTake = () => {
  const t = S.build.takes || {};
  if (t.master) return t.master;
  const last = flat().nodes.filter((n) => t[n.id]).pop();
  return last ? t[last.id] : null;
};

function barsOf(doc) {
  const ends = (doc.blocks || []).flatMap((b) => (b.parts || []).map((p) => (p.at || 1) + (Number(p.length) || 0) - 1));
  const used = Math.max(16, ...ends, ...sectionsOf().map((s) => s.at + s.length - 1));
  return Math.min(256, Math.ceil((used + 2) / 2) * 2);
}

export function renderPlaylist() {
  const box = $('#b-playlist');
  if (!box || !playlistEnabled()) return;
  if (!box.classList.contains('on') && built) return;   // not shown: draw when it is
  built = true;
  const startedAt = performance.now();
  const scroller = box.querySelector('.pl-scroll');
  const scroll = scroller ? { x: scroller.scrollLeft, y: scroller.scrollTop } : { x: 0, y: 0 };
  box.textContent = '';
  if (!S.doc) { box.append(el('p', { className: 'note', textContent: 'Open a song to see its lines.' })); return; }
  const tempo = S.doc.project?.tempo || 120;
  spb = (barBeats() * 60) / tempo;
  songBars = barsOf(activeDoc());
  model = derivePlaylist(activeDoc(), S.tpl, { ...viewOf(), groups: S.groups }, { statusOf: (name) => mapStatus(nodeOf(name)), songBars, beatsPerBar: barBeats() });
  const W = songBars * view.bw;
  if (gs.scoreState.loadedFor !== `${S.project?.id || ''}:${S.path}`) gs.loadScore().then((def) => { if (def) renderPlaylist(); });

  box.append(toolbar());
  if (gs.hasScore() && gs.scoreState.show) box.append(statesMatrix());
  const scrollEl = el('div', { className: 'pl-scroll', tabIndex: -1 });
  const rows = el('div', { className: 'pl-rows', style: `width:${W + LABEL + (view.routing ? GUTTER : 0)}px` });
  scrollEl.append(rows);
  rows.append(rulerRow(W));
  for (const g of model.groups) {
    rows.append(groupRow(g, W));
    if (view.folded.has(g.title)) continue;
    for (const id of g.lines) { const line = lineOfId(id); if (line) appendLine(rows, line, W); }
  }
  for (const t of model.terminals) rows.append(blockRow(t, W, 'terminal'));   // the bus the lines play into, then what comes after it
  for (const t of model.tail) rows.append(blockRow(t.block, W, 'tail'));
  rows.append(masterRow(W));
  if (model.loose.length) rows.append(...looseRows(W));
  if (view.routing) rows.append(cables());
  box.append(minimap(scrollEl), scrollEl, dock());
  scrollEl.addEventListener('scroll', () => { const svg = scrollEl.querySelector('.pl-cables'); if (svg) svg.style.left = scrollEl.scrollLeft + 'px'; }, { passive: true });   // the cables stay beside the sticky gutter
  scrollEl.scrollLeft = scroll.x;
  scrollEl.scrollTop = scroll.y;
  unlisten ||= onPlayer(updatePlayhead);
  updatePlayhead(playerState());
  requestAnimationFrame(() => drawCables());
  box.setAttribute('aria-label', 'Playlist');
  box.dataset.renderMs = String(Math.round(performance.now() - startedAt));   // read by the performance spec
}

const LABEL = 210, GUTTER = 150;

// ------------------------------------------------------------------------------------------------ minimap

/** A thin overview of the whole song above the rows: the sections, one thin band for each line, and the window that is shown. A click or a drag moves the window; the arrow keys scroll it by four bars. */
function minimap(scrollEl) {
  const wrap = el('div', { className: 'pl-minimap', role: 'group', tabIndex: 0, 'aria-label': 'Overview of the whole song. Click or drag to move the view, or use the arrow keys.' });
  const canvas = el('canvas', { height: 34, 'aria-hidden': 'true' });
  wrap.append(canvas);
  const sections = sectionsOf(), lines = model.lines;
  const colours = ['#5b8def', '#e0833a', '#4fae8b', '#b565d9', '#d9a441', '#d95f7a'];
  const draw = () => {
    const width = Math.max(120, wrap.clientWidth || 600), h = 34;
    canvas.width = width; canvas.height = h; canvas.style.width = width + 'px';
    const g = canvas.getContext('2d'), k = width / songBars, css = getComputedStyle(document.documentElement);
    g.fillStyle = css.getPropertyValue('--panel2') || '#222'; g.fillRect(0, 0, width, h);
    sections.forEach((s, i) => { g.fillStyle = colours[i % colours.length]; g.globalAlpha = 0.25; g.fillRect((s.at - 1) * k, 0, s.length * k, h); g.globalAlpha = 1; });
    const rowH = Math.max(1, Math.min(4, (h - 4) / Math.max(1, lines.length)));
    lines.forEach((line, i) => { g.fillStyle = css.getPropertyValue('--acc') || '#5b8def'; for (const c of line.clips) g.fillRect((c.at - 1) * k, 2 + i * rowH, Math.max(1, (c.length || songBars - c.at + 1) * k), Math.max(1, rowH - 0.5)); });
    const left = (scrollEl.scrollLeft / view.bw) * k, size = Math.max(6, ((scrollEl.clientWidth - LABEL - (view.routing ? GUTTER : 0)) / view.bw) * k);
    g.strokeStyle = '#fff'; g.lineWidth = 1.5; g.strokeRect(left + 0.75, 0.75, Math.min(size, width - left) - 1.5, h - 1.5);
  };
  const go = (x) => { const k = canvas.clientWidth / songBars; scrollEl.scrollLeft = Math.max(0, (x / k) * view.bw - (scrollEl.clientWidth - LABEL) / 2); };
  wrap.onpointerdown = (e) => { wrap.setPointerCapture(e.pointerId); go(e.clientX - wrap.getBoundingClientRect().left); wrap.onpointermove = (m) => go(m.clientX - wrap.getBoundingClientRect().left); wrap.onpointerup = () => { wrap.onpointermove = wrap.onpointerup = null; }; };
  wrap.onkeydown = (e) => { if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') { e.preventDefault(); scrollEl.scrollLeft += (e.key === 'ArrowRight' ? 4 : -4) * view.bw; } };
  scrollEl.addEventListener('scroll', draw, { passive: true });
  requestAnimationFrame(draw);
  return wrap;
}

// ------------------------------------------------------------------------------------------------ toolbar

function toolbar() {
  const bar = el('div', { className: 'pl-toolbar row' });
  const button = (text, title, fn, extra = {}) => el('button', { type: 'button', textContent: text, title, 'aria-label': title, onclick: fn, ...extra });
  if (S.moduleStack.length) bar.append(button('← Song', 'Leave the module and show the song again', () => setModuleLevel(0)), el('b', { textContent: `Module ${S.moduleStack.at(-1).instance}` }));
  bar.append(
    button('▶ Play', 'Build if needed, then play the song', () => previewSong()),
    button('■', 'Stop', () => { stop(); stopMix(); }),
    button('Play bars', 'Play only the bars chosen on the ruler', () => (S.range ? previewSong(S.range) : toast('Drag over the ruler to choose bars first.'))),
    button('+ Line', 'Add a line: a new source on its own bus', (e) => newLineMenu(e)),
    button('−', 'Narrower bars', () => { view.bw = Math.max(8, view.bw - 4); renderPlaylist(); }),
    button('+', 'Wider bars', () => { view.bw = Math.min(96, view.bw + 4); renderPlaylist(); }),
    button(view.routing ? 'Routing: on' : 'Routing: off', 'Show how the lines connect: cables to their bus, sends and sidechain keys', () => { view.routing = !view.routing; renderPlaylist(); }, { 'aria-pressed': String(view.routing) }),
    button('Open all', 'Open every line to show its chain', () => { for (const l of model.lines) view.open.add(l.id); renderPlaylist(); }),
    button('Close all', 'Close every line', () => { view.open.clear(); renderPlaylist(); }),
    button('WAV', 'Download the built song as WAV', () => { const t = masterTake(); if (t) download(t, 'wav'); else toast('Build the song first.'); }),
    ...(gs.hasScore() ? [
      button(gs.scoreState.show ? 'States: on' : 'States', 'The game score: levels of the lines in each music state', () => { gs.scoreState.show = !gs.scoreState.show; renderPlaylist(); }, { 'aria-pressed': String(gs.scoreState.show) }),
      el('label', { className: 'pl-snap' }, [document.createTextNode('State overlay '), (() => {
        const s = el('select', { 'aria-label': 'State overlay: dim the lines that are off in a state' }, [el('option', { value: '', textContent: 'none' }), ...gs.states().map((id) => el('option', { value: id, textContent: id, selected: id === gs.scoreState.overlay }))]);
        s.onchange = () => { gs.scoreState.overlay = s.value || null; renderPlaylist(); };
        return s;
      })()]),
      button('Hear state', 'Play the built stems of the chosen state (the score has to be built; nothing is rendered here)', () => (gs.scoreState.overlay ? gs.auditionState(gs.scoreState.overlay) : toast('Choose a state in the overlay first.'))),
      button('Stop state', 'Stop the state audition', () => gs.stopAudition()),
    ] : []),
    el('label', { className: 'pl-snap' }, [document.createTextNode('Snap '), (() => {
      const s = el('select', { 'aria-label': 'Snap to bars' }, [1, 2, 4, 8].map((n) => el('option', { value: String(n), textContent: n === 1 ? 'bar' : `${n} bars`, selected: n === view.snap })));
      s.onchange = () => { view.snap = Number(s.value); };
      return s;
    })()]),
  );
  if (phone()) bar.append(el('span', { className: 'note', textContent: 'Read-only on a phone; edit in the Arrangement.' }));
  if (view.routing) bar.append(el('span', { className: 'note', textContent: 'Cables: solid is the route into a bus, dashed is a send or a sidechain key.' }));
  if (previewActive()) {
    const banner = el('div', { className: 'pl-banner', role: 'status' }, [
      el('span', { textContent: 'Preview of a mix, not the build. Mute and solo never change the song; the build ignores them.' }),
      button('Hear it', 'Play the mix of the lines that are heard (approximate: no bus or master processing)', () => playMix(model.lines, S.range, spb)),
      button('Clear', 'Clear mute and solo', () => { clearPreview(); renderPlaylist(); }),
    ]);
    bar.append(banner);
  }
  return bar;
}

function newLineMenu(event) {
  const r = event.currentTarget.getBoundingClientRect();
  showMenu(r.left, r.bottom, act.SOURCE_TYPES.filter((t) => S.tpl?.[t]).map((t) => ({ label: `New ${t} line`, fn: () => act.newLine(t, model.terminals[0], songBars) })));
}

// ------------------------------------------------------------------------------------------------ states (game score)

function statesMatrix() {
  const def = gs.scoreState.def, map = gs.layerMap(model);
  const wrap = el('div', { className: 'pl-states', role: 'region', 'aria-label': 'States of the game score' });
  const table = el('table');
  const head = el('tr', {}, [el('th', { textContent: 'Layer' }), el('th', { textContent: 'Line' })]);
  for (const s of def.states) head.append(el('th', { title: [s.stinger ? `stinger ${s.stinger}` : '', s.fallback ? `falls back to ${s.fallback}` : ''].filter(Boolean).join(', ') }, [document.createTextNode(s.id), ...(s.stinger ? [el('span', { className: 'pl-type', textContent: ` ♪${s.stinger}` })] : []), ...(s.fallback ? [el('span', { className: 'pl-type', textContent: ` →${s.fallback}` })] : [])]));
  table.append(head);
  for (const layer of def.layers) {
    const line = map.get(layer.id);
    const row = el('tr', {}, [el('td', { textContent: layer.id }), el('td', { className: line ? '' : 'pl-warn', textContent: line ? line.name : `block ${layer.block} is not in the song` })]);
    for (const s of def.states) {
      const value = gs.levelOf(s.id, layer.id);
      const cell = el('button', { type: 'button', className: 'pl-cell', title: `${layer.id} in ${s.id}: ${value}. Click to change.`, 'aria-label': `Level of ${layer.id} in ${s.id}: ${value}` }, [el('span', { className: 'pl-cellbar', style: `width:${Math.round(value * 100)}%` }), el('span', { className: 'pl-cellval', textContent: value ? String(value) : '·' })]);
      cell.onclick = async () => {
        if (!editable()) return;
        const { ask } = await import('./ui.js');
        const input = el('input', { type: 'number', min: 0, max: 1, step: 0.05, value: String(value), 'aria-label': 'Level from 0 to 1' });
        const ok = await ask(`${layer.id} in ${s.id}`, 'A level from 0 (silent) to 1 (full). It is written to the score file.', [{ label: 'Cancel', value: false }, { label: 'Set', value: true, primary: true }], [el('label', {}, [document.createTextNode('Level'), input])]);
        if (ok && await gs.setLevel(s.id, layer.id, Number(input.value))) renderPlaylist();
      };
      row.append(el('td', {}, [cell]));
    }
    table.append(row);
  }
  wrap.append(el('div', { className: 'note', textContent: `${gs.scoreState.summary.name}: ${def.layers.length} layers, ${def.states.length} states. A level is written to ${gs.scoreState.summary.path}; build the score to hear it.` }), table);
  return wrap;
}

// ------------------------------------------------------------------------------------------------ rows

const px = (bars) => `${bars * view.bw}px`;

function rulerRow(W) {
  const row = el('div', { className: 'pl-row pl-ruler-row' });
  row.append(el('div', { className: 'pl-label pl-corner', textContent: 'Lines' }));
  if (view.routing) row.append(el('div', { className: 'pl-gutter', textContent: 'routing' }));
  const ruler = el('div', { className: 'pl-ruler', style: `width:${W}px;background-size:${view.bw}px 100%` });
  const tempo = S.doc.project?.tempo || 120;
  for (let i = 0; i < songBars; i += view.bw < 28 ? 4 : 2) ruler.append(Object.assign(el('span', { textContent: `bar ${i + 1} | ${Math.round((i * barBeats() * 60) / tempo)}s` }), { style: `left:${i * view.bw}px;width:${2 * view.bw}px` }));
  const strip = el('div', { className: 'arr-sections', 'aria-label': 'Sections of the song' });
  drawSections(strip, view.bw);
  ruler.append(strip);
  const range = el('div', { className: 'pl-range' });
  if (S.range) { range.style.left = px(S.range.firstBar - 1); range.style.width = px(S.range.bars); }
  const loop = el('div', { className: 'pl-loop', id: 'pl-loop' });
  const head = el('div', { className: 'pl-playhead', id: 'pl-playhead' });
  ruler.append(range, loop, head);
  ruler.oncontextmenu = (e) => { e.preventDefault(); const bar = barAt(e.clientX - ruler.getBoundingClientRect().left, view.bw); sectionMenu(e, bar, sectionAt(sectionsOf(), bar)); };
  ruler.tabIndex = 0;
  ruler.setAttribute('role', 'group');
  ruler.setAttribute('aria-label', 'Ruler of the song in bars. The context menu key opens the menu for sections.');
  ruler.onkeydown = (e) => { if (e.key === 'ContextMenu' || (e.shiftKey && e.key === 'F10')) { e.preventDefault(); const r = ruler.getBoundingClientRect(), bar = S.range?.firstBar || 1; sectionMenu({ clientX: r.left + (bar - 1) * view.bw, clientY: r.bottom }, bar, sectionAt(sectionsOf(), bar)); } };
  ruler.onpointerdown = (e) => {
    if (e.button !== 0) return;
    const rect = ruler.getBoundingClientRect();
    const at = (ev) => Math.max(1, Math.min(songBars, barAt(ev.clientX - rect.left, view.bw)));
    const first = at(e), shift = e.shiftKey;
    ruler.setPointerCapture(e.pointerId);
    let last = first;
    ruler.onpointermove = (ev) => { last = at(ev); const a = Math.min(first, last), b = Math.max(first, last); range.style.left = px(a - 1); range.style.width = px(b - a + 1); };
    ruler.onpointerup = (ev) => {
      ruler.onpointermove = ruler.onpointerup = null;
      last = at(ev);
      const a = Math.min(first, last), b = Math.max(first, last);
      const dur = playerState().duration;
      if (a === b && !shift) { S.range = null; if (dur) seekFraction(((a - 1) * spb) / dur); else toast('Play the song once to move around in it.'); notify(); return; }
      S.range = { firstBar: a, bars: b - a + 1 };
      if (shift && dur) setLoopRegion([((a - 1) * spb) / dur, (b * spb) / dur]);
      notify();
    };
  };
  row.append(ruler);
  return row;
}

function groupRow(g, W) {
  const folded = view.folded.has(g.title);
  const row = el('div', { className: 'pl-row pl-group' });
  const label = el('div', { className: 'pl-label', title: g.custom ? 'A group of the song (the same group as in Nodes)' : 'A group computed from the song' });
  const tg = el('button', { type: 'button', className: 'pl-fold', textContent: folded ? '▸' : '▾', 'aria-expanded': String(!folded), 'aria-label': `${g.title}: ${folded ? 'show' : 'hide'} its lines`, onclick: () => { if (folded) view.folded.delete(g.title); else view.folded.add(g.title); renderPlaylist(); } });
  label.append(tg, el('span', { className: 'pl-name', textContent: g.title }), el('span', { className: 'pl-count', textContent: `${g.lines.length}${g.partial ? ' *' : ''}` }));
  label.oncontextmenu = (e) => { e.preventDefault(); groupMenu(e, g); };
  row.append(label);
  if (view.routing) row.append(el('div', { className: 'pl-gutter' }));
  row.append(el('div', { className: 'pl-track', style: `width:${W}px` }));
  return row;
}

function groupMenu(e, g) {
  if (!editable()) return;
  const items = [{ label: 'Rename group...', fn: async () => { const name = await askName('Rename group', g.title); if (name) { if (g.custom && g.id) act.renameGroup(g.id, name); else toast('This group is computed; select its lines and use Group to make it one of the song.'); } } }];
  if (g.custom && g.id) items.push({ label: 'Ungroup', danger: true, fn: () => act.ungroup(g.id) });
  else items.push({ label: 'Make this a group of the song', fn: () => { act.groupLines(g.lines.map(lineOfId).filter(Boolean), g.title); } });
  showMenu(e.clientX, e.clientY, items);
}

async function askName(title, value) {
  const { ask } = await import('./ui.js');
  const input = el('input', { type: 'text', value, 'aria-label': title, maxLength: 80 });
  const ok = await ask(title, '', [{ label: 'Cancel', value: false }, { label: 'OK', value: true, primary: true }], [el('label', {}, [document.createTextNode('Name'), input])]);
  return ok ? input.value.trim() : null;
}

function appendLine(rows, line, W) {
  rows.append(lineRow(line, W));
  if (!view.open.has(line.id)) return;
  // opened: the sources (with the effects between them and the bus), the note side, the chain, the lanes
  for (const s of line.sources) {
    if (s.block) { rows.append(blockRow(s.block, W, 'source', line)); rows.append(...extraRows(s.block, W)); }
    for (const pre of s.pre) rows.append(blockRow(pre, W, 'chain', line));
  }
  for (const side of line.noteSides) for (const step of side.steps) rows.append(blockRow(step, W, 'note', line, side.steps.length > 0 && model.sharedNotes.includes(step)));
  line.chain.forEach((b, i) => { if (!line.sources.some((s) => s.pre.includes(b))) rows.append(blockRow(b, W, 'chain', line, false, i)); });
  for (const lane of line.lanes) rows.append(blockRow(lane, W, 'lane', line));
  const blocks = [...new Set([...line.sources.flatMap((s) => [s.block, ...s.pre]), ...line.chain].filter(Boolean))];
  for (const block of blocks) rows.append(...autoRows(block, W));
}

async function editAutomation(n, name, text) {
  const result = await openAutomationEditor({ type: n.tpl, name, title: n.title + ' ' + name, text, bars: songBars, tempo: S.doc?.project?.tempo, block: n.id });
  if (result !== undefined) await act.setAutomation(n.id, name, result);
}

/** One row for each parameter of a block that carries an automation: its curve over the bars; a click opens the curve editor. */
function autoRows(block, W) {
  const n = nodeOf(block);
  if (!n) return [];
  return automatedParams(n).map((p) => {
    const row = el('div', { className: 'pl-row pl-sub pl-auto', 'data-block': block });
    const label = el('div', { className: 'pl-label', title: 'Click to edit the curve of ' + p.name }, [el('span', { className: 'pl-indent' }), el('span', { className: 'pl-name', textContent: block + ' · ' + p.name }), el('span', { className: 'pl-type', textContent: p.kind })]);
    label.tabIndex = 0;
    label.onclick = () => editable() && editAutomation(n, p.name, p.text);
    label.onkeydown = (e) => { if (e.key === 'Enter' && editable()) editAutomation(n, p.name, p.text); };
    row.append(label);
    if (view.routing) row.append(el('div', { className: 'pl-gutter' }));
    const track = el('div', { className: 'pl-track pl-curve', style: 'width:' + W + 'px;background-size:' + view.bw + 'px 100%' });
    const H = 24, ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('width', String(W)); svg.setAttribute('height', String(H)); svg.setAttribute('aria-hidden', 'true');
    const d = curvePath(p.points, p.range, view.bw, W, H);
    if (d) { const path = document.createElementNS(ns, 'path'); path.setAttribute('d', d); path.setAttribute('class', 'pl-curve-line'); svg.append(path); }
    else { const text = document.createElementNS(ns, 'text'); text.setAttribute('x', '6'); text.setAttribute('y', '16'); text.textContent = p.text; text.setAttribute('class', 'pl-curve-text'); svg.append(text); }
    track.append(svg);
    track.onclick = () => editable() && editAutomation(n, p.name, p.text);
    row.append(track);
    return row;
  });
}

function lineRow(line, W) {
  const open = view.open.has(line.id);
  const row = el('div', { className: `pl-row pl-line ${line.status ? `st-${line.status.replace(/\s/g, '-')}` : ''}${view.focus === line.id ? ' sel' : ''}`, 'data-line': line.id });
  const label = el('div', { className: 'pl-label' });
  const tg = el('button', { type: 'button', className: 'pl-fold', textContent: open ? '▾' : '▸', 'aria-expanded': String(open), 'aria-label': `${line.name}: ${open ? 'close' : 'open'} its chain`, onclick: (e) => { e.stopPropagation(); if (open) view.open.delete(line.id); else view.open.add(line.id); renderPlaylist(); } });
  const name = el('span', { className: 'pl-name', textContent: line.name, title: `${line.name}${line.customName ? ' (named in the song)' : ''}` });
  const mute = el('button', { type: 'button', className: `pl-ms${preview.muted.has(line.id) ? ' on' : ''}`, textContent: 'M', title: 'Mute in the preview only (never changes the song)', 'aria-label': `Mute ${line.name}`, 'aria-pressed': String(preview.muted.has(line.id)), onclick: (e) => { e.stopPropagation(); togglePreview('mute', line.id); renderPlaylist(); } });
  const solo = el('button', { type: 'button', className: `pl-ms${preview.solo.has(line.id) ? ' on' : ''}`, textContent: 'S', title: 'Solo in the preview only (never changes the song)', 'aria-label': `Solo ${line.name}`, 'aria-pressed': String(preview.solo.has(line.id)), onclick: (e) => { e.stopPropagation(); togglePreview('solo', line.id); renderPlaylist(); } });
  label.append(tg, name, el('span', { className: 'pl-badge', textContent: line.status || '' }));
  const level = gs.hasScore() && gs.scoreState.overlay ? gs.lineLevel(model, line, gs.scoreState.overlay) : null;
  if (level !== null) { label.append(el('span', { className: 'pl-level', title: `Level ${level} in the state ${gs.scoreState.overlay}`, 'aria-label': `Level ${level} in ${gs.scoreState.overlay}` }, [el('span', { style: `width:${Math.round(level * 100)}%` })])); if (level === 0) row.classList.add('state-off'); }
  label.append(mute, solo);
  label.onclick = () => selectLine(line);
  label.ondblclick = async () => { if (!editable()) return; const v = await askName('Name of the line', line.name); if (v !== null) act.renameLine(line, v); };
  label.oncontextmenu = (e) => { e.preventDefault(); lineMenu(e, line); };
  label.tabIndex = 0;
  label.setAttribute('role', 'button');
  label.setAttribute('aria-label', 'Line ' + line.name + (line.status ? ', ' + line.status : '') + '. Enter selects it, the arrow keys open and close its chain, F2 renames it, Alt with the up and down arrows moves it.');
  label.onkeydown = (e) => lineKeys(e, line);
  row.append(label);
  if (view.routing) row.append(gutterCell(line));
  const track = el('div', { className: 'pl-track', style: `width:${W}px;background-size:${view.bw}px 100%` });
  for (const clip of line.clips) track.append(clipEl(clip, line));
  trackDraw(track, line);
  row.append(track);
  if (preview.muted.has(line.id) || (preview.solo.size && !preview.solo.has(line.id))) row.classList.add('muted');
  return row;
}

const lineHead = (line) => line.chain.at(-1) || line.sources.find((s) => s.block)?.block || null;
function selectLine(line, additive = false) {
  view.focus = line.id;
  const id = lineHead(line);
  if (id) select(id, additive);
  notify();
}

function lineMenu(e, line) {
  const items = [
    { label: line.chain.length || line.sources.length ? 'Open in Nodes' : 'Open', fn: () => { const n = nodeOf(lineHead(line)); if (n) focusNode(n); } },
    { label: 'Open in Source', fn: () => { const n = nodeOf(lineHead(line)); if (n) gotoSource(n); } },
  ];
  if (editable()) {
    items.push(
      { label: 'Rename line...', fn: async () => { const v = await askName('Name of the line', line.name); if (v !== null) act.renameLine(line, v); } },
      { label: 'Move up', fn: () => act.moveLine(model.lines, line, -1) },
      { label: 'Move down', fn: () => act.moveLine(model.lines, line, 1) },
      { label: 'Add an effect at the end of the chain...', fn: () => effectMenu(e, lineHead(line)) },
      { label: 'Route to another bus', children: busTargets(line).map((b) => ({ label: b, fn: () => act.rerouteLine(line, b) })) },
      { label: 'Add a send to...', children: busTargets(line, true).map((b) => ({ label: b, fn: () => act.addSend(line, b) })) },
      { label: 'Group with the selected lines', fn: () => act.groupLines([...new Set([line, ...[...view.picked].map((id) => lineOfId(id)).filter(Boolean)])], '') },
      { label: 'Remove the line...', danger: true, fn: () => act.removeLine(line, model) },
    );
  }
  showMenu(e.clientX, e.clientY, items);
}

function busTargets(line, includeSends = false) {
  const mine = new Set([...line.chain, ...line.sources.map((s) => s.block)]);
  return (activeDoc().blocks || []).filter((b) => ['bus', 'mixer'].includes(b.type) && !mine.has(b.name) || (includeSends && b.type === 'send')).map((b) => b.name).filter((n) => !mine.has(n));
}

function effectMenu(e, after) {
  const x = e.clientX ?? e.currentTarget?.getBoundingClientRect().left, y = e.clientY ?? e.currentTarget?.getBoundingClientRect().bottom;
  showMenu(x, y, act.effectTemplates().sort((a, b) => a.title.localeCompare(b.title)).map((t) => ({ label: t.title || t.id, fn: () => act.insertEffect(after, t.id) })));
}

function lineKeys(e, line) {
  if (!editable()) return;
  if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); selectLine(line); }
  else if (e.altKey && e.key === 'ArrowUp') { e.preventDefault(); act.moveLine(model.lines, line, -1); }
  else if (e.altKey && e.key === 'ArrowDown') { e.preventDefault(); act.moveLine(model.lines, line, 1); }
  else if (e.key === 'ArrowRight') { view.open.add(line.id); renderPlaylist(); }
  else if (e.key === 'ArrowLeft') { view.open.delete(line.id); renderPlaylist(); }
  else if (e.key === 'F2') { e.preventDefault(); askName('Name of the line', line.name).then((v) => { if (v !== null) act.renameLine(line, v); }); }
}

/** One row of a block: a source, a chain step, a note step, a lane, the tail or the terminal bus. */
function blockRow(block, W, role, line = null, shared = false, index = 0) {
  const n = nodeOf(block);
  const row = el('div', { className: `pl-row pl-sub pl-${role}${view.focus === `block:${block}` || S.selSet?.has(block) ? ' sel' : ''}`, 'data-block': block });
  const label = el('div', { className: 'pl-label' });
  const type = el('span', { className: 'pl-type', textContent: n?.tpl || '' });
  label.append(el('span', { className: 'pl-indent' }), el('span', { className: 'pl-name', textContent: block, title: `${block}: ${n?.tpl || ''}${shared ? ' (shared by several lines)' : ''}` }), type);
  if (shared) label.append(el('span', { className: 'pl-badge', textContent: 'shared' }));
  const status = mapStatus(n);
  if (status) label.append(el('span', { className: `pl-badge st-${status.replace(/\s/g, '-')}`, textContent: status }));
  if ((n?.flags || []).includes('disabled')) label.classList.add('off');
  label.onclick = () => { view.focus = `block:${block}`; select(block); notify(); };
  label.oncontextmenu = (e) => { e.preventDefault(); blockMenu(e, block, role, line, index); };
  label.tabIndex = 0;
  label.setAttribute('role', 'button');
  label.setAttribute('aria-label', block + ', ' + (n?.tpl || 'block') + (status ? ', ' + status : '') + '. Enter selects it' + (role === 'chain' ? ', Delete takes it out of the chain' : '') + '.');
  label.onkeydown = (e) => { if (e.key === 'Enter') { select(block); notify(); } else if (e.key === 'Delete' && role === 'chain' && editable()) act.bypassEffect(block); };
  row.append(label);
  if (view.routing) row.append(el('div', { className: 'pl-gutter' }));
  const track = el('div', { className: 'pl-track', style: `width:${W}px;background-size:${view.bw}px 100%` });
  const parts = n?.parts?.length ? n.parts.map((p, i) => ({ block, index: i, at: p.at, length: Number(p.length) > 0 ? Number(p.length) : null, auto: !(Number(p.length) > 0), name: p.name || '' })) : [{ block, index: 0, at: 1, length: Number(n?.params?.bars) > 0 ? Number(n.params.bars) : songBars, auto: false, name: '', implicit: true }];
  if (role === 'source' || role === 'note' || role === 'lane') for (const clip of parts) track.append(clipEl(clip, line || null, role));
  else track.append(rangeBar(parts, status));
  row.append(track);
  return row;
}

/** Rows under a source: the pattern sections of a drums song, the lanes of a sequencer. */
function extraRows(block, W) {
  const n = nodeOf(block);
  if (!n) return [];
  const first = n.parts?.[0]?.at || 1;
  const end = n.parts?.length ? Math.max(...n.parts.map((p) => p.at + (Number(p.length) || 0) - 1)) : songBars;
  const rows = [];
  const subRow = (label, kind) => {
    const row = el('div', { className: 'pl-row pl-sub pl-' + kind, 'data-block': block });
    row.append(el('div', { className: 'pl-label' }, [el('span', { className: 'pl-indent' }), el('span', { className: 'pl-name', textContent: label }), el('span', { className: 'pl-type', textContent: kind })]));
    if (view.routing) row.append(el('div', { className: 'pl-gutter' }));
    const track = el('div', { className: 'pl-track', style: 'width:' + W + 'px;background-size:' + view.bw + 'px 100%' });
    row.append(track);
    return { row, track };
  };
  if (n.tpl === 'drums' && String(n.params.song || '').trim()) {
    const { entries } = parseSong(n.params.song);
    const cycle = entries.reduce((sum, e) => sum + e.bars, 0);
    if (entries.length && cycle) {
      const { row, track } = subRow(block + ' patterns', 'patterns');
      for (let at = first, cycleNo = 0; at <= Math.min(end, 256); cycleNo++) {
        for (const [i, e] of entries.entries()) {
          if (at > Math.min(end, 256)) break;
          const piece = el('div', { className: 'pl-pattern', title: e.name + ': ' + e.bars + (e.bars === 1 ? ' bar' : ' bars') + ', from bar ' + at + (cycleNo ? ' (the song repeats)' : '. Drag to move, drag the right edge to resize, right-click for more.'), textContent: e.name });
          piece.style.left = px(at - 1); piece.style.width = px(Math.min(e.bars, end - at + 1));
          piece.style.setProperty('--pattern', 'var(--sectl-' + colourOf(e.name) + ', var(--acc))');
          if (cycleNo === 0) {
            piece.classList.add('editable');
            piece.onpointerdown = (ev) => patternDrag(ev, piece, n, entries, i, first);
            piece.oncontextmenu = (ev) => { ev.preventDefault(); ev.stopPropagation(); patternMenu(ev, n, entries, i); };
            piece.tabIndex = 0;
            piece.setAttribute('role', 'button');
            piece.setAttribute('aria-label', 'Section ' + e.name + ', ' + e.bars + (e.bars === 1 ? ' bar' : ' bars') + '. Left and right arrows move it, with Shift they resize it, Delete removes it.');
            piece.onkeydown = (ev) => patternKeys(ev, n, entries, i);
          }
          track.append(piece);
          at += e.bars;
        }
      }
      track.oncontextmenu = (ev) => { if (ev.target === track) { ev.preventDefault(); patternMenu(ev, n, entries, -1); } };
      rows.push(row);
    }
  }
  if (n.tpl === 'sequencer') {
    const doc = activeDoc();
    const parsed = parseSequencer(blockLines(doc.text, n.line));
    const lanes = laneClips(parsed, first);
    for (const lane of lanes) {
      const { row, track } = subRow(lane.id + ' · ' + lane.channels.join(' + '), 'lane');
      for (const c of lane.clips) {
        const piece = el('div', { className: 'pl-pattern editable', title: c.pattern + ': bars ' + c.at + ' to ' + (c.at + c.length - 1) + '. Drag to move, drag the right edge to resize, right-click for more. A clip that plays several lanes moves on all of them.', textContent: c.pattern });
        piece.style.left = px(c.at - 1); piece.style.width = px(c.length);
        piece.style.setProperty('--pattern', 'var(--sectl-' + colourOf(c.pattern) + ', var(--acc))');
        piece.onpointerdown = (ev) => laneDrag(ev, piece, n, c, first);
        piece.oncontextmenu = (ev) => { ev.preventDefault(); ev.stopPropagation(); laneClipMenu(ev, n, parsed, c, first); };
        piece.tabIndex = 0;
        piece.setAttribute('role', 'button');
        piece.setAttribute('aria-label', 'Clip ' + c.pattern + ', bars ' + c.at + ' to ' + (c.at + c.length - 1) + '. Left and right arrows move it, with Shift they resize it, Delete removes it.');
        piece.onkeydown = (ev) => laneKeys(ev, n, c, first);
        track.append(piece);
      }
      track.oncontextmenu = (ev) => {
        if (ev.target !== track || !editable()) return;
        ev.preventDefault();
        const bar = barAt(ev.clientX - track.getBoundingClientRect().left, view.bw);
        const names = Object.entries(parsed.patterns).filter(([, p]) => p.channels.some((ch) => lane.channels.includes(ch))).map(([name]) => name);
        const laneTrack = lane.clips[0]?.track || 1;
        showMenu(ev.clientX, ev.clientY, names.map((name) => ({ label: 'Add a clip of ' + name + ' at bar ' + bar, fn: () => act.editSequencerPlaylist(n.line, { add: { pattern: name, track: laneTrack, at: Math.max(1, bar - first + 1), length: parsed.patterns[name].bars } }) })));
      };
      rows.push(row);
    }
  }
  return rows;
}

/** A drag of a pattern section of a drums song: the right edge changes its bars, the rest moves it among the sections. */
function patternDrag(ev, piece, n, entries, i, first) {
  if (ev.button !== 0 || !editable()) return;
  ev.stopPropagation();
  const edge = ev.offsetX > piece.clientWidth - 6, x0 = ev.clientX, track = piece.parentElement;
  let d = 0, moved = false;
  piece.setPointerCapture(ev.pointerId);
  piece.onpointermove = (m) => {
    moved = true; d = m.clientX - x0;
    if (edge) piece.style.width = Math.max(view.bw, entries[i].bars * view.bw + d) + 'px'; else piece.style.transform = 'translateX(' + d + 'px)';
  };
  piece.onpointerup = async (u) => {
    piece.onpointermove = piece.onpointerup = null;
    if (!moved) return;
    piece.style.transform = '';
    if (edge) await act.setDrumSong(n.id, setBars(entries, i, entries[i].bars + Math.round(d / view.bw)));
    else await act.setDrumSong(n.id, moveSection(entries, i, dropIndex(entries, i, u.clientX - track.getBoundingClientRect().left - (first - 1) * view.bw, view.bw)));
    renderPlaylist();
  };
}

function patternMenu(e, n, entries, i) {
  if (!editable()) return;
  const names = sectionNames(parsePatterns(n.params.patterns || '').patterns);
  const items = [];
  if (i >= 0) {
    items.push(
      { label: 'Repeat this section', fn: () => act.setDrumSong(n.id, addSection(entries, entries[i].name, entries[i].bars, i + 1)) },
      { label: 'Change the pattern', children: names.map((name) => ({ label: name, fn: () => act.setDrumSong(n.id, entries.map((x, k) => (k === i ? { ...x, name } : x))) })) },
      { label: 'Remove this section', danger: true, fn: () => act.setDrumSong(n.id, removeSection(entries, i)) },
    );
  }
  items.push({ label: 'Add a section at the end', children: names.map((name) => ({ label: name, fn: () => act.setDrumSong(n.id, addSection(entries, name, 1)) })) }, { label: 'Edit the pattern grid...', fn: () => { select(n.id); notify(); } });
  showMenu(e.clientX, e.clientY, items);
}

/** A drag of a clip in a sequencer lane: it moves or resizes the clip of the block's own playlist (written into the song text). */
function laneDrag(ev, piece, n, clip, first) {
  if (ev.button !== 0 || !editable()) return;
  ev.stopPropagation();
  const edge = ev.offsetX > piece.clientWidth - 6, x0 = ev.clientX;
  let d = 0, moved = false;
  piece.setPointerCapture(ev.pointerId);
  piece.onpointermove = (m) => {
    moved = true; d = Math.round((m.clientX - x0) / view.bw / view.snap) * view.snap;
    if (edge) piece.style.width = Math.max(view.bw, (clip.length + d) * view.bw) + 'px'; else piece.style.transform = 'translateX(' + d * view.bw + 'px)';
  };
  piece.onpointerup = async () => {
    piece.onpointermove = piece.onpointerup = null;
    if (!moved || !d) return;
    piece.style.transform = '';
    const rel = clip.at - first + 1;
    const change = edge ? { move: { row: clip.row, length: Math.max(1, clip.length + d) } } : { move: { row: clip.row, at: Math.max(1, rel + d) } };
    await act.editSequencerPlaylist(n.line, change);
    renderPlaylist();
  };
}

function patternKeys(e, n, entries, i) {
  if (!editable()) return;
  if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
    e.preventDefault();
    const d = e.key === 'ArrowRight' ? 1 : -1;
    act.setDrumSong(n.id, e.shiftKey ? setBars(entries, i, entries[i].bars + d) : moveSection(entries, i, i + d));
  } else if (e.key === 'Delete' || e.key === 'Backspace') { e.preventDefault(); act.setDrumSong(n.id, removeSection(entries, i)); }
}

function laneKeys(e, n, clip, first) {
  if (!editable()) return;
  if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
    e.preventDefault();
    const d = (e.key === 'ArrowRight' ? 1 : -1) * view.snap;
    act.editSequencerPlaylist(n.line, e.shiftKey ? { move: { row: clip.row, length: Math.max(1, clip.length + d) } } : { move: { row: clip.row, at: Math.max(1, clip.at - first + 1 + d) } });
  } else if (e.key === 'Delete' || e.key === 'Backspace') { e.preventDefault(); act.editSequencerPlaylist(n.line, { remove: clip.row }); }
}

function laneClipMenu(e, n, parsed, clip, first) {
  if (!editable()) return;
  void first;
  showMenu(e.clientX, e.clientY, [
    { label: 'Change the pattern', children: Object.keys(parsed.patterns).map((name) => ({ label: name, fn: () => act.editSequencerPlaylist(n.line, { pattern: { row: clip.row, name } }) })) },
    { label: 'Delete this clip', danger: true, fn: () => act.editSequencerPlaylist(n.line, { remove: clip.row }) },
    { label: 'Open the sequencer', fn: () => { select(n.id); notify(); } },
  ]);
}

function rangeBar(parts, status) {
  const first = Math.min(...parts.map((p) => p.at)), last = Math.max(...parts.map((p) => p.at + (p.length || songBars) - 1));
  const bar = el('div', { className: `pl-span${status ? ` st-${status.replace(/\s/g, '-')}` : ''}` });
  bar.style.left = px(first - 1);
  bar.style.width = px(last - first + 1);
  return bar;
}

function blockMenu(e, block, role, line, index) {
  const n = nodeOf(block);
  const items = [
    { label: 'Open in Nodes', fn: () => n && focusNode(n) },
    { label: 'Open in Source', fn: () => n && gotoSource(n) },
  ];
  if (n?.isModule) items.push({ label: 'Open the module', fn: () => enterModule(n) });
  if (n && editable() && addableParams(n).length) items.push({ label: 'Automate a parameter', children: addableParams(n).map((p) => ({ label: p.name + (p.range?.unit ? ' (' + p.range.unit + ')' : ''), fn: () => editAutomation(n, p.name, n.params[p.name] ?? '') })) });
  if (n && (n.tpl === 'midi-clip' || role === 'note')) items.push({ label: 'Edit the notes...', fn: () => openMidiNotesForBlock(n) });
  if (editable() && line) {
    if (role === 'chain' && !['bus', 'mixer', 'send', 'join', 'master'].includes(n?.tpl)) {
      const chain = line.chain;
      const at = chain.indexOf(block);
      const prev = chain[at - 1], next = chain[at + 1];
      const inBlock = (activeDoc().blocks || []).find((b) => b.name === block)?.froms.find((f) => f.input !== 'key')?.block;
      items.push(
        { label: 'Add an effect after...', fn: () => effectMenu(e, block) },
        { label: (n.flags || []).includes('disabled') ? 'Switch on' : 'Switch off', fn: () => act.setBlockDisabled(block, !(n.flags || []).includes('disabled')) },
        { label: 'Move up (towards the source)', fn: () => (prev && !['bus', 'mixer'].includes(nodeOf(prev)?.tpl) ? act.moveEffect(block, (activeDoc().blocks || []).find((b) => b.name === prev)?.froms.find((f) => f.input !== 'key')?.block || inBlock) : toast('It is already first.')) },
        { label: 'Move down (towards the bus)', fn: () => (next && !['bus', 'mixer'].includes(nodeOf(next)?.tpl) ? act.moveEffect(block, next) : toast('It is last before the bus.')) },
        { label: 'Take out of the chain', danger: true, fn: () => act.bypassEffect(block) },
      );
      if ((n.ins || []).some((i) => i[0] === 'key')) {
        items.push({ label: 'Sidechain from line', children: model.lines.filter((l) => l !== line).map((l) => ({ label: l.name, fn: () => act.setKey(l, block) })) }, { label: 'Clear the sidechain', fn: () => act.clearKey(block) });
      }
    } else if (role === 'chain' || role === 'source') {
      items.push({ label: 'Add an effect after...', fn: () => effectMenu(e, block) });
    }
  }
  showMenu(e.clientX, e.clientY, items);
}

function masterRow(W) {
  const row = el('div', { className: 'pl-row pl-master' });
  row.append(el('div', { className: 'pl-label' }, [el('span', { className: 'pl-name', textContent: 'Master' }), el('span', { className: 'pl-type', textContent: previewActive() ? 'dimmed: the build ignores mute and solo' : 'the built song' })]));
  if (view.routing) row.append(el('div', { className: 'pl-gutter' }));
  const track = el('div', { className: `pl-track pl-wave${previewActive() ? ' dim' : ''}`, style: `width:${W}px` });
  const canvas = el('canvas', { width: Math.min(W, 4000), height: 40, 'aria-label': 'Waveform of the built song' });
  canvas.style.width = `${W}px`;
  track.append(canvas);
  row.append(track);
  const take = masterTake();
  if (take) peaksOf(take, Math.min(W, 4000)).then((p) => drawPeaks(canvas, p, '#7fb7ff')).catch(() => {});
  return row;
}

function looseRows(W) {
  const out = [el('div', { className: 'pl-row pl-group' }, [el('div', { className: 'pl-label' }, [el('span', { className: 'pl-name', textContent: 'Loose blocks' }), el('span', { className: 'pl-count', textContent: String(model.loose.length) })]), el('div', { className: 'pl-track', style: `width:${W}px` })])];
  for (const l of model.loose) {
    const row = blockRow(l.block, W, 'loose');
    row.querySelector('.pl-label').append(el('span', { className: 'pl-reason', textContent: l.reason }));
    out.push(row);
  }
  return out;
}

// ------------------------------------------------------------------------------------------------ clips

function trackDraw(track, line) {
  // a drag on an empty stretch draws a new clip on this line
  track.onpointerdown = (e) => {
    if (e.button !== 0 || e.target !== track) return;
    if (e.shiftKey) { lasso(e); return; }
    if (!editable()) return;
    const rect = track.getBoundingClientRect();
    const first = snapBar(barAt(e.clientX - rect.left, view.bw), view.snap);
    const ghost = el('div', { className: 'pl-clip ghost' });
    ghost.style.left = px(first - 1); ghost.style.width = px(1);
    track.append(ghost);
    track.setPointerCapture(e.pointerId);
    let last = first;
    track.onpointermove = (ev) => { last = Math.max(first, snapBar(barAt(ev.clientX - rect.left, view.bw), view.snap)); ghost.style.width = px(last - first + 1); };
    track.onpointerup = async () => {
      track.onpointermove = track.onpointerup = null;
      ghost.remove();
      const length = last - first + 1;
      if (length >= 1 && last > first) await act.newClip(line, first, length, songBars);
    };
  };
}

/** Shift and drag on an empty stretch: a rectangle that picks every clip it touches (then they move together). */
function lasso(e) {
  const rows = $('#b-playlist .pl-rows');
  const base = rows.getBoundingClientRect();
  const x0 = e.clientX, y0 = e.clientY;
  const band = el('div', { className: 'pl-lasso', 'aria-hidden': 'true' });
  rows.append(band);
  const draw = (x, y) => { band.style.left = Math.min(x0, x) - base.left + 'px'; band.style.top = Math.min(y0, y) - base.top + 'px'; band.style.width = Math.abs(x - x0) + 'px'; band.style.height = Math.abs(y - y0) + 'px'; };
  draw(x0, y0);
  const move = (m) => draw(m.clientX, m.clientY);
  const up = (u) => {
    removeEventListener('pointermove', move); removeEventListener('pointerup', up);
    band.remove();
    const r = { l: Math.min(x0, u.clientX), r: Math.max(x0, u.clientX), t: Math.min(y0, u.clientY), b: Math.max(y0, u.clientY) };
    const keys = [...document.querySelectorAll('#b-playlist .pl-clip[data-key]')].filter((c) => { const b = c.getBoundingClientRect(); return b.left < r.r && b.right > r.l && b.top < r.b && b.bottom > r.t; }).map((c) => c.dataset.key);
    view.picked = new Set(keys);
    notify();
  };
  addEventListener('pointermove', move);
  addEventListener('pointerup', up);
}

function clipEl(clip, line, role = 'source') {
  const n = nodeOf(clip.block);
  const length = clip.length || Math.max(1, songBars - clip.at + 1);
  const status = mapStatus(n);
  const key = clipKey(clip);
  const b = el('button', {
    type: 'button',
    className: `pl-clip${clip.auto ? ' auto' : ''}${view.picked.has(key) ? ' picked' : ''}${status ? ` st-${status.replace(/\s/g, '-')}` : ''}${role === 'lane' ? ' lane' : ''}`,
    'data-key': key,
    title: `${clip.block}${clip.name ? ` · ${clip.name}` : ''}: bars ${clip.at} to ${clip.at + length - 1}${clip.auto ? ' (length not known yet)' : ''}`,
    'aria-label': `${clip.block}${clip.name ? ` ${clip.name}` : ''}, bars ${clip.at} to ${clip.at + length - 1}${status ? `, ${status}` : ''}${clip.auto ? ', length not known yet' : ''}`,
  });
  b.style.left = px(clip.at - 1);
  b.style.width = px(length);
  b.append(el('span', { className: 'pl-clip-name', textContent: clip.name || clip.block }), el('span', { className: 'pl-grip', 'aria-hidden': 'true' }));
  drawNotes(b, line, length);
  b.onclick = (e) => { e.stopPropagation(); if (e.shiftKey || e.ctrlKey || e.metaKey) { if (view.picked.has(key)) view.picked.delete(key); else view.picked.add(key); } else { view.picked = new Set([key]); } select(clip.block, e.shiftKey || e.ctrlKey); notify(); };
  b.ondblclick = (e) => { e.stopPropagation(); const noteBlock = line?.noteSides.find((s) => s.instrument === clip.block)?.steps.find((s) => nodeOf(s)?.tpl === 'midi-clip') || (n?.tpl === 'midi-clip' ? clip.block : null); if (noteBlock) openMidiNotesForBlock(nodeOf(noteBlock)); else if (n) gotoSource(n); };
  b.oncontextmenu = (e) => { e.preventDefault(); e.stopPropagation(); clipMenu(e, clip, line); };
  b.onkeydown = (e) => clipKeys(e, clip, line, length);
  b.onpointerdown = (e) => clipDrag(e, b, clip, line, length);
  return b;
}

/** A small piano roll in a clip: the notes of the note source, when they are written in the compact form. */
function drawNotes(clipEl2, line, length) {
  const steps = line?.noteSides.flatMap((s) => s.steps) || [];
  const src = steps.map(nodeOf).find((x) => x?.tpl === 'midi-clip' && /\d+:\d+(\.\d+)?:/.test(x.params.notes || ''));
  if (!src) return;
  const notes = String(src.params.notes).trim().split(/\s+/).slice(0, 4000).map((t) => t.split(':').map(Number)).filter((t) => t.length >= 4 && t.every(Number.isFinite));
  if (!notes.length) return;
  const pitches = notes.map((t) => t[3]), lo = Math.min(...pitches), hi = Math.max(...pitches, lo + 1);
  const canvas = el('canvas', { className: 'pl-notes', width: Math.max(8, Math.round(length * view.bw)), height: 22, 'aria-hidden': 'true' });
  const g = canvas.getContext('2d');
  g.fillStyle = 'rgba(255,255,255,.7)';
  for (const [bar, beat, len, pitch] of notes) {
    const x = ((bar - 1) + beat / barBeats()) * view.bw, w = Math.max(1.5, (len / barBeats()) * view.bw);
    if (x > canvas.width) continue;
    g.fillRect(x, 20 - ((pitch - lo) / (hi - lo)) * 18, w, 2);
  }
  clipEl2.prepend(canvas);
}

function clipDrag(e, b, clip, line, length) {
  if (e.button !== 0 || !editable()) return;
  e.stopPropagation();
  const edge = e.offsetX > b.clientWidth - 8;
  const x0 = e.clientX, picked = view.picked.has(clipKey(clip)) && view.picked.size > 1 ? [...view.picked].map((k) => ({ block: k.slice(0, k.lastIndexOf(':')), index: Number(k.slice(k.lastIndexOf(':') + 1)) })) : [clip];
  let delta = 0, changed = false;
  b.setPointerCapture(e.pointerId);
  b.onpointermove = (ev) => {
    const d = Math.round((ev.clientX - x0) / view.bw / view.snap) * view.snap;
    if (d === delta) return;
    delta = d; changed = true;
    if (edge) b.style.width = px(Math.max(1, length + delta));
    if (!edge) { b.style.transform = `translateX(${delta * view.bw}px)`; if (picked.length > 1) for (const k of picked) document.querySelector(`.pl-clip[data-key="${k.block}:${k.index}"]`)?.style.setProperty('transform', `translateX(${delta * view.bw}px)`); }
  };
  b.onpointerup = async () => {
    b.onpointermove = b.onpointerup = null;
    if (!changed || !delta) return;
    b.style.transform = '';
    if (edge) await act.resizeClip(clip, Math.max(1, length + delta), songBars);
    else if (picked.length > 1) await act.moveClips(picked, delta, songBars);
    else await act.moveClip(clip, clip.at + delta, songBars);
    renderPlaylist();
  };
  b.onpointercancel = () => { b.onpointermove = b.onpointerup = null; renderPlaylist(); };
}

function clipKeys(e, clip, line, length) {
  if (!editable()) return;
  const step = e.shiftKey ? 'resize' : 'move';
  if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
    e.preventDefault();
    const d = e.key === 'ArrowRight' ? view.snap : -view.snap;
    if (step === 'resize') act.resizeClip(clip, Math.max(1, length + d), songBars); else act.moveClip(clip, Math.max(1, clip.at + d), songBars);
  } else if (e.altKey && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) {
    e.preventDefault();
    const i = model.lines.indexOf(line), target = model.lines[i + (e.key === 'ArrowDown' ? 1 : -1)];
    if (target) act.moveClipToLine(clip, line, target, songBars);
  } else if (e.key === 'Delete' || e.key === 'Backspace') { e.preventDefault(); act.deleteClip(clip, line, songBars); }
  else if (e.key === 'd' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); act.repeatClip(clip, songBars); }
  else if (e.key === 's' || e.key === 'S') { e.preventDefault(); splitHere(clip); }
  else if ((e.key === 'Home' || e.key === 'End') && sectionAt(sectionsOf(), clip.at) >= 0) {
    e.preventDefault();
    const s = sectionsOf()[sectionAt(sectionsOf(), clip.at)];
    act.moveClip(clip, e.key === 'Home' ? s.at : Math.max(s.at, s.at + s.length - length), songBars);
  }
  else if (e.key === 'Enter') { e.preventDefault(); e.currentTarget.ondblclick?.(e); }
  else if (e.key === 'ContextMenu' || (e.shiftKey && e.key === 'F10')) { e.preventDefault(); const r = e.currentTarget.getBoundingClientRect(); clipMenu({ clientX: r.left, clientY: r.bottom }, clip, line); }
}

function splitHere(clip) {
  const st = playerState();
  const bar = st.duration && st.take === masterTake() ? Math.floor(st.time / spb) + 1 : (S.range?.firstBar || null);
  if (!bar) { toast('Put the playhead or the chosen bars inside the clip, then split.'); return; }
  act.splitClip(clip, bar, songBars);
}

function clipMenu(e, clip, line) {
  const items = [
    { label: 'Select the block', fn: () => { select(clip.block); notify(); } },
    { label: 'Open in Nodes', fn: () => nodeOf(clip.block) && focusNode(nodeOf(clip.block)) },
    { label: 'Open in Source', fn: () => nodeOf(clip.block) && gotoSource(nodeOf(clip.block)) },
  ];
  if (editable() && line) {
    items.push(
      { label: 'Split at the playhead or chosen bars', fn: () => splitHere(clip) },
      { label: 'Repeat: add another part after this one', fn: () => act.repeatClip(clip, songBars) },
      { label: 'Copy as a new block', fn: () => act.copyClipAsBlock(clip, line) },
      { label: 'Move to line', children: model.lines.filter((l) => l !== line).map((l) => ({ label: l.name, fn: () => act.moveClipToLine(clip, line, l, songBars) })) },
      { label: 'Delete this clip', danger: true, fn: () => act.deleteClip(clip, line, songBars) },
    );
  }
  showMenu(e.clientX, e.clientY, items);
}

// ------------------------------------------------------------------------------------------------ routing

function gutterCell(line) {
  const cell = el('div', { className: 'pl-gutter' });
  const route = act.routeOf(line);
  const port = el('button', { type: 'button', className: 'pl-port', 'data-line': line.id, title: route ? `${line.name} goes to ${route.to}. Drag to another bus to re-route.` : 'Not connected to a bus', 'aria-label': `Route of ${line.name}${route ? `, now to ${route.to}` : ''}` });
  port.onpointerdown = (e) => routeDrag(e, line);
  port.onkeydown = (e) => { if (e.key === 'Enter' && editable()) { const r = port.getBoundingClientRect(); showMenu(r.right, r.top, busTargets(line).map((b) => ({ label: `Route to ${b}`, fn: () => act.rerouteLine(line, b) }))); } };
  cell.append(port, el('span', { className: 'pl-dest', textContent: route ? route.to : '—' }));
  return cell;
}

function routeDrag(e, line) {
  if (e.button !== 0 || !editable()) return;
  e.preventDefault();
  const rows = $('#b-playlist .pl-rows');
  const rail = el('div', { className: 'pl-rail' }, busTargets(line).map((b) => el('button', { type: 'button', className: 'pl-bus', 'data-bus': b, textContent: b })));
  rail.style.left = `${LABEL + GUTTER}px`;
  rows.append(rail);
  const move = () => {};
  const up = async (ev) => {
    removeEventListener('pointermove', move);
    removeEventListener('pointerup', up);
    const target = document.elementFromPoint(ev.clientX, ev.clientY)?.closest?.('[data-bus]');
    rail.remove();
    if (target) await act.rerouteLine(line, target.dataset.bus);
  };
  addEventListener('pointermove', move);
  addEventListener('pointerup', up);
}

/** The cables: from the port of each line to its bus label, dashed for sends and sidechain keys. They live in one wrapper so they move with the rows. */
function cables() {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('class', 'pl-cables');
  svg.setAttribute('aria-hidden', 'true');
  return svg;
}

function drawCables() {
  const svg = $('#b-playlist .pl-cables');
  const rows = $('#b-playlist .pl-rows');
  if (!svg || !rows || !model) return;
  const base = rows.getBoundingClientRect();
  svg.setAttribute('width', String(LABEL + GUTTER));
  svg.setAttribute('height', String(rows.scrollHeight));
  svg.style.left = ($('#b-playlist .pl-scroll')?.scrollLeft || 0) + 'px';
  const portOf = (lineId) => { const p = rows.querySelector(`.pl-port[data-line="${lineId}"]`); if (!p) return null; const r = p.getBoundingClientRect(); return { x: r.left - base.left + r.width / 2, y: r.top - base.top + r.height / 2 }; };
  const busRow = (name) => { const r = rows.querySelector(`.pl-row[data-block="${CSS.escape(name)}"]`); if (!r) return null; const b = r.getBoundingClientRect(); return { x: LABEL + GUTTER - 6, y: b.top - base.top + b.height / 2 }; };
  let html = '';
  const path = (a, b, cls) => `<path class="${cls}" d="M${a.x},${a.y} C${a.x + 60},${a.y} ${b.x - 60},${b.y} ${b.x},${b.y}"/>`;
  const colours = ['#5b8def', '#e0833a', '#4fae8b', '#b565d9', '#d9a441', '#d95f7a'];
  model.lines.forEach((line, i) => {
    const a = portOf(line.id), route = act.routeOf(line);
    const b = route ? busRow(route.to) : null;
    if (a && b) html += path(a, b, `cable c${i % colours.length}`);
    else if (a) html += `<circle cx="${a.x}" cy="${a.y}" r="3" class="cable off"/>`;
  });
  for (const k of model.links.keys) { const a = portOf(k.fromLine), b = portOf(k.toLine); if (a && b) html += path({ x: a.x, y: a.y }, { x: b.x, y: b.y }, 'cable key'); }
  for (const s of model.links.sends) { const a = portOf(s.fromLine), b = s.toLine ? portOf(s.toLine) : null; if (a && b) html += path(a, b, 'cable send'); }
  svg.innerHTML = html;
}

// ------------------------------------------------------------------------------------------------ dock and playhead

/** The last change of the song text as lines that went (-) and lines that came (+), kept short. */
function editsList() {
  const prev = S.undo?.at(-1);
  const change = typeof prev === 'string' ? lastChange(prev, activeDoc()?.text) : null;
  if (!change) return null;
  const box = el('details', { className: 'pl-edits' }, [el('summary', { textContent: 'Last change: line ' + change.line + ', ' + change.removed.length + ' removed, ' + change.added.length + ' added' })]);
  box.append(el('pre', { textContent: [...change.removed.map((l) => '- ' + l), ...change.added.map((l) => '+ ' + l)].join('\n') }));
  return box;
}

function dock() {
  const d = el('div', { className: 'pl-dock' });
  const id = S.sel;
  const n = id ? nodeOf(id) : null;
  const edits = editsList();
  if (!n) { d.append(el('span', { className: 'note', textContent: 'Select a line, a clip or a block to see it here. Parameters, takes and variants are edited in the panel on the right.' }), ...(edits ? [edits] : [])); return d; }
  const line = model.lines.find((l) => l.chain.includes(id) || l.sources.some((s) => s.block === id) || l.lanes.includes(id));
  const keys = model.links.keys.filter((k) => k.toBlock === id || k.fromBlock === id);
  d.append(
    ...(line ? [graphSvg(line)] : []),
    el('b', { textContent: n.title }), el('span', { className: 'pl-type', textContent: n.tpl }),
    el('span', { className: 'note', textContent: line ? `on the line ${line.name}` : 'not on a line' }),
    ...keys.map((k) => el('span', { className: 'note', textContent: k.toBlock === id ? `sidechain key from ${k.fromBlock}` : `keys ${k.toBlock}` })),
    el('button', { type: 'button', textContent: 'Open in Nodes', onclick: () => focusNode(n) }),
    el('button', { type: 'button', textContent: 'Open in Source', onclick: () => gotoSource(n) }),
    ...(n.tpl === 'midi-clip' ? [el('button', { type: 'button', textContent: 'Edit the notes', onclick: () => openMidiNotesForBlock(n) })] : []),
    ...(edits ? [edits] : []),
  );
  return d;
}

/** The small graph of the selected line (nodes from the notes to the bus); a click on a node selects its block. */
function graphSvg(line) {
  const g = lineGraph(line, model.links.keys.filter((k) => k.toLine === line.id));
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('class', 'pl-graph');
  svg.setAttribute('role', 'group');
  svg.setAttribute('aria-label', 'Graph of the line ' + line.name);
  svg.setAttribute('width', String(g.width + 8));
  svg.setAttribute('height', String(g.height + 8));
  svg.setAttribute('viewBox', '-4 -4 ' + (g.width + 8) + ' ' + (g.height + 8));
  const at = new Map(g.nodes.map((n) => [n.id, n]));
  for (const e of g.edges) {
    const a = at.get(e.from), b = at.get(e.to);
    const p = document.createElementNS(ns, 'path');
    p.setAttribute('d', 'M' + (a.x + a.w) + ',' + (a.y + a.h / 2) + ' C' + (a.x + a.w + 14) + ',' + (a.y + a.h / 2) + ' ' + (b.x - 14) + ',' + (b.y + b.h / 2) + ' ' + b.x + ',' + (b.y + b.h / 2));
    p.setAttribute('class', 'pl-edge' + (e.dashed ? ' dashed' : ''));
    svg.append(p);
  }
  for (const n of g.nodes) {
    const group = document.createElementNS(ns, 'g');
    group.setAttribute('class', 'pl-gnode ' + n.kind);
    group.setAttribute('tabindex', '0');
    group.setAttribute('role', 'button');
    group.setAttribute('aria-label', n.label + ' (' + n.kind + ')');
    const rect = document.createElementNS(ns, 'rect');
    rect.setAttribute('x', n.x); rect.setAttribute('y', n.y); rect.setAttribute('width', n.w); rect.setAttribute('height', n.h); rect.setAttribute('rx', 4);
    const text = document.createElementNS(ns, 'text');
    text.setAttribute('x', n.x + 6); text.setAttribute('y', n.y + n.h / 2 + 4);
    text.textContent = n.label.length > 14 ? n.label.slice(0, 13) + '…' : n.label;
    group.append(rect, text);
    const target = n.block || n.id;
    const pick = () => { select(target); notify(); };
    group.onclick = pick;
    group.onkeydown = (ev) => { if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); pick(); } };
    svg.append(group);
  }
  return svg;
}

function updatePlayhead(st) {
  const head = $('#pl-playhead');
  if (!head || !model) return;
  const show = st.take && st.take === masterTake() && st.duration > 0;
  head.style.display = show ? 'block' : 'none';
  if (show) head.style.left = `${(st.time / spb) * view.bw}px`;
  const loop = $('#pl-loop');
  if (loop) {
    if (st.loopRegion && st.duration) { loop.style.display = 'block'; loop.style.left = `${(st.loopRegion[0] * st.duration / spb) * view.bw}px`; loop.style.width = `${((st.loopRegion[1] - st.loopRegion[0]) * st.duration / spb) * view.bw}px`; } else loop.style.display = 'none';
  }
}

