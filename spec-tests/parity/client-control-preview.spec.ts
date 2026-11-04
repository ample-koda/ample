import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

const song = `composition "Local control preview" { ample 1  tempo 96  master-seed 20261017 }

midi-clip phrase {
  chords C Am F G
  bars 4
}

synth source {
  wave triangle
  from phrase.notes -> notes
}

group wrapped {
  from source.audio -> input
}

scorer timing {
  limit 120 ms
  from source.audio -> audio
}

reverb space {
  from wrapped.output -> audio
  mix 25
}

master output {
  from space.audio -> audio
}
`;

freshServer({ song });

test('group pass-through keeps the synth and downstream reverb in the local preview', async ({ page }) => {
  await openApp(page, { reset: false });
  let builds = 0;
  page.on('request', (request) => {
    if (request.url().includes('/api/build') && request.method() === 'POST') builds++;
  });

  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect(page.locator('#toast')).toContainText('Client mix preview');
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);
  await expect(page.locator('#toast')).toContainText('processed locally');
  expect(builds).toBe(0);
  await page.click('#btn-stop');
});

test('local preview does not invoke a scorer on its parallel analysis branch', async ({ page }) => {
  await openApp(page, { reset: false });
  let scorerRequests = 0;
  page.on('request', (request) => {
    if (request.url().includes('/api/scor')) scorerRequests++;
  });

  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect(page.locator('#toast')).toContainText('Client mix preview');
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);
  expect(scorerRequests).toBe(0);
  await page.click('#btn-stop');
});
