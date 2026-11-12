import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// GUI flow: AMPLE Jam follows the song. While the song plays in the Studio, a second playhead (the brand colour) runs over the clip at the place that sounds now,
// and it is gone when the song stops. The Jam's own player (the green head) is not started by it.
const SONG = `composition "Follow" { ample 1  tempo 120  master-seed 1 }
midi-clip riff at 1 for 8 {
  notes 1:0:1:60:90 1:1:1:64:90 1:2:1:67:90 1:3:1:72:90 2:0:1:60:90 2:1:1:64:90 2:2:1:67:90 2:3:1:72:90 3:0:1:60:90 4:0:1:64:90 5:0:1:67:90 6:0:1:72:90 7:0:4:60:90 8:0:4:64:90
  bars 8
}
midi-clip piped at 1 for 8 {
  notes riff.notes |> shift(0bars)
}
synth lead at 1 for 8 {
  wave saw
  gain -12 dB
  from riff.notes -> notes
}
master out {
  from lead.audio -> audio
}
`;
freshServer({ song: SONG });

test('the Jam shows a second head that follows the song while it plays', async ({ page }) => {
  test.setTimeout(240000);
  await openApp(page, { reset: false });
  await page.waitForTimeout(800);
  await page.click('#btn-build');
  await page.waitForTimeout(1500);
  await page.waitForFunction(() => !/Cancel build/.test(document.body.innerText), null, { timeout: 120000 });
  await page.waitForTimeout(800);
  await page.locator('.node[data-id="riff"]').click({ position: { x: 20, y: 8 } });   // the way a person opens it: select the clip, press F7
  await page.keyboard.press('F7');
  const jam = page.locator('dialog.mls-panel');
  await expect(jam.locator('canvas.mls-grid')).toBeVisible();
  await expect(jam).toHaveAttribute('data-songhead', '');   // nothing plays: no second head

  await page.click('#btn-preview');
  await expect.poll(async () => Number((await jam.getAttribute('data-songhead')) || -1), { timeout: 20000 }).toBeGreaterThan(0);
  const first = Number(await jam.getAttribute('data-songhead'));
  await page.waitForTimeout(1500);
  const second = Number(await jam.getAttribute('data-songhead'));
  expect(second).toBeGreaterThan(first);                       // it runs with the song
  await expect(jam).toHaveAttribute('data-playhead', '0');     // the Jam's own player was not started

  await page.click('#btn-stop');
  await expect(jam).toHaveAttribute('data-songhead', '', { timeout: 10000 });
});

test('the read-only view of a pipeline clip follows the song too', async ({ page }) => {
  test.setTimeout(240000);
  await openApp(page, { reset: false });
  await page.waitForTimeout(800);
  await page.click('#btn-build');
  await page.waitForTimeout(1500);
  await page.waitForFunction(() => !/Cancel build/.test(document.body.innerText), null, { timeout: 120000 });
  await page.waitForTimeout(800);
  await page.locator('.node[data-id="piped"]').click({ position: { x: 20, y: 8 } });
  await page.keyboard.press('F7');
  const jam = page.locator('dialog.mls-panel');
  await expect(jam.locator('canvas.mls-grid')).toBeVisible();
  await page.click('#btn-preview');
  await expect.poll(async () => Number((await jam.getAttribute('data-songhead')) || -1), { timeout: 20000 }).toBeGreaterThan(0);
  await page.click('#btn-stop');
});
