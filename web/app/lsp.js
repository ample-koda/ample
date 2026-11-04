// A small client of the language server over the WebSocket of "ample serve" (/lsp). The page sends the whole text on every
// change (full sync); the answers come from Ample.Lsp, so the editor, the CLI and the UI agree. @ample:language-support

export class LspClient {
  /** `url` is the path of the socket on this host: /lsp (the project of the server) or another one, like /lsp/remix. */
  constructor(uri, { url = '/lsp' } = {}) {
    this.uri = uri;
    this.url = url;
    this.id = 0;
    this.pending = new Map();
    this.version = 0;
    this.onDiagnostics = () => {};
    this.onOffline = () => {};   // the page falls back on what the browser can check by itself
    this.closing = false;
    this.ready = null;
  }

  connect() {
    if (this.ready) return this.ready;
    this.ready = new Promise((resolve, reject) => {
      const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}${this.url}`);
      this.ws = ws;
      ws.onerror = () => { this.ready = null; reject(new Error('The language server is not reachable.')); if (!this.closing) this.onOffline(); };
      ws.onclose = () => { this.ready = null; for (const p of this.pending.values()) p.reject(new Error('The language server closed.')); this.pending.clear(); if (!this.closing) this.onOffline(); };
      ws.onmessage = (e) => {
        const m = JSON.parse(e.data);
        if (m.method === 'textDocument/publishDiagnostics' && m.params.uri === this.uri) this.onDiagnostics(m.params.diagnostics);
        else if (m.id !== undefined && this.pending.has(m.id)) {
          const p = this.pending.get(m.id);
          this.pending.delete(m.id);
          if (m.error) p.reject(new Error(m.error.message)); else p.resolve(m.result);
        }
      };
      ws.onopen = () => this.request('initialize', { capabilities: {} }).then(() => resolve(), reject);
    });
    return this.ready;
  }

  send(msg) { this.ws.send(JSON.stringify({ jsonrpc: '2.0', ...msg })); }

  request(method, params) {
    return new Promise((resolve, reject) => {
      const id = ++this.id;
      this.pending.set(id, { resolve, reject });
      this.send({ id, method, params });
    });
  }

  async open(text) {
    await this.connect();
    this.version = 1;
    this.send({ method: 'textDocument/didOpen', params: { textDocument: { uri: this.uri, languageId: 'amplet', version: 1, text } } });
  }

  async change(text) {
    await this.connect();
    this.send({ method: 'textDocument/didChange', params: { textDocument: { uri: this.uri, version: ++this.version }, contentChanges: [{ text }] } });
  }

  async ask(method, extra) {
    await this.connect();
    return this.request(method, { textDocument: { uri: this.uri }, ...extra });
  }

  close() { this.closing = true; if (this.ws) this.ws.close(); this.ready = null; }
}
