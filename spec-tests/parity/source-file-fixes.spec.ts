import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { freshServer, openApp, srcText } from '../../web/tests/support';

// A file line of a song that names a file the project does not have: it is an error at once (not after a build), with the near file as a quick fix, a
// file browser to pick from, and the files offered after `file`.
const NL = String.fromCharCode(10);
const SONG = ['composition "files" { ample 1  tempo 100  master-seed 3 }', '', 'import-audio hit at 1 for 1 {', '  file "my-kick.wav"', '}', ''].join(NL);
const WAV = Buffer.concat([Buffer.from('RIFF'), Buffer.alloc(4), Buffer.from('WAVE'), Buffer.alloc(32)]);
freshServer({ song: SONG, prepare: (_cli, folder) => { fs.mkdirSync(path.join(folder, 'samples'), { recursive: true }); fs.writeFileSync(path.join(folder, 'samples', 'my kick.wav'), WAV); } });
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); await page.click('#vs-source'); await expect.poll(() => srcText(page)).toMatch(/composition/); });

test('a file the project does not have is an error in the list, and its tip offers the near file and the file browser', async ({ page }) => {
  const problem = page.locator('.seerr[data-code="AMPL925"]');
  await expect(problem).toBeVisible({ timeout: 20000 });
  await expect(problem).toContainText('my-kick.wav');
  await problem.dblclick();   // the line in focus, with its tip open
  const tip = page.locator('#se-errtip');
  await expect(tip).toBeVisible();
  await expect(tip.locator('.et-fix', { hasText: 'Pick a file' })).toBeVisible();
  await tip.locator('.et-fix', { hasText: 'Use "samples/my kick.wav" instead' }).click();
  await expect.poll(() => srcText(page)).toContain('file "samples/my kick.wav"');
  await expect(page.locator('.seerr[data-code="AMPL925"]')).toHaveCount(0);   // the error is gone with the fix
});

test('the file browser can be opened from the tip, and after file the list starts with Pick a file', async ({ page }) => {
  await expect(page.locator('.seerr[data-code="AMPL925"]')).toBeVisible({ timeout: 20000 });
  await page.locator('.seerr[data-code="AMPL925"]').dblclick();
  await page.locator('#se-errtip .et-fix', { hasText: 'Pick a file' }).click();
  await expect(page.locator('dialog.project-file-picker')).toBeVisible();
  await page.locator('dialog.project-file-picker button', { hasText: 'Cancel' }).click();
  await expect(page.locator('dialog.project-file-picker')).toHaveCount(0);
  // after "file" the files of the project are offered, the file browser first
  await page.locator('#se-cm .cm-content').click();
  await page.keyboard.press('Control+End');
  await page.keyboard.type(`${NL}import-audio more at 2 for 1 {${NL}  file `);
  const list = page.locator('#se-comp div');
  await expect(list.first()).toContainText('Pick a file', { timeout: 20000 });
  await expect(list.nth(1)).toContainText('samples/my kick.wav');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect.poll(() => srcText(page)).toContain('file "samples/my kick.wav"');
});
