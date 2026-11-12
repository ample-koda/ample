import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServerPerTest, openApp } from '../../web/tests/support';
import { connect, wavFixture } from './support/live-chain-fixture';

freshServerPerTest();

// Follow-up of 2.4: the key block of a compressor in the live chain makes the program pump. The program is a steady tone, the key a loud click every half second; the level that
// comes out of the chain dips after every click and comes back before the next one. Without the key cable the program is its own detector and stays level.
function clickWav(seconds: number) {
  const rate = 44_100, frames = rate * seconds, bytes = Buffer.alloc(44 + frames * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12);
  bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24);
  bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
  for (let frame = 0; frame < frames; frame++) bytes.writeInt16LE(Math.round((frame % (rate / 2)) < rate * .03 ? Math.sin(2 * Math.PI * 1000 * frame / rate) * 30000 : 0), 44 + frame * 2);
  return bytes;
}
const dips = (trace: number[], high: number, low: number) => { let count = 0, down = false; for (const value of trace) { if (!down && value < low) { down = true; count++; } else if (down && value > high) down = false; } return count; };

async function trace(page, block: string, seconds: number) {
  return page.evaluate(async ({ block, seconds }) => {
    const { chainLevels } = await import('/app/live-chain.js');
    const out: number[] = [], started = performance.now();
    await new Promise<void>((resolve) => { const timer = setInterval(() => { out.push(chainLevels(block)?.left ?? 0); if (performance.now() - started > seconds * 1000) { clearInterval(timer); resolve(); } }, 5); });
    return out;
  }, { block, seconds });
}

test('@audio a key block makes the program of a compressor pump against the key in the live chain', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'src'); await addTemplateBlock(page, 'drums', 'kick'); await addTemplateBlock(page, 'compressor', 'pump');
  await connect(page, 'src', 'pump', 0);
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => s.flat().links.filter((l) => l.to === 'pump').length))).toBe(1);
  await connect(page, 'kick', 'pump', 1);
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => s.flat().links.filter((l) => l.type === 'audio' && l.inName === 'key').length))).toBe(1);
  const tone = wavFixture(4, 220, 3300), clicks = clickWav(4);   // the tone is about -20 dB: below the threshold on its own
  await page.route('**/api/takes/take-src/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: tone }));
  await page.route('**/api/takes/take-kick/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: clicks }));
  await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'src' || key === 'kick' ? `take-${String(key)}` : undefined) }), set: () => {} }); }));
  await page.evaluate(() => import('/app/store.js').then((s) => s.edit([
    { type: 'setParameter', block: 'pump', name: 'threshold', value: '-18 dB' }, { type: 'setParameter', block: 'pump', name: 'ratio', value: '20' },
    { type: 'setParameter', block: 'pump', name: 'attack', value: '1 ms' }, { type: 'setParameter', block: 'pump', name: 'release', value: '150 ms' }])));
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('pump'))).toBe(true);
  await page.waitForTimeout(700);
  const keyed = await trace(page, 'pump', 3);
  const top = Math.max(...keyed), bottom = Math.min(...keyed);
  console.log('pump', { top, bottom, dipCount: dips(keyed, top * .8, top * .45) });
  expect(bottom, 'the program dips after a click').toBeLessThan(top * .45);
  expect(Math.abs(dips(keyed, top * .8, top * .45) - 6), 'about one dip per click (two per second, three seconds)').toBeLessThanOrEqual(2);
  expect(keyed.filter((value) => value > top * .8).length, 'between the clicks the program is back at its level').toBeGreaterThan(keyed.length * .3);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
