import { test, expect } from '@playwright/test';
import { node, openApp, state } from '../../web/tests/support';

test.beforeEach(async ({ page }) => { await openApp(page); await page.click('#ltabs button[data-l="struct"]'); });

test('the structure tab lists the blocks and clicking one selects it on the canvas', async ({ page }) => {
  const rows = page.locator('#pane-struct .blk');
  await expect(rows).toHaveCount(6);
  await expect(rows.first()).toContainText('chords');
  await rows.nth(2).click();
  expect((await state(page)).sel).toEqual(['bass']);
  await expect(node(page, 'bass')).toHaveClass(/sel/);
});

test('the problems of the song are listed with their codes', async ({ page }) => {
  await expect(page.locator('#pane-struct')).toContainText('Problems');
  await expect(page.locator('#pane-struct')).toContainText('none');
});

test('the outline of the language server (over the websocket) equals the structure list', async ({ page }) => {
  const names = await page.evaluate(() => new Promise<string[]>((resolve, reject) => {
    const ws = new WebSocket(`ws://${location.host}/lsp`);
    const text = (window as any).AMPLE_STATE.doc.text as string;
    ws.onerror = () => reject(new Error('websocket failed'));
    ws.onopen = () => {
      ws.send(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { capabilities: {} } }));
      ws.send(JSON.stringify({ jsonrpc: '2.0', method: 'textDocument/didOpen', params: { textDocument: { uri: 'file:///song.amplet', languageId: 'amplet', version: 1, text } } }));
      ws.send(JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'textDocument/documentSymbol', params: { textDocument: { uri: 'file:///song.amplet' } } }));
    };
    ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id === 2) { ws.close(); resolve(m.result.map((s: { name: string }) => s.name)); } };
  }));
  const listed = await page.locator('#pane-struct .blk span:nth-child(2)').allTextContents();
  expect(names).toEqual(listed);
});
