import { test, expect } from '@playwright/test';
import { freshServerPerTest, node, openApp } from '../../web/tests/support';

freshServerPerTest();
test.beforeEach(async ({ page }) => { await openApp(page); });

function generatedWav() {
  const bytes = new Uint8Array(44 + 22050 * 2), view = new DataView(bytes.buffer);
  const text = (offset: number, value: string) => [...value].forEach((char, index) => view.setUint8(offset + index, char.charCodeAt(0)));
  text(0, 'RIFF'); view.setUint32(4, bytes.length - 8, true); text(8, 'WAVE'); text(12, 'fmt '); view.setUint32(16, 16, true);
  view.setUint16(20, 1, true); view.setUint16(22, 1, true); view.setUint32(24, 22050, true); view.setUint32(28, 44100, true);
  view.setUint16(32, 2, true); view.setUint16(34, 16, true); text(36, 'data'); view.setUint32(40, 22050 * 2, true);
  return Buffer.from(bytes);
}

test('local transcription reviews generated stems and imports corrected vocal MIDI', async ({ page }) => {
  await page.getByRole('button', { name: /Separate and transcribe audio/ }).click();
  const dialog = page.locator('#dlg');
  await dialog.locator('input[type=file]').setInputFiles({ name: 'generated.wav', mimeType: 'audio/wav', buffer: generatedWav() });
  await dialog.locator('textarea').fill('{"includeMidi":true,"computeDevice":"cpu"}');
  await dialog.getByRole('button', { name: 'Run locally' }).click();
  const review = page.locator('#b-arr').getByText('Audio analysis review');
  await expect(review).toBeVisible();
  await expect(page.locator('#b-arr canvas[aria-label*="Vocal pitch lane"]')).toBeVisible();
  await expect(page.locator('#b-arr tr[data-note="0"]')).toBeVisible();
  await page.locator('#b-arr tr[data-note="0"] input').nth(2).fill('60');
  await page.locator('#b-arr').getByRole('button', { name: 'Import reviewed melody as MIDI block' }).click();
  await expect(node(page, 'reviewed_vocal')).toBeVisible();
  await page.locator('#b-arr').getByRole('button', { name: 'Import backing-midi as MIDI block' }).click();
  await expect(node(page, 'backing_midi')).toBeVisible();
});
