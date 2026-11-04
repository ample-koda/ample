import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

const vectors = JSON.parse(fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'prng', 'vectors.json'), 'utf8'));

test('the published vectors give the same integers in the browser as in the C# code (and in Node, in the unit tests)', async ({ page }) => {
  await openApp(page);
  const outputs: string[] = await page.evaluate(async (seed) => {
    const mod = await import('/lib/stable-rng.js');
    const g = mod.splitMix64(seed);
    return Array.from({ length: 5 }, () => String(g.next().value));
  }, vectors.splitmix64.seed);
  expect(outputs).toEqual(vectors.splitmix64.outputs);

  for (const v of vectors.xoshiro256ss) {
    const first: string[] = await page.evaluate(async ({ seed, n }) => {
      const mod = await import('/lib/stable-rng.js');
      const r = new mod.StableRng(seed);
      return Array.from({ length: n }, () => String(r.next()));
    }, { seed: v.seed, n: v.first64.length });
    expect(first).toEqual(v.first64);
  }
});

test('another locale changes nothing: the numbers are integers, not formatted text', async ({ browser }) => {
  const context = await browser.newContext({ locale: 'nl-NL' });
  const page = await context.newPage();
  await openApp(page);
  const out: string[] = await page.evaluate(async (seed) => {
    const mod = await import('/lib/stable-rng.js');
    const g = mod.splitMix64(seed);
    return Array.from({ length: 5 }, () => String(g.next().value));
  }, vectors.splitmix64.seed);
  expect(out).toEqual(vectors.splitmix64.outputs);
  await context.close();
});
