// The song log tab: the toggles of the session layer (assertions, log level) and the events of the log view (plan/diagnostics WP8).
// A toggle sets the value in the server session (GET/PUT /api/diagnostics/session) and in the browser evaluation
// (setAssertionsSession) in the same step, so both runtimes check the same assertions; it is kept in memory only and never
// changes the song or the music.
import { el, toast } from './ui.js';
import { S, notify } from './store.js';
import { api } from './api.js';
import { setAssertionsSession } from './song-helpers.js';
import { LEVELS, emptyText, inSong, levelCounts, logRows, messageText, whereText } from '../lib/log-view.mjs';

const ASSERTIONS = 'diagnostics.assertions';
const LEVEL = 'logging.level';
let values = {};
let loaded = false;
// the levels the log view shows (a view choice of this page, not a session value: it changes what is shown, not what is kept)
const shown = new Set(LEVELS);

/** Reads the session values of the server once (a reload of the page keeps the server session). */
async function load() {
  if (loaded) return;
  loaded = true;
  try {
    values = { ...((await api.get('/api/diagnostics/session'))?.values || {}) };
    applyBrowser();
    notify();
  } catch { /* an older server has no session layer: the toggles start as the song says */ }
}

function applyBrowser() {
  setAssertionsSession(values[ASSERTIONS] === 'on' ? true : values[ASSERTIONS] === 'off' ? false : null);
}

// Changes run one after another: each starts from the values the one before left (two quick choices never undo each other),
// and only the newest read of the song is applied (an older answer that comes in late never overwrites a newer one).
let queue = Promise.resolve();
let reads = 0;

/** Sets one session value ('' is "as the song says"), then reads the song again so its problems and events follow. */
export function setSessionValue(key, value) {
  const run = queue.then(() => applySessionValue(key, value));
  queue = run.catch(() => {});
  return run;
}

async function applySessionValue(key, value) {
  const next = { ...values };
  if (value) next[key] = value; else delete next[key];
  const answer = await api.put('/api/diagnostics/session', { values: next });
  values = { ...(answer?.values || next) };
  applyBrowser();
  if (S.path) {
    const read = ++reads;
    const doc = await api.get(`/api/doc?path=${encodeURIComponent(S.path)}`);
    if (read === reads && S.doc && doc.path === S.doc.path) {
      S.doc = { ...S.doc, diagnostics: doc.diagnostics, observations: doc.observations };
      S.songDiagnostics = doc.diagnostics || [];
    }
  }
  notify();
}

function choice(label, key, options, title) {
  const select = el('select', { title, 'aria-label': label });
  for (const [value, text] of options) select.append(el('option', { value, textContent: text }));
  select.value = values[key] ?? '';
  select.dataset.session = key;
  select.onchange = () => setSessionValue(key, select.value).catch((error) => { toast(error.message || String(error)); select.value = values[key] ?? ''; });
  return el('label', { className: 'song-log-choice' }, [el('span', { textContent: label }), select]);
}

/** The toggles at the top of the song log. */
export function sessionTools() {
  load();
  return el('div', { className: 'song-log-tools', title: 'For this session only: never written to the song, never changes the music' }, [
    choice('Assertions', ASSERTIONS, [['', 'as the song'], ['on', 'on'], ['off', 'off']], 'Check the assert statements of the song in this session (server and browser)'),
    choice('Log level', LEVEL, [['', 'as the song'], ['off', 'off'], ['warning', 'warning'], ['info', 'info'], ['debug', 'debug'], ['trace', 'trace']], 'Which log, debug and trace events of the song are kept in this session'),
  ]);
}

/** The events of the log view of the song (log, debug, trace and warn) in the order they happened: a filter per level, repeats folded into one row with a count, a click
 * on an event of the song goes to its line, the stack and the hint under it, and a budget notice says how much was left out. */
export function eventRows() {
  const events = S.doc?.observations || [];
  const counts = levelCounts(events);
  const filters = el('div', { className: 'song-log-filters', role: 'group', 'aria-label': 'Levels shown' }, LEVELS.map((level) => {
    const button = el('button', { type: 'button', className: `song-log-filter ${level}`, textContent: `${level} ${counts[level]}`, title: `Show or hide the ${level} events` });
    button.setAttribute('aria-pressed', String(shown.has(level)));
    button.dataset.level = level;
    button.onclick = () => { if (shown.has(level)) shown.delete(level); else shown.add(level); notify(); };
    return button;
  }));
  const rows = logRows(events, shown);
  const out = [el('div', { className: 'song-log-events-head' }, [el('span', { textContent: `Log (${events.length})` }), filters])];
  if (!rows.length) out.push(el('div', { className: 'song-log-empty song-log-events-empty', textContent: emptyText({ events, level: values[LEVEL] ?? '' }) }));
  for (const { kind, event, count } of rows) {
    const row = el('div', { className: `song-event ${kind === 'budget' ? 'budget' : event.level}` });
    row.dataset.level = event.level;
    const head = `${kind === 'budget' ? 'left out' : event.level}  ${whereText(event)}`;
    const body = el('span', { className: 'song-event-message', textContent: messageText(event) });
    if (count > 1) body.append(el('span', { className: 'song-event-count', textContent: ` \u00d7${count}`, title: `${count} times the same event` }));
    row.append(el('span', { className: 'song-event-head', textContent: head }), body);
    const more = [event.hint, ...(event.stack ?? []).map((frame) => `at ${typeof frame === 'string' ? frame : [frame.name, frame.instance && `in ${frame.instance}`].filter(Boolean).join(' ')}`)].filter(Boolean);
    if (more.length) row.append(el('span', { className: 'song-event-more', textContent: more.join('\n') }));
    if (inSong(event)) {
      row.tabIndex = 0;
      row.title = 'Double-click: open this line in Source';
      const open = () => import('./source.js').then((source) => source.showSongDiagnostic({ line: event.line, col: event.col }));
      row.ondblclick = open;
      row.onkeydown = (keyEvent) => { if (keyEvent.key === 'Enter') { keyEvent.preventDefault(); open(); } };
    }
    out.push(row);
  }
  return out;
}
