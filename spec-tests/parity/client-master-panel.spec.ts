import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { freshServer, openApp, node } from '../../web/tests/support';

const song = `composition "Master controls" { ample 1  tempo 120 master-seed 17 }
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
  const length = 88200, wav = Buffer.alloc(44 + length * 2);
  wav.write('RIFF'); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVEfmt ', 8); wav.writeUInt32LE(16, 16);
  wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(44100, 24); wav.writeUInt32LE(88200, 28);
  wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(length * 2, 40);
  for (let i = 0; i < length; i++) wav.writeInt16LE(Math.round(Math.sin(i * Math.PI / 32) * 24000), 44 + i * 2);
  return wav;
}

freshServer({ song, prepare: (_cli, folder) => fs.writeFileSync(path.join(folder, 'source.wav'), sourceWav()) });

test('master GUI previews ceiling changes and A/B locally using the upstream take', async ({ page }) => {
  await openApp(page, { reset: false });
  await node(page, 'master').locator('.studio-advanced-node').click();
  await expect(page.getByRole('region', { name: 'Master controls' })).toBeVisible();
  const preview = page.getByRole('button', { name: 'Live local preview', exact: true });
  const buildInput = page.getByRole('button', { name: 'Build and use input', exact: true });
  // the panel builds its input by itself when it opens; the button is for when that has not happened
  await expect(async () => { if (await buildInput.isEnabled()) await buildInput.click(); await expect(preview).toBeEnabled({ timeout: 2000 }); }).toPass({ timeout: 60000 });
  const serverRenders: string[] = [];
  page.on('request', (request) => { if (request.method() === 'POST' && /\/api\/(build|effects\/preview)(?:\?|$)/.test(request.url())) serverRenders.push(request.url()); });
  await page.getByRole('button', { name: '↻ Loop', exact: true }).click();
  await preview.click();
  await expect(page.locator('.studio-master-levels')).toContainText('Output: -2.7 dBFS');
  await page.getByRole('slider', { name: 'Ceiling', exact: true }).press('Home');
  await expect(page.locator('.studio-master-levels')).toContainText('Output: -12.0 dBFS', { timeout: 15000 });
  await page.getByRole('slider', { name: 'Loudness reference', exact: true }).press('Home');
  await expect(page.locator('.studio-master-levels')).toContainText('target -36 LUFS');
  await expect(page.locator('.studio-master-levels')).toContainText('Output: -12.0 dBFS');
  await page.getByRole('button', { name: 'A/B bypass' }).click();
  await expect(page.locator('.studio-master-levels')).toContainText('Output: -2.7 dBFS');
  expect(serverRenders).toEqual([]);
  await page.getByRole('button', { name: 'Close advanced controls' }).click();
  await node(page, 'master').locator('.node-nameplate').click();
  await page.locator('#props .studio-advanced-props').click();
  await expect(page.getByRole('region', { name: 'Master controls' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Live local preview', exact: true })).toBeEnabled();
  expect(serverRenders).toEqual([]);
  await page.getByRole('button', { name: 'Close advanced controls' }).click();
});
