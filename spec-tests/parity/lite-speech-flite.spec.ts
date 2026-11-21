import { expect, Page, test } from '@playwright/test';
import { freshServer, launchUrl } from '../../web/tests/support';

// A speech block with engine flite in Remix: it builds in the browser, gives audio, and Listen lights the word that sounds.
// The Lite build service is the one of this local server, not a deployed host.
freshServer({ env: { AMPLE_LITE_BUILDS_PER_MINUTE: '500', AMPLE_LITE_BUILDS_PER_HOUR: '5000', AMPLE_LITE_DOWNLOADS_PER_MINUTE: '500', AMPLE_LITE_DOWNLOADS_PER_HOUR: '5000' } });

const liteUrl = () => launchUrl().replace('/#token=', '/remix/#token=');

const song = `composition "Flite in Remix" { ample 1  tempo 96 master-seed 17 }

speech voice at 1 for 3 {
  engine flite
  text "Hello from AMPLE. This sentence is spoken by the flite engine."
  pitch_hz 95
}

master output {
  from voice.audio -> audio
}
`;

const wordsSong = `composition "Flite sung in Remix" { ample 1  tempo 96 master-seed 17 }

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

async function openWithSong(page: Page, text: string = song) {
  await page.goto(liteUrl());
  await page.waitForSelector('html[data-lite-ready="true"]');
  await page.evaluate(() => new Promise<void>((resolve) => { const request = indexedDB.deleteDatabase('ample-remix'); request.onsuccess = request.onerror = request.onblocked = () => resolve(); }));
  await page.reload();
  await page.waitForSelector('html[data-lite-ready="true"]');
  await page.locator('#lite-editor .cm-content').click();
  await page.keyboard.press('Control+A');
  await page.keyboard.insertText(text);
  await expect(page.locator('#lite-saved')).toHaveText('saved on this browser');
}

test('a flite speech block builds in the browser and gives audio', async ({ page }) => {
  await openWithSong(page);
  await page.click('#lite-build');
  await expect(page.locator('#lite-output-info')).toContainText('seconds', { timeout: 90000 });
  const duration = await page.evaluate(async () => {
    const audio = document.querySelector('#lite-output') as HTMLAudioElement;
    for (let i = 0; i < 50 && !(audio.duration > 0); i++) await new Promise((resolve) => setTimeout(resolve, 200));
    return audio.duration;
  });
  expect(duration).toBeGreaterThan(1);
});

test('Listen in Remix lights the word that the flite voice speaks', async ({ page }) => {
  await openWithSong(page);
  await page.click('#lite-listen-play');
  await expect(page.locator('#lite-listen-status')).toContainText('hearing build', { timeout: 60000 });
  const seen = new Set<string>();
  await expect.poll(async () => {
    for (const text of await page.locator('.cm-content .lp-now').allInnerTexts()) seen.add(text.trim());
    return seen.size;
  }, { timeout: 25000, intervals: [60] }).toBeGreaterThanOrEqual(2);
  const line = await page.locator('#lite-editor .cm-content').innerText();
  for (const word of seen) expect(line, `"${word}" is a word of the text`).toContain(word);
});

// words mode: each note of a clip speaks the next word of the text at one steady pitch; three blocks, each with its own notes
test('words mode builds in the browser, gives audio, and Listen lights the words that are sung', async ({ page }) => {
  await openWithSong(page, wordsSong);
  await page.click('#lite-build');
  await expect(page.locator('#lite-output-info')).toContainText('seconds', { timeout: 90000 });
  const duration = await page.evaluate(async () => {
    const audio = document.querySelector('#lite-output') as HTMLAudioElement;
    for (let i = 0; i < 50 && !(audio.duration > 0); i++) await new Promise((resolve) => setTimeout(resolve, 200));
    return audio.duration;
  });
  expect(duration).toBeGreaterThan(1);

  await page.click('#lite-listen-play');
  await expect(page.locator('#lite-listen-status')).toContainText('hearing build', { timeout: 60000 });
  const seen = new Set<string>();
  await expect.poll(async () => {
    for (const text of await page.locator('.cm-content .lp-now').allInnerTexts()) seen.add(text.trim());
    return seen.size;
  }, { timeout: 25000, intervals: [60] }).toBeGreaterThanOrEqual(2);
  const source = await page.locator('#lite-editor .cm-content').innerText();
  for (const word of seen) expect(source, `"${word}" is a word of one of the texts`).toContain(word);
});
