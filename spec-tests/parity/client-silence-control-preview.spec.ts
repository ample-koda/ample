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
volume tone {
  from metronome.audio -> audio
  from sweep.level -> gain
}
master master {
  from tone.audio -> audio
}
`;

const pianoSong = `composition "Local piano gain" { ample 1  tempo 120  master-seed 17 }
midi-clip phrase {
  chords C Am
  bars 2
}
piano felt {
  set felt-piano
  humanize 0
  from phrase.notes -> notes
}
curve sweep at 1 for 2 {
  value auto(1:0, 3:1000)
}
volume tone {
  from felt.audio -> audio
  from sweep.level -> gain
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

test('a gain that follows a curve is made in the browser when its sound is exact here', async ({ page }) => {
  const builds = await playPreview(page, controlSong);
  expect(builds, 'the click and the volume are made in the browser').toHaveLength(0);
  await page.click('#btn-stop');
});

test('a gain that follows a curve stays with the build when its sound is an approximate local instrument', async ({ page }) => {
  const builds = await playPreview(page, pianoSong);
  expect(builds.length, 'the volume after the piano was built on the server').toBeGreaterThan(0);
  await page.click('#btn-stop');
});
