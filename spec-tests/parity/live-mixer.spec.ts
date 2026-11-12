import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

freshServer();

function wavFixture() {
  const rate = 44_100, frames = rate * 2, bytes = Buffer.alloc(44 + frames * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12);
  bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24);
  bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
  for (let frame = 0; frame < frames; frame++) bytes.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 330 * frame / rate) * 12000), 44 + frame * 2);
  return bytes;
}

test('@audio Hear live loops the inputs of the mixer, a fader is heard while it moves and written to the song only on release', async ({ page }) => {
  await page.addInitScript(() => {
    (window as any).__params = 0;
    const original = AudioParam.prototype.setTargetAtTime;
    AudioParam.prototype.setTargetAtTime = function (...args) { (window as any).__params++; return original.apply(this, args); };
  });
  await openApp(page, { reset: false });
  const wav = wavFixture();
  await page.route('**/api/takes/**/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: wav }));
  // the inputs of the mixer count as built (the song of the test server has no takes yet)
  await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (typeof key === 'string' && !['mix', 'master'].includes(key) ? `take-${key}` : undefined), has: () => true }), set: () => {} }); s.notify(); }));
  await page.locator('#btabs button[data-t="mixer"]').click();
  const strip = page.locator('#b-mixer .mixstrip[data-block="mix"]');
  await strip.getByRole('button', { name: 'Hear mix live' }).click();
  await expect(strip.getByRole('button', { name: 'Hear mix live' })).toHaveText('■ Stop live');
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing)).toBe(true);
  // the inputs have a wide (left and right) meter that moves
  await expect(strip.locator('.mixchm').first().locator('b')).toHaveCount(2);
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('mix')?.left ?? 0)), { timeout: 8000 }).toBeGreaterThan(0.005);

  const undoBefore = await page.evaluate(() => import('/app/store.js').then((m) => m.S.undo.length));
  const gainsBefore = await page.evaluate(() => import('/app/store.js').then((m) => String(m.flat().nodes.find((n) => n.id === 'mix').params.gains)));
  const versionBefore = await page.evaluate(() => import('/app/store.js').then((s) => s.S.doc.version));
  const before = await page.evaluate(() => (window as any).__params);
  await strip.locator('.mixfader').first().evaluate((input: HTMLInputElement) => { for (const value of ['-3', '-7', '-12']) { input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })); } });
  expect(await page.evaluate(() => (window as any).__params)).toBeGreaterThan(before);   // heard now
  expect(await page.evaluate(() => import('/app/store.js').then((s) => s.S.doc.version))).toBe(versionBefore);   // not written yet
  await strip.locator('.mixfader').first().evaluate((input: HTMLInputElement) => { input.dispatchEvent(new Event('change', { bubbles: true })); });
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => s.S.doc.version))).not.toBe(versionBefore);   // written on release

  // a whole drag is one history step: many input events, one change; Undo brings the old gains back (and the live mix follows the song)
  expect(await page.evaluate(() => import('/app/store.js').then((m) => m.S.undo.length))).toBe(undoBefore + 1);
  await page.locator('#btn-undo').click();
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((m) => String(m.flat().nodes.find((n) => n.id === 'mix').params.gains)))).toBe(gainsBefore);
  await page.locator('#b-mixer .mixstrip[data-block="mix"]').getByRole('button', { name: 'Hear mix live' }).click();
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainActive()))).toBe(false);
});
