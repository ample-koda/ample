import { test, expect } from '@playwright/test';
import { api, freshServer, openApp, setSrc, srcText } from '../../web/tests/support';

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

// A slow read of the sequence file that arrives after the song tab was chosen must not put the sequence text into the song (or the song text into the sequence file).
test('a late answer for the sequence file never ends up in the song', async ({ page }) => {
  await openApp(page, { reset: false });
  const seqText = '# seq marker\npiano ppq=480\n';
  await api('/api/file', { method: 'PUT', body: { path: 'score.ampleseq', text: seqText } });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toContain('composition');
  let release = () => {};
  const gate = new Promise<void>((resolve) => { release = resolve; });
  await page.route('**/api/file?path=score.ampleseq*', async (route) => { await gate; await route.continue(); });
  await page.evaluate(() => document.dispatchEvent(new CustomEvent('ample:open-file', { detail: 'score.ampleseq' })));
  await page.locator('#tabbar .tab').first().click();
  await expect.poll(() => srcText(page)).toContain('composition');
  release();
  await page.waitForTimeout(600);
  await page.keyboard.press('Control+s');
  await page.waitForTimeout(600);
  const doc = await api('/api/doc?path=song.amplet') as { text: string };
  expect(doc.text).toContain('composition');
  expect(doc.text).not.toContain('seq marker');
  const seq = await api('/api/file?path=score.ampleseq') as { text: string };
  expect(seq.text).toBe(seqText);
});

// Opening a sequence file in Source, typing in it, going to Blocks and back to the song tab used to leave the editor on the sequence file while the tab said song; a later Save could then write one file's text into the other.
test('the editor follows the song tab after Blocks, and typed sequence text is kept as a draft', async ({ page }) => {
  await openApp(page, { reset: false });
  const seqText = '# seq marker\npiano ppq=480\n';
  await api('/api/file', { method: 'PUT', body: { path: 'score.ampleseq', text: seqText } });
  const before = await api('/api/doc?path=song.amplet') as { text: string };
  await page.click('#vs-source');
  await page.evaluate(() => document.dispatchEvent(new CustomEvent('ample:open-file', { detail: 'score.ampleseq' })));
  await expect.poll(() => srcText(page)).toContain('seq marker');
  await setSrc(page, `${await srcText(page)}# typed\n`);
  await page.click('#vs-blocks');
  await page.locator('#tabbar .tab').first().click();
  await page.click('#vs-source');
  await expect(page.locator('#se-title')).toHaveText('song.amplet');
  await expect.poll(() => srcText(page)).toContain('composition');
  await page.keyboard.press('Control+s');
  await page.waitForTimeout(700);
  const doc = await api('/api/doc?path=song.amplet') as { text: string };
  expect(doc.text).toBe(before.text);
  expect(((await api('/api/file?path=score.ampleseq')) as { text: string }).text).toBe(seqText);
  await page.locator('#tabbar .tab[data-path="score.ampleseq"]').click();
  await expect.poll(() => srcText(page)).toContain('# typed');
});
