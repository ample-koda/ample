import { expect, test } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The speech panel on a phone-sized touch screen: nothing reaches past the screen, the voice choice shows its whole label, the table of
// valid phonemes is one closed line that a tap opens, and the knobs sit two to a row. (The Studio is not public and asks for a login, so this
// runs on a local server; the Remix and Lite hosts have no panels and are checked by live-remix-flite.)
const SONG = `composition "demo" { ample 1  tempo 100  master-seed 1 }
speech voice at 1 for 2 {
  text "Hello there"
}
master master {
  from voice.audio -> audio
}
`;
freshServer({ song: SONG });
test.use({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });

const docText = async () => ((await api('/api/doc?path=song.amplet')) as { text: string }).text;

test('the speech panel fits a phone screen and keeps the phoneme table to one line', async ({ page }) => {
  await openApp(page, { reset: false });
  await node(page, 'voice').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const dialog = page.locator('#studio[open]');
  await dialog.getByRole('combobox', { name: 'Voice' }).selectOption('flite');
  await expect.poll(docText).toContain('engine "flite"');
  await dialog.getByLabel('Write phonemes instead of text').check();
  await dialog.getByRole('textbox', { name: 'Phonemes to speak' }).fill('HH XX1 L');

  const note = dialog.getByRole('status').filter({ hasText: 'not a phoneme' });
  await expect(note).toBeVisible();
  // one short sentence and a closed summary line: the 40 symbols do not push the controls off the screen
  const list = dialog.locator('details.speech-phone-list');
  await expect(list).toHaveJSProperty('open', false);
  const closed = await note.boundingBox();
  expect(closed!.height).toBeLessThan(90);
  await list.locator('summary').tap();
  await expect(list).toHaveJSProperty('open', true);
  // a tap on a phoneme inserts it into the field (the keyboard does not come up: the field is not focused)
  await list.getByRole('button', { name: 'Insert phoneme IY', exact: true }).tap();
  await expect(dialog.getByRole('textbox', { name: 'Phonemes to speak' })).toHaveValue('HH XX1 L IY');
  await list.locator('summary').tap();
  await expect(list).toHaveJSProperty('open', false);

  // no part of the panel reaches past the right edge of the screen, and the page does not scroll sideways
  const outside = await page.evaluate(() => {
    const panel = document.querySelector('#studio[open] .studio-speech-panel') as HTMLElement;
    const limit = document.documentElement.clientWidth + 1;
    return [...panel.querySelectorAll('*')].filter((item) => item.getBoundingClientRect().right > limit && item.getBoundingClientRect().width > 0).map((item) => item.className || item.tagName);
  });
  expect(outside).toEqual([]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(1);

  // the pitch and the pitch range, and the rate and the gain, sit two to a row (one top coordinate each)
  const rows = await dialog.locator('.speech-knob-row').evaluateAll((items) => items.map((item) => [...item.children].map((child) => Math.round(child.getBoundingClientRect().top))));
  expect(rows.length).toBeGreaterThanOrEqual(2);
  for (const tops of rows) expect(new Set(tops).size).toBe(1);
});
