import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// A component with Notes in and Audio out (an instrument with its effects): a known block with a notes input and an audio output, built as ordinary blocks.
const DEMO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../demo-projects/components/components');

freshServer({
  song: 'composition "mixed" { ample 1  tempo 100  master-seed 1 }\nimport "components/dream-piano.amplec"\nmidi-clip melody at 1 for 2 {\n  notes 1:0:1:60:100 1:1:1:64:100\n}\nkoda.fx.dreamPiano voice at 1 for 2 {\n  from melody.notes -> notes\n}\n',
  prepare: (_cli, folder) => {
    fs.mkdirSync(path.join(folder, 'components'), { recursive: true });
    fs.copyFileSync(path.join(DEMO, 'dream-piano.amplec'), path.join(folder, 'components', 'dream-piano.amplec'));
  },
});

test.beforeEach(async ({ page }) => { await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } }); await openApp(page, { reset: false }); });

test('the block is known, the palette says notes to sound, and the card has the notes in and the audio out', async ({ page }) => {
  await expect(node(page, 'voice')).toHaveCount(1);
  await expect(node(page, 'voice').locator('.tag.missing')).toHaveCount(0);
  await expect(page.locator('#lib .tplitem[data-tpl="koda.fx.dreamPiano"] .tpl-component-badges')).toContainText('mixed');
  await expect(node(page, 'voice').locator('.port.in')).toHaveCount(1);
  await expect(node(page, 'voice').locator('.port.out')).toHaveCount(1);
  expect(await page.evaluate(async () => { const n = (await import('/app/store.js')).flat().nodes.find((x: { id: string }) => x.id === 'voice'); return JSON.stringify([n.ins, n.outs]); })).toBe('[[["notes","midi"]],[["audio","audio"]]]');
});

test('built, it has a player on the card and its graph shows the piano and the effects', async ({ page }) => {
  await page.evaluate(async () => { await (await import('/app/store.js')).build(); });
  await expect(node(page, 'voice').locator('.player')).toHaveCount(1, { timeout: 60000 });
  const graph = await api('/api/components/graph?path=song.amplet&block=voice');
  expect(graph.nodes.map((n: { id: string }) => n.id)).toEqual(['piano1', 'delay1', 'reverb1']);
});

test('the Live switch loops the instrument the server rendered and runs the effects after it @quarantine', async ({ page }) => {
  await node(page, 'voice').click({ position: { x: 24, y: 12 } });
  const live = page.locator('#props .cui-live');
  await expect(live).toBeEnabled();
  await live.click();
  await expect(page.locator('#toast')).toContainText('Build voice first');   // never builds by itself
  await page.evaluate(async () => { await (await import('/app/store.js')).build(); });
  await expect(node(page, 'voice').locator('.player')).toHaveCount(1, { timeout: 60000 });
  await node(page, 'voice').click({ position: { x: 24, y: 12 } });
  await page.locator('#props .cui-live').click();
  await expect(page.locator('#props .cui-live')).toHaveAttribute('aria-pressed', 'true', { timeout: 15000 });
  await page.locator('#props .cui-live').click();
  await expect(page.locator('#props .cui-live')).toHaveAttribute('aria-pressed', 'false', { timeout: 10000 });
});
