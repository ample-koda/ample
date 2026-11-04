import { test, expect, type Page } from '@playwright/test';
import { addTemplateBlock, freshServerPerTest, node, openApp } from '../../web/tests/support';

// MLS11 (neighbouring-clips strip) of AMPLE Jam. DOM only: the chips, the panel position and the back route.
freshServerPerTest();
test.beforeEach(async ({ page }) => { await openApp(page); });

const edit = (page: Page, ops: unknown[]) => page.evaluate(async (list) => { const s = await import('/app/store.js'); await s.edit(list as never); }, ops);

/** Three consecutive clips a, b, c (bars 1-6) and one far away at bar 60 that must not show next to them. The sample song's own unplaced 'chords' clip has no position and is left out. */
async function song(page: Page) {
  for (const name of ['a', 'b', 'c', 'far']) await addTemplateBlock(page, 'midi-clip', name);
  await edit(page, [
    { type: 'setParts', block: 'a', parts: [{ at: 1, length: 2, name: '' }] }, { type: 'setParts', block: 'b', parts: [{ at: 3, length: 2, name: '' }] },
    { type: 'setParts', block: 'c', parts: [{ at: 5, length: 2, name: '' }] },
    { type: 'setParts', block: 'far', parts: [{ at: 60, length: 2, name: '' }] },
  ]);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
}
async function openLite(page: Page, name: string) {
  await node(page, name).locator('.nh .t').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  await expect(panel).toHaveAttribute('aria-label', `AMPLE Jam of ${name}`);
  return panel;
}
const chipNames = (panel: ReturnType<Page['locator']>) => panel.locator('.mls-strip .mls-chip:not(.mls-back)').evaluateAll((els) => els.map((e) => e.getAttribute('data-clip')));

test('the middle clip shows its two neighbours, not unplaced or far clips', async ({ page }) => {
  await song(page);
  const panel = await openLite(page, 'b');
  await expect(panel.locator('.mls-strip')).toBeVisible();
  expect(await chipNames(panel)).toEqual(['a', 'b', 'c']);
  await expect(panel.locator('.mls-strip .mls-chip.current')).toHaveCount(1);
  await expect(panel.locator('.mls-strip .mls-chip.current')).toContainText('bar 3-4');
  await expect(panel.locator('.mls-strip .mls-back')).toHaveCount(0);
});

test('clicking a neighbour opens it in place; Alt+Left and the Back chip return', async ({ page }) => {
  await song(page);
  const panel = await openLite(page, 'b');
  const before = await panel.boundingBox();
  await panel.locator('.mls-strip .mls-chip[data-clip="c"]').click();
  await expect(page.locator('dialog.mls-panel')).toHaveCount(1);
  const next = page.locator('dialog.mls-panel');
  await expect(next).toHaveAttribute('aria-label', 'AMPLE Jam of c');
  const after = (await next.boundingBox())!;
  expect(Math.abs(after.x - before!.x)).toBeLessThan(2); expect(Math.abs(after.y - before!.y)).toBeLessThan(2);
  expect(Math.abs(after.width - before!.width)).toBeLessThan(2);
  await expect(next.locator('.mls-strip .mls-back')).toHaveAttribute('aria-label', 'Back to b');
  await next.locator('canvas.mls-grid').focus();
  await page.keyboard.press('Alt+ArrowLeft');
  await expect(page.locator('dialog.mls-panel')).toHaveAttribute('aria-label', 'AMPLE Jam of b');
  // forward again, then back with the chip
  await page.locator('.mls-strip .mls-chip[data-clip="a"]').click();
  await expect(page.locator('dialog.mls-panel')).toHaveAttribute('aria-label', 'AMPLE Jam of a');
  await page.locator('.mls-strip .mls-back').click();
  await expect(page.locator('dialog.mls-panel')).toHaveAttribute('aria-label', 'AMPLE Jam of b');
  await expect(page.locator('.mls-strip .mls-back')).toHaveCount(0);
});

test('the strip follows a neighbour that is moved elsewhere in the project; a lone clip has no strip', async ({ page }) => {
  await song(page);
  const panel = await openLite(page, 'b');
  await expect(panel.locator('.mls-strip .mls-chip[data-clip="c"]')).toContainText('bar 5-6');
  await edit(page, [{ type: 'setParts', block: 'c', parts: [{ at: 7, length: 1, name: '' }] }]);
  await expect(panel.locator('.mls-strip .mls-chip[data-clip="c"]')).toContainText('bar 7');
  await edit(page, [{ type: 'setParts', block: 'c', parts: [{ at: 40, length: 1, name: '' }] }]);
  await expect(panel.locator('.mls-strip .mls-chip[data-clip="c"]')).toHaveCount(0);
  await panel.locator('.mls-close').click();
  const lone = await openLite(page, 'far');
  await expect(lone.locator('.mls-strip')).toBeHidden();
});

test('the strip never edits the other clips and Alt+Left nudges the selection instead of going back when notes are selected', async ({ page }) => {
  await song(page);
  await edit(page, [{ type: 'setParameter', block: 'a', name: 'notes', value: '"1:0:1:60:96"' }]);
  const panel = await openLite(page, 'b');
  await panel.locator('.mls-strip .mls-chip[data-clip="a"]').click();
  const a = page.locator('dialog.mls-panel');
  await expect(a).toHaveAttribute('aria-label', 'AMPLE Jam of a');
  await a.locator('canvas.mls-grid').focus();
  await page.keyboard.press('Control+a');
  await page.keyboard.press('Alt+ArrowLeft');   // a selection exists: this is the nudge, not "back"
  await expect(page.locator('dialog.mls-panel')).toHaveAttribute('aria-label', 'AMPLE Jam of a');
});
