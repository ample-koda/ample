import { test, expect } from '@playwright/test';
import { addTemplateBlock, api, freshServer, node, openApp } from '../../web/tests/support';

// GUI flow 9: a panel with an upstream input builds it when it opens, so the analysis is there at once (Cancel stops waiting).
freshServer();
const edit = async (op: Record<string, unknown>) => {
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: doc.version, op } });
};
test('opening a panel builds its input without pressing Build', async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 950 });
  await openApp(page, { reset: false });
  await edit({ type: 'addBlock', blockType: 'midi-clip', name: 'src-notes' });
  await edit({ type: 'setParameter', block: 'src-notes', name: 'chords', value: '"Am F C G"' });
  await edit({ type: 'addBlock', blockType: 'synth', name: 'src' });
  await edit({ type: 'connect', from: 'src-notes', output: 'notes', to: 'src', input: 'notes' });
  await edit({ type: 'addBlock', blockType: 'delay', name: 'echo' });
  await edit({ type: 'connect', from: 'src', output: 'audio', to: 'echo', input: 'audio' });
  await page.reload();
  await node(page, 'echo').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const dialog = page.locator('dialog.studio-floating[open]');
  await expect(dialog).toBeVisible();
  await expect(dialog.locator('footer')).toContainText('Input: src', { timeout: 30000 });
});

test('a panel without an upstream input does not build anything', async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 950 });
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'reverb', 'lonely');
  await node(page, 'lonely').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const dialog = page.locator('dialog.studio-floating[open]');
  await expect(dialog).toBeVisible();
  await expect(dialog.locator('footer')).toContainText('Connect an audio input');
  await expect(dialog.locator('.studio-cancel-build')).toHaveCount(0);
});
