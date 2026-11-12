import { test, expect } from '@playwright/test';
import { freshServerPerTest } from '../../web/tests/support';
import { startedChain, wavFixture } from './support/live-chain-fixture';

freshServerPerTest();

// Local preview 5.5: a loop range of a few bars. A 12 s take, a bar of the song tempo; a 1-bar range keeps the loop position inside one bar.
test('a 1-bar loop range keeps the playhead inside one bar, and the choice is remembered', async ({ page }) => {
  await startedChain(page, [['eq', 'tone']], wavFixture(12, 440, 6000));
  await page.locator('.livechain .lcloop').selectOption('1');
  expect(await page.evaluate(() => localStorage.getItem('ample.live.loopBars'))).toBe('1');
  const first = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  const bar = await page.evaluate(() => import('/app/store.js').then((s) => 60 / Number(s.S.doc.project.tempo) * 4));
  expect(first.region.length).toBeCloseTo(bar, 5);
  const samples: number[] = [];
  for (let index = 0; index < 8; index++) { samples.push(await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState().position))); await page.waitForTimeout(400); }
  for (const position of samples) { expect(position).toBeGreaterThanOrEqual(first.region.start - 0.01); expect(position).toBeLessThan(first.region.start + first.region.length + 0.01); }
  // 3.2 s went by: with a one-bar period the position must have wrapped at least once
  expect(Math.min(...samples.slice(1).map((value, i) => value - samples[i]))).toBeLessThan(-first.region.length * 0.4);
  await page.locator('.livechain .lcloop').selectOption('0');
  expect((await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()))).region).toBe(null);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => { m.setLoopBars(0); m.stopChain(); }));
});
