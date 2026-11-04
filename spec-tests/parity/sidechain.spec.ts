import { test, expect } from '@playwright/test';
import { addTemplateBlock, api, freshServer, node, openApp } from '../../web/tests/support';

// A compressor has a key input: one chooser in its properties writes (and takes away) the connection.
freshServer();

test('the sidechain chooser of a compressor writes the key connection and "none" takes it away', async ({ page }) => {
  await openApp(page);
  await addTemplateBlock(page, 'compressor', 'comp');
  await node(page, 'comp').click();
  const chooser = page.getByLabel('Sidechain from');
  await expect(chooser).toBeVisible();
  await chooser.selectOption('drums');
  const text = async () => ((await api('/api/doc?path=song.amplet')) as { text: string }).text;
  await expect.poll(text, { timeout: 15000 }).toMatch(/from drums\.audio -> key/);
  await chooser.selectOption('');
  await expect.poll(text, { timeout: 15000 }).not.toMatch(/-> key/);
});
