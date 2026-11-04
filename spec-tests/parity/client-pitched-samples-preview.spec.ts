import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { freshServer, openApp } from '../../web/tests/support';

const song = `composition "Local pitched samples" { ample 1  tempo 120 master-seed 17 }

midi-clip phrase {
  chords C
  bars 1
}

piano piano {
  set samples/piano
  humanize 0
  from phrase.notes -> notes
}

bass bass {
  set samples/bass
  pattern bass-A
  humanize 0
  from phrase.notes -> notes
}

mixer mix {
  from piano.audio -> audio1
  from bass.audio -> audio2
}

master output {
  from mix.audio -> audio
}
`;

function sampleWav() {
  const samples = Buffer.alloc(44100 * 2), wav = Buffer.alloc(44 + samples.length);
  wav.write('RIFF', 0); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVE', 8); wav.write('fmt ', 12);
  wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(44100, 24);
  wav.writeUInt32LE(88200, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(samples.length, 40);
  for (let i = 0; i < 44100; i++) samples.writeInt16LE(Math.round(Math.sin(i * Math.PI / 32) * 5000), i * 2);
  samples.copy(wav, 44);
  return wav;
}

freshServer({ song, prepare: (_cli, folder) => {
  for (const set of ['piano', 'bass']) {
    const directory = path.join(folder, 'samples', set);
    fs.mkdirSync(directory, { recursive: true });
    fs.writeFileSync(path.join(directory, 'c4.wav'), sampleWav());
  }
} });

test('simple project piano and bass sample folders preview without a server audio build', async ({ page }) => {
  await openApp(page, { reset: false });
  const builds: unknown[] = [], reads = new Set<string>();
  page.on('request', (request) => {
    if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(request.postDataJSON());
    if (request.url().includes('/api/project/audio?path=')) reads.add(new URL(request.url()).searchParams.get('path') || '');
  });
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);
  await expect.poll(() => [...reads]).toContain('samples/piano/c4.wav');
  expect([...reads]).toContain('samples/bass/c4.wav');
  expect(builds).toHaveLength(0);
  await page.click('#btn-stop');
});
