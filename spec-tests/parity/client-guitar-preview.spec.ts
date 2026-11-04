import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { freshServer, openApp } from '../../web/tests/support';

const song = `composition "Client guitar preview" { ample 1  tempo 120  master-seed 17 }

midi-clip phrase {
  chords C Am F G
  bars 4
}

guitar strum at 1 for 4 {
  set samples/guitar
  voicing power
  strum D...............
  humanize 0
  from phrase.notes -> notes
}

master master {
  from strum.audio -> audio
}
`;

function sampleWav() {
  const frames = 8000, wav = Buffer.alloc(44 + frames * 2);
  wav.write('RIFF', 0); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVE', 8); wav.write('fmt ', 12); wav.writeUInt32LE(16, 16);
  wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(8000, 24); wav.writeUInt32LE(16000, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(frames * 2, 40);
  for (let i = 0; i < frames; i++) wav.writeInt16LE(Math.round(Math.sin(i / 8) * 12000), 44 + i * 2);
  return wav;
}

freshServer({ song, prepare: (_cli, folder) => { fs.mkdirSync(path.join(folder, 'samples', 'guitar'), { recursive: true }); fs.writeFileSync(path.join(folder, 'samples', 'guitar', 'e2.wav'), sampleWav()); } });

test('a supported mono guitar set previews locally without an audio build', async ({ page }) => {
  await openApp(page, { reset: false });
  const builds: string[] = [], assets: string[] = [];
  page.on('request', (request) => {
    if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(request.url());
    if (request.url().includes('/api/project/audio?')) assets.push(request.url());
  });
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);
  expect(builds).toHaveLength(0);
  expect(assets).toHaveLength(1);
  await expect(page.locator('#toast')).toContainText('Local instrument approximation: strum');
  await page.click('#btn-stop');
});
