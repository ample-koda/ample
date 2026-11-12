import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';
import { demoSong } from '../../web/tests/server';

// Plan local-preview 7.2: the stored take of an AI block plays through the live chain, and the chain says when that take was made; it never starts a build.
freshServer({ song: `${demoSong()}
ace-music song1 {
}
reverb room {
  from song1.audio -> audio
}
` });
test('a live chain after an AI block says it plays the stored take, and from when', async ({ page }) => {
  await openApp(page, { reset: false });
  const rate = 44_100, frames = rate * 2, bytes = Buffer.alloc(44 + frames * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12); bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24); bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
  for (let f = 0; f < frames; f++) bytes.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 440 * f / rate) * 9000), 44 + f * 2);
  let builds = 0;
  page.on('request', (request) => { if (request.url().includes('/api/build') && request.method() === 'POST') builds++; });
  await page.route('**/api/takes/take-ace/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: bytes }));
  await page.route('**/api/takes/take-ace', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ take: { takeId: 'take-ace', created: '2026-11-10T10:30:00+00:00' } }) }));
  await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'song1' ? 'take-ace' : undefined) }), configurable: true }); }));
  const started = await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('room'));
  expect(started).toBe(true);
  await expect(page.locator('.livechain .lcstored')).toContainText('Stored take of');
  const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  expect(state.storedTakes).toEqual([{ id: 'song1', created: '2026-11-10T10:30:00+00:00' }]);
  expect(builds).toBe(0);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
