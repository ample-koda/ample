import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

const song = `composition "Local MIDI source" { ample 1  tempo 120  master-seed 17 }

midi performance at 2 for 2 {
  file source.mid
  track all
  bars 2
}

transpose octave {
  semitones 12
  from performance.notes -> notes
}

synth instrument {
  wave triangle
  gain -12 dB
  from octave.notes -> notes
}

master master {
  from instrument.audio -> audio
}
`;

freshServer({ song });

test('MIDI note source feeds local transforms and synth without building server audio', async ({ page }) => {
  await openApp(page, { reset: false });
  const requests: string[] = [], builds: unknown[] = [];
  await page.route('**/api/midi/notes?**', async (route) => {
    requests.push(route.request().url());
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ppq: 480, tracks: 1, truncated: false, notes: [
      { start: 240, end: 720, pitch: 60, velocity: 96, track: 0 },
      { start: 2400, end: 3120, pitch: 64, velocity: 88, track: 0 },
    ] }) });
  });
  page.on('request', (request) => { if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(request.postDataJSON()); });
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect(page.locator('#toast')).toContainText('Client mix preview');
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);
  expect(requests).toHaveLength(1);
  expect(requests[0]).toContain('file=source.mid');
  expect(requests[0]).toContain('track=all');
  expect(builds).toHaveLength(0);
  await expect(page.locator('#toast')).toContainText('Local MIDI transforms: octave');
  await page.click('#btn-stop');
});
