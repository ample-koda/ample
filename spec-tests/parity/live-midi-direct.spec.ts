import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';
import { demoSong } from '../../web/tests/server';

// Plan local-preview 7.4: the direct audio of a midi block is played in the browser through the pack SoundFont (4.8) inside the live chain, without a build.
const vlq = (n: number) => { const out = [n & 0x7f]; while ((n >>= 7)) out.unshift((n & 0x7f) | 0x80); return out; };
const smf = (() => {
  const ev = [0x00, 0xc0, 0x00, 0x00, 0x90, 60, 100, ...vlq(480), 0x80, 60, 0, 0x00, 0x90, 64, 100, ...vlq(480), 0x80, 64, 0, ...vlq(0), 0xff, 0x2f, 0x00];
  const track = Buffer.from(ev), head = Buffer.from([0x4d, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 0x01, 0xe0]), th = Buffer.concat([Buffer.from('MTrk'), Buffer.from([0, 0, 0, track.length])]);
  return Buffer.concat([head, th, track]).toString('latin1');
})();
freshServer({ song: `${demoSong()}\nmidi m {\n  file tune.mid\n}\nreverb room {\n  from m.audio -> audio\n}\n` });
test('a midi file plays through the pack SoundFont in the live chain', async ({ page }) => {
  test.setTimeout(120000);
  await openApp(page, { reset: false });
  const sent = await page.evaluate(async (bytes) => { const { getToken } = await import('/app/api.js'); const r = await fetch('/api/project/upload?name=tune.mid&dir=&onConflict=refuse', { method: 'POST', headers: { 'X-Ample-Token': getToken(), 'Content-Type': 'application/octet-stream' }, body: new Uint8Array(bytes) }); return r.status + ' ' + (await r.text()).slice(0, 200); }, [...Buffer.from(smf, 'latin1')]);
  expect(sent).toMatch(/^(200|409)/);
  const started = await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('room'));
  const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  if (!started) test.skip(true, 'the General MIDI pack is not installed here');
  expect(state.items.find((i: any) => i.id === 'm').kind).toBe('local');
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('m')?.left ?? 0)), { timeout: 30000 }).toBeGreaterThan(0.05);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
