import { test, expect } from '@playwright/test';
import { addTemplateBlock, openApp } from '../../web/tests/support';
import { connect, wavFixture } from './support/live-chain-fixture';
import { freshServerPerTest } from '../../web/tests/support';

freshServerPerTest();

// Local preview 6.3: selecting a block warms the chain, so the start itself needs no download.
test('@audio selecting an effect downloads its source once, and the chain then starts from the cache', async ({ page }) => {
  let hits = 0;
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'src');
  await addTemplateBlock(page, 'eq', 'tone');
  await connect(page, 'src', 'tone');
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => s.flat().links.some((l) => l.from === 'src' && l.to === 'tone')))).toBe(true);
  const wav = wavFixture();
  await page.route('**/api/takes/**/download*', async (route) => { hits++; await new Promise((r) => setTimeout(r, 300)); await route.fulfill({ status: 200, contentType: 'audio/wav', body: wav }); });
  await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'src' ? 'take-src' : undefined) }), set: () => {} }); }));
  await page.evaluate(() => import('/app/live-chain.js'));   // loads the module (it listens to the selection)
  await page.evaluate(() => import('/app/store.js').then((s) => { s.S.sel = 'tone'; s.notifySelection(); }));
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.prewarmCount())), { timeout: 8000 }).toBe(1);
  expect(hits).toBe(1);
  const took = await page.evaluate(async () => { const t = performance.now(); const ok = await (await import('/app/live-chain.js')).startChain('tone'); return { ok, ms: performance.now() - t }; });
  expect(took.ok).toBe(true);
  expect(hits).toBe(1);   // no second download
  expect(took.ms).toBeLessThan(250);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
