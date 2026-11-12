import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';
import { demoSong } from '../../web/tests/server';

// Plan local-preview 4.3-4.6 (re-render route of streaming-decision.md): stretch, granular and denoise after a built sound play in the live chain without a build.
freshServer({ song: `${demoSong()}\nstretch slow {\n  speed 50\n  from drums.audio -> audio\n}\ngranular cloud {\n  from drums.audio -> audio\n}\ndenoise clean {\n  from drums.audio -> audio\n}\n` });
for (const id of ['slow', 'cloud', 'clean']) {
  test(`${id} plays in the live chain without a build`, async ({ page }) => {
    await openApp(page, { reset: false });
    const rate = 44_100, frames = rate * 2, bytes = Buffer.alloc(44 + frames * 2);
    bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12); bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24); bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
    for (let f = 0; f < frames; f++) bytes.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 440 * f / rate) * 9000 + (Math.random() - .5) * 800), 44 + f * 2);
    await page.route('**/api/takes/**/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: bytes }));
    await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'drums' ? 'take-drums' : undefined) }), configurable: true }); }));
    expect(await page.evaluate(async (t) => (await import('/app/live-chain.js')).startChain(t), id)).toBe(true);
    expect((await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()))).items.find((i: any) => i.id === id).kind).toBe('local');
    await expect.poll(() => page.evaluate((b) => import('/app/live-chain.js').then((m) => m.chainLevels(b)?.left ?? 0), id), { timeout: 30000 }).toBeGreaterThan(0.01);
    await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
  });
}
