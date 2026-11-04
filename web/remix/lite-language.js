// The language tooling of Remix for the open file. A song (.amplet) is checked, completed and explained by the same language server as Studio, over a WebSocket (/lsp/remix) that
// is first given the text files of the project; a component file (.amplec) goes through the component checks (/api/remix/components/...), like in Studio. The server never reads a
// file of its own for either: it only sees what the page sends.

import { LspClient } from '../app/lsp.js';
import { getToken } from '../app/api.js';
import { isComponent, isSong } from './lite-paths.mjs';

const SEVERITY = { 1: 'error', 2: 'warning', 3: 'info', 4: 'info' };
const toDiagnostic = (d) => ({ line: d.range.start.line + 1, col: d.range.start.character + 1, endCol: d.range.end.character + 1, severity: SEVERITY[d.severity] ?? 'info', code: d.code ?? '', message: d.message });

async function post(path, body) {
  const response = await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Ample-Token': getToken() }, body: JSON.stringify(body) });
  if (!response.ok) throw new Error(`${response.status}`);
  return response.json();
}

/** `textFiles()` gives the current text files of the project: [{path, text}]. `onDiagnostics(list)` is called with the problems of the open file. */
export function createLanguage({ textFiles, onDiagnostics, onOffline = () => {}, onOnline = () => {} }) {
  let client = null;
  let path = '';
  let text = '';
  let sent = '';   // the text the language server has
  let timer = 0;
  let generation = 0;

  let retries = 0;
  let reconnecting = false;

  const others = () => textFiles().filter((f) => f.path !== path);

  /** The connection was lost (a phone that sleeps or leaves the tab closes it): connect again by itself a few times, and say "offline" only when that does not work. */
  function lost(from) {
    if (from !== client) return;
    if (!isSong(path) || retries >= 3) { onOffline(); return; }
    retries++;
    setTimeout(() => reconnect(), 300 * retries * retries);
  }

  async function reconnect() {
    if (reconnecting || !path || !isSong(path)) return;
    reconnecting = true;
    const before = client;
    try {
      client = new LspClient(`lite:///${path}`, { url: '/lsp/remix' });
      before?.close();
      await connectSong();
      retries = 0;
      onOnline();
    } catch {
      lost(client);
    } finally {
      reconnecting = false;
    }
  }

  // coming back to the tab: a socket that died in the background is replaced at once
  if (typeof document !== 'undefined') {
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState !== 'visible' || !client || !isSong(path)) return;
      const state = client.ws?.readyState;
      if (state === undefined || state > 1) { retries = 0; reconnect(); }
    });
  }

  async function checkComponent() {
    const mine = ++generation;
    try {
      const result = await post('/api/remix/components/check', { files: others(), text });
      if (mine === generation) onDiagnostics(result.diagnostics.map(toDiagnostic));
    } catch { if (mine === generation) onOffline(); }
  }

  async function connectSong() {
    const mine = client;
    mine.onDiagnostics = (list) => { if (mine === client) onDiagnostics(list.map(toDiagnostic)); };
    mine.onOffline = () => lost(mine);
    await mine.connect();
    mine.send({ method: 'ample/lite/files', params: { files: textFiles() } });
    sent = text;
    await mine.open(text);
  }

  /** An import line ("import "components/x.amplec"") with the caret inside the quotes: the file it names, when the project has it (the name as written, or with .amplec, or under components/). */
  function importTarget(position) {
    const line = text.split(String.fromCharCode(10))[position.line] ?? '';
    const match = /^(\s*import\s+")([^"]+)"/.exec(line);
    if (!match) return null;
    const start = match[1].length;
    if (position.character < start || position.character > start + match[2].length) return null;
    const have = new Set(textFiles().map((f) => f.path));
    const name = match[2].replace(/^\.?\/+/, '');
    const found = [name, `${name}.amplec`, `components/${name}`, `components/${name}.amplec`].find((c) => have.has(c));
    return found ? { path: found === path ? undefined : found, line: 0, character: 0 } : null;
  }

  const api = {
    /** Starts for a file: a song or a component file (any other kind has no language tooling). */
    async open(newPath, newText) {
      this.close();
      path = newPath;
      text = newText;
      if (isSong(path)) {
        client = new LspClient(`lite:///${path}`, { url: '/lsp/remix' });
        retries = 0;
        const opened = client;
        await connectSong().catch(() => lost(opened));
      } else if (isComponent(path)) {
        await checkComponent();
      } else {
        onDiagnostics([]);
      }
    },
    /** The text of the open file changed. */
    change(newText) {
      text = newText;
      clearTimeout(timer);
      timer = setTimeout(() => {
        timer = 0;
        if (client) { sent = text; client.change(text).catch(() => lost(client)); }
        else if (isComponent(path)) checkComponent();
      }, 250);
    },
    /** Another file of the project changed (a component the song imports): the language server gets the new text and rereads the open song. */
    fileChanged(changedPath, changedText, deleted = false) {
      if (changedPath === path || !client) return;
      client.send({ method: 'ample/lite/file', params: { path: changedPath, text: changedText ?? '', deleted } });
      clearTimeout(timer);
      timer = setTimeout(() => { timer = 0; sent = text; client?.change(text).catch(() => {}); }, 120);
    },
    /** A question (completion, hover, definition) is about the text as it is now: a change that still waits for its turn (the editor tells the change a moment after a key) is sent first. */
    async sync(current) {
      if (!client) return;
      if (current !== undefined) text = current;
      if (text === sent) return;
      clearTimeout(timer);
      timer = 0;
      sent = text;
      await client.change(text).catch(() => lost(client));
    },
    async complete(position, current) {
      if (client) {
        await api.sync(current);
        const result = await client.ask('textDocument/completion', { position: { line: position.line, character: position.character } });
        return (result?.items ?? []).map((i) => ({ label: i.label, detail: i.detail ?? '', insert: i.insertText }));
      }
      if (isComponent(path)) {
        const result = await post('/api/remix/components/complete', { files: others(), text: current, line: position.line, character: position.character });
        return (result.items ?? []).map((i) => ({ label: i.label, detail: i.detail ?? '', insert: i.insert ?? undefined }));
      }
      return [];
    },
    async hover(position) {
      if (!client) return null;
      await api.sync();
      const result = await client.ask('textDocument/hover', { position });
      return result?.contents?.value ?? null;
    },
    /** Where the thing under the caret is declared: {path?, line, character} (path only when it is another file of the project). An import line gives the file it names. */
    async definition(position) {
      const imported = importTarget(position);
      if (imported) return imported;
      if (!client) return null;
      await api.sync();
      const result = await client.ask('textDocument/definition', { position });
      const found = Array.isArray(result) ? result[0] : result;
      if (!found?.uri || !found.range) return null;
      // the type of a component that lives in an imported file: the server names the file in `file`
      const named = typeof found.file === 'string' && found.file ? found.file.replace(/^\.?\/+/, '') : null;
      const target = named ?? (found.uri.startsWith('lite:///') ? found.uri.slice('lite:///'.length) : found.uri.replace(/^file:\/+/, ''));
      return { path: target === path ? undefined : target, line: found.range.start.line, character: found.range.start.character };
    },
    close() {
      clearTimeout(timer);
      generation++;
      client?.close();
      client = null;
    },
  };
  return api;
}
