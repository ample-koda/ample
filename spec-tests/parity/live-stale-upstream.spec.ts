import { test, expect } from '@playwright/test';
import { freshServerPerTest, addTemplateBlock, openApp } from '../../web/tests/support';
import { connect, wavFixture } from './support/live-chain-fixture';

freshServerPerTest();

// Local preview 8.1: when a block upstream changes after it was built (the editor drops its take) the live chain keeps playing the old build, says so, and restarts on the new build.
test('@audio a block upstream that changes does not stop the chain: it is marked out of date until rebuilt', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'src');
  await addTemplateBlock(page, 'eq', 'tone');
  await connect(page, 'src', 'tone');
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => s.flat().links.some((l) => l.from === 'src' && l.to === 'tone')))).toBe(true);
  await page.route('**/api/takes/**/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: wavFixture() }));
  await page.evaluate(() => import('/app/store.js').then((s) => { (window as any).__take = 'take-1'; Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'src' ? (window as any).__take : undefined) }), set: () => {} }); }));
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('tone'))).toBe(true);
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('tone')?.left ?? 0)), { timeout: 8000 }).toBeGreaterThan(0.005);

  // the upstream block changes: its take is gone
  await page.evaluate(async () => { (window as any).__take = undefined; const s = await import('/app/store.js'); await s.edit([{ type: 'setParameter', block: 'src', name: 'humanize', value: '5' }]); });
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState().stale))).toEqual(['src']);
  expect(await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState().active))).toBe(true);
  await expect(page.locator('.livechain .lcstale')).toContainText('Out of date');
  expect(await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('tone')?.left ?? 0))).toBeGreaterThanOrEqual(0);

  // rebuilt: a new take id restarts the chain on it and the mark goes
  await page.evaluate(async () => { (window as any).__take = 'take-2'; const s = await import('/app/store.js'); await s.edit([{ type: 'setParameter', block: 'src', name: 'humanize', value: '6' }]); });
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState().stale)), { timeout: 10000 }).toEqual([]);
  await expect(page.locator('.livechain .lcstale')).toHaveCount(0);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
