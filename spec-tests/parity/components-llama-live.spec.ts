import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The Delay Llama inside a component sings live in the browser: no build, the voice changes while a knob moves.
const DEMO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../demo-projects/components/components');

freshServer({
  song: 'composition "llama" { ample 1  tempo 100  master-seed 1 }\nimport "components/llama-choir.amplec"\nmidi-clip melody at 1 for 2 {\n  notes 1:0:2:57:100 1:2:2:60:100\n}\nkoda.fx.llamaChoir choir {\n  from melody.notes -> notes\n}\n',
  prepare: (_cli, folder) => {
    fs.mkdirSync(path.join(folder, 'components'), { recursive: true });
    fs.copyFileSync(path.join(DEMO, 'llama-choir.amplec'), path.join(folder, 'components', 'llama-choir.amplec'));
  },
});

test.beforeEach(async ({ page }) => { await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } }); await openApp(page, { reset: false }); });

test('the llama choir sings live without a build, a knob sends its values, and the switch stops it', async ({ page }) => {
  let sent = 0;
  await page.route('**/api/components/graph-values', (route) => { sent++; return route.continue(); });
  await node(page, 'choir').click({ position: { x: 24, y: 12 } });
  const live = page.locator('#props .cui-live');
  await expect(live).toBeEnabled();
  await live.click();
  await expect(page.locator('#props .cui-live')).toHaveAttribute('aria-pressed', 'true', { timeout: 20000 });
  const knob = page.locator('#props .cui svg[role=slider]').first();
  const box = (await knob.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2 - 30, { steps: 6 });
  await expect.poll(() => sent, { timeout: 10000 }).toBeGreaterThan(0);
  await page.mouse.up();
  await expect(page.locator('body')).not.toHaveClass(/studio-dragging/);   // the cursor is back
  await page.locator('#props .cui-live').click();
  await expect(page.locator('#props .cui-live')).toHaveAttribute('aria-pressed', 'false', { timeout: 10000 });
});

test('a double click on a knob opens a field to type the value, Enter applies it', async ({ page }) => {
  await node(page, 'choir').click({ position: { x: 24, y: 12 } });
  const knob = page.locator('#props .cui svg[role=slider]').first();
  await knob.dblclick();
  const entry = page.locator('#props .studio-knob-entry');
  await expect(entry).toBeVisible();
  await entry.fill('12');
  await entry.press('Enter');
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text, { timeout: 10000 }).toMatch(/vowel 12/);
  await expect(page.locator('body')).not.toHaveClass(/studio-dragging/);
});

test('the Stop of the header is enabled while a live preview goes and ends it', async ({ page }) => {
  await node(page, 'choir').click({ position: { x: 24, y: 12 } });
  const stop = page.locator('#btn-stop');
  await expect(stop).toBeDisabled();
  await page.locator('#props .cui-live').click();
  await expect(page.locator('#props .cui-live')).toHaveAttribute('aria-pressed', 'true', { timeout: 20000 });
  await expect(stop).toBeEnabled();
  await stop.click();
  await expect(page.locator('#props .cui-live')).toHaveAttribute('aria-pressed', 'false', { timeout: 10000 });
  await expect(stop).toBeDisabled();
});
