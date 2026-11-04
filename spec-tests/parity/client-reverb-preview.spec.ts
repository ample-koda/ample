import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

const song = `composition "Local reverb preview" { ample 1  tempo 120 master-seed 17 }

midi-clip phrase {
  notes 1:0:1:60:100 1:1:1:64:100 1:2:1:67:100
  bars 2
}

synth source {
  wave triangle
  from phrase.notes -> notes
}

reverb room {
  size 50%
  decay 60%
  mix 25%
  from source.audio -> audio
}

master output {
  from room.audio -> audio
}
`;

freshServer({ song });

test('reverb uses the cancellable local worker in the hybrid instrument chain', async ({ page }) => {
  await openApp(page, { reset: false });
  const builds: unknown[] = [];
  page.on('request', (request) => { if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(request.postDataJSON()); });
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);
  expect(builds).toHaveLength(0);
  await expect(page.locator('#toast')).toContainText('Local instrument approximation: source. Local effect approximation: room');
  await page.click('#btn-stop');
});
