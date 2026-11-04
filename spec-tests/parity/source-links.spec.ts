import { test, expect } from '@playwright/test';
import { freshServer, openApp, setSrc, srcText } from '../../web/tests/support';

// Ctrl held in the source editor underlines what can be clicked (block names): the underlines are marks of the editor itself, so they sit under the text they belong to.
freshServer();

test('with Ctrl held the block names are underlined by marks inside the lines of the editor, not by an overlay', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
  const song = [
    'composition "p" { ample 1  tempo 100  master-seed 3 }',
    '',
    'midi-clip tune at 1 for 4 {',
    '  notes 1:0:1:60:100',
    '}',
    '',
    'gm piano at 1 for 4 {',
    '  file pack:musescore-general',
    '  from tune.notes -> notes',
    '}',
    '',
    'master out {',
    '  from piano.audio -> audio',
    '}',
    '',
  ].join('\n');
  await setSrc(page, song);
  await page.locator('.cm-content').click();
  await page.keyboard.down('Control');
  const marks = page.locator('.cm-content .lk-mark');
  await expect(marks.first()).toBeVisible({ timeout: 10000 });
  expect(await marks.count()).toBeGreaterThanOrEqual(2);
  // every mark is a piece of a line of the editor (so it sits under its own text), and no mark is taller than the line
  const fit = await marks.evaluateAll((els) => els.map((e) => { const line = e.closest('.cm-line'); if (!line) return false; const a = e.getBoundingClientRect(), b = line.getBoundingClientRect(); return a.top >= b.top - 1 && a.bottom <= b.bottom + 1; }));
  expect(fit.every(Boolean)).toBe(true);
  await expect(page.locator('#se-links')).toHaveText('');   // the old overlay draws nothing
  await page.keyboard.up('Control');
  await expect(page.locator('.cm-content .lk-mark')).toHaveCount(0);
});
