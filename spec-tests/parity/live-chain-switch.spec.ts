import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServerPerTest, openApp } from '../../web/tests/support';
import { connect, startedChain, wavFixture } from './support/live-chain-fixture';

freshServerPerTest();

// A chain that is still preparing (its takes and impulses are fetched while the old chain keeps playing) is cancelled by Stop and by opening another project,
// and a switch that fails while preparing leaves the old chain playing.

async function slowChain(page) {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'src');
  await addTemplateBlock(page, 'eq', 'tone');
  await connect(page, 'src', 'tone');
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => s.flat().links.some((l) => l.type === 'audio' && l.from === 'src' && l.to === 'tone')))).toBe(true);
  await page.route('**/api/takes/**/download*', async (route) => { await new Promise((resolve) => setTimeout(resolve, 1500)); await route.fulfill({ status: 200, contentType: 'audio/wav', body: wavFixture() }); });
  await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'src' ? 'take-src' : undefined) }), set: () => {} }); }));
}

for (const [name, cancel] of [['Stop', "m.stopChain()"], ['opening another project', "document.dispatchEvent(new Event('ample:open'))"]] as const) {
  test(`@audio ${name} while a chain is still preparing cancels that start`, async ({ page }) => {
    await slowChain(page);
    const started = await page.evaluate(async (code) => {
      const m = await import('/app/live-chain.js');
      const pending = m.startChain('tone');
      await new Promise((resolve) => setTimeout(resolve, 300));
      new Function('m', code)(m);
      return pending;
    }, cancel);
    expect(started).toBe(false);
    await page.waitForTimeout(2500);
    expect(await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainActive())), 'the cancelled start did not begin to play afterwards').toBe(false);
  });
}

test('@audio a switch that fails while preparing leaves the old chain playing', async ({ page }) => {
  await startedChain(page, [['convolution', 'room']]);
  let impulseRequests = 0;
  await page.route('**/api/effects/impulse', (route) => { impulseRequests++; return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'no impulse today' }) }); });
  const switched = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.startChain('room')));
  expect(switched).toBe(false);
  expect(impulseRequests).toBeGreaterThan(0);
  expect(await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainActive())), 'the old chain is still there').toBe(true);
  await page.waitForTimeout(600);
  expect(await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('room')?.left ?? 0)), 'and still sounds').toBeGreaterThan(0.002);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
