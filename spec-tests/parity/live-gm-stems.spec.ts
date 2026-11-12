import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Plan local-preview 3.7: a GM note of a pack that was rendered once is kept in the browser (IndexedDB) and plays again, also after a reload, without asking the server.
freshServer();
function wav() {
  const rate = 44_100, frames = rate / 2, bytes = Buffer.alloc(44 + frames * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12); bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24); bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
  for (let f = 0; f < frames; f++) bytes.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 330 * f / rate) * 8000), 44 + f * 2);
  return bytes;
}
test('a rendered pack note is cached in IndexedDB and survives a reload', async ({ page }) => {
  let hits = 0;
  await page.route('**/api/gm/audition', (route) => { hits++; route.fulfill({ status: 200, contentType: 'audio/wav', body: wav() }); });
  await openApp(page, { reset: false });
  const play = () => page.evaluate(async () => {
    const { createJamInstrument } = await import('/app/jam-instrument.js');
    const voice = createJamInstrument({ file: 'pack:musescore-general', bank: 0, program: 4 });
    voice.play(62, { delay: 0 });
    await new Promise((resolve) => setTimeout(resolve, 1500));
    voice.stop();
  });
  await play();
  expect(hits).toBe(1);
  await page.evaluate(() => import('/app/jam-instrument.js'));   // same page: the memory cache answers
  await play();
  expect(hits).toBe(1);
  await page.reload(); await page.waitForSelector('body[data-ready="1"]');
  await play();   // a new page: only IndexedDB can answer
  expect(hits).toBe(1);
  const bytes = await page.evaluate(async () => (await (await import('/lib/sf-sample-cache.mjs')).sharedSampleCache()).bytes());
  expect(bytes).toBeGreaterThan(10000);
  await page.evaluate(async () => (await (await import('/lib/sf-sample-cache.mjs')).sharedSampleCache()).clear());
});
test('a project SoundFont is cached under its size and time and survives a reload', async ({ page }) => {
  let hits = 0;
  await page.route('**/api/gm/audition', (route) => { hits++; route.fulfill({ status: 200, contentType: 'audio/wav', body: wav() }); });
  await openApp(page, { reset: false });
  const sent = await page.evaluate(async () => { const { getToken } = await import('/app/api.js'); const r = await fetch('/api/project/upload?name=own.sf2&dir=&onConflict=refuse', { method: 'POST', headers: { 'X-Ample-Token': getToken(), 'Content-Type': 'application/octet-stream' }, body: new Uint8Array(64) }); return r.status; });
  expect([200, 409]).toContain(sent);
  const play = () => page.evaluate(async () => {
    const { createJamInstrument } = await import('/app/jam-instrument.js');
    const voice = createJamInstrument({ file: 'own.sf2', bank: 0, program: 4 });
    voice.play(64, { delay: 0 }); await new Promise((resolve) => setTimeout(resolve, 1200)); voice.stop();
  });
  await play();
  await page.reload(); await page.waitForSelector('body[data-ready="1"]');
  await play();
  expect(hits).toBe(1);

});
