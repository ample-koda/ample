import { isQuoted, quoteLiteral, unquoteLiteral } from '../lib/string-literal.mjs';
import { aceDefaults, withEngineModel } from '../lib/ace-generation.mjs';
import { propertiesIn, renderComponentUi, usesLayout } from './component-ui.js';
import { liveOptions } from './component-live-ui.js';
const MAX_SEED = 18446744073709551615n;
// The library (palette), the properties panel, the bottom panel and the left file lists.
// @ample:templates @ample:properties @ample:structure-browser

import { $, activatable, el, showMenu, ask, toast } from './ui.js';
import { withJamIcon } from './window-chrome.js';
import { S, applyText, build, editsDone, current, edit, flat, notify, readOnly, paramProblem, saveNotes, select, container } from './store.js';
import { noteAbove, withNoteAbove } from '../lib/block-notes.mjs';
import { addBlock, alignSelected, deleteSelected, distributeSelected, enter, fit, focusNode, groupSelected, selectAll, toggleFlag, ungroupNode, up } from './canvas.js';
import { W } from './geometry.js';
import { renderControls } from './controls.js';
import { componentRows, sealLabel } from './components.js';
import { openLyrics } from './lyrics.js';
import { openPluginSettings } from './pluginui.js';
import { openProjects } from './projects.js';
import { seedDice } from './seed-dice.js';
import { openLibrary } from './libraryui.js';
import { api } from './api.js';
import { chooseControl } from './control-choice.js';
import { appendHistoryStructure } from './history.js';
import { renderDrumGrid, renderStrum } from './stepseq.js';
import { meter } from './studio/meter.js';
import { fieldFor, kindOf } from './fields.js';
import { renderEffectCard } from './effects-ui.js';
import { openStudioPanel } from './studio/modal.js';
import { automatable, openAutomationEditor } from './automation-editor.js';
import { openMidiFileRoll, openRoll } from './roll-views.js';
import { V } from './views.js';
import { TINTS, tintOf } from '../lib/tint.mjs';

let structureQuery = '';

/** Shows the block in the source editor (the Source view), selecting its first line. */
export async function gotoSource(n) {
  const { gotoSource: show } = await import('./source.js');
  return show(n);
}

export function setBottomTab(t) {
  document.querySelectorAll('#btabs button[data-t]').forEach((b) => b.classList.toggle('on', b.dataset.t === t));
  document.querySelectorAll('.bsec').forEach((s) => s.classList.toggle('on', s.id === `b-${t}`));
  $('#bottom').classList.remove('min');
  $('#b-min').textContent = '▾';
}

/** Open and focus the exact source diagnostic or original upstream build error. */
export function showBlockStatusError(block, diagnostic) {
  setBottomTab('log');
  let target = block;
  if (S.groups[block]) target = S.groups[block].members.find((id) => S.build.status[id]?.status === 'failed') || S.groups[block].members.find((id) => S.build.status[id]?.status === 'skipped') || block;
  const seen = new Set();
  while (!diagnostic && !seen.has(target) && S.build.status[target]?.status === 'skipped') {
    seen.add(target);
    const upstream = /comes from ['"]([^'"]+)['"]/.exec(S.build.status[target]?.message || '')?.[1];
    if (!upstream) break;
    target = upstream;
  }
  activeLogTab = diagnostic ? 'song' : 'build';
  renderBottom();
  const line = diagnostic && (diagnostic.range?.start.line ?? diagnostic.line - 1);
  const rows = [...document.querySelectorAll(diagnostic ? '#slog .song-diagnostic' : '#blog .build-log-row')];
  const row = rows.find((item) => diagnostic ? Number(item.dataset.line) === line && item.dataset.code === String(diagnostic.code) : item.dataset.block === target && item.classList.contains('failed')) || rows.find((item) => item.dataset.block === target);
  if (row) { row.tabIndex = 0; row.scrollIntoView({ block: 'center' }); row.focus({ preventScroll: true }); row.classList.add('log-focus'); setTimeout(() => row.classList.remove('log-focus'), 4000); }
}

/* ---------- library ---------- */
// The order the sketch shows the palette in (presentation only; templates not listed come last, by title).
const PALETTE_ORDER = ['midi-clip', 'drums', 'bass', 'piano', 'ai-voice', 'choir', 'lyrics', 'import-audio', 'mixer', 'bus', 'master', 'variants', 'scorer', 'group'];
const rank = (t) => { const i = PALETTE_ORDER.indexOf(t.id); return i < 0 ? PALETTE_ORDER.length : i; };
// the manual has a page for every block type (template-<id>); read the list of pages once, so a block without a page gets no link
let manualPages = null;
const manualTemplates = () => manualPages ||= fetch('/help').then((r) => r.ok ? r.text() : '').then((html) => new Set([...html.matchAll(/id="template-([a-z0-9-]+)"/g)].map((m) => m[1]))).catch(() => new Set());
function addManualLink(host, tpl) {
  manualTemplates().then((pages) => {
    if (!pages.has(tpl) || !host.isConnected) return;
    host.append(el('h4', { textContent: 'Manual', style: 'padding-left:0' }), el('a', { className: 'manual-link', href: `/help#template-${tpl}`, target: '_blank', rel: 'noopener', textContent: 'Open the manual page of this block' }));
  });
}
let paletteTip;
/** One line for the hover preview of a block in the library: its kind and its first parameters. */
function libraryPreview(t) {
  const names = (t.parameters || []).map((p) => p.name).filter(Boolean);
  return [t.kind || t.class, names.length ? `parameters: ${names.slice(0, 6).join(', ')}${names.length > 6 ? ` (+${names.length - 6})` : ''}` : 'no parameters'].filter(Boolean).join(' · ');
}
let escapeWired = false;
function wirePaletteTip(item) {
  const show = () => {
    paletteTip ||= el('div', { className: 'palette-tooltip', role: 'tooltip', id: 'palette-tooltip' });
    const [hint, ...body] = item.dataset.tip.split('\n\n').reverse();   // the last paragraph is the how-to line: shown dimmer under the description
    paletteTip.replaceChildren(...(body.length ? [el('div', { className: 'pt-body', textContent: body.reverse().join('\n\n') })] : []), ...(item.dataset.preview ? [el('div', { className: 'pt-params', textContent: item.dataset.preview })] : []), el('div', { className: 'pt-hint', textContent: hint }));   // pt-params: a small preview of the block
    document.body.append(paletteTip);
    item.setAttribute('aria-describedby', 'palette-tooltip');
    const rect = item.getBoundingClientRect();
    const tip = paletteTip.getBoundingClientRect();
    paletteTip.style.left = `${Math.max(8, Math.min(rect.left, innerWidth - tip.width - 8))}px`;
    paletteTip.style.top = `${Math.max(8, rect.top - tip.height - 8)}px`;
    paletteTip.classList.add('visible');
  };
  // a short delay before it shows, so moving the pointer over the list does not make it flicker
  let timer = 0;
  const hide = () => { clearTimeout(timer); paletteTip?.classList.remove('visible'); item.removeAttribute('aria-describedby'); };
  const later = () => { clearTimeout(timer); timer = setTimeout(show, 180); };
  item.addEventListener('pointerenter', later);
  item.addEventListener('pointerleave', hide);
  item.addEventListener('focus', show);
  item.addEventListener('blur', hide);
  item.addEventListener('keydown', (e) => { if (e.key === 'Escape') hide(); });
  if (!escapeWired) { escapeWired = true; document.addEventListener('keydown', (e) => { if (e.key === 'Escape') paletteTip?.classList.remove('visible'); }, true); }   // Escape hides it wherever the focus is
}

const readList = (key) => { try { const v = JSON.parse(localStorage.getItem(key) || '[]'); return Array.isArray(v) ? v : []; } catch { return []; } };
const writeList = (key, list) => { try { localStorage.setItem(key, JSON.stringify(list)); } catch { /* storage may be blocked */ } };

export function renderLib() {
  const q = $('#lib-q').value.toLowerCase();
  const box = $('#lib');
  box.innerHTML = '';
  const sorted = [...S.templates].sort((a, b) => rank(a) - rank(b) || a.title.localeCompare(b.title));
  const cats = [...new Set(sorted.map((t) => t.category))];
  // favourites (a star on an item, kept in this browser) and the blocks that were added last come first
  const favs = new Set(readList('ample.lib.fav')), recent = readList('ample.lib.recent');
  const special = (title, ids, id) => {
    const items = ids.map((tid) => sorted.find((t) => t.id === tid)).filter((t) => t && (!q || `${t.title} ${t.description}`.toLowerCase().includes(q)));
    if (!items.length) return;
    const d = el('details', { open: true, className: 'lib-special', id });
    d.append(el('summary', { textContent: `${title} (${items.length})` }));
    items.forEach((t) => {
      const it = el('div', { className: 'tplitem', draggable: true, 'data-tpl': t.id, tabindex: '0' }, [el('span', { className: 'tpl-title', textContent: t.title })]);
      it.ondragstart = (e) => e.dataTransfer.setData('text/plain', t.id);
      it.ondblclick = () => { const r = $('#cv').getBoundingClientRect(); addBlock(t.id, (r.width / 2 - S.view.x) / S.view.k - W / 2, (r.height / 2 - S.view.y) / S.view.k - 30); };
      d.append(it);
    });
    box.append(d);
  };
  special('★ Favourites', [...favs], 'lib-fav'); special('Recent', recent.filter((tid) => !favs.has(tid)), 'lib-recent');
  cats.forEach((cat) => {
    const items = sorted.filter((t) => t.category === cat && (!q || `${t.title} ${t.description}`.toLowerCase().includes(q)));
    if (!items.length) return;
    const d = el('details', { open: true });
    d.append(el('summary', { textContent: `${cat} (${items.length})` }));
    items.forEach((t) => {
      const tip = `${t.description || ''}${t.description ? '\n\n' : ''}Drag on the canvas to add`;
      const it = el('div', { className: 'tplitem fancy-tip', draggable: true, 'data-tpl': t.id, 'data-tip': tip, 'data-preview': libraryPreview(t), tabindex: '0' });
      wirePaletteTip(it);
      it.append(el('button', { type: 'button', className: `tplstar${favs.has(t.id) ? ' on' : ''}`, textContent: '★', title: favs.has(t.id) ? 'Remove from the favourites' : 'Add to the favourites', 'aria-pressed': String(favs.has(t.id)), 'aria-label': `Favourite: ${t.title}`, onclick: (e) => { e.stopPropagation(); writeList('ample.lib.fav', favs.has(t.id) ? [...favs].filter((x) => x !== t.id) : [...favs, t.id]); renderLib(); } }));
      it.append(el('span', { className: 'tpl-title', textContent: t.title }), el('small', {}, [el('span', { className: `cls ${t.class} tpl-version`, textContent: `${t.class} v${t.version}` })]));
      if (t.component) {
        // the status of the default version at a glance: sealed or not, and deprecated
        const v = t.componentVersions.find((x) => x.version === t.version) || t.componentVersions[0];
        it.append(el('small', { className: 'tpl-component-badges' }, [
          el('span', { className: `tag ${v?.seal === 'sealed' ? '' : 'peek'}`, textContent: v?.seal || 'unsealed', title: v ? sealLabel(v) : '' }),
          ...(v?.deprecated ? [el('span', { className: 'tag peek', textContent: 'deprecated', title: v.deprecated })] : []),
          ...(t.kind && t.kind !== 'Notes' ? [el('span', { className: 'tag', textContent: t.kind.toLowerCase(), title: t.kind === 'Mixed' ? 'Notes in, sound out: an instrument with its effects' : t.kind === 'Audio' ? (t.clientPreview ? 'An audio component: its processors can be previewed in the browser' : `An audio component: the server renders it (no browser preview for ${t.noPreview.join(', ')})`) : 'Notes and audio' })] : []),
          ...(t.componentVersions.length > 1 ? [el('span', { className: 'tag', textContent: `${t.componentVersions.length} versions` })] : []),
        ]));
      }

      it.ondragstart = (e) => e.dataTransfer.setData('text/plain', t.id);
      it.ondblclick = () => {
        const r = $('#cv').getBoundingClientRect();
        const p = { x: (r.width / 2 - S.view.x) / S.view.k, y: (r.height / 2 - S.view.y) / S.view.k };
        addBlock(t.id, p.x - W / 2, p.y - 30);
      };
      it.oncontextmenu = (e) => {
        e.preventDefault();
        const place = (version) => {
          const r = $('#cv').getBoundingClientRect();
          addBlock(t.id, (r.width / 2 - S.view.x) / S.view.k - W / 2, (r.height / 2 - S.view.y) / S.view.k - 30, version);
        };
        const versionItems = t.component && t.componentVersions.length > 1
          ? [{ label: 'Add version', children: t.componentVersions.map((v) => ({ label: `${v.version}${v.current ? ' (current)' : ''}${v.prerelease ? ' (pre-release)' : ''} — ${sealLabel(v)}`, fn: () => place(v.version) })) }]
          : [];
        showMenu(e.clientX, e.clientY, [{ head: t.title }, { label: 'Add to canvas', fn: () => it.ondblclick() }, ...versionItems, { label: 'Template info', fn: () => ask(t.title, `${t.description}\nclass: ${t.class}  version: ${t.version}\nports in: ${t.inputs.map((x) => x.name).join(', ') || '-'}\nports out: ${t.outputs.map((x) => x.name).join(', ') || '-'}${t.plugin ? `\nneeds plugin: ${t.plugin}` : ''}`, [{ label: 'Close', value: true, primary: true }]) }]);
      };
      d.append(it);
    });
    box.append(d);
  });
}

export function renderFiles() {
  const box = $('#pfiles');
  box.innerHTML = '';
  const name = S.project ? S.project.name : 'project';
  const row = (icon, label, depth, file, dim) => {
    const r = el('div', { className: `pf${file ? ' click' : ''}${dim ? ' dim' : ''}`, title: dim ? 'history, take bank, locks: managed by AMPLE' : file ? 'Click to open' : '' });
    r.style.paddingLeft = `${6 + depth * 14}px`;
    r.append(el('span', { textContent: icon, style: 'width:16px;text-align:center' }), el('span', { className: 'pn', textContent: label }));
    if (file && S.doc && S.doc.path === file && S.doc.dirty) r.append(el('span', { className: 'tag peek', textContent: '●', title: 'not saved' }));
    if (file) { r.onclick = () => document.dispatchEvent(new CustomEvent('ample:open', { detail: file })); activatable(r, `Open ${file}`); }
    box.append(r);
  };
  row('📁', `${name}/`, 0);
  row('⚙', '.ample/', 1, null, true);
  S.files.forEach((f) => row('📄', f, 1, f));
}

/* ---------- linked folders (the library's mounts) ---------- */
let mountData = [];

export async function refreshMounts() {
  try { mountData = (await api.get('/api/library')).mounts; } catch { mountData = []; }
  renderMounts();
}

export function renderMounts() {
  const box = $('#mounts');
  if (!box) return;
  box.innerHTML = '';
  [['global', 'Global catalogs (every project)'], ['project', 'This project']].forEach(([scope, label]) => {
    box.append(el('div', { className: 'sect', textContent: label }));
    const list = mountData.filter((m) => m.scope === scope);
    if (!list.length) box.append(el('div', { className: 'note', textContent: 'none yet', style: 'margin:0 0 4px 4px' }));
    list.forEach((m) => {
      const r = el('div', { className: `mrow${m.state !== 'online' ? ' bad' : ''}`, title: `${m.name}\n${m.path}\ncatalog ${m.uuid}\n${m.readOnly ? 'read-only' : 'read-write'}\nClick: show in the library manager` });
      r.dataset.mount = m.id;
      r.append(el('span', { className: 'mi', textContent: m.readOnly ? '🔒' : '✎' }), el('div', { className: 'mt' }, [el('div', { className: 'mn' }, [el('b', { textContent: m.name }), m.state !== 'online' ? el('span', { className: 'tag missing', textContent: m.state }) : '']), el('div', { className: 'mp', textContent: m.path })]));
      r.onclick = () => openLibrary({ kind: 'mount', id: m.id });
      activatable(r, `Show ${m.name} in the library manager`);
      box.append(r);
    });
  });
  box.append(el('button', { textContent: '+ Link a folder...', id: 'link-folder', style: 'margin:8px 0 2px;width:100%', onclick: () => openLibrary(null).then(() => $('#lb-link') && $('#lb-link').click()) }));
}

export function renderPlugins() {
  const box = $('#plugins');
  box.innerHTML = '';
  if (!S.plugins.length) box.append(el('div', { className: 'note', textContent: 'No plugins found.' }));
  S.plugins.forEach((p) => {
    const r = el('div', { className: 'prow' });
    r.append(el('span', { className: 'pnm' }, [el('b', { textContent: p.id }), el('small', { textContent: `v${p.version} · ${p.determinism}` })]), el('button', { textContent: '⚙', title: 'Settings of this plugin', className: 'pgear', onclick: () => openPluginSettings(p.id).catch((e) => toast(e.message)) }));
    box.append(r);
  });
}

/* ---------- properties ---------- */
/**
 * The sidechain of a compressor or duck block in one place: a chooser of the blocks that can drive it (their sound is the key), written as a connection to the key
 * input; "none" takes it away again.
 */
// X03: the master's measured level, from the sound check's always-available numeric master peak/RMS/loudness
// (not only its text findings, which only fire when the master is unusually loud or quiet). Reuses the studio
// meter widget (web/app/studio/meter.js), built earlier but never wired into any panel.
function renderMasterMeter() {
  const peakBar = meter({ value: -60, min: -60, max: 0, label: 'Peak', unit: 'dBFS', decimals: 1 });
  const lufsBar = meter({ value: -60, min: -60, max: 0, label: 'Integrated loudness', unit: 'LUFS', decimals: 1 });
  const text = el('p', { className: 'note master-meter-text', textContent: 'Build the song to see its level.' });
  const refresh = el('button', { type: 'button', textContent: '↻ Measure', title: 'Build (from cache when unchanged) and measure the master’s peak and loudness' });
  const load = async () => {
    text.textContent = 'Measuring...';
    try {
      const result = await api.get(`/api/sound-check?path=${encodeURIComponent(S.path)}`);
      if (!result.ok) { text.textContent = result.message; return; }
      if (!result.master) { text.textContent = 'No master take yet - build the song first.'; return; }
      peakBar.setMeterValue(result.master.peakDbfs);
      lufsBar.setMeterValue(result.master.lufs ?? -60);
      text.textContent = `Peak ${result.master.peakDbfs.toFixed(1)} dBFS · RMS ${result.master.rmsDbfs.toFixed(1)} dBFS · Loudness ${result.master.lufs != null ? `${result.master.lufs.toFixed(1)} LUFS` : 'n/a'}`;
    } catch (error) { text.textContent = error.message || String(error); }
  };
  refresh.onclick = load;
  load();
  return el('div', { className: 'master-meter' }, [
    el('h4', { textContent: 'Level', style: 'padding-left:0' }),
    el('div', { className: 'master-meter-bars' }, [peakBar, lufsBar]),
    text, refresh,
  ]);
}

function sidechainRow(n, disabled) {
  const current = S.doc.blocks.find((b) => b.name === n.id)?.froms?.find((f) => f.input === 'key');
  const candidates = flat().nodes.filter((b) => b.id !== n.id && !b.isGroup && !b.proxy && (b.outs || []).some((p) => p[1] === 'audio'));
  const select = el('select', { disabled, 'aria-label': 'Sidechain from', title: 'The sound of this block drives the reduction (the key)' }, [
    el('option', { value: '', textContent: 'none' }),
    ...candidates.map((b) => el('option', { value: b.id, textContent: b.title || b.id, selected: current?.block === b.id })),
  ]);
  select.onchange = async () => {
    const ops = [];
    if (current) ops.push({ type: 'disconnect', to: n.id, input: 'key' });
    if (select.value) {
      const source = candidates.find((b) => b.id === select.value);
      ops.push({ type: 'connect', from: select.value, output: (source.outs.find((p) => p[1] === 'audio') || source.outs[0])[0], to: n.id, input: 'key' });
    }

    if (ops.length) await edit(ops);
  };
  return el('div', { className: 'row sidechain-row' }, [el('label', { textContent: 'Sidechain from ' }), select]);
}

const CHIP_TEXT = { expression: 'formula', baked: 'baked', override: 'override', stale: 'stale bake', dirty: 'changed by hand' };
const CHIP_ACTIONS = {
  expression: [['Bake', 'bake', 'Freeze what the formula gives now (the formula stays in the source)']],
  baked: [['Dynamic again', 'again', 'Remove the bake: the formula drives the value again']],
  override: [['Use the formula', 'again', 'Remove the override: the formula drives the value again']],
  stale: [['Bake again', 'bake', 'What the formula is made of changed: freeze the new value']],
  dirty: [['Bake again', 'bake', 'The baked value was edited by hand: freeze what the formula gives now'], ['Keep mine', 'override', 'Keep the hand-edited value on purpose']],
};

/** The state chip under a property that is driven by a formula, with the actions that fit its state. */
export function propertyChip(block, name, binding, disabled) {
  const volatileInput = binding.dynamic ? ' · reads the clock' : '';
  const chip = el('div', { className: 'prop-chip', 'data-state': binding.state, title: `${binding.expressionText || binding.expression}${binding.sourceText ? ' now gives ' + binding.sourceText : ''}` }, [
    el('span', { className: 'badge' + (binding.state === 'stale' || binding.state === 'dirty' ? ' bad' : ''), textContent: (CHIP_TEXT[binding.state] || binding.state) + volatileInput }),
    ...(CHIP_ACTIONS[binding.state] || []).map(([label, mode, title]) => el('button', { type: 'button', className: 'mini', textContent: label, title, disabled, onclick: () => edit([{ type: 'setPropertyValue', block, name, value: String(binding.value ?? ''), mode }]) })),
  ]);
  return chip;
}

export function renderProps() {
  const box = $('#props');
  box.innerHTML = '';
  const v = current();
  const n = S.sel && v.nodes.find((x) => x.id === S.sel);
  if (V.mode === 'src' && S.sourceEditMessage && (S.sel || S.selSet.size)) { box.append(el('h4', { textContent: 'Source properties' }), el('p', { className: 'note', textContent: S.sourceEditMessage })); return; }
  const ro = readOnly();
  let pad = el('div', { className: 'pad' });
  const row = (label, node, param) => {
    const d = el('div', { className: 'prop' });
    if (param && n) { d.dataset.kodaBlock = n.id; d.dataset.kodaParam = param; }
    const bad = param && n ? paramProblem(n.id, param) : null;
    if (bad) { d.classList.add('prop-err'); d.title = `${bad.code} ${bad.message}`; }
    const lab = el('label', { textContent: label });
    // a small note next to the label of a field: ask Koda what it does (shown on hover and focus)
    if (param && n) lab.append(el('button', { type: 'button', className: 'koda-ask', textContent: '♪', title: `Ask Koda about ${label}`, 'aria-label': `Ask Koda about ${label}`, onclick: (e) => { e.preventDefault(); e.stopPropagation(); import('./koda.js').then((m) => m.ask(m.subjectOfField(n.id, param), 'explain')); } }));
    d.append(lab, node);
    pad.append(d);
  };
  if (S.selSet.size > 1) {
    box.append(el('h4', { textContent: `${S.selSet.size} blocks selected` }));
    const r = el('div', { className: 'row' });
    r.append(el('button', { textContent: 'Group (Ctrl+G)', className: 'primary', onclick: groupSelected, disabled: ro }), el('button', { textContent: 'Align left', onclick: () => alignSelected('x'), disabled: ro }), el('button', { textContent: 'Align top', onclick: () => alignSelected('y'), disabled: ro }));
    const r2 = el('div', { className: 'row' });
    r2.append(el('button', { textContent: 'Distribute across', onclick: () => distributeSelected('x'), disabled: ro }), el('button', { textContent: 'Distribute down', onclick: () => distributeSelected('y'), disabled: ro }), el('button', { textContent: 'Delete all', onclick: deleteSelected, disabled: ro }));
    pad.append(r, r2, el('p', { className: 'note', textContent: 'Shift/Ctrl+click adds or removes a block; Shift+drag on the background selects with a rectangle; Ctrl+A selects all. Dragging one selected block moves them all.' }), el('p', { className: 'note', textContent: v.nodes.filter((x) => S.selSet.has(x.id)).map((x) => x.title).join(', ') }));
    box.append(pad);
    return;
  }
  if (!n) {
    box.append(el('h4', { textContent: 'Song properties' }));
    const p = S.doc && S.doc.project;
    row('name', Object.assign(el('input', { value: p ? p.name : '', disabled: true })));
    row('tempo', Object.assign(el('input', { value: p ? p.tempo : '', disabled: true })));
    const seedLine = /^((?:composition|project)\s+"(?:[^"\\]|\\.)*"\s*\{\s*(?:ample\s+\d+\s+)?tempo\s+\d+(?:\.\d+)?\s+master-seed\s+)(\d+)(\s*\})/m;
    const seedMatch = seedLine.exec(S.doc?.text || '');
    const projectSeed = String(S.project?.masterSeed ?? '');
    const seedOverride = seedMatch && seedMatch[2] !== projectSeed ? seedMatch[2] : '';
    const seedInput = el('input', { type: 'text', inputMode: 'numeric', value: seedOverride, placeholder: `Project seed (${projectSeed})`, disabled: ro || !seedMatch || S.moduleStack.length > 0, 'aria-label': 'Song seed override', title: 'Leave blank to follow the project master seed. Set a value to override it for this song.' });
    seedInput.onchange = async () => {
      const requested = seedInput.value.trim();
      if (requested && (!/^\d{1,20}$/.test(requested) || BigInt(requested) > MAX_SEED)) { toast('Enter an unsigned 64-bit whole-number seed.'); seedInput.value = seedOverride; return; }
      if (!seedMatch || !projectSeed) { seedInput.value = seedOverride; return; }
      const nextSeed = requested || projectSeed;
      const nextText = (S.doc?.text || '').replace(seedLine, (_line, before, _oldSeed, after) => `${before}${nextSeed}${after}`);
      if (nextText === S.doc?.text || await applyText(nextText)) return;
      seedInput.value = seedOverride;
    };
    row('Seed override', el('span', { className: 'seed-row' }, [seedInput, seedDice(() => seedInput, { disabled: seedInput.disabled })]));
    pad.append(el('button', { id: 'open-settings', textContent: 'Project settings', onclick: () => openProjects('settings') }));
    box.append(pad);
    return;
  }
  const propertiesHeading = el('h4', { className: 'prop-heading' }, [el('span', { className: 'prop-heading-title', textContent: n.proxy ? 'Group boundary' : n.isGroup ? 'Group properties' : 'Block properties' })]);
  box.append(propertiesHeading);
  if (n.proxy) { pad.append(el('p', { className: 'note', textContent: 'These dots are the ports of the group you are inside.' })); box.append(pad); return; }
  const title = el('input', { value: n.title, disabled: ro });
  title.onchange = async () => {
    if (n.isGroup) { S.groups[n.id].title = title.value; notify(); return; }
    if (!(await edit([{ type: 'rename', block: n.id, newName: title.value }]))) title.value = n.title;
  };
  if (!n.isGroup) {
    // the friendly name; the technical type shows on hover, and on a tap (touch has no hover)
    const friendly = S.tpl[n.tpl]?.title || n.tpl;
    const tech = el('small', { className: 'block-type-tech', textContent: ` ${n.tpl}`, hidden: true });
    const badge = el('span', { className: 'block-type', textContent: friendly, title: `Block type: ${n.tpl}`, tabIndex: 0, onclick: () => { tech.hidden = !tech.hidden; } });
    badge.append(tech);
    row('type', badge);
  }
  row('title', title);
  if (!n.isGroup) {
    // the tint: a colour for the card, any block (the tint parameter; clicking the colour in use takes it away)
    const current = tintOf(n.params);
    const swatches = el('div', { className: 'tint-row', role: 'radiogroup', 'aria-label': 'Block tint' }, Object.entries(TINTS).map(([name, colour]) => el('button', {
      type: 'button', className: `tint-dot${name === current ? ' on' : ''}`, title: name, disabled: ro, role: 'radio', 'aria-checked': String(name === current), 'aria-label': name, style: `--tint:${colour}`,
      onclick: () => edit([name === current ? { type: 'removeParameter', block: n.id, name: 'tint' } : { type: 'setParameter', block: n.id, name: 'tint', value: name }]),
    })));
    row('tint', swatches);
  }
  if (n.isGroup) {
    pad.append(el('p', { className: 'note', textContent: `${S.groups[n.id].members.length} blocks: ${S.groups[n.id].members.join(', ')}. Groups are kept in this view for now; the source is unchanged.` }));
    const r = el('div', { className: 'row' });
    r.append(el('button', { textContent: 'Open ⤢', onclick: () => enter(n) }), el('button', { textContent: 'Ungroup', title: 'Ungroup (Ctrl+Shift+G)', onclick: () => ungroupNode(n) }));
    pad.append(r);
    box.append(pad);
    return;
  }
  propertiesHeading.append(el('button', { type: 'button', className: 'studio-source-props', textContent: '</>', title: 'Edit this block in source', 'aria-label': 'Edit in source', onclick: () => gotoSource(n) }));
  box.append(pad);
  const panes = {};
  const pane = (key) => {
    const section = el('div', { className: 'prop-tabpanel', role: 'tabpanel' });
    panes[key] = section;
    return section;
  };
  const info = pane('info'), parametersPane = pane('parameters'), portsPane = pane('ports'), notesPane = pane('notes');
  const stepPane = n.tpl === 'drums' || n.tpl === 'guitar' ? pane('steps') : null;
  const tabs = el('div', { className: 'prop-tabs', role: 'tablist', 'aria-label': 'Block properties' });
  const deck = el('div', { className: 'prop-deck' });
  pad = info;
  const t = S.tpl[n.tpl];
  if (t?.description?.trim()) pad.append(el('h4', { textContent: 'Tip', style: 'padding-left:0' }), el('p', { className: 'note block-tip', textContent: t.description }));
  if (n.tpl === 'master') pad.append(renderMasterMeter());
  if (t?.parameters?.length && !['eq', 'filter', 'distortion', 'delay', 'reverb', 'compressor'].includes(n.tpl)) {
    const advanced = el('button', { type: 'button', className: 'studio-advanced-props', textContent: '↗', disabled: ro, title: 'Open advanced controls', 'aria-label': 'Open advanced controls' });
    advanced.onclick = () => {
      const source = flat().links.find((link) => !link.disabled && link.to === n.id && (link.type === 'audio' || link.type === 'render'));
      const audioSource = n.outs.some((port) => port[1] === 'audio');
      let selectedTake = null;
      const getTakeId = () => source ? S.build.takes?.[source.from] || selectedTake : n.tpl === 'convolution' ? null : audioSource ? S.build.takes?.[n.id] : null;
      const buildInput = source ? async () => { await build(source.from); const take = S.build.takes?.[source.from] || null; if (take) await api.post('/api/control/select', { path: S.path, block: source.from, take }); return take; } : n.tpl !== 'convolution' && audioSource ? async () => { await build(n.id); return S.build.takes?.[n.id] || null; } : null;
      const convolution = n.tpl === 'convolution' ? { getTakeId, loadInput: source ? async () => { selectedTake = (await api.get(`/api/control/block?path=${encodeURIComponent(S.path)}&block=${encodeURIComponent(source.from)}`)).selected || null; } : null, inputTitle: flat().nodes.find((item) => item.id === source?.from)?.title || source?.from, realPreview: (params, takeId) => api.effectPreview({ path: S.path, block: n.id, takeId: takeId || getTakeId(), fromMs: 0, toMs: 30_000, params }), loadImpulse: (params) => api.effectImpulse({ path: S.path, block: n.id, params }) } : {};
      openStudioPanel({ type: n.tpl, title: n.title, block: n.id, definitions: t.parameters, getParams: () => flat().nodes.find((item) => item.id === n.id)?.params || n.params, readOnly: ro || locked, takeId: getTakeId(), buildInput, buildLabel: audioSource && !source ? 'Build block' : 'Build and use input', playLabel: audioSource && !source ? 'Play block' : 'Play input', preview: () => {}, commit: (name, value) => edit([{ type: 'setParameter', block: n.id, name, value: String(value) }]), ...convolution, ...(n.tpl === 'master' ? { tempo: S.doc?.project?.tempo || 120, getTakeId, loadInput: source ? async () => { selectedTake = (await api.get(`/api/control/block?path=${encodeURIComponent(S.path)}&block=${encodeURIComponent(source.from)}`)).selected || null; } : null } : {}) });
    };
    propertiesHeading.append(advanced);
  }
  row('template', el('span', { textContent: t ? t.title : '(unknown type)', title: t?.component ? `${t.title}\nfull name: ${t.id}\nid: ${t.componentId}` : '' }));
  row('update class', el('span', { textContent: n.cls }));
  row('version', el('span', { textContent: `v${n.ver}${n.cls === 'live' ? '  (follows template)' : n.cls === 'pinned' ? '  (pinned by hash)' : ''}` }));
  if (t?.component) {
    for (const [label, value] of componentRows(t, n.ver)) row(label, el('span', { className: label === 'deprecated' ? 'tag peek' : '', textContent: value }));
  }
  const locked = n.flags.includes('lock');
  if (stepPane) {
    pad = stepPane;
    const holder = el('div');   // filled when the sample sets have arrived, but it keeps its place above the parameters
    pad.append(holder);
    (n.tpl === 'drums' ? renderDrumGrid : renderStrum)(holder, n, ro || locked).catch(() => {});
  }
  pad = parametersPane;
  // the pane reads Actions (what you can open from this block), then Parameters; the build / source / delete buttons below the tabs are the Management part
  const hasActions = ['midi', 'sequencer', 'midi-clip'].includes(n.tpl);
  pad.append(el('h4', { textContent: hasActions ? 'Actions' : 'Parameters', style: 'padding-left:0' }));
  let fixedParams = [];   // parameters the chosen model does not let you change (shown, but not editable)
  const params = { ...(t?.parameters || []).reduce((all, p) => ({ ...all, [p.name]: p.default }), {}), ...n.params };
  if (['ace-music', 'ai-voice'].includes(n.tpl)) {
    const defaults = aceDefaults(withEngineModel(S.doc?.settings || {}, n.params.engine));
    fixedParams = defaults.fixed;
    for (const key of ['guidance', 'steps', 'shift']) if (!String(n.params[key] ?? '').trim()) params[key] = String(defaults[key]);
  }
  const effect = renderEffectCard(n.tpl, t?.parameters || [], params, { disabled: ro || locked, readOnly: ro, take: S.build.takes[n.id], block: n.id, build: () => build(n.id), getParams: () => flat().nodes.find((item) => item.id === n.id)?.params || params, setParameter: (name, value) => edit([{ type: 'setParameter', block: n.id, name, value }]) });
  if (effect) pad.append(effect);
  if (n.ins.some((p) => p[0] === 'key')) pad.append(sidechainRow(n, ro || locked));
  if (!effect && n.tpl === 'midi') {
    const view = el('button', { type: 'button', textContent: '🎹 Show notes', title: 'See the notes of the MIDI file in a piano roll (read-only) and make an editable copy', disabled: !n.params.file });
    view.onclick = () => openMidiFileRoll(n.id);
    pad.append(el('div', { className: 'row' }, [view]));
  }
  if (!effect && n.tpl === 'sequencer') {
    const open = el('button', { type: 'button', id: 'open-sequencer', className: 'primary', textContent: 'Open the sequencer', title: 'The workspace of this block: channels, patterns, a playlist and the lanes', disabled: ro });
    open.onclick = () => import('./sequencer.js').then((m) => m.openSequencer(n.id));
    pad.append(el('div', { className: 'row' }, [open]));
  }
  if (!effect && n.tpl === 'midi-clip') {
    const track = el('button', { type: 'button', textContent: 'Track view', title: 'The clip in the arrangement, its notes and the automation of the blocks after it, in one view' });
    track.onclick = () => openRoll(n.id, { track: true });
    const lite = el('button', { type: 'button', className: 'primary', textContent: 'AMPLE Jam', title: 'Draw, paint, slice and hear the notes of this block on a roll or a step grid. The notes text stays the source. A clip with chords or a melody is written out as notes first (undoable).' });
    withJamIcon(lite);
    lite.onclick = () => openRoll(n.id);
    pad.append(el('div', { className: 'row' }, [lite, track]));
    if (n.params.melody !== undefined || n.params.chords !== undefined) {
      const words = n.params.chords !== undefined && n.params.melody === undefined ? 'chords' : 'melody';
      const me = el('button', { type: 'button', className: 'primary', textContent: `Edit ${words}`, title: `Edit the ${words} of this clip on a roll, in notation or as a list. The ${words} line stays the source.` });
      me.onclick = () => import('./melody-editor.js').then((m) => m.openMelodyEditor(n.id));
      pad.append(el('div', { className: 'row' }, [me]));
    }
  }
  // the Jam button of a Notes property: it opens in Jam as a clip of its own (the notes stay in the song as the value of that property)
  const jamButton = (p) => {
      const jam = el('button', { type: 'button', className: 'primary', textContent: `AMPLE Jam · ${p.name}`, title: `Draw the notes of ${p.name} on a roll`, disabled: ro });
      withJamIcon(jam);
      jam.onclick = async () => {
        const { openPianoRollLite } = await import('./pianoroll-lite.js');
        const value = (flat().nodes.find((x) => x.id === n.id)?.params[p.name] ?? '').trim();
        const text = value.startsWith('"') ? value : `"${value}"`;
        openPianoRollLite(n.id, { pseudo: { id: n.id, title: `${n.title} · ${p.name}`, params: {}, flags: [], tpl: 'midi-clip', parts: n.parts || [] }, text, ppq: 1_000_000, write: (next) => edit([next === '' ? { type: 'removeParameter', block: n.id, name: p.name } : { type: 'setParameter', block: n.id, name: p.name, value: next }]) });
      };
      return jam;
  };
  if (t?.component) {
    // a Notes property marked #[Editor(jam)] has its Jam button here, unless a layout of the component puts it somewhere itself
    for (const p of t.parameters.filter((x) => x.editor === 'jam' && /^Notes\??$/.test(x.type))) {
      if (!propertiesIn(t.ui?.panel).has(p.name)) pad.append(el('div', { className: 'row' }, [jamButton(p)]));
    }
  }
  // the layout of a component (knobs, sliders, groups, ...): the properties it shows are drawn by it, the others keep their standard rows below it
  let laidOut = new Set();
  if (t?.component && usesLayout(t.ui?.panel)) {
    const laid = renderComponentUi(t, params, t.ui.panel, 'panel', {
      disabled: ro || locked,
      save: (name, text) => edit([{ type: 'setParameter', block: n.id, name, value: text }]),
      saveMany: (items) => edit(items.map((i) => ({ type: 'setParameter', block: n.id, name: i.name, value: i.text }))),
      ...liveOptions(t, n.id),
      jam: (name) => { const p = t.parameters.find((x) => x.name === name && /^Notes\??$/.test(x.type)); return p ? jamButton(p) : null; },
    });
    laidOut = laid.used;
    pad.append(laid.element);
  }
  if (hasActions && Object.keys(params).length) pad.append(el('h4', { textContent: 'Parameters', style: 'padding-left:0' }));
  Object.entries(params).forEach(([k, val]) => {
    if (effect || laidOut.has(k)) return;
    const meta = t && t.parameters ? t.parameters.find((p) => p.name === k) : null;
    // a string is in "quotes" in the source, but the field shows the text itself (no quotes, no escapes) and puts the quotes back when it saves
    const quoted = isQuoted(val);
    const shown = quoted ? unquoteLiteral(val) : val;
    const display = meta && meta.multiline && !quoted ? shown.replace(/\\n/g, '\n') : shown;
    const toStored = (text) => ((quoted || (['file', 'sampleset'].includes(kindOf(k, meta)) && /[\s"]/.test(text))) && text !== '' ? quoteLiteral(text) : text);   // a path with a space needs its quotes even if it had none
    const clean = (value) => {
      const normalized = meta && !meta.multiline ? value.replace(/[\r\n]+/g, ' ') : value.replace(/\r\n/g, '\n');
      return meta && meta.multiline && !quoted ? normalized.replace(/\n/g, '\\n') : normalized;
    };
    const save = async (value, inp) => {
      const next = clean(value);
      if (!(await edit([{ type: 'setParameter', block: n.id, name: k, value: toStored(next) }]))) inp.value = display;
      else inp.value = next;
    };
    const fixed = fixedParams.includes(k);
    // a property that is a formula: say what state it is in, and offer the choices that fit that state (override, bake, again) instead of a plain overwrite
    const binding = (S.doc?.properties || []).find((p) => p.block === n.id && p.name === k);
    const withChip = (control) => (binding ? el('div', {}, [control, propertyChip(n.id, k, binding, ro || locked)]) : control);
    const tip = locked ? 'Locked: unlock the block to change its parameters' : fixed ? `Fixed by the chosen model: a turbo model works with ${k} ${params[k]} only. Choose a Base or SFT model in the plugin settings to change it.` : (meta && meta.documentation ? meta.documentation : '');
    const conflict = fixed && String(n.params[k] ?? '').trim() !== '' && String(n.params[k]).trim() !== String(params[k]);   // stored value the turbo model refuses: stays editable so it can be repaired
    const field0 = fieldFor(k, meta, display, { disabled: ro || locked, title: tip, tpl: n.tpl, params: n.params, save: (text) => edit([text === '' && ['file', 'sampleset', 'engine'].includes(kindOf(k, meta)) ? { type: 'removeParameter', block: n.id, name: k } : { type: 'setParameter', block: n.id, name: k, value: kindOf(k, meta) === 'engine' ? text : toStored(text) }]) });   // the engine field writes its own quotes
    let field = field0;
    if (field0 && fixed) {
      const noteId = `fixed-${n.id}-${k}`;
      const note = el('div', { id: noteId, className: 'hint', textContent: conflict ? `This block asks for ${k} ${n.params[k]}, but a turbo model works with ${k} ${params[k]} only: the build would refuse it.` : `Fixed by the chosen model: a turbo model works with ${k} ${params[k]} only.` });
      const inputs = field0.matches?.('input,select,textarea') ? [field0] : [...field0.querySelectorAll('input,select,textarea')];
      for (const input of inputs) {
        input.setAttribute('aria-describedby', noteId);
        if (!conflict && !ro && !locked) { input.readOnly = true; input.setAttribute('aria-disabled', 'true'); }
      }
      const parts = [field0, note];
      if (conflict && !ro && !locked) {
        const fix = el('button', { type: 'button', className: 'mini', textContent: `Set to ${params[k]}` });
        fix.onclick = () => edit([{ type: 'setParameter', block: n.id, name: k, value: String(params[k]) }]);
        parts.push(fix);
      }
      field = el('div', {}, parts);
    }
    // parameters that can change over the song get an Automate button next to their field (the curve editor writes the auto(...) text)
    const automateButton = () => {
      if (!automatable(n.tpl, k)) return null;
      const button = el('button', { type: 'button', textContent: '∿', className: 'mini', title: `Automate ${k}: draw how it changes over the bars`, 'aria-label': `Automate ${k}`, disabled: ro || locked });
      const followButton = el('button', { type: 'button', textContent: '\u2307', className: 'mini', title: `Steer ${k} with a control (a follow, curve or map block)`, 'aria-label': `Steer ${k} with a control`, disabled: ro || locked });
      followButton.onclick = () => chooseControl(n, k);
      button.onclick = async () => {
        const bars = Math.max(0, ...flat().nodes.flatMap((node) => (node.parts || []).map((part) => part.at + part.length)));
        const result = await openAutomationEditor({ type: n.tpl, name: k, title: `${n.title} ${k}`, text: val, bars, tempo: S.doc?.project?.tempo, block: n.id });
        if (result === undefined) return;
        await edit([result === '' ? { type: 'removeParameter', block: n.id, name: k } : { type: 'setParameter', block: n.id, name: k, value: result }]);
      };
      return el('span', { className: 'row', style: 'margin:0;gap:2px' }, [button, followButton]);
    };
    if (field) {
      const extra = automateButton();
      row(meta && meta.label ? meta.label : k, withChip(extra ? el('div', { className: 'row', style: 'margin:0' }, [field, extra]) : field), k);
      return;
    }
    const inp = el('input', { value: display, disabled: ro || locked, title: tip, placeholder: meta && meta.placeholder ? meta.placeholder : '' });
    inp.onchange = () => save(inp.value, inp);
    if (meta && meta.modalEditor) {
      const expand = el('button', { textContent: '…', title: `Edit ${meta.label || k} in a larger editor`, 'aria-label': `Edit ${meta.label || k} in a larger editor`, disabled: ro || locked });
      expand.onclick = async () => {
        const text = el('textarea', { rows: 20, value: display, className: meta.multiline ? 'parameter-modal-editor' : '', style: 'width:100%;box-sizing:border-box;font:13px/1.45 ui-monospace,Consolas,monospace;white-space:pre-wrap;overflow-wrap:anywhere' });
        const dialog = document.querySelector('#dlg');
        if (meta.multiline) dialog.classList.add('multiline-parameter-dialog');
        try {
          const result = await ask(meta.label || k, `${meta.documentation || 'Edit this value.'}${meta.multiline ? ' Line breaks are preserved.' : ' Line breaks are converted to spaces.'}`, [{ label: 'Cancel', value: false }, { label: 'Apply', value: true, primary: true }], [text]);
          if (result) await save(text.value, inp);
        } finally { dialog.classList.remove('multiline-parameter-dialog'); }
      };
      row(meta.label || k, withChip(el('div', { className: 'row', style: 'margin:0' }, [inp, expand])), k);
    } else {
      const extra = automateButton();
      row(meta && meta.label ? meta.label : k, withChip(extra ? el('div', { className: 'row', style: 'margin:0' }, [inp, extra]) : inp), k);
    }
  });
  if (!Object.keys(params).length) pad.append(el('p', { className: 'note', textContent: 'No parameters.' }));
  pad = info;
  pad.append(el('h4', { textContent: 'Status', style: 'padding-left:0' }));
  [['lock', 'locked (parameters frozen)'], ['bake', 'baked (data in the project)'], ['refresh', 'always refetch']].forEach(([flag, label]) => {
    const cb = el('input', { type: 'checkbox', checked: n.flags.includes(flag), disabled: ro, style: 'width:auto' });
    cb.onchange = () => toggleFlag(n, flag);
    const l = el('label', {}, [cb, ` ${label}`]);
    l.style.display = 'block';
    pad.append(l);
  });
  if (n.dyn) renderControls(pad, n);
  else if (n.freeze) pad.append(el('p', { className: 'note', textContent: `Freeze level: ${n.freeze}.` }));
  if (n.plugin) pad.append(el('p', { className: 'note', textContent: `Needs plugin "${n.plugin}": ${S.plugins.some((p) => p.id === n.plugin) ? 'installed' : 'NOT installed (opt-in; AMPLE reports it instead of skipping it)'}` }));
  addManualLink(info, n.tpl);
  pad = portsPane;
  pad.append(el('p', { className: 'note', textContent: `in: ${n.ins.map((x) => `${x[0]}:${x[1]}`).join(', ') || '-'}   out: ${n.outs.map((x) => `${x[0]}:${x[1]}`).join(', ') || '-'}` }));
  pad = notesPane;
  // the note is the comment right above the block in the song (a note from before is shown until it is saved here once)
  const header = S.doc?.blocks.find((b) => b.name === n.id);
  const inSong = header && !S.moduleStack.length;
  const nt = el('textarea', { rows: 3, value: (inSong && noteAbove(S.doc.text, header.line)) || S.notes[n.id] || '', placeholder: inSong ? 'notes about this block (kept as comment lines above it in the song)...' : 'notes about this block (saved in the project)...' });
  nt.onchange = async () => {
    if (!inSong) { S.notes[n.id] = nt.value; saveNotes(); return; }
    const line = S.doc.blocks.find((b) => b.name === n.id)?.line;
    if (line === undefined) return;
    const doc = await applyText(withNoteAbove(S.doc.text, line, nt.value));
    if (doc && S.notes[n.id]) { delete S.notes[n.id]; saveNotes(); }
  };
  pad.append(nt);
  const r2 = el('div', { className: 'row' });
  if (n.tpl === 'lyrics') {
    r2.append(el('button', { textContent: '… Edit lyrics', className: 'primary', title: 'The canonical timed source used by linked voice and ACE music blocks', onclick: () => openLyrics(n) }));
    pad.append(el('p', { className: 'note', textContent: 'Lyrics editor file = the canonical words and timing.' }));
  }
  if (n.tpl === 'ai-voice') pad.append(el('p', { className: 'note', textContent: 'AI voice is vocal-only: Vocal direction describes the performance, linked lyrics provide the words, optional melody is sent as MIDI conditioning, and Mood / scene supplies reusable style.' }));
  if (n.tpl === 'ace-music') pad.append(el('p', { className: 'note', textContent: 'ACE music may create accompaniment. Lyrics are optional: omit them for instrumental music; connect them for a full song.' }));
  const available = [
    ...(Object.keys(params).length ? [['parameters', 'Parameters']] : []),
    ...(n.ins.length || n.outs.length ? [['ports', 'Ports']] : []), ['notes', 'Notes'], ['info', 'Info'],
    ...(stepPane ? [['steps', 'Steps']] : []),
  ];
  let remembered = n.tpl === 'drums' || n.tpl === 'guitar' ? 'steps' : 'parameters';
  try { remembered = localStorage.getItem(`ample.props.${n.tpl}`) || remembered; } catch { /* storage may be blocked */ }
  const active = available.some(([key]) => key === remembered) ? remembered : available[0][0];
  const choose = (key) => {
    tabs.querySelectorAll('[role=tab]').forEach((button) => {
      const on = button.dataset.key === key;
      button.classList.toggle('active', on);
      button.setAttribute('aria-selected', String(on));
      button.tabIndex = on ? 0 : -1;
    });
    Object.entries(panes).forEach(([name, section]) => section.classList.toggle('active', name === key));
    try { localStorage.setItem(`ample.props.${n.tpl}`, key); } catch { /* storage may be blocked */ }
  };
  available.forEach(([key, label]) => {
    const button = el('button', { type: 'button', role: 'tab', textContent: label, 'aria-selected': String(key === active), tabIndex: key === active ? 0 : -1 });
    button.dataset.key = key;
    button.onclick = () => choose(key);
    button.onkeydown = (event) => {
      const index = available.findIndex(([tab]) => tab === key);
      const next = event.key === 'ArrowRight' ? (index + 1) % available.length : event.key === 'ArrowLeft' ? (index + available.length - 1) % available.length : event.key === 'Home' ? 0 : event.key === 'End' ? available.length - 1 : -1;
      if (next >= 0) { event.preventDefault(); const target = tabs.querySelectorAll('[role=tab]')[next]; choose(available[next][0]); target.focus(); }
    };
    tabs.append(button);
  });
  Object.values(panes).forEach((section) => deck.append(section));
  box.append(tabs, deck);
  choose(active);
  box.append(el('h4', { textContent: 'Management', className: 'prop-management', style: 'padding-left:0;margin:8px 0 2px' }));
  pad = el('div', { className: 'prop-actions row' });
  r2.append(el('button', { textContent: 'Build this block', title: 'Build this block with what is in the fields now', onmousedown: (event) => event.preventDefault(), onclick: async () => { document.activeElement?.blur?.(); await editsDone(); await build(n.id); } }), ...(['ace-music', 'ai-voice'].includes(n.tpl) ? [el('button', { textContent: 'Force full render', title: 'Run ACE now, even when this exact render is cached', onmousedown: (event) => event.preventDefault(), onclick: async () => { document.activeElement?.blur?.(); await editsDone(); await build(n.id, null, null, true); } })] : []), el('button', { textContent: 'Source', onclick: () => gotoSource(n) }), el('button', { textContent: 'Delete', onclick: deleteSelected, disabled: ro }));
  pad.append(r2);
  box.append(pad);
}

/** From the build log to the block that failed: the node editor, the block selected and in view. */
async function showFailedBlock(name) {
  const { setView } = await import('./views.js');
  const { focusNode } = await import('./canvas.js');
  const { current, notify, select } = await import('./store.js');
  if (!setView('blocks')) return;
  select(name);
  notify();
  const node = current().nodes.find((n) => n.id === name);
  if (node) focusNode(node); else toast(`The block ${name} is not in the view you are in (it may be inside a group or a module).`);
}

/* ---------- bottom panel: source and log ---------- */
let buildLogFollowsTop = true;
let buildLogScrollWired = false;
let activeLogTab = 'build';
export function renderBottom() {
  const logBox = $('#blog');
  const projectLogBox = $('#plog');
  const songLogBox = $('#slog');
  const logActions = $('#log-actions');
  const selectLogTab = () => {
    logBox.hidden = activeLogTab !== 'build';
    projectLogBox.hidden = activeLogTab !== 'project';
    songLogBox.hidden = activeLogTab !== 'song';
    logActions.hidden = activeLogTab !== 'build';
    document.querySelectorAll('[data-log-tab]').forEach((item) => item.classList.toggle('on', item.dataset.logTab === activeLogTab));
  };
  document.querySelectorAll('[data-log-tab]').forEach((button) => {
    button.onclick = () => { activeLogTab = button.dataset.logTab; selectLogTab(); };
  });
  selectLogTab();
  const clearLog = $('#blog-clear'), exportLog = $('#blog-export');
  if (clearLog && !clearLog.dataset.wired) {
    clearLog.dataset.wired = '1';
    clearLog.onclick = () => { S.build.log.length = 0; notify(); };
    exportLog.onclick = () => {
      const text = [...S.build.log].map((entry) => {
        if (typeof entry === 'string') return entry;
        const time = new Date(entry.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
        const elapsed = entry.elapsedMs == null ? '' : ` +${(entry.elapsedMs / 1000).toFixed(2)}s`;
        return `${time}${elapsed}  ${entry.message}`;
      }).join('\n');
      const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
      const link = el('a', { href: url, download: `${(S.project?.name || 'ample').replace(/[^\w.-]+/g, '-')}-build-log.txt` });
      link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    };
  }
  if (!buildLogScrollWired) {
    logBox.addEventListener('scroll', () => { buildLogFollowsTop = logBox.scrollTop <= 2; }, { passive: true });
    buildLogScrollWired = true;
  }
  const oldHeight = logBox.scrollHeight, oldTop = logBox.scrollTop, wasFollowing = buildLogFollowsTop;
  const buildNumbers = new Map();
  let buildCount = 0;
  S.build.log.forEach((entry) => { if (/^Build started\b/.test(typeof entry === 'string' ? entry : entry.message)) buildNumbers.set(entry, ++buildCount); });
  const newestFirst = [...S.build.log].reverse();
  logBox.replaceChildren(...newestFirst.flatMap((entry, position) => {
    const line = typeof entry === 'string' ? entry : entry.message;
    const name = /^([^:\s]+): (?:failed|skipped)/.exec(line)?.[1];
    const mark = name && S.buildErrors?.items?.[name];
    const row = el('div', { className: 'build-log-row' });
    if (name) row.dataset.block = name;
    const time = typeof entry === 'string' ? '' : new Date(entry.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    const elapsed = typeof entry === 'string' || entry.elapsedMs == null ? '' : `${(entry.elapsedMs / 1000).toFixed(2)}s`;
    row.append(el('span', { className: 'build-log-time', textContent: time }), el('span', { className: 'build-log-elapsed', textContent: elapsed }), el('span', { className: 'build-log-message', textContent: line || ' ' }));
    if (/\bfailed\b|Build failed/i.test(line)) row.classList.add('failed');
    else if (/\bskipped\b/i.test(line)) row.classList.add('skipped');
    else if (/Build (started|done)|complete/i.test(line)) row.classList.add('milestone');
    if (mark) {
      row.classList.add('logerr');
      row.title = 'Open the code at the line this error points to';
      row.onclick = () => import('./source.js').then((m) => m.showBuildError(name));
      const go = el('button', { className: 'logblock', textContent: 'Show block', title: `Go to the block ${name} in the node editor` });
      go.onclick = (event) => { event.stopPropagation(); showFailedBlock(name).catch((error) => toast(error.message || String(error))); };
      row.querySelector('.build-log-message').append(' ', go);
    }
    // the first line of a build is the last one on the screen: a splitter under it separates it from the build before
    if (buildNumbers.has(entry) && position < newestFirst.length - 1) {
      row.classList.add('build-start');
      return [row, el('div', { className: 'build-log-split', role: 'separator', 'aria-label': `Build ${buildNumbers.get(entry)} starts above this line` }, [el('span', { textContent: `build ${buildNumbers.get(entry)}` })])];
    }
    return [row];
  }));
  if (wasFollowing) logBox.scrollTop = 0;
  else logBox.scrollTop = oldTop + logBox.scrollHeight - oldHeight;
  projectLogBox.replaceChildren(...[...(S.projectLog || [])].slice().reverse().map((entry) => {
    const row = el('div', { className: `project-log-row ${entry.state || ''}` });
    const time = new Date(entry.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    row.append(el('span', { className: 'build-log-time', textContent: time }), el('span', { className: 'project-log-state', textContent: entry.state || '' }), el('span', { className: 'build-log-message', textContent: entry.step === entry.state ? entry.block : `${entry.block}: ${entry.step}` }));
    return row;
  }));
  const diagnostics = [...(S.songDiagnostics || S.doc?.diagnostics || [])]
    .filter((d) => ['error', 'warning', 1, 2].includes(d.severity))
    .sort((a, b) => {
      const line = (a.range?.start.line ?? a.line - 1) - (b.range?.start.line ?? b.line - 1);
      return line || (a.range?.start.character ?? a.col - 1) - (b.range?.start.character ?? b.col - 1);
    });
  songLogBox.replaceChildren();
  if (!diagnostics.length) songLogBox.append(el('div', { className: 'song-log-empty', textContent: S.path ? 'No warnings or errors in the current song.' : 'Open a song to see its warnings and errors.' }));
  diagnostics.forEach((diagnostic) => {
    const error = diagnostic.severity === 'error' || diagnostic.severity === 1;
    const start = diagnostic.range?.start;
    const line = start ? start.line + 1 : diagnostic.line || 1;
    const col = start ? start.character + 1 : diagnostic.col || 1;
    const row = el('div', { className: `song-diagnostic ${error ? 'error' : 'warning'}`, title: 'Double-click to open this line in Source', tabindex: '0' });
    row.dataset.line = String(line - 1); row.dataset.code = String(diagnostic.code);
    row.append(el('span', { className: 'song-diagnostic-code', textContent: `${error ? '● error' : '▲ warning'}  ${line}:${col}  ${diagnostic.code}` }), el('span', { className: 'song-diagnostic-message', textContent: diagnostic.message }));
    const open = () => import('./source.js').then((source) => source.showSongDiagnostic(diagnostic));
    row.ondblclick = open;
    row.onkeydown = (event) => { if (event.key === 'Enter') { event.preventDefault(); open(); } };
    songLogBox.append(row);
  });
}

export function renderStructure() {
  const box = $('#pane-struct');
  box.innerHTML = '';
  appendHistoryStructure(box);
  box.append(el('h5', { textContent: 'Blocks' }));
  const search = el('input', { type: 'search', placeholder: 'Search blocks..', value: structureQuery, title: 'Search block names and types', 'aria-label': 'Search Structure blocks' });
  const list = el('div', { className: 'structure-block-list' });
  const empty = el('div', { className: 'dim', textContent: 'No matching blocks.', style: 'padding:2px 6px' });
  search.oninput = () => {
    structureQuery = search.value;
    const q = structureQuery.trim().toLowerCase();
    let matches = 0;
    list.querySelectorAll('.blk').forEach((row) => { row.hidden = !row.dataset.search.includes(q); if (!row.hidden) matches++; });
    empty.hidden = matches > 0;
  };
  box.append(search, list);
  const v = current();
  v.nodes.filter((n) => !n.proxy).forEach((n) => {
    const r = el('div', { className: `blk${S.selSet.has(n.id) ? ' sel' : ''}` });
    r.dataset.search = `${n.id} ${n.title} ${n.tpl} ${n.cls}`.toLowerCase();
    r.append(el('span', { textContent: n.isGroup ? '▣' : '▫' }), el('span', { textContent: n.title }), el('span', { className: `cls ${n.cls}`, textContent: n.tpl }));
    r.onclick = () => { select(n.id); notify(); focusNode(n); };
    // a double-click goes to the block in the source (like a double-click on its card); a group or a module has no text of its own
    if (!n.isGroup && !n.isModule) { r.ondblclick = () => gotoSource(n); r.title = 'Double-click: show this block in the source'; }
    list.append(r);
  });
  search.oninput();
  list.append(empty);
  // the problems of the song with their codes (they were part of this pane before the search box came)
  box.append(el('h5', { textContent: 'Problems' }));
  const diags = S.doc ? S.doc.diagnostics : [];
  if (!diags.length) box.append(el('div', { className: 'dim', textContent: 'none', style: 'padding:2px 6px' }));
  diags.forEach((d) => box.append(el('div', { className: `seerr ${d.severity}`, textContent: `${d.code} ${d.line + 1}:${d.col + 1} ${d.message}` })));
}

export { fit, selectAll, up, container, flat };
