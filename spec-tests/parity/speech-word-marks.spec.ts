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
