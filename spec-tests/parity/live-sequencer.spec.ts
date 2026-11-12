import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { freshServer, openApp } from '../../web/tests/support';

// Plan local-preview 3.6: the lanes of an inline sequencer are made in the browser in the live chain (one item per used output), without a build.
freshServer({ song: fs.readFileSync(path.resolve('docs/clientside-preview/demo/advanced/sequencer.amplet'), 'utf8') });
test('sequencer lanes play through the live chain and the mixer without a build', async ({ page }) => {
  await openApp(page, { reset: false });
  const started = await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('mix'));
  expect(started).toBe(true);
  const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  const ids = state.items.map((i: any) => i.id);
  expect(ids).toEqual(expect.arrayContaining(['sq:lane1', 'sq:lane2', 'space', 'mix']));
  expect(state.items.find((i: any) => i.id === 'sq:lane2').kind).toBe('local');
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('mix')?.left ?? 0)), { timeout: 10000 }).toBeGreaterThan(0.005);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
