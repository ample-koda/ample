import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The flite voice in the speech panel: the voice choice, its own pitch, the pitch range, and no words mode.
const SONG = `composition "demo" { ample 1  tempo 100  master-seed 1 }
speech voice at 1 for 2 {
  text "Hello there"
  pitch_hz 105 Hz
}
master master {
  from voice.audio -> audio
}
`;
freshServer({ song: SONG });

const docText = async () => ((await api('/api/doc?path=song.amplet')) as { text: string }).text;

test('the flite voice has its own pitch and range and speaks both modes', async ({ page }) => {
  await openApp(page, { reset: false });
  await node(page, 'voice').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const dialog = page.locator('#studio[open]');
  await expect(dialog.getByText('Pitch range', { exact: true })).toHaveCount(0);

  await dialog.getByRole('combobox', { name: 'Voice' }).selectOption('flite');
  await expect.poll(docText).toContain('engine "flite"');
  await expect.poll(docText).toMatch(/pitch_hz 95 Hz/);
  await expect(dialog.getByText('Pitch range', { exact: true })).toBeVisible();
  await expect(dialog.getByRole('combobox', { name: 'Mode' }).locator('option')).toHaveCount(2);

  // words mode: one word per note; the voice follows a steady pitch of 50 to 480 Hz and says so
  await dialog.getByRole('combobox', { name: 'Mode' }).selectOption('words');
  await expect.poll(docText).toContain('mode "words"');
  await expect(dialog.locator('.speech-flite-range')).toBeVisible();
  await expect(dialog.getByText('Pitch range', { exact: true })).toHaveCount(0);
  await expect.poll(docText).toContain('engine "flite"');

  await dialog.getByRole('combobox', { name: 'Voice' }).selectOption('humanico');
  await expect(dialog.locator('.speech-flite-range')).toHaveCount(0);
  await expect.poll(docText).toMatch(/pitch_hz 105 Hz/);
  await expect(dialog.getByText('Pitch range', { exact: true })).toHaveCount(0);
  await expect(dialog.getByRole('combobox', { name: 'Mode' }).locator('option')).toHaveCount(2);
});
