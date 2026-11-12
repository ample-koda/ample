import { test, expect } from '@playwright/test';
import { freshServerPerTest } from '../../web/tests/support';
import { startedChain, wavFixture } from './support/live-chain-fixture';

freshServerPerTest();

// Local preview 5.3: before and after a block. A tone at the centre of the EQ's mid band, boosted 9 dB: after exceeds before by the boost.
test('the spectrum after an EQ boost exceeds the one before by the boost, and the view draws', async ({ page }) => {
  await startedChain(page, [['eq', 'tone']], wavFixture(2, 866, 6000));
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainPreview('tone', 'mid', '9')));
  await page.waitForTimeout(1500);
  const delta = await page.evaluate(async () => {
    const { chainSpectrum } = await import('/app/live-chain.js');
    const { bandCentres, logBands } = await import('/lib/spectrum-math.mjs');
    const s = chainSpectrum('tone')!;
    const at = [866];
    const pre = logBands(s.pre, s.rate, at, 1 / 3)[0], post = logBands(s.post, s.rate, at, 1 / 3)[0];
    return { pre, post, diff: post - pre };
  });
  expect(delta.diff).toBeGreaterThan(8); expect(delta.diff).toBeLessThan(10);
  await page.locator('.livechain .lcchip[data-block="tone"] .lcspecbtn').click();
  const canvas = page.locator('.livechain .lcspec-canvas');
  await expect(canvas).toBeVisible();
  await expect.poll(() => canvas.evaluate((c: HTMLCanvasElement) => { const d = c.getContext('2d')!.getImageData(0, 0, c.width, c.height).data; const first = [d[0], d[1], d[2]]; for (let i = 0; i < d.length; i += 4) if (d[i] !== first[0] || d[i + 1] !== first[1] || d[i + 2] !== first[2]) return true; return false; }), { timeout: 5000 }).toBe(true);
  await page.getByRole('button', { name: 'Show scope' }).click();
  await expect(page.locator('.livechain .lcspec-head')).toContainText('before');
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
  await expect(page.locator('.livechain')).toHaveCount(0);
});
