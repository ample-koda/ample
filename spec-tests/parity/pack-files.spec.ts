import { test, expect } from '@playwright/test';
import { addTemplateBlock, api, freshServer, node, openApp } from '../../web/tests/support';

// The file field of an audio block offers the WAV files of the installed audio packs (pack:<pack>/<name>).
freshServer();

test('the pack file chooser exists on an import-audio block and the endpoint lists pack: values', async ({ page }) => {
  await openApp(page);
  const listed = (await api('/api/packs/files')) as { files: string[] };
  expect(Array.isArray(listed.files)).toBe(true);
  for (const value of listed.files) expect(value.startsWith('pack:')).toBe(true);
  await addTemplateBlock(page, 'import-audio', 'clip');
  await node(page, 'clip').click();
  await expect(page.getByRole('button', { name: 'Choose pack file' })).toBeVisible();
});
