import { test, expect, type Page } from '@playwright/test';
import { freshServerPerTest, openApp } from '../../web/tests/support';

// Live Preview in the code editor: the word that sounds is lit in the text of its speech block, when the block is listened to on its own, when a block that it feeds is,
// and when the whole song or some bars are (the voice is placed on the timeline of the song).
freshServerPerTest();

const song = `composition "Speech marks" { ample 1  tempo 120  master-seed 7 }
speech voice at 1 for 2 {
  text "Hello there, I am a robot."
}
master out {
  from voice.audio -> audio
}
`;
const WORDS = ['Hello', 'there', 'I', 'am', 'a', 'robot'];

async function openSource(page: Page) {
  await openApp(page, { reset: false });
  await page.evaluate((text) => import('/app/store.js').then((s) => s.applyText(text)), song);
  await page.click('#vs-source');
  await expect(page.locator('#se-listen')).toBeVisible();
}

async function caretIn(page: Page, needle: string, plus = 2) {
  await page.locator('#se-ta').evaluate((ta: HTMLTextAreaElement, [n, p]) => {
    const i = ta.value.indexOf(n as string) + (p as number);
    ta.focus();
    ta.setSelectionRange(i, i);
    ta.onselectionchange?.(new Event('selectionchange'));
  }, [needle, plus]);
}

test('the word of a speech block that sounds is marked in its text while it plays', async ({ page }) => {
  await openSource(page);
  await caretIn(page, 'speech voice', 8);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(page.locator('#lp-status')).toContainText('hearing build #1 of voice', { timeout: 30000 });
  await expect(page.locator('#lp-play')).toHaveText('■ Stop');

  // the marks follow the voice: over the loop at least two different words are lit (the sentence is about two seconds long)
  const seen = new Set<string>();
  await expect.poll(async () => {
    for (const text of await page.locator('.cm-content .lp-now').allInnerTexts()) seen.add(text.trim());
    return seen.size;
  }, { timeout: 20000, intervals: [60] }).toBeGreaterThanOrEqual(2);
  for (const word of seen) expect(WORDS, `"${word}" is not a word of the text`).toContain(word);

  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(page.locator('#lp-status')).toHaveText('Stopped');
  await expect(page.locator('.cm-content .lp-now')).toHaveCount(0);
});

const placed = `composition "Speech marks" { ample 1  tempo 120  master-seed 7 }
speech voice at 2 for 2 {
  text "Hello there, I am a robot."
}
reverb room at 2 for 2 {
  size 30 %
  from voice.audio -> audio
}
master out {
  from room.audio -> audio
}
`;

async function openPlaced(page: Page) {
  await openApp(page, { reset: false });
  await page.evaluate((text) => import('/app/store.js').then((s) => s.applyText(text)), placed);
  await page.click('#vs-source');
  await expect(page.locator('#se-listen')).toBeVisible();
}

/** The words that were lit while the sound played, collected for a while. */
async function litWords(page: Page, atLeast: number) {
  const seen = new Set<string>();
  await expect.poll(async () => {
    for (const text of await page.locator('.cm-content .lp-now').allInnerTexts()) seen.add(text.trim());
    return seen.size;
  }, { timeout: 25000, intervals: [60] }).toBeGreaterThanOrEqual(atLeast);
  for (const word of seen) expect(WORDS, `"${word}" is not a word of the text`).toContain(word);
}

test('the words are lit when the whole song is listened to, and the voice starts at its bar', async ({ page }) => {
  await openPlaced(page);
  await page.selectOption('#lp-target', 'song');
  await page.click('#lp-play');
  await expect(page.locator('#lp-play')).toHaveText('■ Stop', { timeout: 30000 });
  await litWords(page, 2);
  await page.click('#lp-play');
});

test('the words are lit when bars of the song are listened to', async ({ page }) => {
  await openPlaced(page);
  await page.selectOption('#lp-target', 'bars');
  await page.fill('#lp-first', '2'); await page.locator('#lp-first').dispatchEvent('change');
  await page.fill('#lp-last', '3'); await page.locator('#lp-last').dispatchEvent('change');
  await page.click('#lp-play');
  await expect(page.locator('#lp-play')).toHaveText('■ Stop', { timeout: 30000 });
  await litWords(page, 2);
  await page.click('#lp-play');
});

test('the words of the voice are lit when the effect that it feeds is listened to', async ({ page }) => {
  await openPlaced(page);
  await page.locator('#se-ta').evaluate((ta: HTMLTextAreaElement) => {
    const i = ta.value.indexOf('reverb room') + 8;
    ta.focus(); ta.setSelectionRange(i, i); ta.onselectionchange?.(new Event('selectionchange'));
  });
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(page.locator('#lp-status')).toContainText('hearing build #1 of room', { timeout: 30000 });
  await litWords(page, 2);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
});

// Flite phonemes: the take plays (the engine renders `phonemes` as one syllable), no word of a written text sounds, so nothing is lit and nothing breaks.
const flitePhones = `composition "Flite phones" { ample 1  tempo 120  master-seed 7 }
speech voice at 1 for 2 {
  engine "flite"
  phonemes "HH AH0 L OW1"
}
master out {
  from voice.audio -> audio
}
`;

test('a flite block with phonemes plays in Live Preview and marks no word', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.evaluate((text) => import('/app/store.js').then((s) => s.applyText(text)), flitePhones);
  await page.click('#vs-source');
  await expect(page.locator('#se-listen')).toBeVisible();
  await caretIn(page, 'speech voice', 8);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(page.locator('#lp-status')).toContainText('hearing build #1 of voice', { timeout: 30000 });
  await expect(page.locator('#lp-play')).toHaveText('■ Stop');
  await page.waitForTimeout(1500);
  await expect(page.locator('.cm-content .lp-now')).toHaveCount(0);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(page.locator('#lp-status')).toHaveText('Stopped');
});

// Flite words mode: each note of the melody speaks the next word at one pitch; the word that sounds is lit in the text like for the other engines.
const fliteWords = `composition "Flite words" { ample 1  tempo 120  master-seed 7 }
midi-clip melody {
  notes "1:0:1:60:100 1:1:1:64:100 1:2:1:67:100 1:3:1:72:100"
  bars 1
}
speech voice at 1 for 1 {
  engine "flite"
  mode "words"
  text "Hello there I am"
  from melody.notes -> notes
}
master out {
  from voice.audio -> audio
}
`;

test('a flite block in words mode plays in Live Preview and lights its words', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.evaluate((text) => import('/app/store.js').then((s) => s.applyText(text)), fliteWords);
  await page.click('#vs-source');
  await expect(page.locator('#se-listen')).toBeVisible();
  await caretIn(page, 'speech voice', 8);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(page.locator('#lp-status')).toContainText('hearing build #1 of voice', { timeout: 30000 });
  await expect(page.locator('#lp-play')).toHaveText('■ Stop');
  const seen = new Set<string>();
  await expect.poll(async () => {
    // the notes of the melody that sound are lit too (their origin in the clip): only the words of the text count here
    for (const text of await page.locator('.cm-content .lp-now').allInnerTexts()) if (!/^\d+:\d/.test(text.trim())) seen.add(text.trim());
    return seen.size;
  }, { timeout: 25000, intervals: [60] }).toBeGreaterThanOrEqual(2);
  for (const word of seen) expect(['Hello', 'there', 'I', 'am'], `"${word}" is not a word of the text`).toContain(word);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(page.locator('#lp-status')).toHaveText('Stopped');
});
