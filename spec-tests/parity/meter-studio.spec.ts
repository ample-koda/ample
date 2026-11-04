import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// A song in 3/4: the server sends project.beatsPerBar, and the browser draws and plays bars of three beats.
const song = `composition "Waltz" { ample 1  tempo 120  meter 3/4  master-seed 17 }
click metronome at 1 for 4 {
  accent on
}
master master {
  from metronome.audio -> audio
}
`;

freshServer({ song });

test('the document view carries the meter and the local preview is three beats per bar long', async ({ page }) => {
  await openApp(page, { reset: false });
  const doc = await api('/api/doc?path=song.amplet');
  expect(doc.project.beatsPerBar).toBe(3);
  expect(doc.project.meterNumerator).toBe(3);
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);
  // 4 bars of 3 beats at 120 bpm is 6 seconds (4 bars of 4/4 would be 8)
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().duration), { timeout: 20000 }).toBeGreaterThan(0);
  const seconds = await page.evaluate(() => (window as any).AMPLE_PLAYER.state().duration);
  expect(Math.abs(seconds - 6)).toBeLessThan(0.3);
  await page.click('#btn-stop');
});
