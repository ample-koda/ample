import { expect, test } from '@playwright/test';

// A check against a deployed Remix host, on a phone-sized screen: Flite sings in words mode (one word per note, three speech blocks, well inside the
// limits of Remix: at most 8 speech blocks, 60 seconds and 4096 characters each), the build gives audio, and the page does not scroll sideways.
// It runs only when AMPLE_LIVE_REMIX names the host (for example https://remix.ample-koda.dev); otherwise it is skipped.
const host = process.env.AMPLE_LIVE_REMIX;
test.skip(!host, 'AMPLE_LIVE_REMIX is not set');
test.use({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });

const song = `composition "Flite sung in Remix" { ample 1  tempo 96 master-seed 17 }

midi-clip tune1 at 1 for 1 {
  notes "1:0:0.9:57:100 1:1:0.9:60:100 1:2:0.9:64:100 1:3:0.9:60:100"
  bars 1
}
speech sing1 at 1 for 1 {
  engine flite
  mode words
  duration_policy fit
  text "Hello from AMPLE today"
  reference_midi 57
  reference_hz 220
  from tune1.notes -> notes
}
midi-clip tune2 at 2 for 1 {
  notes "1:0:0.9:62:100 1:1:0.9:60:100 1:2:0.9:57:100 1:3:0.9:55:100"
  bars 1
}
speech sing2 at 2 for 1 {
  engine flite
  mode words
  duration_policy fit
  text "This voice sings words"
  reference_midi 57
  reference_hz 220
  from tune2.notes -> notes
}
midi-clip tune3 at 3 for 1 {
  notes "1:0:0.9:64:100 1:1:0.9:62:100 1:2:0.9:60:100 1:3:0.9:57:100"
  bars 1
}
speech sing3 at 3 for 1 {
  engine flite
  mode words
  duration_policy fit
  text "One word per note"
  reference_midi 57
  reference_hz 220
  from tune3.notes -> notes
}

mixer voices {
  gains 0 0 0
  pan c c c
  from sing1.audio -> audio1
  from sing2.audio -> audio2
  from sing3.audio -> audio3
}

master output {
  from voices.audio -> audio
}
`;

test('Flite sings in words mode on the live Remix host on a phone', async ({ page }) => {
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
