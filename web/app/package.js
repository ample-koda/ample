// The .ampletx package: the export dialog (Full or Derived, with the impact before anything is written), the read-only viewer of a package, and the import dialog
// (the differences with this environment, how every file is matched by SHA-256, and the choice: import anyway, try to convert, create a new project, or do not import).
// The dialogs are wide and fixed in height: a head, an area that scrolls and a foot with the buttons that never leaves the screen; long names are cut with their full text in the tooltip.
// @ample:package

import { api } from './api.js';
import { S, reloadDoc } from './store.js';
import { saveEverything } from './savecontrol.js';
import { el, toast } from './ui.js';

const mb = (bytes) => (bytes >= 1e6 ? `${(bytes / 1e6).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1e3))} kB`);
const short = (sha) => (sha ? String(sha).replace('sha256:', '').slice(0, 8) : '');
const say = (error) => toast(`${error.code ? `${error.code}: ` : ''}${error.message || error}`);
const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;
const refreshFiles = () => import('./projectpanel.js').then((m) => m.refreshProjectPanel()).catch(() => {});

/** The Derived-export markers in the text of a song: blocks that are a boundary and sources that are always included as they are. */
export function packageMarks(text) {
  const bake = new Set(), asIs = new Set();
  for (const line of String(text || '').split('\n')) {
    const m = /^#\s*export\s+(bake|as-is)\s+(\S+)\s*$/.exec(line.trim());
    if (m) (m[1] === 'bake' ? bake : asIs).add(m[2]);
  }
  return { bake, asIs };
}

/** Sets or clears a marker of a block in the working copy of the song (it is saved with the song, like any change). */
export async function markBlock(block, kind) {
  try { await api.post('/api/tx/mark', { path: S.path, block, kind }); await reloadDoc(); } catch (error) { say(error); }
}

/** Whether a block type reads files of its own (so it can be an "always include as-is" source). */
export const readsFiles = (type) => { const t = S.tpl && S.tpl[type]; return !!t && (t.parameters || []).some((p) => p.kind === 'file' || p.name === 'kit' || p.name === 'set'); };

/** Whether a block makes audio that can be rendered (so it can be a Derived boundary). */
export const makesAudio = (type) => { const t = S.tpl && S.tpl[type]; return !!t && (t.outputs || []).some((o) => o.type === 'audio'); };

/** The menu entries of a block for the Derived export: a boundary ("bake to here") and, for a source, "always include as-is". */
export function derivedMenuItems(node) {
  const marks = packageMarks(S.doc?.text);
  const items = [];
  if (makesAudio(node.type)) {
    const on = marks.bake.has(node.id);
    items.push({ label: on ? 'Remove the Derived export boundary' : 'Bake to here for Derived export', title: 'In a Derived .ampletx export the rendered output of this block becomes the new audio source of this path; what comes before it is baked into audio and no longer editable after an import. An explicit boundary beats the automatic one.', fn: () => markBlock(node.id, on ? 'none' : 'bake') });
  }
  if (readsFiles(node.type)) {
    const on = [...marks.asIs].some((m) => m === node.id || m.startsWith(`${node.id}.`));
    items.push({ label: on ? 'Stop including as-is in Derived exports' : 'Always include as-is in Derived exports', title: 'A Derived export keeps this source with its files instead of replacing it by rendered audio.', fn: () => markBlock(node.id, on ? 'none' : 'as-is') });
  }
  return items;
}

/** The small marks on a block that has a Derived export marker. */
export function derivedChips(id) {
  const marks = packageMarks(S.doc?.text);
  const chips = [];
  if (marks.bake.has(id)) chips.push(el('span', { className: 'tx-mark bake', textContent: '↧ boundary', title: 'Derived export boundary: the output of this block becomes an audio input in a Derived package' }));
  if ([...marks.asIs].some((m) => m === id || m.startsWith(`${id}.`))) chips.push(el('span', { className: 'tx-mark asis', textContent: 'as-is', title: 'Always included as-is in Derived exports' }));
  return chips;
}

// ---- building blocks of the dialogs ----

function dialogBox(title, id) {
  document.getElementById(id)?.remove();
  const close = el('button', { type: 'button', className: 'tx-x', textContent: '✕', title: 'Close (Esc)', 'aria-label': 'Close' });
  const body = el('div', { className: 'tx-body' });
  const foot = el('div', { className: 'tx-foot' });
  const box = el('dialog', { id, className: 'tx-dialog', 'aria-label': title }, [el('div', { className: 'tx-head' }, [el('h2', { textContent: title }), el('span', { className: 'sp' }), close]), body, foot]);
  close.onclick = () => box.close();
  box.addEventListener('close', () => box.remove());
  document.body.append(box);
  box.showModal();
  return { box, body, foot };
}

const chip = (text, kind = '') => el('span', { className: `tx-chip ${kind}`, textContent: text });
const section = (title, ...kids) => el('div', { className: 'tx-sec' }, [el('h3', { textContent: title }), ...kids]);
const tile = (label, value, sub = '') => el('div', { className: 'tx-tile' }, [el('div', { className: 'tx-tile-label', textContent: label }), el('div', { className: 'tx-tile-value', textContent: value }), el('div', { className: 'tx-tile-sub', textContent: sub })]);
const facts = (pairs) => el('dl', { className: 'tx-dl' }, pairs.flatMap(([k, v]) => [el('dt', { textContent: k }), el('dd', { textContent: String(v ?? '') })]));
const cut = (value, title) => el('span', { className: 'tx-cut', textContent: value, title: title || value });

/** A table with fixed column widths: one line per row, long text is cut and shown in full as a tooltip. */
function grid(columns, rows, cls = '') {
  const group = el('colgroup', {}, columns.map((c) => el('col', { style: c.width && c.width !== 'auto' ? `width:${c.width}` : '' })));
  const head = el('thead', {}, [el('tr', {}, columns.map((c) => el('th', { textContent: c.label, title: c.title || '' })))]);
  const bodyRows = rows.map((r) => el('tr', {}, r.map((cell, i) => el('td', { className: columns[i].cls || '' }, [cell instanceof Node ? cell : cut(String(cell ?? ''))]))));
  return el('table', { className: `tx-grid ${cls}` }, [group, head, el('tbody', {}, bodyRows)]);
}

/** Buttons that switch between panes. */
function segmented(names, onPick) {
  const bar = el('div', { className: 'tx-seg' });
  for (const [key, label] of names) bar.append(el('button', { type: 'button', textContent: label, 'data-tab': key, onclick: () => { for (const b of bar.children) b.classList.toggle('on', b.getAttribute('data-tab') === key); onPick(key); } }));
  bar.firstChild?.classList.add('on');
  return bar;
}

// ---- export ----

export function openExportDialog() {
  if (!S.path) { toast('Open a song first.'); return; }
  const { box, body, foot } = dialogBox('Export a package (.ampletx)', 'tx-export');
  body.classList.add('two');
  const state = { mode: 'full', master: true, compress: false, whole: false, flac: false, flacApproval: null, name: '' };
  const preview = el('div', { id: 'tx-preview', className: 'tx-preview' });
  const result = el('div', { id: 'tx-result' });
  const exportButton = el('button', { id: 'tx-do-export', type: 'button', className: 'primary', textContent: 'Export package', disabled: true });
  const radio = (value, label, hint) => el('label', { className: 'tx-opt' }, [el('input', { type: 'radio', name: 'tx-mode', value, checked: state.mode === value, onchange: () => { state.mode = value; load(); } }), el('span', {}, [el('b', { textContent: label }), el('small', { textContent: hint })])]);
  const check = (key, label, hint, id) => el('label', { className: 'tx-opt' }, [el('input', { id, type: 'checkbox', checked: state[key], onchange: (e) => { state[key] = e.target.checked; load(); } }), el('span', {}, [el('b', { textContent: label }), el('small', { textContent: hint })])]);
  const flacOption = check('flac', 'Store the audio inputs as FLAC (Derived only)', 'Smaller package. FLAC is lossless in principle, but there is NO guarantee that the audio stays identical. Before the package is written you hear the result before (WAV) and after (FLAC) and must approve it.', 'tx-opt-flac');
  const nameInput = el('input', { id: 'tx-name', type: 'text', placeholder: `${S.path.replace(/\.amplet$/, '')}-full`, 'aria-label': 'File name of the package', oninput: (e) => { state.name = e.target.value.trim(); } });
  const aside = el('aside', { className: 'tx-aside' }, [
    el('p', { className: 'tx-lead', textContent: `${S.path}` }),
    section('Kind of package', radio('full', 'Full', 'Everything needed to build the song again, source files included.'), radio('derived', 'Derived', 'Parts of the graph are rendered to audio and become new audio inputs; fewer source files, and what is baked cannot be edited after an import.')),
    section('Options',
      check('master', 'Include final master as preview', 'The finished render of exactly this saved state, for instant playback and as a reference.', 'tx-opt-master'),
      check('compress', 'Compress package', 'Also put the package in a zip (the same file inside).', 'tx-opt-compress'),
      check('whole', 'Include the whole sets', 'Whole sample sets and soundfonts. Off: only what this song plays.', 'tx-opt-whole'),
      flacOption),
    section('File name', nameInput, el('small', { className: 'tx-hint', textContent: 'Goes in the folder "exports" of the project. An existing package is never overwritten.' })),
  ]);
  const main = el('div', { className: 'tx-main' }, [preview, result]);
  body.append(aside, main);
  foot.append(exportButton, el('button', { type: 'button', textContent: 'Close', onclick: () => box.close() }), el('span', { className: 'note', textContent: 'A package is made from the saved song only. Nothing in the project is changed.' }));

  let loadSeq = 0;
  async function load() {
    const seq = ++loadSeq;
    state.flacApproval = null;
    if (state.mode !== 'derived') { state.flac = false; flacOption.querySelector('input').checked = false; }
    flacOption.style.display = state.mode === 'derived' ? '' : 'none';
    exportButton.textContent = state.flac ? 'Listen before and after...' : 'Export package';
    exportButton.disabled = true;
    result.textContent = '';
    nameInput.placeholder = `${S.path.replace(/\.amplet$/, '')}-${state.mode}`;
    preview.replaceChildren(el('p', { className: 'tx-busy', textContent: 'Working out what goes in... (the song is built once, which can take a moment)' }));
    try {
      const r = await api.get(`/api/tx/export/preview?path=${encodeURIComponent(S.path)}&mode=${state.mode}&master=${state.master}&compress=${state.compress}&whole=${state.whole}`);
      if (seq !== loadSeq) return;
      preview.replaceChildren(...renderImpact(r));
      exportButton.disabled = false;
    } catch (error) {
      if (seq !== loadSeq) return;
      const notSaved = error.code === 'AMP5430';
      preview.replaceChildren(el('div', { className: 'tx-error', id: 'tx-error' }, [
        el('b', { textContent: notSaved ? 'Save the song first' : 'This song cannot be exported yet' }),
        el('p', { textContent: `${error.code ? `${error.code}: ` : ''}${error.message || error}` }),
        notSaved ? el('button', { id: 'tx-save-first', type: 'button', className: 'primary', textContent: 'Save now', onclick: async () => { await saveEverything(); load(); } }) : el('span'),
      ]));
    }
  }

  async function listenTest() {
    exportButton.disabled = true;
    result.replaceChildren(el('p', { className: 'tx-busy', textContent: 'Rendering the result twice, with the original audio inputs and with the audio inputs that went through FLAC...' }));
    try {
      const t = await api.post('/api/tx/flac/preview', { path: S.path, wholeSets: state.whole });
      const player = (label, which) => el('div', { className: 'tx-listen' }, [el('b', { textContent: label }), el('audio', { controls: true, preload: 'metadata', id: `tx-flac-${which}`, src: `/api/tx/flac/audio?id=${t.id}&which=${which}` })]);
      const lossy = t.stems.filter((x) => x.supported && !x.identicalFile);
      result.replaceChildren(el('div', { className: 'tx-flac', id: 'tx-flac-test' }, [
        el('b', { textContent: t.mastersIdentical ? 'The two results are byte for byte the same' : 'The two results are not byte for byte the same: listen' }),
        el('p', { className: 'tx-hint', textContent: `${t.stems.length} audio input(s): ${t.stems.filter((x) => x.supported && x.identicalFile).length} came back identical, ${lossy.length} with a difference${lossy.filter((x) => x.identicalSamples).length ? ` (${lossy.filter((x) => x.identicalSamples).length} only in the file header)` : ''}, ${t.stems.filter((x) => !x.supported).length} cannot be stored as FLAC and stay WAV. There is no guarantee of equality.` }),
        player('Before (WAV)', 'before'), player('After (FLAC)', 'after'),
        el('div', { className: 'tx-buttons' }, [el('button', { id: 'tx-flac-approve', type: 'button', className: 'primary', textContent: 'I listened: use FLAC and write the package', onclick: async () => { await api.post('/api/tx/flac/approve', { id: t.id }); state.flacApproval = t.id; await doExport(); } }), el('button', { type: 'button', textContent: 'No, keep WAV', onclick: () => { state.flac = false; flacOption.querySelector('input').checked = false; result.textContent = ''; load(); } })]),
      ]));
    } catch (error) { result.replaceChildren(el('div', { className: 'tx-error' }, [el('b', { textContent: 'The listening test did not finish' }), el('p', { textContent: `${error.code ? `${error.code}: ` : ''}${error.message}` })])); exportButton.disabled = false; }
  }

  exportButton.onclick = async () => {
    if (state.flac && !state.flacApproval) { await listenTest(); return; }
    await doExport();
  };

  async function doExport() {
    exportButton.disabled = true;
    result.replaceChildren(el('p', { className: 'tx-busy', textContent: 'Writing the package...' }));
    try {
      const r = await api.post('/api/tx/export', { path: S.path, mode: state.mode, includeMaster: state.master, compress: state.compress, wholeSets: state.whole, flac: state.flac, flacApproval: state.flacApproval || undefined, name: state.name || undefined });
      result.replaceChildren(el('div', { className: 'tx-done', id: 'tx-done' }, [
        el('b', { textContent: 'The package is ready' }),
        facts([['File', r.path], ['Size', mb(r.size)], ['SHA-256', r.sha256], ['Final master inside', r.masterIncluded ? 'yes' : 'no'], ['Files inside', r.entries], ...(r.zip ? [['Compressed copy', `${r.zip} (${mb(r.zipSize)})`]] : [])]),
        el('div', { className: 'tx-buttons' }, [el('button', { type: 'button', className: 'primary', textContent: 'Open it read-only', onclick: () => { box.close(); openPackageViewer(r.path); } })]),
      ]));
      result.scrollIntoView({ block: 'nearest' });
      refreshFiles();
      exportButton.disabled = false;   // another package (with another name) can follow
    } catch (error) { result.replaceChildren(el('div', { className: 'tx-error' }, [el('b', { textContent: 'The export did not happen' }), el('p', { textContent: `${error.code ? `${error.code}: ` : ''}${error.message}` })])); exportButton.disabled = false; }
  }
  load();
}

function renderImpact(r) {
  const out = [];
  const sourceBytes = r.assets.reduce((n, a) => n + a.size, 0);
  out.push(el('div', { className: 'tx-tiles' }, [
    tile('Package', r.mode === 'full' ? 'Full' : 'Derived', r.mode === 'full' ? 'self-contained' : `${plural(r.derived.paths.length, 'audio input', 'audio inputs')} rendered`),
    tile('Source files', String(r.assets.length), mb(sourceBytes) + (r.leftOut?.files ? ` · ${r.leftOut.files} unplayed samples left out` : '')),
    tile('About', mb(r.approximateBytes), 'as text, with the Base64 overhead'),
    tile('Final master', r.master.included ? 'in' : r.includeMaster ? 'none' : 'off', r.master.included ? 'current for this saved state' : ''),
  ]));
  const warnings = r.warnings.filter((w) => !w.includes('is not part of the final result'));
  if (warnings.length) out.push(el('div', { className: 'tx-warn', id: 'tx-warnings' }, [el('b', { textContent: 'Export warnings' }), el('ul', {}, warnings.map((w) => el('li', { textContent: w })))]));
  const panes = {};
  const area = el('div', { className: 'tx-pane' });
  const names = [['files', `Files (${r.assets.length})`]];
  if (r.derived) names.unshift(['paths', `Audio paths (${r.derived.paths.length})`]);
  names.push(['captured', 'Captured settings']);
  panes.files = filesPane(r);
  panes.captured = facts([['The song', `${r.song}, saved; ${r.build.blocksInFinalResult} of ${r.build.blocksInSong} blocks are part of the final result`], ['Project settings', 'tempo, master seed, features, plugin settings, render settings (sample rate, bit depth, resampler)'], ['Versions', 'AMPLE with a hash of the program, the version of every block type and renderer, plugins and models with their hashes'], ['Fingerprint', 'a SHA-256 of the settings and of every file, so a later import can tell what differs'], ['Not included', 'interface preferences and anything that does not change the sound']]);
  if (r.derived) panes.paths = derivedPane(r);
  const show = (key) => area.replaceChildren(panes[key]);
  out.push(segmented(names, show), area);
  show(names[0][0]);
  return out;
}

function filesPane(r) {
  const left = r.leftOut && r.leftOut.files > 0 ? el('p', { className: 'tx-hint', textContent: `${plural(r.leftOut.files, 'sample file', 'sample files')} (${mb(r.leftOut.bytes)}) of the sample sets are never played by this song and stay out. Switch on "Include the whole sets" to put them in.` }) : el('span');
  return el('div', {}, [left, grid([{ label: 'File', width: 'auto' }, { label: 'What goes in', width: '250px' }, { label: 'Size', width: '78px', cls: 'num' }, { label: 'SHA-256', width: '84px', cls: 'mono' }, { label: 'Used by', width: '150px' }], r.assets.map((a) => [a.path, a.selection === 'needed' ? (a.presets ? `needed part only: ${a.presets.map((p) => p.replace(/^\d+:\d+ /, '')).join(', ')} (of ${mb(a.originalSize)})` : 'needed') : a.whole ? `whole (${a.whole})` : 'whole', mb(a.size), short(a.sha256), a.usedBy.join(', ')]), 'tx-files')]);
}

function derivedPane(r) {
  const d = r.derived;
  const marks = [...r.marks.bake].map((b) => `bake ${b}`).concat(r.marks.asIs.map((b) => `as-is ${b}`));
  const rows = d.paths.map((p) => {
    const boundary = p.boundary.startsWith('none') ? 'source' : p.boundary;
    return el('details', { className: `tx-path ${p.identity ? 'identity' : ''}`, 'data-asset': p.generatedAsset }, [
      el('summary', {}, [
        el('b', { className: 'tx-cut', textContent: p.generatedAsset.replace('derived/', ''), title: p.generatedAsset }),
        chip(boundary === 'source' ? 'straight from the source' : `${boundary} boundary`, boundary),
        p.identity ? chip('untransformed', 'warn') : el('span'),
        el('span', { className: 'tx-sum' }, [cut(`at ${p.boundaryBlock}`), cut(`bakes ${plural(p.bakedBlocks.length, 'block', 'blocks')}`), cut(`${plural(p.editableDownstream.length, 'block', 'blocks')} stay editable`), cut(`${p.seconds} s · ${mb(p.size)}`)]),
      ]),
      facts([['Original source', p.origin], ['Original source embedded', p.originalSourceEmbedded ? 'yes' : 'no'], ['Boundary', `${boundary === 'source' ? 'none: rendered straight from the source' : boundary} — the output of ${p.boundaryBlock}`], ['Baked into the audio (no longer editable)', p.bakedBlocks.join(', ')], ['Stays editable downstream', p.editableDownstream.join(', ')], ['Full upstream reconstruction possible after import', p.fullUpstreamReconstructionAfterImport ? 'yes' : 'no'], ['Always include as-is override', p.includeAsIsOverride ? 'yes' : 'no'], ['Generated asset', `${p.generatedAsset} · SHA-256 ${short(p.sha256)}`]]),
    ]);
  });
  const kept = d.keptSources.map((k) => el('details', { className: 'tx-path asis', 'data-source': k.block }, [
    el('summary', {}, [el('b', { className: 'tx-cut', textContent: k.block }), chip(k.includeAsIsOverride ? 'always included as-is' : 'included as-is (no boundary replaces it)', 'asis'), el('span', { className: 'tx-sum' }, [cut('original source embedded: yes'), cut(plural(k.files.length, 'file', 'files')), cut(`override: ${k.includeAsIsOverride ? 'yes' : 'no'}`)])]),
    el('p', { className: 'tx-hint', textContent: 'The source stays a live source with its files after the import; only the paths listed above are replaced by audio.' }),
  ]));
  return el('div', {}, [
    el('p', { className: 'tx-hint', textContent: `Markers in the song: ${marks.join(', ') || 'none (automatic boundaries only)'}. An explicit boundary beats the automatic one; the automatic one is the earliest meaningful transformation after a source. Click a row for the details.` }),
    ...rows, ...kept,
    el('p', { className: 'tx-hint', textContent: `Editable after an import: ${d.editableBlocks.join(', ')}. Left out of the package: ${plural(d.droppedBlocks.length, 'block', 'blocks')} (baked into the audio inputs or not part of the result).` }),
  ]);
}

// ---- the viewer ----

export async function openPackageViewer(path) {
  let info;
  try { info = await api.get(`/api/tx/inspect?path=${encodeURIComponent(path)}`); } catch (error) { say(error); return; }
  const { box, body, foot } = dialogBox(info.file, 'tx-viewer');
  const m = info.project || {};
  const panes = {};
  const area = el('div', { className: 'tx-pane' });
  const master = info.masterAvailable ? el('div', { className: 'tx-master', id: 'tx-master' }, [
    el('b', { textContent: 'Final master preview' }),
    el('audio', { controls: true, preload: 'metadata', src: `/api/tx/master?path=${encodeURIComponent(path)}`, id: 'tx-master-audio' }),
    el('small', { textContent: `${info.master.durationSeconds} s · ${info.master.sampleRate} Hz · ${info.master.channelLayout} · ${info.master.bitDepth} bit · rendered ${info.master.renderedAt?.slice(0, 19).replace('T', ' ')} from the saved state of this package. It plays straight from the package; nothing is built.` }),
  ]) : el('p', { className: 'tx-hint', textContent: 'This package has no master preview (it was made without one). Import it and build to hear it.' });
  const sourceFiles = info.entries.filter((e) => e.role === 'source').length;
  panes.overview = el('div', {}, [
    el('div', { className: 'tx-tiles' }, [
      tile('Package', info.kind === 'full' ? 'Full' : 'Derived', `format ${info.format}`),
      tile('Project', m.name || '', m.song || ''),
      tile('Files', String(info.entries.length), `${sourceFiles} source files · ${mb(info.packageSize)}`),
      tile('Made by', `AMPLE ${info.application?.version || ''}`, `program hash ${short(info.application?.executable?.sha256)}`),
    ]),
    master,
    facts([['Created', info.created?.slice(0, 19).replace('T', ' ')], ['Package SHA-256', info.packageSha256], ['Song SHA-256', m.packageSongSha256], ['Settings fingerprint', info.settingsFingerprint]]),
    info.derived ? section('Derived package', el('p', { className: 'tx-hint', textContent: info.derived.note }), grid([{ label: 'Audio input', width: '210px' }, { label: 'Rendered from', width: '160px' }, { label: 'Boundary', width: '90px' }, { label: 'Baked blocks', width: 'auto' }], info.derived.inputs.map((i) => [i.inputBlock, i.renderedFrom, i.boundary, i.bakedBlocks.join(', ')])), el('p', { className: 'tx-hint', textContent: `Stays editable: ${info.derived.editableBlocks.join(', ')}. Kept sources: ${info.derived.keptSources.join(', ') || 'none'}.` })) : el('span'),
    info.warnings?.length ? el('div', { className: 'tx-warn' }, [el('b', { textContent: 'Warnings from the export' }), el('ul', {}, info.warnings.map((w) => el('li', { textContent: w })))]) : el('span'),
  ]);
  panes.files = grid([{ label: 'File', width: 'auto' }, { label: 'Role', width: '150px' }, { label: 'Size', width: '78px', cls: 'num' }, { label: 'SHA-256', width: '84px', cls: 'mono' }, { label: 'Came from', width: '280px' }], info.entries.map((e) => [e.path, e.role + (e.info?.selection === 'needed' ? ' · needed part' : ''), mb(e.size), short(e.sha256), e.originalPath]), 'tx-files');
  panes.settings = el('pre', { className: 'tx-pre', textContent: JSON.stringify({ settings: info.settings, fingerprint: info.settingsFingerprint }, null, 2) });
  panes.deps = el('pre', { className: 'tx-pre', textContent: JSON.stringify(info.dependencies, null, 2) });
  panes.song = el('pre', { className: 'tx-pre', textContent: info.songText || '' });
  const show = (key) => area.replaceChildren(panes[key]);
  body.append(el('div', { className: 'tx-badges' }, [chip('READ-ONLY', 'readonly'), chip(info.kind === 'full' ? 'FULL' : 'DERIVED', info.kind), chip(`format ${info.format}`)]), segmented([['overview', 'Overview'], ['files', 'Files'], ['settings', 'Settings'], ['deps', 'Dependencies'], ['song', 'Song']], show), area);
  show('overview');
  foot.append(el('button', { id: 'tx-import', type: 'button', className: 'primary', textContent: 'Import...', onclick: () => { box.close(); openImportDialog(path); } }), el('button', { type: 'button', textContent: 'Close', onclick: () => box.close() }), el('span', { className: 'note', textContent: 'Read-only: this file is never changed. Import makes an editable project from it.' }));
}

// ---- import ----

export function openImportDialog(path) {
  const { box, body, foot } = dialogBox('Import a package', 'tx-import-dialog');
  const state = { newFolder: `${S.project?.folder || ''}-imported`, embedded: false, projectName: '', overrides: {} };
  const plan = el('div', { id: 'tx-plan' });
  const result = el('div', { id: 'tx-import-result' });
  const folder = el('input', { id: 'tx-new-folder', type: 'text', value: state.newFolder, 'aria-label': 'Folder of the new project', oninput: (e) => { state.newFolder = e.target.value.trim(); } });
  const embedded = el('input', { id: 'tx-prefer-embedded', type: 'checkbox', onchange: (e) => { state.embedded = e.target.checked; load(); } });
  const buttons = foot;
  foot.id = 'tx-import-buttons';
  body.append(
    el('p', { className: 'tx-lead', textContent: `${path} is only read. Nothing is written until you choose below, and nothing that exists is overwritten.` }),
    el('label', { className: 'tx-opt wide' }, [embedded, el('span', {}, [el('b', { textContent: 'Use the files in the package' }), el('small', { textContent: 'Even when an installed pack has the same content.' })])]),
    plan, section('Folder for "Create new project from package"', folder), result);

  let current = null;
  async function load() {
    plan.replaceChildren(el('p', { className: 'tx-busy', textContent: 'Comparing the package with this AMPLE and with the files of the project...' }));
    try { current = await api.post('/api/tx/import/plan', { path, mode: 'anyway', preferEmbedded: state.embedded, overrides: state.overrides }); } catch (error) { plan.replaceChildren(el('div', { className: 'tx-error' }, [el('b', { textContent: 'The package cannot be read' }), el('p', { textContent: error.message })])); return; }
    plan.replaceChildren(...renderPlan(current, (file, value) => { state.overrides[file] = value; load(); }));
    drawButtons();
  }

  const apply = async (mode) => {
    result.replaceChildren(el('p', { className: 'tx-busy', textContent: 'Importing...' }));
    try {
      const r = await api.post('/api/tx/import/apply', { path, mode, newFolder: mode === 'new-project' ? state.newFolder : undefined, preferEmbedded: state.embedded, projectName: state.projectName || undefined, overrides: state.overrides });
      result.replaceChildren(el('div', { className: 'tx-done', id: 'tx-imported' }, [
        el('b', { textContent: r.newProject ? 'A new project was made' : 'Imported into this project' }),
        facts([['Project', r.project], ['Song', r.song], ['Files written', r.writtenCount], ['Relinked by SHA-256', r.relinked.length], ['Identical, already there', r.identicalAlreadyThere], ...(r.flacDifferingFromOriginal?.length ? [['FLAC audio that differs from the original', r.flacDifferingFromOriginal.join(', ')]] : []), ['Import record', r.record]]),
        r.referenceChanges.length ? el('div', {}, [el('b', { textContent: 'Changed in the song' }), el('ul', {}, r.referenceChanges.map((c) => el('li', { textContent: `${c.from} → ${c.to}` })))]) : el('span'),
        r.differences.length ? el('div', { className: 'tx-warn' }, [el('b', { textContent: 'Differences that remain (kept in the import record)' }), el('ul', {}, r.differences.map((d) => el('li', { textContent: `${d.severity}: ${d.message}` })))]) : el('span'),
        r.newProject ? el('button', { type: 'button', className: 'primary', textContent: 'Open the new project', onclick: async () => { try { const o = await api.post('/api/projects/open', { folder: r.project }); window.open(o.url, '_blank'); } catch (error) { say(error); } } }) : el('button', { type: 'button', className: 'primary', textContent: 'Reload the project files', onclick: refreshFiles }),
      ]));
      result.scrollIntoView({ block: 'nearest' });
      buttons.replaceChildren(el('button', { type: 'button', textContent: 'Close', onclick: () => box.close() }));
    } catch (error) { result.replaceChildren(el('div', { className: 'tx-error' }, [el('b', { textContent: 'The import did not happen' }), el('p', { textContent: `${error.code ? `${error.code}: ` : ''}${error.message}` })])); }
  };

  function drawButtons() {
    const o = current.options;
    const remaining = current.differences.filter((d) => d.severity !== 'info');
    const best = () => {
      result.replaceChildren(el('div', { className: 'tx-warn', id: 'tx-best-effort' }, [
        el('b', { textContent: 'Best-effort compatible project' }),
        el('p', { textContent: 'The package is not exactly what this AMPLE would make, so the new project is a best effort: exact where the files are identical, converted where AMPLE has an explicit rule, and the differences below stay. It is not presented as exact.' }),
        el('ul', {}, remaining.map((d) => el('li', { textContent: `${d.severity}: ${d.message} — ${d.impact}` }))),
        el('div', { className: 'tx-buttons' }, [el('button', { id: 'tx-proceed', type: 'button', className: 'primary', textContent: 'Proceed with best-effort compatible project', onclick: () => apply('new-project') }), el('button', { type: 'button', textContent: 'Cancel', onclick: () => result.replaceChildren() })]),
      ]));
      result.scrollIntoView({ block: 'nearest' });
    };
    buttons.replaceChildren(
      el('button', { id: 'tx-new', type: 'button', className: 'primary', textContent: 'Create new project from package', title: 'A new project with a new identity; the package and this project are not changed', onclick: () => (remaining.length ? best() : apply('new-project')) }),
      el('button', { id: 'tx-anyway', type: 'button', textContent: 'Import anyway', disabled: !o.anyway, title: o.anyway ? 'Use this environment as it is and keep a record of the differences' : 'A file of the package would overwrite a different file: choose Try to convert or a new project', onclick: () => apply('anyway') }),
      el('button', { id: 'tx-convert', type: 'button', textContent: 'Try to convert', disabled: !o.convert, title: o.convert ? 'Apply the changes listed under "What would change in the song"' : 'There is nothing to convert: the package fits as it is', onclick: () => apply('convert') }),
      el('button', { id: 'tx-cancel', type: 'button', textContent: 'Do not import', onclick: () => box.close() }),
      el('span', { className: 'note', textContent: o.convert ? 'Try to convert applies only the listed changes.' : 'Nothing to convert: the package fits as it is.' }));
  }
  load();
}

function renderPlan(p, onOverride) {
  const out = [];
  const s = p.summary;
  out.push(el('div', { className: 'tx-tiles' }, [
    tile('Package', p.kind === 'full' ? 'Full' : 'Derived', p.package),
    tile('Identical already', String(s.same), 'same SHA-256 at the same place'),
    tile('Relinked', String(s.relink), 'same SHA-256 elsewhere or in an installed pack'),
    tile('Extracted', String(s.extract), mb(s.extractBytes)),
  ]));
  out.push(section('Compared with this AMPLE', p.differences.length ? el('ul', { className: 'tx-diffs', id: 'tx-differences' }, p.differences.map((d) => el('li', { className: d.severity }, [chip(d.severity, d.severity), el('span', { textContent: d.message }), el('small', { textContent: d.impact })]))) : el('p', { className: 'tx-hint', textContent: 'No differences found: the formats, block versions, renderers and plugins of the package are the ones of this AMPLE.' })));
  if (p.conversions.length) out.push(section('What would change in the song (Try to convert)', el('ul', { id: 'tx-conversions' }, p.conversions.map((c) => el('li', { textContent: c.description })))));
  out.push(section(`Files (${p.assets.length})`, grid([{ label: 'File in the package', width: 'auto' }, { label: 'Stored original', width: '210px', title: 'The file name and path the package remembers' }, { label: 'What happens', width: '190px' }, { label: 'Where it will be', width: '270px', title: 'You can change the place of an extraction before anything is written' }, { label: 'SHA-256', width: '84px', cls: 'mono' }, { label: 'Size', width: '78px', cls: 'num' }], p.assets.map((a) => [a.path, `${a.originalName} · ${a.originalPath}`, actionLabel(a), a.action === 'extract' || a.action === 'relocate' ? el('input', { type: 'text', className: 'tx-dest', value: a.destination, title: a.destination, 'aria-label': `Where to extract ${a.path}`, onchange: (e) => onOverride(a.path, e.target.value.trim()) }) : a.action === 'pack-installed' ? a.originalPath : a.action === 'relink' ? a.foundAt : a.destination, short(a.sha256), mb(a.size)]), 'tx-files')));
  return out;
}

const actionLabel = (a) => ({ same: 'identical file already there', relink: 'relinked (same SHA-256 elsewhere)', 'pack-installed': 'relinked to the installed pack', extract: 'extracted', relocate: 'extracted elsewhere (place taken)' })[a.action] || a.action;
