import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

const song = `composition "Local transpose preview" { ample 1  tempo 120  master-seed 17 }

midi-clip phrase {
  notes 1:0:1:60:96 1:1:1:64:96 1:2:1:67:96
  bars 2
}

transpose octave {
  semitones 12
  from phrase.notes -> notes
}

synth instrument {
  wave triangle
  gain -12 dB
  from octave.notes -> notes
}

master master {
  from instrument.audio -> audio
}
`;

freshServer({ song });

test('transpose feeds the local instrument chain without rendering on the server', async ({ page }) => {
  await openApp(page, { reset: false });
  const builds: unknown[] = [], clips: unknown[] = [];
  page.on('request', (request) => {
    if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(request.postDataJSON());
    if (request.url().includes('/api/midi/clip-notes') && request.method() === 'POST') clips.push(request.postDataJSON());
  });
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);
  expect(clips).toHaveLength(1);
  expect(builds).toHaveLength(0);
  await expect(page.locator('#toast')).toContainText('Local MIDI transforms: octave');
  await page.click('#btn-stop');
});
