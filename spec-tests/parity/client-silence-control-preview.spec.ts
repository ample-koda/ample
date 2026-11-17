import { test, expect } from '@playwright/test';
import { freshServerPerTest, openApp } from '../../web/tests/support';

// The client preview (Build and play preview) makes silence itself, and a block whose parameter follows a control comes from the server: the
// effects of the preview do not apply controls yet, and before this the block sounded as if the control were not there.
freshServerPerTest();

const silenceSong = `composition "Local silence" { ample 1  tempo 120  master-seed 17 }
click metronome at 1 for 2 {
  per_beat 1
}
silence gap at 1 for 2 {
}
mixer mix {
  inputs 2
  from metronome.audio -> audio1
  from gap.audio -> audio2
}
master master {
  from mix.audio -> audio
}
`;

const controlSong = `composition "Local control" { ample 1  tempo 120  master-seed 17 }
click metronome at 1 for 2 {
  per_beat 2
}
curve sweep at 1 for 2 {
  value auto(1:0, 3:1000)
}
compressor tone {
  from metronome.audio -> audio
  from sweep.level -> threshold
}
master master {
  from tone.audio -> audio
}
`;

async function playPreview(page, song: string) {
  await openApp(page, { reset: false });
  await page.evaluate((text) => import('/app/store.js').then((s) => s.applyText(text)), song);
  const builds: { block?: string }[] = [];
  page.on('request', (request) => { if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(request.postDataJSON() ?? {}); });
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 30000 }).toBe(true);
  return builds;
}

test('silence is made in the browser: no server build', async ({ page }) => {
  const builds = await playPreview(page, silenceSong);
  expect(builds).toHaveLength(0);
  await page.click('#btn-stop');
});

test('a compressor whose threshold follows a curve comes from the server, and the preview says why', async ({ page }) => {
  const builds = await playPreview(page, controlSong);
  expect(builds.length, 'the steered compressor was built on the server').toBeGreaterThan(0);
  await expect(page.locator('#toast')).toContainText('threshold follows a control');
  await page.click('#btn-stop');
});
