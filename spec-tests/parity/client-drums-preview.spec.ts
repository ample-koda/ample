import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

const song = `composition "Local built-in drums" { ample 1  tempo 120  master-seed 17 }

drums groove at 2 for 4 {
  kit builtin
  pattern beat-B
  song beat-A beat-B
}

master output {
  from groove.audio -> audio
}
`;

freshServer({ song });

test('built-in drums render locally with the effective seed and song pattern sequence', async ({ page }) => {
  await openApp(page, { reset: false });
  const builds: unknown[] = [], seedBlocks: string[] = [];
  page.on('request', (request) => {
    if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(request.postDataJSON());
    if (request.url().includes('/api/control/seed')) seedBlocks.push(new URL(request.url()).searchParams.get('block') || '');
  });
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);
  await expect(page.locator('#toast')).toContainText('Local exact instruments: groove');
  expect(seedBlocks).toEqual(['groove']);
  expect(builds).toHaveLength(0);
  await page.click('#btn-stop');
});
