import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// The vocoder is worked out in the browser: the preview of the song needs no server build for it.
const SONG = `composition "Local vocoder" { ample 1  tempo 120  master-seed 17 }
midi-clip line at 1 for 1 {
  bars 1
  notes 1:0:4:57:100
}
synth voice at 1 for 1 {
  wave saw
  from line.notes -> notes
}
synth pad at 1 for 1 {
  wave saw
  from line.notes -> notes
}
vocoder talk {
  from voice.audio -> modulator
  from pad.audio -> carrier
}
master master {
  from talk.audio -> audio
}
`;
freshServer({ song: SONG });

test('the vocoder is processed in the client chain', async ({ page }) => {
  await openApp(page, { reset: false });
  const builds: string[] = [];
  page.on('request', (request) => { if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(String(request.postData())); });
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 30000 }).toBe(true);
  expect(builds.filter((b) => b.includes('talk'))).toHaveLength(0);
  await page.click('#btn-stop');
});
