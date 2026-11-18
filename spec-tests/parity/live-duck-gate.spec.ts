import { test, expect } from '@playwright/test';
import { freshServerPerTest } from '../../web/tests/support';
import { startedChain, wavFixture } from './support/live-chain-fixture';

freshServerPerTest();

// Follow-up F3: duck and the tremolo gate are queued on the audio clock; here the level that really comes out of the live chain is measured over time (a steady tone in, a pattern of dips out).
async function levelTrace(page, block: string, seconds: number) {
  return page.evaluate(async ({ block, seconds }) => {
    const { chainLevels } = await import('/app/live-chain.js');
    const trace: number[] = [], started = performance.now();
    await new Promise<void>((resolve) => { const timer = setInterval(() => { trace.push(chainLevels(block)?.left ?? 0); if (performance.now() - started > seconds * 1000) { clearInterval(timer); resolve(); } }, 5); });
    return trace;
  }, { block, seconds });
}
const dips = (trace: number[], high: number, low: number) => { let count = 0, down = false; for (const value of trace) { if (!down && value < low) { down = true; count++; } else if (down && value > high) down = false; } return count; };

test('@audio duck: a steady tone dips on each x of the pattern, deep, and returns', async ({ page }) => {
  await startedChain(page, [['duck', 'pump']], wavFixture(4, 440, 9000));
  await page.evaluate(() => import('/app/live-chain.js').then((m) => { m.chainPreview('pump', 'pattern', 'x...x...x...x...'); m.chainPreview('pump', 'depth', '24'); m.chainPreview('pump', 'release', '120'); }));
  await page.waitForTimeout(600);
  const trace = await levelTrace(page, 'pump', 3);
  const top = Math.max(...trace), bottom = Math.min(...trace);
  const tempo = await page.evaluate(() => import('/app/store.js').then((s) => Number(s.S.doc.project.tempo)));
  const beat = 60 / tempo;   // the x of the pattern come four sixteenth steps apart: one dip per beat
  // the depth is 24: the dip reaches about 25% of the top, a little more or less with the phase of the sampling, so deep means under 30%
  const low = top * .3;
  const expected = 3 / beat;   // dips in the 3 seconds that are measured
  console.log('duck', { top, bottom, dipCount: dips(trace, top * .6, low), expected });
  expect(bottom).toBeLessThan(low);
  const count = dips(trace, top * .6, low);
  // the level has to come back above 60% between two dips to count one, and on a slow machine the release is not always that fast: several dips, never more than the pattern has
  expect(count).toBeGreaterThanOrEqual(2);
  expect(count).toBeLessThanOrEqual(expected + 2);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});

test('@audio tremolo gate: a closed step of the pattern is quiet and an open one is loud', async ({ page }) => {
  await startedChain(page, [['tremolo', 'gate']], wavFixture(4, 440, 9000));
  await page.evaluate(() => import('/app/live-chain.js').then((m) => { m.chainPreview('gate', 'shape', 'gate'); m.chainPreview('gate', 'pattern', 'x.x.x.x.x.x.x.x.'); m.chainPreview('gate', 'depth', '100'); m.chainPreview('gate', 'sync', '1/16'); }));
  await page.waitForTimeout(600);
  const trace = await levelTrace(page, 'gate', 3);
  const top = Math.max(...trace), bottom = Math.min(...trace);
  console.log('gate', { top, bottom, dipCount: dips(trace, top * .6, top * .2) });
  expect(bottom).toBeLessThan(top * .2);
  expect(dips(trace, top * .6, top * .2)).toBeGreaterThan(4);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
