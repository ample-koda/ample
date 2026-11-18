import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// A sequencer that the browser cannot make itself (a noise channel) comes from the build; each lane of it goes through its own effect, so the preview has to ask the
// server for lane1 and lane2 separately, not for the mix of all lanes (the output name "lane1" was refused by a pattern that lost its backslash).
const song = `composition "Lanes from the build" { ample 1  tempo 100  master-seed 5 }

sequencer sq at 1 for 4 {
  lanes lane1=bass lane2=lead
  sequence {
    sequencer 1
    channel bass bass vol=-6
    channel lead notes wave=noise vol=-10
    pattern verse bars=2
      bass 1:0:1:45:100 1:2:1:45:90 2:0:1:43:100 2:2:1:43:90
      lead 1:0:1:69:96 1:1:1:72:96 1:2:2:76:96 2:0:2:74:96
    playlist
      clip verse track=1 at=1 for=4
  }
}

volume low {
  gain -12
  from sq.lane1 -> audio
}

reverb space {
  from sq.lane2 -> audio
  mix 40
}

mixer mix {
  from low.audio -> audio1
  from space.audio -> audio2
}

master master bake {
  from mix.audio -> audio
}
`;
freshServer({ song });

test('the lanes of a sequencer that comes from the build are asked for one by one, each with its own effect', async ({ page }) => {
  await openApp(page, { reset: false });
  const roles: string[] = [];
  page.on('request', (request) => {
    const url = new URL(request.url());
    if (url.pathname.includes('/api/takes/') && url.pathname.endsWith('/download') && url.searchParams.get('role')) roles.push(url.searchParams.get('role')!);
  });
  await page.click('#btn-build');   // the takes the preview takes the sound from
  await expect.poll(async () => page.evaluate(() => import('/app/store.js').then((s) => Object.keys(s.S.build.takes || {}).includes('sq'))), { timeout: 90000 }).toBe(true);
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 30000 }).toBe(true);
  expect(new Set(roles), 'both lanes were asked for, each by its role').toEqual(new Set(['lane1', 'lane2']));
  await page.click('#btn-stop');
});
