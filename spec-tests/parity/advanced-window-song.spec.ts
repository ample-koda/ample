import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { freshServer, openApp, node } from '../../web/tests/support';

// An Advanced window belongs to the song it was opened for (issue #159): the same block id in another song never reaches it.
const body = (name: string, tempo: number) => `composition "${name}" { ample 1  tempo ${tempo} master-seed 17 }
import-audio source at 1 for 1 {
  file source.wav
}
master master {
  limiter -1 dB
  target -14 LUFS
  from source.audio -> audio
}
`;

function sourceWav() {
  const length = 8820, wav = Buffer.alloc(44 + length * 2);
  wav.write('RIFF'); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVEfmt ', 8); wav.writeUInt32LE(16, 16);
  wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(44100, 24); wav.writeUInt32LE(88200, 28);
  wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(length * 2, 40);
  for (let i = 0; i < length; i++) wav.writeInt16LE(Math.round(Math.sin(i * Math.PI / 32) * 24000), 44 + i * 2);
  return wav;
}

freshServer({
  song: body('First', 120),
  files: { 'second.amplet': body('Second', 90) },
  prepare: (_cli, folder) => fs.writeFileSync(path.join(folder, 'source.wav'), sourceWav()),
});

test('the Advanced window closes when another song is opened, and the other song opens its own', async ({ page }) => {
  await openApp(page, { reset: false });
  await node(page, 'master').locator('.studio-advanced-node').click();
  await expect(page.locator('.studio-dialog')).toHaveCount(1);

  await page.evaluate(() => document.dispatchEvent(new CustomEvent('ample:open', { detail: 'second.amplet' })));
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_STATE.path)).toBe('second.amplet');
  await expect(page.locator('.studio-dialog')).toHaveCount(0);

  await node(page, 'master').locator('.studio-advanced-node').click();
  await expect(page.locator('.studio-dialog')).toHaveCount(1);
});
