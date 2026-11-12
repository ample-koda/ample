import { expect } from '@playwright/test';
import { addTemplateBlock, openApp } from '../../../web/tests/support';

/** A mono WAV of a 440 Hz sine (2 s), used as the built take of a source block. */
export function wavFixture(seconds = 2, hz = 440, amplitude = 9000) {
  const rate = 44_100, frames = rate * seconds, bytes = Buffer.alloc(44 + frames * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12);
  bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24);
  bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
  for (let frame = 0; frame < frames; frame++) bytes.writeInt16LE(Math.round(Math.sin(2 * Math.PI * hz * frame / rate) * amplitude), 44 + frame * 2);
  return bytes;
}

/** Drags a cable from the output of one block to an input of another. */
export async function connect(page, fromId: string, toId: string, inPort = 0) {
  const from = page.locator(`.node[data-id="${fromId}"] .port.out`).first();
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

/**
 * Opens the app, puts a drums block `src` (counted as built) in front of the given effects in a row, and starts the live chain that ends at the last one.
 * Returns once the chain plays. Effects are [template, id] pairs.
 */
export async function startedChain(page, effects: Array<[string, string]>, wav = wavFixture()) {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'src');
  for (const [tpl, id] of effects) await addTemplateBlock(page, tpl, id);
  let previous = 'src';
  for (const [, id] of effects) {
    await connect(page, previous, id);
    await expect.poll(() => page.evaluate(([from, to]) => import('/app/store.js').then((s) => s.flat().links.some((l) => l.type === 'audio' && l.from === from && l.to === to)), [previous, id])).toBe(true);
    previous = id;
  }
  await page.route('**/api/takes/**/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: wav }));
  await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'src' ? 'take-src' : undefined) }), set: () => {} }); }));
  const last = effects[effects.length - 1][1];
  const started = await page.evaluate(async (target) => (await import('/app/live-chain.js')).startChain(target), last);
  expect(started).toBe(true);
  await expect.poll(() => page.evaluate((target) => import('/app/live-chain.js').then((m) => m.chainLevels(target)?.left ?? 0), last), { timeout: 8000 }).toBeGreaterThan(0.005);
  return last;
}
