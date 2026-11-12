import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';
import { demoSong } from '../../web/tests/server';

// Plan local-preview 2.8: a curve block steers a parameter of a block in the live chain while it plays (the numbers are those of the server: a general control is scaled to the range of the parameter).
freshServer({ song: `${demoSong()}\ncurve sw {\n  value auto(1:0, 3:1000)\n}\ndelay echo {\n  from drums.audio -> audio\n  from sw.level -> mix\n}\n` });
test('@audio a curve moves the mix of a delay in the live chain', async ({ page }) => {
  await openApp(page, { reset: false });
  const rate = 44_100, frames = rate * 8, bytes = Buffer.alloc(44 + frames * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12); bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24); bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
  for (let f = 0; f < frames; f++) bytes.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 440 * f / rate) * 9000), 44 + f * 2);
  await page.route('**/api/takes/**/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: bytes }));
  await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'drums' ? 'take-drums' : undefined) }), configurable: true }); }));
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('echo'))).toBe(true);
  // the preview value the control has set on the delay (read through the live graph's last parameters)
  const mixNow = () => page.evaluate(() => import('/app/live-chain.js').then((m) => (m as any).controlledParams?.('echo')?.mix ?? null));
  await expect.poll(mixNow, { timeout: 6000 }).not.toBeNull();
  const first = parseInt(await mixNow());
  await page.waitForTimeout(2500);
  const later = parseInt(await mixNow());
  expect(first).toBeGreaterThanOrEqual(0); expect(later).not.toBe(first);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
