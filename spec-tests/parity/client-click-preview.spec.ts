import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

const song = `composition "Local click" { ample 1  tempo 120  master-seed 17 }
click metronome at 1 for 2 {
  accent on
  per_beat 2
}
master master {
  from metronome.audio -> audio
}
`;

freshServer({ song });

test('the click block is rendered in the browser without a server build', async ({ page }) => {
  await openApp(page, { reset: false });
  const builds: unknown[] = [];
  page.on('request', (request) => { if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(request.postDataJSON()); });
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);
  expect(builds).toHaveLength(0);
  await page.click('#btn-stop');
});
