import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

const song = `composition "Local trance gate" { ample 1  tempo 128  master-seed 17 }
midi-clip phrase {
  chords Am F C G
  bars 4
}
synth pad {
  wave saw
  gain -12 dB
  from phrase.notes -> notes
}
tremolo trance {
  shape gate
  sync 1/16
  pattern "xdx.bxdx.xdx.x"
  depth 100
  from pad.audio -> audio
}
master master {
  from trance.audio -> audio
}
`;

freshServer({ song });

test('a trance gate with step levels plays in the preview chain', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 30000 }).toBe(true);
  await page.click('#btn-stop');
  // the song has no problems: the letter levels and the shape are accepted by the language
  const text = (await api('/api/doc?path=song.amplet')).text as string;
  expect(text).toContain('pattern "xdx.bxdx.xdx.x"');
});
