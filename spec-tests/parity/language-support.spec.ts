import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

test.beforeEach(async ({ page }) => { await openApp(page); });

/** Sends requests to the language server over the WebSocket of the page and returns the answers by id. */
async function lsp(page, text: string, requests: Array<{ method: string; params: unknown }>) {
  return page.evaluate(({ text, requests }) => new Promise<Record<string, any>>((resolve, reject) => {
    const ws = new WebSocket(`ws://${location.host}/lsp`);
    const out: Record<string, any> = {};
    const uri = 'file:///t.amplet';
    ws.onerror = () => reject(new Error('websocket failed'));
    ws.onopen = () => {
      ws.send(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { capabilities: {} } }));
      ws.send(JSON.stringify({ jsonrpc: '2.0', method: 'textDocument/didOpen', params: { textDocument: { uri, languageId: 'amplet', version: 1, text } } }));
      requests.forEach((r, i) => ws.send(JSON.stringify({ jsonrpc: '2.0', id: 10 + i, method: r.method, params: { textDocument: { uri }, ...(r.params as object) } })));
    };
    ws.onmessage = (e) => {
      const m = JSON.parse(e.data);
      if (m.method === 'textDocument/publishDiagnostics') out.diagnostics = m.params.diagnostics;
      if (m.id >= 10) out[m.id - 10] = m.result;
      if (out.diagnostics && requests.every((_, i) => i in out)) { ws.close(); resolve(out); }
    };
  }), { text, requests });
}

test('a song with a mistake gets coded diagnostics with a link to the help', async ({ page }) => {
  const r = await lsp(page, 'midi-clip chords {\n}\n', []);
  const codes = r.diagnostics.map((d: any) => d.code);
  expect(codes).toContain('AMP1001');
  expect(r.diagnostics.find((d: any) => d.code === 'AMP1001').codeDescription.href).toBe('/help#AMP1001');
});

test('completion offers block types and hover explains one', async ({ page }) => {
  const song = 'composition "demo" { ample 1  tempo 92  master-seed 1 }\nmidi-clip chords {\n  chords Am\n}\n\n';
  const r = await lsp(page, song, [
    { method: 'textDocument/completion', params: { position: { line: 4, character: 0 } } },
    { method: 'textDocument/hover', params: { position: { line: 1, character: 3 } } },
  ]);
  expect(r[0].items.map((i: any) => i.label)).toContain('drums');
  expect(r[1].contents.value.length).toBeGreaterThan(5);
});

test('the lint rules report with their codes', async ({ page }) => {
  const r = await lsp(page, 'composition "demo" { ample 1  tempo 92  master-seed 1 }\ndrums d {\n}\n', []);
  expect(r.diagnostics.some((d: any) => /^L00\d$/.test(d.code))).toBe(true);
});
