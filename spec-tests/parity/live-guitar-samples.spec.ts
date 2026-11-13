import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';
import { demoSong } from '../../web/tests/server';

// Plan local-preview 3.5 measurement: guitar and the pitched sample sets play in the live chain from a project folder of note-named WAV samples (a generated fixture), without a build.
freshServer({ song: `${demoSong()}\nguitar gtr {\n  set sounds/gtr\n  from chords.notes -> notes\n}\nreverb room {\n  from gtr.audio -> audio\n}\n` });
const wav = (hz: number) => {
  const rate = 44_100, frames = rate, bytes = Buffer.alloc(44 + frames * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12); bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24); bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
  for (let f = 0; f < frames; f++) bytes.writeInt16LE(Math.round(Math.sin(2 * Math.PI * hz * f / rate) * Math.exp(-f / 20000) * 12000), 44 + f * 2);
  return [...bytes];
};
test('guitar plays a project sample folder in the live chain', async ({ page }) => {
  test.setTimeout(120000);
  await openApp(page, { reset: false });
  const notes: Array<[string, number]> = [['C2', 65.41], ['G2', 98], ['C3', 130.81], ['G3', 196], ['C4', 261.63], ['G4', 392], ['C5', 523.25]];
  const statuses = await page.evaluate(async (list) => {
    const { getToken } = await import('/app/api.js'), out: number[] = [];
    for (const [name, bytes] of list) out.push((await fetch(`/api/project/upload?name=${name}.wav&dir=sounds/gtr&onConflict=refuse`, { method: 'POST', headers: { 'X-Ample-Token': getToken(), 'Content-Type': 'application/octet-stream' }, body: new Uint8Array(bytes) })).status);
    return out;
  }, notes.map(([name, hz]) => [name, wav(hz)] as [string, number[]]));
  expect(statuses.every((s) => s === 200 || s === 409), JSON.stringify(statuses)).toBe(true);
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('room'))).toBe(true);
  expect((await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()))).items.find((i: any) => i.id === 'gtr').kind).toBe('local');
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('gtr')?.left ?? 0)), { timeout: 30000 }).toBeGreaterThan(0.005);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
