import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { freshServer, openApp } from '../../web/tests/support';

const song = `composition "Local imported audio" { ample 1  tempo 120  master-seed 17 }

import-audio bed at 1 for 1 {
  file source.wav
}

mixer mix {
  from bed.audio -> audio1
}

master master {
  from mix.audio -> audio
}
`;

function sourceWav() {
  const data = Buffer.alloc(44100 * 2), wav = Buffer.alloc(44 + data.length);
  wav.write('RIFF', 0); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVE', 8); wav.write('fmt ', 12);
  wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(44100, 24);
  wav.writeUInt32LE(88200, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(data.length, 40);
  for (let frame = 0; frame < 44100; frame++) data.writeInt16LE(Math.round(Math.sin(frame * Math.PI / 32) * 8000), frame * 2);
  data.copy(wav, 44);
  return wav;
}

freshServer({ song, prepare: (_cli, folder) => fs.writeFileSync(path.join(folder, 'source.wav'), sourceWav()) });

test('import-audio reads the project WAV locally without a server build', async ({ page }) => {
  await openApp(page, { reset: false });
  const builds: unknown[] = [];
  let sourceRead = false;
  page.on('request', (request) => {
    if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(request.postDataJSON());
    if (request.url().includes('/api/project/audio?path=source.wav')) sourceRead = true;
  });
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);
  expect(sourceRead).toBe(true);
  expect(builds).toHaveLength(0);
  await page.click('#btn-stop');
});
