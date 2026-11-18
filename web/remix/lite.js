// AMPLE Remix: the page. A file tree, an editor, build and play. Everything is kept on this browser (lite-store.mjs); the server only builds what it is sent.
// Plan: plan/lite/PLAN.md. This file imports nothing of Studio except the token helper (api.js) and the language client (lsp.js).

import { initToken, getToken } from '../app/api.js';
import { buildForm, sendSummary, sizeText, withHashes, checkBuild, describe, neededFiles, pickEntry } from './lite-build.mjs';
import { missingFileDiagnostics } from './lite-missing-files.mjs';
import { CATEGORIES, demoFiles, entryOf, filterDemos, isUnchangedDemo, loadCatalog, shareUrl, sharedDemoId } from './lite-demos.mjs';
import { createLiteEditor } from './lite-editor.js';
import { createRoll } from './lite-roll.js';
import { createPlayer } from './lite-player.js';
import { createRemixListen } from './lite-listen.js';
import { songTemplate } from './lite-templates.mjs';
import { createBuildGate, createSaver } from './lite-save.mjs';
import { findClips, tempoOf } from './lite-roll-source.mjs';
import { createLanguage } from './lite-language.js';
import { setReportEmail, showReportEmail } from './lite-contact.mjs';
import { idbKv, memoryKv, setStoreName, storeName } from './lite-kv.mjs';
import { createImporter } from './lite-import.js';
import { readZip } from './lite-unzip.mjs';
import { createTour } from './lite-tour.js';
import { copyText, initInfo, openManage, openPublish } from './lite-share.js';
import { postWithProgress } from './lite-upload.mjs';
import { DEFAULT_LIMITS, checkFile, cleanPath, extensionOf, isAudio, isComponent, isSong, isText } from './lite-paths.mjs';
import { createLiteStore, LiteStoreError } from './lite-store.mjs';
import { LEVELS, emptyText, inSong, levelCounts, logRows, messageText, whereText } from '../lib/log-view.mjs';

// the public Remix (ample serve --lite-only) says so on its limits and needs no token or sign-in; every other server asks for one as before
const serverLimits = await fetch('/api/remix/limits', { cache: 'no-store' }).then((r) => (r.ok ? r.json() : null)).catch(() => null);
const publicLite = serverLimits?.public === true;
setStoreName(serverLimits?.storeName);
setReportEmail(serverLimits?.reportEmail);
showReportEmail();
if (!publicLite) initToken();
const $ = (selector) => document.querySelector(selector);
const SESSION_PREFIX = `${storeName()}.`;

const app = {
  openSeq: 0, copyUrl: '',
  rev: 0,
  builtRev: -1, builtProject: null, builtFrom: null, kv: null, store: null, limits: DEFAULT_LIMITS,
  project: null, files: [], active: null, editor: null, language: null, catalog: [], collapsed: new Set(),
  texts: new Map(), saveTimer: 0, live: [], built: [], lastBuilt: null, building: false, outputUrl: '', filter: { query: '', category: '' }, binaryUrl: '' };
// the choices of the Song log for the next builds of this page (see renderSongLog)
const songLog = { events: [], entry: null, options: {}, builtWith: '{}', shown: new Set(LEVELS) };

// ---- small helpers

function toast(text, kind = 'info') {
  const box = document.createElement('div');
  box.className = kind;
  box.textContent = text;
  $('#lite-toasts').append(box);
  setTimeout(() => box.remove(), kind === 'error' ? 9000 : 4500);
}
const status = (text) => { $('#lite-status').textContent = text; };
// a test seam: with ?hooks=1 the tests reach the state of the page (they delay the store with it); nothing else reads it
if (new URLSearchParams(location.search).has('hooks')) window.__remixTest = { app };

// Live Preview: listen to the song while you edit it (lite-listen.js); the page gives it its build, its editor and its player
const listen = createRemixListen({
  $,
  build: (options) => build(options),
  getText: () => app.editor?.getText() ?? '',
  hasSong: () => !!app.project && !!pickEntry(app.files.map((f) => f.path), app.active, app.lastBuilt),
  setMarks: (marks) => app.editor?.setPlayMarks?.(marks),
  buildError: () => app.lastBuildError,
  isBuilding: () => gate.busy(),
  abortBuild: () => { gate.abort('listen'); },   // never the build the person started
  activePath: () => app.active,
  entryPath: () => pickEntry(app.files.map((f) => f.path), app.active, app.lastBuilt),
  isSong: (path) => isSong(path),
  pauseOutput: () => $('#lite-output').pause(),
  toast: (text, kind) => toast(text, kind),
});

/** A dialog with an optional text field: resolves to the text (or true) on OK, null on Cancel. */
function dialog({ title, text = '', field = null, ok = 'OK', cancel = 'Cancel', danger = false }) {
  return new Promise((resolve) => {
    const d = $('#lite-dialog');
    $('#lite-dialog-title').textContent = title;
    const body = $('#lite-dialog-body');
    body.textContent = '';
    if (text) { const p = document.createElement('p'); p.textContent = text; body.append(p); }
    let input = null;
    if (field) {
      input = document.createElement('input');
      input.type = 'text'; input.value = field.value ?? ''; input.setAttribute('aria-label', field.label ?? title); input.id = 'lite-dialog-field';
      body.append(input);
    }
    const okButton = $('#lite-dialog-ok');
    okButton.textContent = ok;
    $('#lite-dialog-cancel').textContent = cancel;
    okButton.classList.toggle('danger', danger);
    const finish = (value) => { d.close(); resolve(value); };
    okButton.onclick = (event) => { event.preventDefault(); finish(input ? input.value.trim() : true); };
    $('#lite-dialog-cancel').onclick = () => finish(null);
    d.oncancel = () => resolve(null);
    d.showModal();
    (input ?? okButton).focus();
    input?.select();
    input?.addEventListener('keydown', (event) => { if (event.key === 'Enter') { event.preventDefault(); finish(input.value.trim()); } });
  });
}

const errorText = (error) => (error instanceof Error ? error.message : String(error));

// ---- saving

// The newest text of every file that is not in the store yet lives in the saver (lite-save.mjs): saves run one after the other, a text that could not be saved
// stays (it is built, shown again and tried again), and a newer text of the same file always wins.
const saver = createSaver({
  write: (projectId, path, text) => app.store.writeFile(projectId, path, { text }),
  onSaved: (entry) => { if (app.project?.id === entry.projectId) app.texts.set(entry.path, entry.text); },
  onState: ({ failed, dirty }) => paintSaved(failed, dirty),
});
const gate = createBuildGate();   // the right to build: taken before the first await of build()
if (window.__remixTest) Object.assign(window.__remixTest, { saver, gate });   // the test seam sees them too

function paintSaved(failed, dirty) {
  const label = $('#lite-saved');
  if (failed.length) {
    label.textContent = 'not saved: click to try again';
    label.title = errorText(failed[0].error);
    label.classList.add('lite-link');
  } else {
    label.textContent = dirty ? 'saving...' : 'saved on this browser';
    label.title = '';
    label.classList.remove('lite-link');
    if (!dirty) refreshUsage().catch(() => {});
  }
}

function queueSave(path, text) {
  changed();
  saver.queue(app.project.id, path, text);
  clearTimeout(app.saveTimer);
  app.saveTimer = setTimeout(() => { flushSave(); }, 350);
}

/** Saves what is typed: resolves { ok, failed } when every text that was dirty (and what came in meanwhile) was tried once. It never rejects. */
function flushSave() {
  clearTimeout(app.saveTimer);
  return saver.flush().then((result) => {
    if (!result.ok) toast(errorText(result.failed[0].error), 'error');
    return result;
  });
}
addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') flushSave(); });
addEventListener('pagehide', () => { flushSave(); });

// ---- the file tree

const textFiles = () => [...app.texts].map(([path, text]) => ({ path, text }));

function treeOf(paths) {
  const root = { folders: new Map(), files: [] };
  for (const path of paths) {
    const parts = path.split('/');
    let node = root;
    for (const folder of parts.slice(0, -1)) { if (!node.folders.has(folder)) node.folders.set(folder, { folders: new Map(), files: [] }); node = node.folders.get(folder); }
    node.files.push(path);
  }
  return root;
}

/** On a phone the file browser is a near full screen over the page (the button with the folder opens it); elsewhere it is the column beside the editor. */
function setFilesOpen(open) {
  document.body.classList.toggle('lite-files-open', open);
  $('#lite-files-toggle').setAttribute('aria-expanded', String(open));
}

function renderTree() {
  const tree = $('#lite-tree');
  tree.textContent = '';
  const row = (className, icon, label, depth, onclick, selected = false) => {
    const el = document.createElement('div');
    el.className = `lite-row ${className}`;
    el.setAttribute('role', 'treeitem');
    el.tabIndex = 0;
    el.style.paddingLeft = `${6 + depth * 14}px`;
    if (selected) el.setAttribute('aria-selected', 'true');
    el.innerHTML = '<span class="ic"></span><span class="nm"></span>';
    el.querySelector('.ic').textContent = icon;
    el.querySelector('.nm').textContent = label;
    el.onclick = () => { onclick(); if (className === 'file') setFilesOpen(false); };   // on a phone the file browser is a screen of its own: choosing a file closes it
    el.onkeydown = (event) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); onclick(); } };
    tree.append(el);
    return el;
  };
  const walk = (node, prefix, depth) => {
    for (const [name, child] of [...node.folders].sort((a, b) => a[0].localeCompare(b[0]))) {
      const path = prefix + name;
      const closed = app.collapsed.has(path);
      row('folder', closed ? '▸' : '▾', name, depth, () => { if (closed) app.collapsed.delete(path); else app.collapsed.add(path); renderTree(); }).dataset.path = path;
      if (!closed) walk(child, `${path}/`, depth + 1);
    }
    for (const path of node.files.sort((a, b) => a.localeCompare(b))) {
      const icon = isSong(path) ? '♪' : isComponent(path) ? '◇' : isAudio(path) ? '〰' : isText(path) ? '≡' : '◼';
      row('file', icon, path.split('/').pop(), depth, () => openFile(path), path === app.active).dataset.path = path;
    }
  };
  walk(treeOf(app.files.map((f) => f.path)), '', 0);
}

/** The marks of the open song: what the language server says, and the files the song names that the project does not have (found in the text at once, not after a build). */
function applyLive() {
  const text = app.texts.get(app.active) ?? '';
  const missing = /[.]amplet$/i.test(app.active || '') ? missingFileDiagnostics(text, app.files.map((f) => f.path)) : [];
  app.live = [...(app.serverLive ?? []), ...missing];
  renderProblems();
  app.editor?.setDiagnostics(app.live);
}

async function refreshFiles() {
  app.files = app.project ? await app.store.listFiles(app.project.id) : [];
  applyLive();   // an upload or a delete changes which files are there
  renderTree();
  await refreshUsage();
}
async function refreshUsage() {
  if (!app.project) { $('#lite-usage').textContent = ''; return; }
  const usage = await app.store.usage(app.project.id);
  $('#lite-usage').textContent = `${usage.files} file${usage.files === 1 ? '' : 's'}, ${(usage.bytes / 1024 / 1024).toFixed(usage.bytes > 1048576 ? 1 : 2)} MB of ${Math.floor(app.limits.maxProjectBytes / 1024 / 1024)} MB`;
}

// ---- projects

async function refreshProjects() {
  const list = await app.store.listProjects();
  const select = $('#lite-projects');
  select.textContent = '';
  for (const project of list) {
    const option = document.createElement('option');
    option.value = project.id;
    option.textContent = project.name;
    select.append(option);
  }
  if (app.project) select.value = app.project.id;
  $('#lite-share').hidden = !app.project;
  $('#lite-empty').hidden = list.length > 0 && !!app.project;
  select.hidden = list.length === 0;
}

async function openProject(id) {
  const saved = await flushSave();
  const leave = { title: 'Not saved', text: 'The latest text of this project could not be saved on this browser. Leave the project and lose that text?', ok: 'Leave it', danger: true };
  if (!saved.ok && !(await dialog(leave))) return;
  listen.stop();
  app.openSeq++;   // a file that is being opened belongs to the project that is left
  await flushSave();
  app.project = await app.store.getProject(id);
  stopSound();   // another project: the sound of the old one stops, the button goes back to Build & play
  changed();
  await app.store.setLast(id);
  app.texts.clear();
  for (const file of await app.store.listFiles(id)) if (isText(file.path)) app.texts.set(file.path, (await app.store.readFile(id, file.path)).text);
  app.collapsed.clear();
  app.active = null;
  app.built = [];
  app.lastBuilt = null;
  closeEditor();
  await refreshFiles();
  await refreshProjects();
  const paths = app.files.map((f) => f.path);
  const first = (app.project.active && paths.includes(app.project.active) ? app.project.active : pickEntry(paths, null, null)) ?? paths[0] ?? null;
  if (first) await openFile(first); else showEmptyFile();
}

const SONG_TEMPLATE = (name) => songTemplate(name);   // a starter song that plays: chords, a bass and a beat through a mixer to the master (lite-templates.mjs)
const COMPONENT_TEMPLATE = (name) => `/** What this component does, in one sentence. */\ncomponent koda.lite.${name.replace(/[^A-Za-z0-9]/g, '') || 'thing'} version 1.0.0 current {\n  id "demo.koda.lite.${name.toLowerCase().replace(/[^a-z0-9]+/g, '-') || 'thing'}"\n\n  input source: Notes\n  output notes: Notes\n\n  process {\n    emit source\n  }\n}\n`;

async function newProject() {
  const name = await dialog({ title: 'New project', field: { label: 'Project name', value: 'My song' }, ok: 'Create' });
  if (!name) return;
  const project = await app.store.createProject({ name, files: [{ path: 'song.amplet', text: SONG_TEMPLATE(name) }] });
  await openProject(project.id);
}

// ---- files

function showEmptyFile() {
  app.openSeq++;   // nothing is open: an open on its way must not bring a file back
  closeEditor();
  $('#lite-filename').textContent = '';
  $('#lite-empty').hidden = !!app.project && app.files.length > 0;
  for (const id of ['#lite-rename', '#lite-delete', '#lite-save-file', '#lite-notes']) $(id).hidden = true;
}

function closeEditor() {
  app.language?.close();
  app.editor?.destroy();
  app.editor = null;
  $('#lite-editor').textContent = '';
  $('#lite-editor').hidden = false;
  $('#lite-binary').hidden = true;
  if (app.binaryUrl) { URL.revokeObjectURL(app.binaryUrl); app.binaryUrl = ''; }
  app.live = []; app.serverLive = [];
  renderProblems();
}

/**
 * Opens a file in the editor. The newest request owns the editor: after every await a task asks mine(), and a task that is not the newest any more (or whose
 * project was left) changes nothing: not the labels, not the editor, not the language client, not an object URL.
 */
async function openFile(path) {
  const seq = ++app.openSeq, projectId = app.project?.id;
  const mine = () => seq === app.openSeq && app.project?.id === projectId;
  await flushSave();
  if (!mine()) return;
  app.active = path;
  listen.fileOpened(path);   // another song ends the listening, a component does not
  if (isSong(path) && app.outputEntry && path !== app.outputEntry) stopSound();   // another song: stop the sound of the one that was built
  syncMain();
  await app.store.setActive(projectId, path);
  if (!mine()) return;
  closeEditor();
  renderTree();
  $('#lite-empty').hidden = true;
  $('#lite-filename').textContent = path;
  for (const id of ['#lite-rename', '#lite-delete']) $(id).hidden = false;
  $('#lite-notes').hidden = !isSong(path);
  const content = await app.store.readFile(projectId, path);
  if (!mine()) return;   // another file was asked for meanwhile: this answer is old
  const kept = content.kind === 'text' ? saver.get(projectId, path) : undefined;
  if (kept !== undefined) content.text = kept;   // the newest text of this file is not in the store (its save failed): the editor shows it
  const copy = $('#lite-save-file');
  copy.hidden = false;
  copy.download = path.split('/').pop();
  if (app.copyUrl) URL.revokeObjectURL(app.copyUrl);
  app.copyUrl = URL.createObjectURL(content.kind === 'text' ? new Blob([content.text], { type: 'text/plain' }) : content.blob);
  copy.href = app.copyUrl;
  if (content.kind === 'text') await openText(path, content.text, mine);
  else openBinary(path, content);
}

async function openText(path, text, mine = () => true) {
  if (!mine()) return;
  const language = createLanguage({ textFiles, onDiagnostics: (list) => { if (app.language === language) { app.serverLive = list; applyLive(); } },
    onOffline: () => { if (app.language === language) status('Language help is offline (close other Remix tabs and reload the page; sign in again if it keeps happening)'); },
    onOnline: () => { if (app.language === language) status(''); } });
  app.language = language;
  const editor = await createLiteEditor($('#lite-editor'), {
    text,
    isCurrent: mine,
    onChange: (value) => { app.texts.set(path, value); queueSave(path, value); language.change(value); },
    complete: (position, current) => language.complete(position, current),
    files: () => app.files,
    hover: (position) => language.hover(position),
    definition: async (position) => rollTarget(position) ?? await language.definition(position),
    onOpenTarget: async (target) => {
      if (target.roll) { app.roll.open(target.roll); return; }
      if (target.path && app.files.some((f) => f.path === target.path)) { await openFile(target.path); app.editor?.gotoPosition(target); } else app.editor?.gotoPosition(target);
    },
  });
  if (!editor || !mine()) {   // not the newest open any more: give back what this one made, and leave what the newer one made
    editor?.destroy();
    language.close();
    if (app.language === language) app.language = null;
    return;
  }
  app.editor = editor;
  await language.open(path, text);
}

function openBinary(path, content) {
  $('#lite-editor').hidden = true;
  const box = $('#lite-binary');
  box.hidden = false;
  box.textContent = '';
  const info = document.createElement('p');
  info.textContent = `${path} (${Math.ceil(content.size / 1024)} KB). Reference it from a song, for example: kit ${path.split('/').slice(0, -1).join('/') || path}`;
  box.append(info);
  if (isAudio(path)) {
    app.binaryUrl = URL.createObjectURL(content.blob);
    const audio = document.createElement('audio');
    audio.controls = true; audio.src = app.binaryUrl;
    box.append(audio);
  }
}

async function newFile() {
  const suggestion = app.files.some((f) => f.path === 'song.amplet') ? 'components/thing.amplec' : 'song.amplet';
  const path = await dialog({ title: 'New file', text: 'A path like song.amplet or components/echo.amplec.', field: { label: 'File path', value: suggestion }, ok: 'Create' });
  if (!path) return;
  try {
    cleanPath(path, app.limits);
    if (!isText(path)) throw new Error('Make text files here; add sounds with Upload.');
    if (app.files.some((f) => f.path.toLowerCase() === path.toLowerCase())) throw new Error(`"${path}" exists already.`);
    const base = path.split('/').pop().replace(/\.[^.]*$/, '');
    const text = isSong(path) ? SONG_TEMPLATE(base) : isComponent(path) ? COMPONENT_TEMPLATE(base) : '';
    await app.store.writeFile(app.project.id, path, { text });
    changed();
    app.texts.set(path, text);
    await refreshFiles();
    await openFile(path);
  } catch (error) { toast(errorText(error), 'error'); }
}

async function renameFile() {
  const from = app.active;
  const to = await dialog({ title: 'Rename', field: { label: 'New path', value: from }, ok: 'Rename' });
  if (!to || to === from) return;
  try {
    if (!(await flushSave()).ok) throw new Error('The latest text could not be saved, so the file was not renamed. Try again.');
    await app.store.renameFile(app.project.id, from, to);
    changed();
    if (app.texts.has(from)) { app.texts.set(to, app.texts.get(from)); app.texts.delete(from); }
    await refreshFiles();
    await openFile(to);
  } catch (error) { toast(errorText(error), 'error'); }
}

/** Ctrl+click on the notes line of a midi-clip (or on its header) opens the note editor on that clip. */
function rollTarget(position) {
  const text = app.editor?.getText() ?? '';
  const clip = findClips(text).find((c) => c.rollable && (c.line === position.line || text.slice(0, c.from).split(String.fromCharCode(10)).length - 1 === position.line));
  return clip ? { roll: clip.name, line: position.line, character: position.character } : null;
}

async function deleteFile() {
  const path = app.active;
  if (!await dialog({ title: 'Delete file', text: `Delete "${path}" from this project? This cannot be undone.`, ok: 'Delete', danger: true })) return;
  saver.drop(app.project.id, path);
  await app.store.deleteFile(app.project.id, path);
  changed();
  app.texts.delete(path);
  app.language?.fileChanged(path, '', true);
  closeEditor();
  app.active = null;
  await refreshFiles();
  const next = pickEntry(app.files.map((f) => f.path), null, null) ?? app.files[0]?.path;
  if (next) await openFile(next); else showEmptyFile();
}

/** The path for a file that comes in (picked, dropped, or from a .zip): a name that is taken gets a number; sounds go to samples/, components to components/, a dropped folder of sounds to samples/<folder>. */
function importTarget(rawPath, taken) {
  const clean = (text) => text.replace(/[^A-Za-z0-9._ -]/g, '-').replace(/^[^A-Za-z0-9]+/, '');
  const parts = rawPath.split('/');
  const name = clean(parts.pop() ?? '') || 'file';
  let dir = parts.map(clean).filter(Boolean).join('/');
  if (!dir) dir = isComponent(name) ? 'components' : isText(name) ? '' : 'samples';
  else if (!isText(name) && !/^samples(\/|$)/.test(dir)) dir = `samples/${dir}`;
  const folder = dir ? `${dir}/` : '';
  let path = folder + name;
  for (let n = 2; taken.has(path.toLowerCase()); n++) path = `${folder}${name.replace(/(\.[^.]*)?$/, `-${n}$1`)}`;
  return path;
}


// ---- problems

/** Why a kind of note is not listed: AMP2211 says a component is not sealed. A seal is for components that are shared and trusted; Remix has no library or sharing, and the demos are development versions on purpose. */
const HIDDEN_CODE = 'AMP2211';
const HIDDEN_WHY = 'sealing is for components that are shared and trusted; Remix has no library or sharing, so the demo components stay unsealed on purpose.';

function renderProblems() {
  const list = $('#lite-problems');
  list.textContent = '';
  const all = [...app.live.map((d) => ({ ...d, here: true })), ...app.built.filter((d) => d.line === 0 || d.code === 'AMPL920' || (!app.live.length && d.file === app.active))];
  const hidden = all.filter((d) => d.code === HIDDEN_CODE);
  const items = all.filter((d) => d.code !== HIDDEN_CODE);
  for (const d of items.slice(0, 100)) {
    const li = document.createElement('li');
    li.className = d.severity;
    li.textContent = describe(d);
    // a click goes to the line (of the file that is open: a problem of another file has no line here)
    if (d.line > 0 && d.here !== false && (!d.file || d.file === app.active)) { li.onclick = () => app.editor?.goto(d.line, d.col);
      li.ondblclick = () => app.editor?.showProblem(d); li.title = 'Click: go to this line. Double click: show it with its quick fix'; }
    list.append(li);
  }
  if (hidden.length) {
    const li = document.createElement('li');
    li.className = 'info lite-hidden-note';
    li.textContent = `${hidden.length} note${hidden.length === 1 ? '' : 's'} about unsealed components (${HIDDEN_CODE}) not listed: ${HIDDEN_WHY}`;
    list.append(li);
  }
  const errors = items.filter((d) => d.severity === 'error').length;
  if (errors > 0 && !app.building) showConsole('problems');   // an error is what you want to see first (a build shows its log)
  $('#lite-problem-count').textContent = items.length ? `${errors} error${errors === 1 ? '' : 's'}, ${items.length - errors} other` : 'none';
}

// ---- build and play

/** A 401 means the sign-in has ended (a restarted server forgets its sessions): go to the sign-in page, which returns to /remix/. The files stay in this browser. */
function signInEnded(response) {
  if (response.status !== 401) return false;
  status('Your sign-in has ended.');
  toast('Your sign-in has ended. Taking you to the sign-in page; your files stay in this browser.', 'error');
  setTimeout(() => location.assign('/login?next=/remix/'), 1500);
  return true;
}

/** An error that carries how long the server wants us to wait (Retry-After): the page counts it down on the button instead of showing a bare refusal. */
function waitError(message, seconds) {
  const error = new Error(message);
  error.retryAfter = Math.max(0, Math.ceil(Number(seconds) || 0));
  return error;
}

/** Disables a button and counts "Try again in n s" on it, then gives it back. `onTick(left)` can show the same on the status line. */
function backoff(button, seconds, onTick = () => {}) {
  clearInterval(button._backoff);
  const label = button.dataset.label ?? button.textContent;
  button.dataset.label = label;
  button.dataset.wait = '1';
  button.disabled = true;
  let left = Math.max(1, Math.ceil(seconds));
  const tick = () => {
    if (left <= 0) {
      clearInterval(button._backoff);
      delete button.dataset.wait;
      button.textContent = label;
      button.disabled = false;
      onTick(0);
      return;
    }
    button.textContent = `Try again in ${left} s`;
    onTick(left);
    left--;
  };
  tick();
  button._backoff = setInterval(tick, 1000);
}

const logLine = (text, started) => {
  const log = $('#lite-log');
  const line = document.createElement('div');
  line.textContent = `+${((performance.now() - started) / 1000).toFixed(1)} s  ${text}`;
  log.append(line);
  log.scrollTop = log.scrollHeight;
};

/** Reads the lines (JSON, one per line) of a streaming build; calls onLine for each. */
async function readLines(response, onLine) {
  const reader = response.body.getReader(), decoder = new TextDecoder();
  let rest = '';
  for (;;) {
    const { value, done } = await reader.read();
    rest += decoder.decode(value ?? new Uint8Array(), { stream: !done });
    let at;
    while ((at = rest.indexOf(String.fromCharCode(10))) >= 0) { const line = rest.slice(0, at).trim(); rest = rest.slice(at + 1); if (line) onLine(JSON.parse(line)); }
    if (done) break;
  }
  if (rest.trim()) onLine(JSON.parse(rest));
}

/** The hashes (of the files that carry one) that the server does not have; null when it cannot be asked (then every file is sent). */
async function askMissing(files) {
  if (!files.every((f) => f.sha256)) return null;
  try {
    const response = await fetch('/api/remix/blobs/missing', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Ample-Token': getToken() }, body: JSON.stringify({ hashes: [...new Set(files.map((f) => f.sha256))] }), signal: app.abort?.signal });
    if (!response.ok) return null;
    return new Set((await response.json()).missing ?? []);
  } catch { return null; }
}

/**
 * Builds the song and plays it. `options.listen` (Live Preview): the sound is not played here; the answer is the result of the build with its sound ({ ...result, blob,
 * entry }), or the result itself when the build did not work, or null when nothing was built (see app.lastBuildError).
 */
async function build(options = {}) {
  if (!app.project) return null;
  // the right to build is taken before the first await: two quick calls cannot both pass, and this build has its own ticket and controller
  const ticket = gate.reserve(options.listen ? 'listen' : 'normal');
  if (!ticket) return null;
  app.building = true;
  app.buildKind = ticket.kind;   // Stop of Live Preview cancels only its own build
  app.abort = ticket.controller;
  const release = () => { if (gate.release(ticket)) { app.abort = null; app.buildKind = null; app.building = false; } };
  const saved = await flushSave();   // what is typed is in the store, or (when that failed) kept for this build
  if (!saved.ok) toast('The latest text could not be saved on this browser: the build takes the text in the editor.', 'error');
  const entry = pickEntry(app.files.map((f) => f.path), app.active, app.lastBuilt);
  if (!entry) { toast('Add a song (.amplet) first.', 'error'); release(); syncMain(); return null; }
  app.lastBuildError = null;
  let outcome = null;
  app.builtFrom = { rev: app.rev, project: app.project.id };   // what this build is made of (the text may change while it runs)
  dropOutput();
  $('#lite-build').disabled = true;
  $('#lite-build').textContent = 'Building...';
  $('#lite-build').dataset.mode = 'build';
  $('#lite-cancel').hidden = false;
  const started = performance.now();
  const bar = $('#lite-progress');
  bar.hidden = false; bar.value = 0;
  $('#lite-log').textContent = '';
  // whoever watches the Song log comes back to it when the build is done (the build log shows the progress meanwhile)
  const backToSongLog = $('#lite-tab-songlog')?.getAttribute('aria-selected') === 'true';
  showConsole('log');
  status('Building...');
  logLine(`Build of ${entry} started.`, started);
  const tour = app.tour;
  tour?.step('send');
  tour?.line('Sending the files to the server...');
  const ticker = setInterval(() => { if (!app.uploading) status(`Building... ${((performance.now() - started) / 1000).toFixed(0)} s`); }, 500);
  app.built = [];
  try {
    const files = neededFiles(saver.withDirty(app.project.id, await app.store.snapshot(app.project.id)));   // a text that could not be saved is built as it is in the editor
    const entryText = files.find((f) => f.path === entry)?.text ?? '';   // the song exactly as this build takes it
    const problems = checkBuild(entry, files, app.limits);
    if (problems.length) throw new Error(problems.join(' '));
    // the files get a hash; the server says which it does not know yet (it knows every file of the demos and keeps earlier uploads for an hour), and only those are sent
    const hashed = await withHashes(files).catch(() => files);
    const missing = await askMissing(hashed);
    const plan = sendSummary(hashed, missing);
    logLine(plan.cached > 0 ? `Sending ${plan.sending} of ${plan.total} files (${sizeText(plan.bytes)}); ${plan.cached} already cached on the server.` : `Sending ${plan.total} file${plan.total === 1 ? '' : 's'} (${sizeText(plan.bytes)}) to the server.`, started);
    tour?.sending(plan, sizeText(plan.bytes));
    app.uploading = plan.bytes > 0;
    const send = (needed) => postWithProgress('/api/remix/build?stream=1', {
      headers: { 'X-Ample-Token': getToken() },
      body: buildForm(entry, hashed, needed, songLog.options),
      signal: ticket.controller.signal,
      onProgress: (sent, total) => {
        if (!total) return;
        bar.value = sent / total;
        status(`Sending... ${Math.round((100 * sent) / total)}%`);
        tour?.uploaded(sent, total, sizeText(sent), sizeText(total));
      },
    });
    let response = await send(missing);
    app.uploading = false;
    if (response.status === 409 && missing) {
      // the server dropped a file between the question and the build: send the build once more with every file
      logLine('A file was dropped by the server in the meantime: sending all files.', started);
      response = await send(null);
    }
    if (signInEnded(response)) throw new Error('Your sign-in has ended.');
    if (!response.ok) {
      const refusal = await response.json().catch(() => ({}));
      throw waitError(refusal.error?.message ?? `The build was refused (${response.status}).`, refusal.error?.retryAfter ?? response.headers.get('Retry-After'));
    }
    let result = null;
    await readLines(response, (message) => {
      if (message.t === 'log' || message.t === 'queue') tour?.step('build');
      if (message.t === 'log') { logLine(message.text, started); if (message.fraction >= 0) bar.value = message.fraction; tour?.line(message.text); tour?.progress(message.fraction); }
      else if (message.t === 'queue') { logLine(`Waiting in the queue: place ${message.place} of ${message.total}.`, started); status(`Waiting in the queue (${message.place} of ${message.total})...`); tour?.line(`Waiting in the queue: place ${message.place} of ${message.total}.`); }
      else if (message.t === 'error') throw waitError(message.message, message.retryAfter);
      else if (message.t === 'result') result = message.result;
    });
    if (!result) throw new Error('The build ended without an answer.');
    bar.value = 1;
    app.built = (result.diagnostics ?? []).map((d) => ({ ...d, file: entry }));
    songLog.events = result.observations ?? [];
    songLog.entry = entry;
    songLog.builtWith = JSON.stringify(songLog.options);
    renderSongLog();
    if (backToSongLog) showConsole('songlog');
    app.lastBuilt = entry;
    logLine(result.ok ? `Done in ${result.seconds} s: ${result.audioSeconds} seconds of audio.` : 'The build did not work: see Problems.', started);
    if (!result.ok) {
      outcome = result;
      status('The build did not work.');
      const firstError = (result.diagnostics ?? []).find((d) => d.severity === 'error');
      if (firstError) showConsole('problems');
      $('#lite-output-info').textContent = 'Fix the problems and build again.';
      tour?.fail('The build did not work: ' + (firstError?.message ?? 'see Problems.'));
    }
    else {
      const played = await playOutput(result, entry, { play: !options.listen });
      outcome = options.listen ? { ...result, blob: played.blob, entry, entryText } : result;
    }
  } catch (error) {
    app.lastBuildError = error?.name === 'AbortError' ? 'The build was cancelled.' : errorText(error);
    if (error?.name === 'AbortError') { status('Build cancelled.'); logLine('Build cancelled. The server stops and removes its temporary files.', started); tour?.fail('Cancelled.'); }
    else if (error?.message === 'Your sign-in has ended.') logLine('Stopped: your sign-in has ended; going to the sign-in page.', started);
    else if (error?.retryAfter > 0) {
      logLine(`Stopped: ${errorText(error)}`, started);
      backoff($('#lite-build'), error.retryAfter, (left) => status(left > 0 ? `Too many builds at once: try again in ${left} s.` : ''));
      tour?.fail(errorText(error), error.retryAfter);
    } else { status(''); logLine(`Stopped: ${errorText(error)}`, started); if (tour) tour.fail(errorText(error)); else toast(errorText(error), 'error'); }
  } finally {
    clearInterval(ticker);
    release();
    app.uploading = false;
    syncMain();
    $('#lite-build').disabled = $('#lite-build').dataset.wait === '1';
    $('#lite-cancel').hidden = true;
    setTimeout(() => { if (!app.building) bar.hidden = true; }, 1500);
    renderProblems();
  }
  return outcome;
}

/** The build of before is not needed any more: the server may drop it (it keeps it for ten minutes anyway), and the MP3 and FLAC buttons go away. */
function dropOutput() {
  if (app.outputId) fetch(`/api/remix/output/${app.outputId}`, { method: 'DELETE', headers: { 'X-Ample-Token': getToken() } }).catch(() => {});
  app.outputId = null;
  for (const format of ['flac', 'mp3']) $(`#lite-download-${format}`).hidden = true;
  syncMain();
}

/** Asks the server for the last build as MP3 or FLAC (encoded there, counted per client, queued) and saves it. A refusal says how long to wait. */
async function downloadEncoded(format) {
  if (!app.outputId) { toast('Build a song first.', 'error'); return; }
  if (format === 'mp3' && app.playFormat === 'mp3' && app.playBlob) { saveBlob(app.playBlob, `${app.outputName ?? 'lite'}.mp3`); return; }   // what is playing is that mp3: no new encode
  const button = $(`#lite-download-${format}`), label = button.innerHTML;
  button.disabled = true;
  button.innerHTML = '<b>' + format.toUpperCase() + '</b><small>encoding...</small>';
  try {
    const response = await fetch(`/api/remix/output/${app.outputId}?format=${format}`, { headers: { 'X-Ample-Token': getToken() } });
    if (signInEnded(response)) return;
    if (response.status === 404) throw new Error('This build is no longer kept on the server (ten minutes): build again to download it.');
    if (!response.ok) {
      const refusal = await response.json().catch(() => ({}));
      throw waitError(refusal.error?.message ?? `The download was refused (${response.status}).`, refusal.error?.retryAfter ?? response.headers.get('Retry-After'));
    }
    const url = URL.createObjectURL(await response.blob());
    const link = document.createElement('a');
    link.href = url;
    link.download = `${app.outputName ?? 'lite'}.${format}`;
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
  } catch (error) {
    if (error?.retryAfter > 0) { button.innerHTML = label; backoff(button, error.retryAfter); return; }
    toast(errorText(error), 'error');
  }
  button.disabled = false;
  button.textContent = label;
}

/** Saves a blob as a file with this name. */
function saveBlob(blob, name) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10000);
}

/** The sound of a build for the player: the mp3 (small, so it starts sooner); the wav only when the server cannot encode it just now. */
async function fetchPlayable(outputId) {
  const headers = { 'X-Ample-Token': getToken() };
  const mp3 = await fetch(`/api/remix/output/${outputId}?format=mp3&use=play`, { headers });
  if (signInEnded(mp3)) throw new Error('Your sign-in has ended.');
  if (mp3.ok) return { blob: await mp3.blob(), format: 'mp3' };
  if (mp3.status === 404) throw new Error('This build is no longer kept on the server (ten minutes): build again.');
  const wav = await fetch(`/api/remix/output/${outputId}`, { headers });
  if (signInEnded(wav)) throw new Error('Your sign-in has ended.');
  if (!wav.ok) throw new Error('The sound could not be fetched.');
  return { blob: await wav.blob(), format: 'wav' };
}

async function playOutput(result, entry, { play = true } = {}) {
  const { blob, format } = await fetchPlayable(result.outputId);
  app.playBlob = blob; app.playFormat = format;
  app.outputId = result.outputId;
  app.builtRev = app.builtFrom?.rev ?? app.rev;
  app.builtProject = app.builtFrom?.project ?? app.project?.id;
  app.outputEntry = entry;   // the song this sound was built from
  app.outputName = entry.replace(/\.amplet$/, '').split('/').pop();
  for (const format of ['flac', 'mp3']) $(`#lite-download-${format}`).hidden = false;
  if (app.outputUrl) URL.revokeObjectURL(app.outputUrl);
  app.outputUrl = URL.createObjectURL(blob);
  const audio = $('#lite-output');
  audio.ampleBlob = blob;   // the player draws the waveform from it (a fetch of the blob URL is not allowed by the page's connect-src)
  audio.src = app.outputUrl;
  const link = $('#lite-download');
  // the wav is fetched when it is asked for: the player plays the mp3
  link.href = format === 'wav' ? app.outputUrl : '#';
  link.download = `${entry.replace(/\.amplet$/, '').split('/').pop()}.wav`;
  link.onclick = format === 'wav' ? null : async (event) => {
    event.preventDefault();
    try {
      const response = await fetch(`/api/remix/output/${result.outputId}`, { headers: { 'X-Ample-Token': getToken() } });
      if (signInEnded(response)) return;
      if (!response.ok) throw new Error(response.status === 404 ? 'This build is no longer kept on the server (ten minutes): build again to download it.' : 'The WAV could not be fetched.');
      saveBlob(await response.blob(), link.download);
    } catch (error) { toast(errorText(error), 'error'); }
  };
  link.hidden = false;
  link.removeAttribute('aria-disabled');
  $('#lite-output-info').textContent = `${entry}: ${result.audioSeconds} seconds, built in ${result.seconds} seconds.`;
  $('#lite-stop').disabled = false;
  if (!play) { status(''); return { blob }; }   // Live Preview plays it itself
  if (app.tour) { status('Ready'); app.tour.ready(); return { blob }; }   // the window's Play button starts the sound (a click is what a browser needs)
  status('Playing');
  await audio.play().catch(() => {
    // a browser lets a page play sound only after a click: the first click anywhere starts the build that is waiting
    status('Click anywhere to play');
    document.addEventListener('pointerdown', () => audio.play().catch(() => status('Press play')), { once: true });
  });
  return { blob };
}

/** Stops the sound that is playing (and rewinds it) without touching the build. */
function stopSound() {
  const audio = $('#lite-output');
  if (!audio.paused) { audio.pause(); audio.currentTime = 0; status(''); }
}

function stop() {
  const audio = $('#lite-output');
  audio.pause();
  audio.currentTime = 0;
  status('');
  syncMain();
}

/** The main button follows the state of the page: "Build & play" until a build exists, then (while the project is still what was built) "Play from start" or "Stop", and "Build & play" again at the first change. */
/** The two parts of the console under the editor, as tabs: the problems of the song and the log of the last build. */
// The Song log: the events of log, debug, trace and warn of the last build (web/lib/log-view.mjs), and a level and assertions for the next builds of this page.
// The choices live in this page only (never in the song, the project or the browser storage) and are sent with a build as its options.

function renderSongLog() {
  const box = $('#lite-songlog');
  if (!box) return;
  const counts = levelCounts(songLog.events);
  const total = songLog.events.length;
  $('#lite-songlog-count').textContent = total ? String(total) : '';
  const filters = $('#lite-songlog-filters');
  filters.replaceChildren(...LEVELS.map((level) => {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = `lite-songlog-filter ${level}`;
    button.dataset.level = level;
    button.textContent = `${level} ${counts[level]}`;
    button.setAttribute('aria-pressed', String(songLog.shown.has(level)));
    button.onclick = () => { if (songLog.shown.has(level)) songLog.shown.delete(level); else songLog.shown.add(level); renderSongLog(); };
    return button;
  }));
  const changed = JSON.stringify(songLog.options) !== songLog.builtWith;
  $('#lite-songlog-note').textContent = changed ? 'Build again to apply the new choice.' : '';
  box.replaceChildren();
  const rows = logRows(songLog.events, songLog.shown);
  if (!rows.length) {
    const empty = document.createElement('div');
    empty.className = 'lite-dim lite-songlog-empty';
    empty.textContent = songLog.entry ? emptyText({ events: songLog.events, level: songLog.options['logging.level'] ?? '' }) : 'Build the song to see what it logs.';
    box.append(empty);
  }
  for (const { kind, event, count } of rows) {
    const row = document.createElement('div');
    row.className = `lite-songlog-row ${kind === 'budget' ? 'budget' : event.level}`;
    const head = document.createElement('span');
    head.className = 'lite-songlog-head';
    head.textContent = `${kind === 'budget' ? 'left out' : event.level}  ${whereText(event)}`;
    const text = document.createElement('span');
    text.className = 'lite-songlog-text';
    text.textContent = messageText(event) + (count > 1 ? `  \u00d7${count}` : '');
    row.append(head, text);
    const more = [event.hint, ...(event.stack ?? []).map((frame) => `at ${frame}`)].filter(Boolean);
    if (more.length) { const extra = document.createElement('span'); extra.className = 'lite-songlog-more'; extra.textContent = more.join('\n'); row.append(extra); }
    if (inSong(event) && songLog.entry === app.active) { row.tabIndex = 0; row.title = 'Click: go to this line'; row.onclick = () => app.editor?.goto(event.line, event.col || 1); }
    box.append(row);
  }
}

function showConsole(which) {
  for (const name of ['problems', 'log', 'songlog']) {
    const on = name === which;
    $('#lite-' + name + '-pane').hidden = !on;
    $('#lite-tab-' + name).setAttribute('aria-selected', String(on));
  }
}

function syncMain() {
  const button = $('#lite-build');
  if (button.dataset.wait === '1' || app.building) return;
  const audio = $('#lite-output');
  const playing = !audio.paused && !audio.ended;
  const entry = app.project ? pickEntry(app.files.map((f) => f.path), app.active, app.lastBuilt) : null;
  // a new choice of log level or assertions makes the sound that was built an old one too: the next press builds
  const fresh = !!app.outputId && app.builtRev === app.rev && app.builtProject === app.project?.id && entry === app.outputEntry && JSON.stringify(songLog.options) === songLog.builtWith;   // a different song than the one that was built is not fresh
  button.textContent = fresh ? (playing ? '\u25A0 Stop' : '\u25B6 Play from start') : 'Build & play';
  button.title = fresh ? (playing ? 'Stop the sound' : 'Play the sound from the start (Ctrl+Enter builds it again)') : 'Build the song and play it (Ctrl+Enter)';
  button.dataset.mode = fresh ? (playing ? 'stop' : 'play') : 'build';
  $('#lite-stop').hidden = fresh || !playing;   // the Stop of the sound of an older build stays when the project has changed since
  listen.paint();
}

/** Any change of the project (an edit, a new, renamed, deleted or uploaded file, another project or demo) makes the sound that was built an old one. */
function changed() {
  app.rev++;
  syncMain();
  listen.changed();
}

function playFromStart() {
  const audio = $('#lite-output');
  audio.currentTime = 0;
  audio.play().catch(() => status('Press play'));
}

// ---- demos

function renderDemos() {
  const chips = $('#lite-demo-cats');
  chips.textContent = '';
  for (const category of ['', ...CATEGORIES]) {
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = category || 'All';
    button.setAttribute('aria-pressed', String(app.filter.category === category));
    button.onclick = () => { app.filter.category = category; renderDemos(); };
    chips.append(button);
  }
  const list = $('#lite-demo-list');
  list.textContent = '';
  for (const demo of filterDemos(app.catalog, app.filter)) {
    const li = document.createElement('li');
    li.dataset.demo = demo.id;
    const title = document.createElement('h3'); title.textContent = demo.name;
    const text = document.createElement('p'); text.textContent = demo.description;
    const meta = document.createElement('div'); meta.className = 'meta';
    const pill = (text, cls) => { const s = document.createElement('span'); s.className = cls; s.textContent = text; return s; };
    meta.append(pill(demo.category, 'pill cat'), pill(demo.level, 'pill level ' + demo.level.toLowerCase()), ...demo.tags.map((tag) => pill(tag, 'pill tag')));
    const use = document.createElement('button'); use.type = 'button'; use.className = 'primary'; use.textContent = 'Use';
    use.title = 'Make your own copy of this demo and open it';
    use.onclick = () => useDemo(demo);
    li.append(title, text, meta, use);
    list.append(li);
  }
}

async function useDemo(demo) {
  status(`Copying "${demo.name}"...`);
  try {
    const files = await demoFiles(demo);
    const project = await app.store.createProject({ name: demo.name, origin: { demo: demo.id }, files });
    $('#lite-demo-dialog').close();
    await openProject(project.id);
    const entry = entryOf(demo);
    if (entry) await openFile(entry);
    status('');
  } catch (error) {
    status('');
    toast(errorText(error), 'error');
  }
}

/** A demo can be shared as a link while this copy is still exactly the demo: the link opens the demo from the server and builds it. */
async function shareDemo() {
  if (!app.project) { toast('Open or make a project first.'); return; }
  await flushSave();
  const id = app.project.origin?.demo;
  const demo = id ? (app.catalog.find((d) => d.id === id) ?? (await loadCatalog().catch(() => [])).find((d) => d.id === id)) : null;
  if (demo) {
    // a demo copy that is still the demo: the short link that opens the demo itself
    let unchanged = false;
    try { unchanged = await isUnchangedDemo(await app.store.listFiles(app.project.id), (path) => app.store.readFile(app.project.id, path), await demoFiles(demo)); } catch (error) { toast(errorText(error), 'error'); return; }
    if (unchanged) { showShare(demo, shareUrl(location, demo)); return; }
  }

  // your own song (or a demo that you changed): the form of a share
  openPublish({
    snapshot: () => app.store.snapshot(app.project.id),
    project: app.project,
    activeSong: () => (app.active && isSong(app.active) ? app.active : null),
    lastBuilt: () => app.lastBuilt,
    limits: app.limits,
    token: getToken,
    toast,
  });
}

/** The window of Share: the link, a Copy to clipboard button, and a visible answer (a green tick when it worked, a red line with what to do when it did not). */
function showShare(demo, url) {
  const box = $('#lite-share-dialog'), field = $('#lite-share-link'), button = $('#lite-share-copy'), result = $('#lite-share-result');
  $('#lite-share-title').textContent = 'Share "' + demo.name + '"';
  field.value = url;
  let timer = 0;
  const say = (ok) => {
    clearTimeout(timer);
    result.dataset.kind = ok ? 'ok' : 'error';
    result.textContent = ok ? '\u2713 The link is on your clipboard.' : '\u2715 Copying did not work here: the link is selected, press Ctrl+C (or hold the link on a phone and choose Copy).';
    button.dataset.state = ok ? 'copied' : '';
    button.textContent = ok ? '\u2713 Copied' : 'Copy to clipboard';
    if (!ok) { field.focus(); field.select(); }
    timer = setTimeout(() => { button.dataset.state = ''; button.textContent = 'Copy to clipboard'; }, 2500);
  };
  button.onclick = async () => say(await copyText(url, field));
  $('#lite-share-close').onclick = () => box.close();
  result.textContent = '';
  result.dataset.kind = '';
  button.dataset.state = '';
  button.textContent = 'Copy to clipboard';
  if (!box.open) box.showModal();
  copyText(url, field).then(say);   // the first copy happens when the window opens (the click on Share is the gesture a browser needs); the button does it again
}

/** A link (#demo=id) opens that demo: an unchanged copy that this browser already has, or a new copy, then builds and plays it. */
/** A link opens a fresh copy of the original; when this browser already has a copy that was made from the same link and holds changes, the person chooses (a reload of the page would otherwise leave their own copy behind). */
async function chooseCopy(mine) {
  const answer = await dialog({ title: 'You already have a copy', text: `"${mine.name}" in this browser was made from this link and holds your own changes. Open your copy, or start again from a fresh copy of the original?`, ok: 'Open my copy', cancel: 'New fresh copy' });
  return answer ? mine : null;
}

/**
 * The project a link opens: a copy of this browser that was made from the link and holds changes comes first (the person is asked: that copy, or a fresh one); a fresh copy that is left over from an earlier visit is reused;
 * null when a new copy has to be made. (A leftover fresh copy must not hide the changed one: it was found first and the question never came.)
 */
async function copyOfLink(kind, id, files) {
  let unchanged = null;
  let changed = null;
  for (const project of await app.store.listProjects()) {   // newest first
    if (project.origin?.[kind] !== id) continue;
    if (await isUnchangedDemo(await app.store.listFiles(project.id), (path) => app.store.readFile(project.id, path), files)) unchanged ??= project;
    else changed ??= project;
  }

  if (changed && await chooseCopy(changed)) return changed;
  return unchanged;
}

async function openShared(id, tour = null) {
  status('Opening the shared demo...');
  try {
    if (!app.catalog.length) app.catalog = await loadCatalog();
    const demo = app.catalog.find((d) => d.id === id);
    if (!demo) { status(''); if (tour) tour.fail('That demo does not exist.'); else toast('That demo does not exist.', 'error'); return false; }
    tour?.title(demo.name);
    tour?.line('Fetching the files of the demo...');
    const files = await demoFiles(demo);
    let found = await copyOfLink('demo', id, files);
    const project = found ?? await app.store.createProject({ name: demo.name, origin: { demo: demo.id }, files });
    await openProject(project.id);
    const entry = entryOf(demo);
    if (entry) await openFile(entry);
    status('');
    return true;
  } catch (error) {
    status('');
    if (tour) tour.fail(errorText(error)); else toast(errorText(error), 'error');
    return false;
  }
}

/** A shared link: the window shows every step (open, send, build, ready), Cancel works all the way, and a Play button ends it (a click is what a browser needs to start sound). */
async function runShared(id, again = null) {
  if (app.tour && !again) return;
  // Try again goes on in the same window: closing it and opening a new one made the old window's "closed" arrive after the new one had started
  const tour = again ?? createTour({
    root: $('#lite-tour'),
    onCancel: () => { if (app.abort) app.abort.abort(); else tour.fail('Cancelled.'); },
    onPlay: () => { $('#lite-output').play().catch(() => status('Press play')); },
    onRetry: () => runShared(id, tour),
    onSkip: () => {},
    onClose: () => { if (app.tour === tour) app.tour = null; },
    wait: (button, seconds) => backoff(button, seconds),
  });
  app.tour = tour;
  tour.start('Opening the demo');
  const opened = await openShared(id, tour);
  if (tour.isCancelled()) { tour.fail('Cancelled.'); return; }
  if (!opened || tour.wasSkipped()) return;   // Skip building: the song is open, nothing is built
  await build();
}

/** A link to a shared song (/s/ID): the window shows who made it and the steps, copies the files into a project of this browser, builds it and ends in Play. */
async function runShareLink(id, again = null) {
  if (app.tour && !again) return;
  const tour = again ?? createTour({
    root: $('#lite-tour'),
    onCancel: () => { if (app.abort) app.abort.abort(); else tour.fail('Cancelled.'); },
    onPlay: () => { $('#lite-output').play().catch(() => status('Press play')); },
    onRetry: () => runShareLink(id, tour),
    onSkip: () => {},
    onClose: () => { if (app.tour === tour) app.tour = null; },
    wait: (button, seconds) => backoff(button, seconds),
  });
  app.tour = tour;
  tour.start('Opening a shared song');
  const opened = await openShareLink(id, tour);
  if (tour.isCancelled()) { tour.fail('Cancelled.'); return; }
  if (!opened || tour.wasSkipped()) return;   // Skip building: the song is open, nothing is built
  await build();
}

async function openShareLink(id, tour) {
  try {
    const headers = { 'X-Ample-Token': getToken() };
    const response = await fetch(`/api/remix/shares/${id}`, { headers });
    if (!response.ok) { tour.fail('This share is no longer available.'); return false; }   // an unknown id and a share that is gone look the same
    const info = await response.json();
    tour.title(info.title);
    tour.about(info);
    tour.line('Fetching the files of the song...');
    // all the files in ONE request (one request per file is too many for a proxy that limits a visitor: the last files got refused)
    const archive = await fetch(`/api/remix/shares/${id}/archive`, { headers });
    if (!archive.ok) throw new Error(archive.status === 410 || archive.status === 404 ? 'This share is no longer available.' : `The files of the share could not be fetched (${archive.status}).`);
    const { entries } = await readZip(await archive.blob());
    const files = await Promise.all(entries.map(async (entry) => { const bytes = await entry.read(); return isText(entry.path) ? { path: entry.path, text: new TextDecoder().decode(bytes) } : { path: entry.path, blob: new Blob([bytes]) }; }));
    if (!files.some((f) => f.path === info.entry)) throw new Error(`The share is missing "${info.entry}".`);
    let found = await copyOfLink('share', id, files);
    const project = found ?? await app.store.createProject({ name: info.title, origin: { share: id }, files });
    await openProject(project.id);
    await openFile(info.entry);
    return true;
  } catch (error) {
    tour.fail(errorText(error));
    return false;
  }
}

async function openDemos() {
  if (!app.catalog.length) {
    try { app.catalog = await loadCatalog(); } catch (error) { toast(errorText(error), 'error'); return; }
  }
  renderDemos();
  $('#lite-demo-dialog').showModal();
  $('#lite-demo-search').focus();
}

// ---- reset

async function resetLite() {
  const ok = await dialog({ title: 'Reset workspace', ok: 'Delete everything', danger: true,
    text: 'This deletes all Remix projects, files, changes to demo copies and uploaded samples that this browser keeps. Studio, its projects, your sign-in and other settings are not touched. This cannot be undone.' });
  if (!ok) return;
  try {
    saver.clear();
    app.language?.close();
    await app.store.reset();
    for (const key of Object.keys(sessionStorage)) if (key.startsWith(SESSION_PREFIX)) sessionStorage.removeItem(key);
    location.reload();
  } catch (error) { toast(errorText(error), 'error'); }
}

// ---- start

/** The demo that a visitor with an empty workspace gets (the demo song every AMPLE project starts with). */
const DEFAULT_DEMO = 'welcome-to-ample';

async function start() {
  $('#lite-new-project').onclick = newProject;
  $('#lite-demos-btn').onclick = openDemos;
  $('#lite-empty-demos').onclick = openDemos;
  $('#lite-new-file').onclick = () => (app.project ? newFile() : toast('Make a project or open a demo first.'));
  const importer = createImporter({
    project: () => app.project,
    store: () => app.store,
    limits: () => app.limits,
    taken: () => new Set(app.files.map((f) => f.path.toLowerCase())),
    targetFor: importTarget,
    isText,
    toast,
    snapshot: () => app.store.snapshot(app.project.id),
    added: async (entries) => { changed(); for (const entry of entries) if (entry.text !== undefined) app.texts.set(entry.path, entry.text); await refreshFiles(); },
    openAsProject: async (name, files) => {
      const project = await app.store.createProject({ name, files });
      await openProject(project.id);
      const song = pickEntry(files.map((f) => f.path), null, null);
      if (song) await openFile(song);
    },
  });
  importer.enableDrop();
  $('#lite-export').onclick = () => importer.exportZip();
  $('#lite-upload').onchange = (event) => { const files = [...event.target.files]; event.target.value = ''; importer.pick(files); };
  app.roll = createRoll({ read: () => findClips(app.editor?.getText() ?? ''), write: (clip, value) => app.editor?.replaceRange(clip.from, clip.to, value), tempo: () => tempoOf(app.editor?.getText() ?? ''), goto: (line) => app.editor?.goto(line + 1), stopMain: () => stop() });
  $('#lite-notes').onclick = () => {
    const clips = findClips(app.editor?.getText() ?? '');
    if (!app.roll.open()) toast(clips.length ? (clips.find((c) => !c.rollable)?.why ?? 'No clip with a notes line.') : 'This song has no midi-clip with a notes line yet. Write one, for example: notes 1:0:1:60:100 1:1:1:64:100', 'error');
  };
  $('#lite-files-toggle').onclick = () => setFilesOpen(!document.body.classList.contains('lite-files-open'));
  $('#lite-files-close').onclick = () => setFilesOpen(false);
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && document.body.classList.contains('lite-files-open')) setFilesOpen(false); });
  $('#lite-tab-problems').onclick = () => showConsole('problems');
  $('#lite-tab-log').onclick = () => showConsole('log');
  $('#lite-tab-songlog').onclick = () => { showConsole('songlog'); renderSongLog(); };
  $('#lite-log-level').onchange = (e) => { songLog.options['logging.level'] = e.target.value; renderSongLog(); syncMain(); };
  $('#lite-assertions').onchange = (e) => { songLog.options['diagnostics.assertions'] = e.target.value; renderSongLog(); syncMain(); };
  $('#lite-rename').onclick = renameFile;
  $('#lite-saved').onclick = () => { if (saver.failed().length) flushSave(); };   // a text that could not be saved: try again
  $('#lite-delete').onclick = deleteFile;
  $('#lite-build').onclick = () => { const mode = $('#lite-build').dataset.mode; if (mode === 'stop') stop(); else if (mode === 'play') playFromStart(); else build(); };
  for (const type of ['play', 'pause', 'ended', 'emptied']) $('#lite-output').addEventListener(type, syncMain);
  listen.wire();
  $('#lite-output').addEventListener('play', () => { if (listen.active()) listen.stop(); });   // one sound at a time
  createPlayer($('#lite-output'));   // the waveform player of the Output pane (the audio element stays, hidden)
  $('#lite-cancel').onclick = () => app.abort?.abort();
  $('#lite-download-flac').onclick = () => downloadEncoded('flac');
  $('#lite-download-mp3').onclick = () => downloadEncoded('mp3');
  // the Export as menu: opens under its button, closes on a choice, a click elsewhere or Escape; the sound formats appear after a build
  { const btn = $('#lite-export-btn'), menu = $('#lite-export-menu');
    const close = () => { menu.hidden = true; btn.setAttribute('aria-expanded', 'false'); };
    btn.onclick = (e) => { e.stopPropagation(); const open = menu.hidden; menu.hidden = !open; btn.setAttribute('aria-expanded', String(open)); $('#lite-export-hint').hidden = !$('#lite-download').hidden; if (open) menu.querySelector('[role=menuitem]:not([hidden])')?.focus(); };
    menu.addEventListener('click', (e) => { if (e.target.closest('[role=menuitem]')) setTimeout(close, 0); });
    document.addEventListener('click', (e) => { if (!menu.hidden && !e.target.closest('#lite-export-wrap')) close(); });
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !menu.hidden) { close(); btn.focus(); } });
    $('#lite-download-amplet').onclick = () => {
      const text = app.editor?.getText() ?? '';
      if (!text.trim()) { toast('Open a song first.', 'error'); return; }
      const name = (app.active || 'song.amplet').split('/').pop().replace(/\.(amplet|amplec)$/, '') || 'song', ext = /\.amplec$/.test(app.active || '') ? '.amplec' : '.amplet';
      const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' })), a = document.createElement('a');
      a.href = url; a.download = name + ext; document.body.appendChild(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(url), 4000);
    };
  }
  $('#lite-stop').onclick = stop;
  $('#lite-reset').onclick = resetLite;
  // a phone has no room for these buttons in the bar: one menu (the hamburger) holds Export as, New project, Share and Reset workspace; each item presses the real button
  { const btn = $('#lite-menu-btn'), menu = $('#lite-menu');
    const entries = [
      { label: 'Export as…', run: () => setTimeout(() => $('#lite-export-btn').click(), 0) },
      { label: 'New project', run: () => $('#lite-new-project').click() },
      { label: 'Share', run: () => $('#lite-share').click(), skip: () => $('#lite-share').hidden },
      { label: 'Reset workspace', run: () => $('#lite-reset').click(), danger: true },
    ];
    const close = () => { menu.hidden = true; btn.setAttribute('aria-expanded', 'false'); };
    btn.onclick = (e) => {
      e.stopPropagation();
      const open = menu.hidden;
      menu.textContent = '';
      for (const entry of entries) {
        if (entry.skip?.()) continue;
        const item = document.createElement('button');
        item.type = 'button'; item.setAttribute('role', 'menuitem'); item.textContent = entry.label;
        if (entry.danger) item.className = 'danger';
        item.onclick = () => { close(); entry.run(); };
        menu.append(item);
      }
      menu.hidden = !open;
      btn.setAttribute('aria-expanded', String(open));
      if (open) menu.querySelector('[role=menuitem]')?.focus();
    };
    document.addEventListener('click', (e) => { if (!menu.hidden && !e.target.closest('#lite-menu-wrap')) close(); });
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !menu.hidden) { close(); btn.focus(); } });
  }
  $('#lite-share').onclick = shareDemo;
  initInfo();
  addEventListener('hashchange', () => { const id = sharedDemoId(location.hash); if (id) runShared(id); });
  $('#lite-projects').onchange = (event) => openProject(event.target.value);
  $('#lite-demo-close').onclick = () => $('#lite-demo-dialog').close();
  $('#lite-demo-search').oninput = (event) => { app.filter.query = event.target.value; renderDemos(); };
  $('#lite-output').onended = () => { status(''); };
  // the status line follows the player at the bottom as well (its own pause button too), not only the buttons of the page
  $('#lite-output').addEventListener('play', () => { if (!app.building) status('Playing'); });
  $('#lite-output').addEventListener('pause', () => {
    const audio = $('#lite-output');
    if (app.building || audio.ended) return;
    status(audio.currentTime === 0 ? '' : 'Paused');   // Stop puts it back at the start: nothing to say then
  });
  addEventListener('keydown', (event) => { if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) { event.preventDefault(); build(); } });
  try { const response = await fetch('/api/remix/limits', { headers: { 'X-Ample-Token': getToken() } }); if (signInEnded(response)) return; if (response.ok) app.limits = { ...DEFAULT_LIMITS, ...(await response.json()) }; } catch { /* the defaults are the same numbers */ }
  let failure = null;
  try {
    app.kv = idbKv(undefined, undefined, { timeoutMs: 6000 });
    app.store = createLiteStore(app.kv, { limits: app.limits });
    await app.store.init();
  } catch (error) {
    failure = error;
  }
  if (failure && !(failure instanceof LiteStoreError)) {
    // the browser storage is blocked or stuck (a private window, an in-app browser, a tab frozen in the middle of a write): Remix still works, only without saving
    app.kv = memoryKv();
    app.store = createLiteStore(app.kv, { limits: app.limits });
    await app.store.init();
    const notice = $('#lite-notice');
    notice.textContent = 'Not saved: this browser does not let Remix keep files, so your work is gone when you close the page. Download what you want to keep. (Close other Remix tabs and reload to save again.)';
    notice.hidden = false;
    failure = null;
  }
  if (failure) {
    $('#lite-empty').hidden = false;
    $('#lite-empty').innerHTML = '';
    const p = document.createElement('p'); p.textContent = errorText(failure);
    const button = document.createElement('button'); button.textContent = 'Reset workspace'; button.className = 'danger'; button.onclick = async () => { await app.kv?.destroy?.(); location.reload(); };
    $('#lite-empty').append(p, button);
    return;
  }
  await refreshProjects();
  const last = await app.store.last();
  const shared = sharedDemoId(location.hash);
  if (last) await openProject(last); else showEmptyFile();
  const emptyWorkspace = !last;
  document.documentElement.dataset.liteReady = 'true';
  const shareLink = /^\/(?:mix|m|s)\/([0-9A-HJKMNP-TV-Z]{8})\/?$/.exec(location.pathname);
  const manageCode = /^#manage=([0-9A-Z]{26})$/.exec(location.hash)?.[1];
  if (shareLink && manageCode) openManage({ id: shareLink[1], code: manageCode, token: getToken });   // the management link of the maker
  else if (shareLink) runShareLink(shareLink[1]);   // a link to a shared song
  else if (shared) runShared(shared);   // a shared demo: the window with the steps, then Play
  else if (/[#&]song=/.test(location.hash)) importSongFromHash().catch((error) => toast(errorText(error), 'error'));   // layered sounds from the docs
  else if (emptyWorkspace) await openShared(DEFAULT_DEMO);   // nothing is kept yet (a first visit, or after Reset workspace): the AMPLE demo song is there to look at, not built and not played
}

/** #song=<base64url of UTF-8 text>&title=<encoded title> (the docs site sends layered previews this way): a NEW local project with that song.amplet, opened, not built. */
async function importSongFromHash() {
  const params = new URLSearchParams(location.hash.slice(1));
  const raw = params.get('song') || '';
  const b64 = raw.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - (raw.length % 4)) % 4);
  const bytes = Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
  const text = new TextDecoder().decode(bytes);
  const name = (params.get('title') || '').trim().slice(0, 80) || 'Remix';
  history.replaceState(null, '', location.pathname + location.search);
  if (!text.trim()) return;
  const project = await app.store.createProject({ name, files: [{ path: 'song.amplet', text }] });
  await refreshProjects();
  await openProject(project.id);
  await openFile('song.amplet');
}

start().catch((error) => toast(errorText(error), 'error'));
