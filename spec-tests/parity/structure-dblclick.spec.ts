import { test, expect, type Page } from '@playwright/test';
import { openApp, srcText } from '../../web/tests/support';

// A double-click on a block in the Structure list goes to that block in the source editor, like a double-click on its card.
test.beforeEach(async ({ page }) => { await openApp(page); });

const caretLine = (page: Page) => page.evaluate(() => {
  const ta = document.querySelector('#se-ta') as HTMLTextAreaElement | null;
  if (!ta) return '';
  return ta.value.split('\n')[ta.value.slice(0, ta.selectionStart).split('\n').length - 1];
});

test('double-clicking a block in Structure puts the source caret on it, also when the source is already open', async ({ page }) => {
  await page.locator('#ltabs button[data-l="struct"]').click();
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/master master/);
  await page.locator('#pane-struct .blk', { hasText: 'chords' }).first().dblclick();
  await expect.poll(() => caretLine(page)).toMatch(/midi-clip chords/);
  await page.locator('#pane-struct .blk', { hasText: 'master' }).first().dblclick();
  await expect.poll(() => caretLine(page)).toMatch(/master master/);
});

test('from the block view a double-click in Structure opens the source on that block', async ({ page }) => {
  await page.locator('#ltabs button[data-l="struct"]').click();
  await page.locator('#pane-struct .blk', { hasText: 'bass' }).first().dblclick();
  await expect.poll(() => caretLine(page)).toMatch(/bass bass/);
});
