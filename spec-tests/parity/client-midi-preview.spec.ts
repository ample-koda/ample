import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { freshServer, openApp } from '../../web/tests/support';

const song = `composition "MIDI server preview" { ample 1  tempo 120  master-seed 17 }

midi performance {
  file source.mid
  track all
  bars 2
}

master master {
  from performance.audio -> audio
}
`;

function sourceMidi() {
  const events = Buffer.from([0x00, 0xff, 0x51, 0x03, 0x07, 0xa1, 0x20, 0x00, 0xc0, 0x00, 0x00, 0x90, 0x3c, 0x64, 0x83, 0x60, 0x80, 0x3c, 0x00, 0x00, 0xff, 0x2f, 0x00]);
  const track = Buffer.alloc(8 + events.length);
  track.write('MTrk', 0); track.writeUInt32BE(events.length, 4); events.copy(track, 8);
  const header = Buffer.alloc(14);
  header.write('MThd', 0); header.writeUInt32BE(6, 4); header.writeUInt16BE(0, 8); header.writeUInt16BE(1, 10); header.writeUInt16BE(480, 12);
  return Buffer.concat([header, track]);
}

freshServer({ song, prepare: (_cli, folder) => fs.writeFileSync(path.join(folder, 'source.mid'), sourceMidi()) });

test('MIDI audio uses AMPLE tempo and GM rendering in the hybrid client preview', async ({ page }) => {
  await openApp(page, { reset: false });
  const builds: any[] = [];
  page.on('request', (request) => { if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(request.postDataJSON()); });
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);
  expect(builds).toHaveLength(1);
  expect(builds[0]).toMatchObject({ only: 'performance', intent: 'preview' });
  await page.click('#btn-stop');
});
