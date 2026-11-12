import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';
import { demoSong } from '../../web/tests/server';

// Plan local-preview 2.8 (fire): notes made from a control play an instrument in the live chain without a build.
freshServer({ song: `${demoSong()}\ncurve ramp {\n  value auto(1:0, 2:1000, 3:0)\n}\nfire hit {\n  threshold 0.5\n  note 60\n  length 400 ms\n  from ramp.level -> in\n}\nsynth lead {\n  from hit.notes -> notes\n}\nreverb room {\n  from lead.audio -> audio\n}\n` });
test('a fire block plays a synth through the live chain', async ({ page }) => {
  await openApp(page, { reset: false });
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('room'))).toBe(true);
  expect((await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()))).items.find((i: any) => i.id === 'lead').kind).toBe('local');
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('lead')?.left ?? 0)), { timeout: 20000 }).toBeGreaterThan(0.005);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
