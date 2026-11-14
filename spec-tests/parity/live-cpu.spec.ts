import { test, expect } from '@playwright/test';
import { freshServerPerTest } from '../../web/tests/support';
import { startedChain } from './support/live-chain-fixture';

freshServerPerTest();

// Local preview 5.2: the load badge and the safe mode. The load reading is fed by hand (a worklet has no clock to burn time on request); the policy is what is tested.
async function overload(page, seconds = 2.5) {
  await page.evaluate(async (count) => {
    const { chainFeedLoad } = await import('/app/live-chain.js');
    for (let index = 0; index <= Math.round(count * 4); index++) chainFeedLoad(1.4, index * .25);
  }, seconds);
}

test('@audio a load that stays over 90 percent bypasses the costliest block with a notice, and only the user brings it back', async ({ page }) => {
  await startedChain(page, [['eq', 'tone'], ['reverb', 'room']]);
  await expect(page.locator('.livechain .lccpu')).toContainText('CPU');
  await overload(page);
  const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  expect(state.items.find((item) => item.id === 'room')?.cpuBypassed).toBe(true);
  expect(state.items.find((item) => item.id === 'tone')?.bypassed).toBe(false);   // the cheap EQ is left alone
  await expect(page.locator('.livechain .lcchip[data-block="room"] .lcbypass')).toHaveText('Bypassed (CPU)');
  await expect(page.locator('body')).toContainText('is bypassed (CPU)');
  await page.locator('.livechain .lcchip[data-block="room"] .lcbypass').click();
  const after = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  expect(after.items.find((item) => item.id === 'room')?.bypassed).toBe(false);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});

test('@audio with safe mode off the badge goes red but nothing is bypassed, and the choice is remembered', async ({ page }) => {
  await startedChain(page, [['eq', 'tone'], ['reverb', 'room']]);
  await page.locator('.livechain .lcsafe').click();
  await expect(page.locator('.livechain .lcsafe')).toHaveText('Safe mode: off');
  expect(await page.evaluate(() => localStorage.getItem('ample.live.safeMode'))).toBe('off');
  await overload(page);
  const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  expect(state.items.every((item) => !item.bypassed)).toBe(true);
  expect(state.cpu.level).toBe('bypass');
  await page.evaluate(() => import('/app/live-chain.js').then((m) => { m.setSafeMode('auto'); m.stopChain(); }));
});

test('@audio Auto Safe does not bypass a block that cannot stop computing (an EQ saves no CPU when bypassed)', async ({ page }) => {
  await startedChain(page, [['eq', 'tone']]);
  await overload(page);
  const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  expect(state.items.every((item) => !item.bypassed)).toBe(true);
});
