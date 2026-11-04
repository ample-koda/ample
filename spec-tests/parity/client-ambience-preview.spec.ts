import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { freshServer, openApp } from '../../web/tests/support';

const song = `composition "Ambience server preview" { ample 1  tempo 120  master-seed 17 }

ambience room at 1 for 2 {
  file room.wav
  segment 4 s
  crossfade 1 s
  variation 20%
}

master master {
  from room.audio -> audio
}
`;

function roomWav() {
  const data = Buffer.alloc(44100 * 8 * 2), wav = Buffer.alloc(44 + data.length);
  wav.write('RIFF', 0); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVE', 8); wav.write('fmt ', 12);
  wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(44100, 24);
  wav.writeUInt32LE(88200, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(data.length, 40);
  for (let frame = 0; frame < 44100 * 8; frame++) data.writeInt16LE(Math.round(Math.sin(frame * Math.PI / 96) * 6000), frame * 2);
  data.copy(wav, 44);
  return wav;
}

freshServer({ song, prepare: (_cli, folder) => fs.writeFileSync(path.join(folder, 'room.wav'), roomWav()) });

test('ambience uses the exact effective seed and processes its source locally', async ({ page }) => {
  await openApp(page, { reset: false });
  const builds: any[] = [];
  let sourceReads = 0, seedReads = 0;
  page.on('request', (request) => {
    if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(request.postDataJSON());
    if (request.url().includes('/api/project/audio?path=room.wav')) sourceReads++;
    if (request.url().includes('/api/control/seed?')) seedReads++;
  });
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);
  expect(builds).toHaveLength(0);
  expect(sourceReads).toBe(1);
  expect(seedReads).toBe(1);
  await expect(page.locator('#toast')).toContainText('Local effect approximation: room');
  await page.click('#btn-stop');
});
