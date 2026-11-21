import { expect, test } from '@playwright/test';

// A check against a deployed Remix host, on a phone-sized screen: a flite speech block builds, gives audio, and the page does not
// scroll sideways. It runs only when AMPLE_LIVE_REMIX names the host (for example https://remix.ample-koda.dev); otherwise it is skipped.
const host = process.env.AMPLE_LIVE_REMIX;
test.skip(!host, 'AMPLE_LIVE_REMIX is not set');
test.use({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });

const song = `composition "Flite live" { ample 1  tempo 96 master-seed 17 }

speech voice at 1 for 3 {
  engine flite
  text "Hello from AMPLE. This sentence is spoken by the flite engine."
  pitch_hz 95
}

master output {
  from voice.audio -> audio
}
`;

test('a flite speech block builds on the live Remix host on a phone', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(String(error)));
  await page.goto(`${host}/`, { waitUntil: 'load' });
  await page.waitForSelector('html[data-lite-ready="true"]', { timeout: 30000 });
  await page.locator('#lite-editor .cm-content').click();
  await page.keyboard.press('Control+A');
  await page.keyboard.insertText(song);
  await page.click('#lite-build');
  await expect(page.locator('#lite-output-info')).toContainText('seconds', { timeout: 120000 });
  const duration = await page.evaluate(async () => {
    const audio = document.querySelector('#lite-output') as HTMLAudioElement;
    for (let i = 0; i < 50 && !(audio.duration > 0); i++) await new Promise((resolve) => setTimeout(resolve, 200));
    return audio.duration;
  });
  expect(duration).toBeGreaterThan(1);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(1);
  expect(errors).toEqual([]);
});
