import { test, expect } from '@playwright/test';
import { api, freshServer, openApp, srcText } from '../../web/tests/support';

// In the Source view a tab of a sequence file shows its text there (no sequencer), and the song tab brings the song back.
const song = 'composition "demo" { ample 1  tempo 90  master-seed 1 }\nsequencer score {\n  file "score.ampleseq"\n  lanes lane1=piano\n}\n';
freshServer({ song });

test('switching between the song and its sequence file in the Source view shows each text', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/file', { method: 'PUT', body: { path: 'score.ampleseq', text: '# seq marker\npiano ppq=480\n' } });
  await page.click('#vs-source');
  await page.evaluate(() => document.dispatchEvent(new CustomEvent('ample:open-file', { detail: 'score.ampleseq' })));
  await expect.poll(() => srcText(page)).toContain('seq marker');
  await page.locator('#tabbar .tab').first().click();
  await expect.poll(() => srcText(page)).toContain('composition');
  await page.locator('#tabbar .tab[data-path="score.ampleseq"]').click();
  await expect.poll(() => srcText(page)).toContain('seq marker');
});

test('going from the sequencer back to the Blocks view makes the song tab the active one again', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/file', { method: 'PUT', body: { path: 'score.ampleseq', text: 'piano ppq=480\n' } });
  const songPath = await page.locator('#tabbar .tab').first().getAttribute('data-path');
  await page.evaluate(() => document.dispatchEvent(new CustomEvent('ample:open-file', { detail: 'score.ampleseq' })));
  await expect(page.locator('#tabbar .tab.on')).toHaveAttribute('data-path', 'score.ampleseq');
  await page.click('#vs-blocks');
  await expect(page.locator('#tabbar .tab.on')).toHaveAttribute('data-path', String(songPath));
});
