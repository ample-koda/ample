import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, openApp } from '../../web/tests/support';
import { demoSong } from '../../web/tests/server';

// Plan local-preview 2.5: join, crossfade, splitter, click, import-audio and ambience are made in the browser inside the live chain (no build for them).
freshServer({ song: `${demoSong()}
click tick at 1 for 4 {
}
delay echo2 {
  from tick.audio -> audio
}
` });
function wavFixture() {
  const rate = 44_100, frames = rate * 2, bytes = Buffer.alloc(44 + frames * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12);
  bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24);
  bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
  for (let frame = 0; frame < frames; frame++) bytes.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 440 * frame / rate) * 9000), 44 + frame * 2);
  return bytes;
}
async function connect(page, fromId: string, toId: string, inPort = 0, outPort = 0) {
  const from = page.locator(`.node[data-id="${fromId}"] .port.out`).nth(outPort);
  const to = page.locator(`.node[data-id="${toId}"] .port.in`).nth(inPort);
  let a!: { x: number; y: number; width: number; height: number }, b!: typeof a;
  await expect(async () => {
    await from.scrollIntoViewIfNeeded(); await to.scrollIntoViewIfNeeded();
    const first = (await from.boundingBox())!, second = (await to.boundingBox())!;
    await page.waitForTimeout(150);
    const again = (await from.boundingBox())!, secondAgain = (await to.boundingBox())!;
    expect(again.x).toBe(first.x); expect(again.y).toBe(first.y); expect(secondAgain.x).toBe(second.x); expect(secondAgain.y).toBe(second.y);
    a = again; b = secondAgain;
  }).toPass({ timeout: 15000 });
  await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2); await page.mouse.down();
  await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2, { steps: 4 }); await page.mouse.up();
}

test('@audio a join of two built sounds and a splitter run in the live chain and make sound', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'one'); await addTemplateBlock(page, 'drums', 'two'); await addTemplateBlock(page, 'join', 'both'); await addTemplateBlock(page, 'splitter', 'halves'); await addTemplateBlock(page, 'delay', 'echo');
  await connect(page, 'one', 'both', 0); await connect(page, 'two', 'both', 1);
  await connect(page, 'both', 'halves', 0); await connect(page, 'halves', 'echo', 0, 1);   // the right output of the splitter
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => s.flat().links.filter((l) => l.type === 'audio').length))).toBeGreaterThanOrEqual(4);
  const wav = wavFixture();
  await page.route('**/api/takes/**/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: wav }));
  await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'one' || key === 'two' ? `take-${String(key)}` : undefined) }), configurable: true }); }));
  const started = await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('echo'));
  expect(started).toBe(true);
  const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  expect(state.items.map((i: any) => i.id)).toEqual(expect.arrayContaining(['one', 'two', 'both', 'halves:right', 'echo']));
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('echo')?.left ?? 0)), { timeout: 8000 }).toBeGreaterThan(0.005);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});

test('@audio a click track is made in the browser and runs through an effect without a build', async ({ page }) => {
  await openApp(page, { reset: false });
  const started = await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('echo2'));
  expect(started).toBe(true);
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('tick')?.left ?? 0)), { timeout: 8000 }).toBeGreaterThan(0.01);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
