import { test, expect } from '@playwright/test';
import { addTemplateBlock, api, freshServerPerTest, openApp } from '../../web/tests/support';
import { connect } from './support/live-chain-fixture';

freshServerPerTest();

test('@audio a rewind knob glides the loop (approximate) while turning, then the exact render is swapped in at normal speed', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'beat'); await addTemplateBlock(page, 'rewind', 'back'); await addTemplateBlock(page, 'eq', 'tone');
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: doc.version, op: { type: 'setParameter', block: 'beat', name: 'kit', value: 'builtin' } } });
  await page.reload(); await page.waitForSelector('body[data-ready="1"]');
  await connect(page, 'beat', 'back'); await connect(page, 'back', 'tone');
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => s.flat().links.filter((l) => l.type === 'audio').length))).toBeGreaterThanOrEqual(2);
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('tone'))).toBe(true);
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('tone')?.left ?? 0)), { timeout: 15000 }).toBeGreaterThan(0.003);
  const swaps = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState().swaps));
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainPreview('back', 'fade', '20')));
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState().gliding))).toEqual(['back']);
  await expect(page.locator('.livechain')).toContainText('approximate while turning');
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState().swaps)), { timeout: 15000 }).toBeGreaterThan(swaps);
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState().gliding))).toEqual([]);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
