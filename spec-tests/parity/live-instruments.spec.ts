import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';
import { demoSong } from '../../web/tests/server';

// Plan local-preview 3.4/3.5: instruments that play the notes of a clip are made in the browser inside the live chain, without a build.
freshServer({ song: `${demoSong()}\nsynth lead {\n  wave saw\n  from chords.notes -> notes\n}\nreverb room {\n  from lead.audio -> audio\n}\nreverb room2 {\n  from piano.audio -> audio\n}\n` });
for (const [target, source] of [['room', 'lead'], ['room2', 'piano']]) {
  test(`${source} plays through the live chain without a build`, async ({ page }) => {
    await openApp(page, { reset: false });
    expect(await page.evaluate(async (t) => (await import('/app/live-chain.js')).startChain(t), target)).toBe(true);
    const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
    expect(state.items.find((i: any) => i.id === source).kind).toBe('local');
    await expect.poll(() => page.evaluate((id) => import('/app/live-chain.js').then((m) => m.chainLevels(id)?.left ?? 0), source), { timeout: 10000 }).toBeGreaterThan(0.005);
    await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
  });
}
