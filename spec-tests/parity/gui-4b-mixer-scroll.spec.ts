import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// GUI flow 4: the row of mixer strips scrolls sideways and stays where it is put (no snapping back to the right, and the tabs above it do not move).
const edit = async (op: Record<string, unknown>) => {
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: doc.version, op } });
};
const DEMO = path.resolve('web/remix/demos/mozart');
freshServer({
  song: fs.readFileSync(path.join(DEMO, 'song.amplet'), 'utf8'),
  prepare: (_cli, folder) => { fs.copyFileSync(path.join(DEMO, 'mozart.ampleseq'), path.join(folder, 'mozart.ampleseq')); },
});

test('the strips scroll left and stay there', async ({ page }) => {
  await page.setViewportSize({ width: 700, height: 800 });
  await openApp(page, { reset: false });
  for (let i = 0; i < 12; i++) await edit({ type: 'addBlock', blockType: 'volume', name: `vol${i}` });
  await page.reload();
  await page.locator('#btabs button[data-t="mixer"]').click();
  const view = page.locator('#b-mixer .mixview');
  await expect(view).toBeVisible();
  const read = () => page.evaluate(() => ({ view: document.querySelector('#b-mixer .mixview')!.scrollLeft, max: document.querySelector('#b-mixer .mixview')!.scrollWidth - document.querySelector('#b-mixer .mixview')!.clientWidth, body: document.querySelector('#bbody')!.scrollLeft, tabs: document.querySelector('#btabs')!.scrollLeft }));
  const first = await read();
  expect(first.max).toBeGreaterThan(100);
  await view.hover();
  await page.mouse.wheel(400, 0);
  await page.waitForTimeout(400);
  await page.mouse.wheel(-150, 0);
  await page.waitForTimeout(1200);
  const after = await read();
  expect(after.body).toBe(0);
  expect(after.tabs).toBe(0);
  expect(after.view).toBeGreaterThan(100);
  expect(after.view).toBeLessThan(400);
});
