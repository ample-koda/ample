import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The speech panel: text and phonemes in one multiline field, a mode switch, and only the controls of the chosen mode.
const SONG = `composition "demo" { ample 1  tempo 100  master-seed 1 }
speech voice at 1 for 2 {
  text "Hello there"
}
master master {
  from voice.audio -> audio
}
`;
freshServer({ song: SONG });

const docText = async () => ((await api('/api/doc?path=song.amplet')) as { text: string }).text;

test('the speech panel writes text, mode and phonemes to the source', async ({ page }) => {
  await openApp(page, { reset: false });
  await node(page, 'voice').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const dialog = page.locator('#studio[open]');
  const area = dialog.getByRole('textbox', { name: 'Text to speak' });
  await expect(area).toHaveValue('Hello there');
  await expect(dialog.getByText('Voice pitch', { exact: true })).toBeVisible();
  await expect(dialog.getByText('Duration', { exact: true })).toHaveCount(0);

  await area.fill('Good\nmorning "friend"');
  await area.blur();
  await expect.poll(docText).toContain(String.raw`text "Good\nmorning \"friend\""`);

  // phonemes replace the text in the source (the renderer refuses both); the typed text comes back when they are switched off
  await dialog.getByLabel('Write phonemes instead of text').check();
  const phonemes = dialog.getByRole('textbox', { name: 'Phonemes to speak' });
  await expect(phonemes).toHaveValue('');
  await phonemes.fill('HH AH0 L OW1');
  await phonemes.blur();
  await expect.poll(docText).toContain('phonemes "HH AH0 L OW1"');
  await expect.poll(async () => /^\s+text /m.test(await docText())).toBe(false);   // the text line is removed, not emptied
  await dialog.getByLabel('Write phonemes instead of text').uncheck();
  await expect(dialog.getByRole('textbox', { name: 'Text to speak' })).toHaveValue('Good\nmorning "friend"');
  await expect.poll(docText).toContain(String.raw`text "Good\nmorning \"friend\""`);
  await expect.poll(async () => /phonemes/.test(await docText())).toBe(false);

  // words mode: the controls of that mode, no phonemes
  await dialog.getByRole('combobox', { name: 'Mode' }).selectOption('words');
  await expect.poll(docText).toMatch(/mode "words"/);
  await expect(dialog.getByText('Duration', { exact: true })).toBeVisible();
  await expect(dialog.getByText('Voice pitch', { exact: true })).toHaveCount(0);
  await expect(dialog.getByLabel('Write phonemes instead of text')).toBeHidden();
  await expect(dialog.getByText('Fit ceiling')).toHaveCount(0);
  await dialog.getByRole('combobox', { name: 'Duration' }).selectOption('fit');
  await expect(dialog.getByText('Fit ceiling')).toBeVisible();
});
