import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

const song = `composition "Local mix preview" { ample 1  tempo 120  master-seed 17 }

midi-clip phrase {
  chords C Am F G
  bars 4
}

synth tone {
  wave triangle
  gain -9 dB
  from phrase.notes -> notes
}

bus group {
  gain -3 dB
  from tone.audio -> audio1
}

mixer blend {
  from group.audio -> audio1
}

master master {
  from blend.audio -> audio
}
`;

freshServer({ song });

test('transport hybrid preview plays the complete local mix chain without a server build', async ({ page }) => {
  await openApp(page, { reset: false });
  const builds: any[] = [];
  let downloadedStoredAudio = false;
  page.on('request', (request) => {
    if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(request.postDataJSON());
    if (request.url().includes('/api/takes/') && request.url().includes('/download?format=wav')) downloadedStoredAudio = true;
  });

  await page.click('#btn-play-options');
  await expect(page.getByRole('menuitem', { name: /Play preview/ })).toBeEnabled();
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);

  expect(builds).toHaveLength(0);
  expect(downloadedStoredAudio).toBe(false);
  await page.click('#btn-stop');
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing)).toBe(false);
});
