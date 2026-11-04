import { test, expect } from '@playwright/test';
import { api, freshServer, openApp, srcText, setSrc, openSourceFile } from '../../web/tests/support';

freshServer({ files: { 'lyrics/chorus.lyr': '[bar 9] la la\n', 'ample.lock': '# ample.lock\nversion 1\n' } });
test.beforeEach(async ({ page }) => { await openApp(page); await page.click('#vs-source'); await expect.poll(() => srcText(page)).toMatch(/composition|master master|bar 9/); });   // the file list is hidden by default; the Files button shows it

test('the file switcher finds the song, the text files the blocks refer to and the generated lock', async ({ page }) => {
  for (const name of ['song.amplet', 'lyrics/chorus.lyr', 'ample.lock']) {
    await page.locator('#se-switch').fill(name);
    await expect(page.locator('#se-switchlist [role="option"]', { hasText: name }).first()).toBeVisible();
  }
});

test('a plain text file opens, applying it writes it, and the generated lock is read-only', async ({ page }) => {
  await openSourceFile(page, 'lyrics/chorus.lyr');
  await expect(page.locator('#se-title')).toHaveText('lyrics/chorus.lyr');
  await expect.poll(() => srcText(page)).toBe('[bar 9] la la\n');
  await setSrc(page, '[bar 9] la la\n[bar 11] di da\n');
  await page.click('#se-apply');
  await expect(page.locator('#toast')).toContainText('Wrote lyrics/chorus.lyr');
  expect((await api('/api/file?path=lyrics/chorus.lyr')).text).toContain('di da');
  await openSourceFile(page, 'ample.lock');
  await expect(page.locator('#se-ta')).toHaveJSProperty('readOnly', true);
  await expect(page.locator('#se-apply')).toBeDisabled();
});

test('another file asks before the unapplied changes of this one are lost', async ({ page }) => {
  await openSourceFile(page, 'lyrics/chorus.lyr');
  await expect.poll(() => srcText(page)).toMatch(/la la/);
  await setSrc(page, 'changed but not applied');
  await openSourceFile(page, 'song.amplet');
  await expect(page.locator('#dlg-b')).toContainText('changes that are not applied');
  await page.locator('#dlg-btns button', { hasText: 'Keep draft and open' }).click();
  await expect(page.locator('#se-title')).toHaveText('song.amplet');
  await expect.poll(() => page.evaluate(() => {
    const projectId = (window as any).AMPLE_STATE.project.id;
    return JSON.parse(localStorage.getItem(`ample.source-draft:${projectId}:lyrics/chorus.lyr`) || 'null')?.text;
  })).toBe('changed but not applied');
});
