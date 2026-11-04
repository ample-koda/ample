import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// A failed build leads from the log to the code: the line is underlined in red, a tooltip shows the message (copyable), an edit of that line takes the mark away
// and the next build brings it back.
freshServer({ song: 'composition "bad" { ample 1  tempo 100  master-seed 1 }\nsequencer sq at 1 for 2 {\n}\n' });
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

test('the log also leads to the failed block in the node editor', async ({ page }) => {
  await page.click('#btn-build');
  await expect(page.locator('#b-failed')).toBeVisible();
  await page.click('#b-failed');
  await page.locator('#blog .logerr').last().getByRole('button', { name: 'Show block' }).click();
  await expect(page.locator('.node[data-id="sq"]')).toHaveClass(/sel/);
  await expect(page.locator('#se-cm')).toBeHidden();
});

test('the log leads to the code line, the tooltip shows the message, an edit clears the mark and a new build restores it', async ({ page }) => {
  await page.click('#btn-build');
  await expect(page.locator('#b-failed')).toBeVisible();
  await page.click('#b-failed');
  const row = page.locator('#blog .logerr', { hasText: 'AMP5380' }).first();   // the log lists the newest first; the plain status line has no code
  await expect(row).toContainText('AMP5380');
  await row.click();
  await expect(page.locator('#se-cm .cm-content')).toBeVisible();
  // the underline is cut into the pieces of the colouring (word, name, version...), together they are the marked line
  await expect.poll(() => page.locator('.cm-content .sq.build').evaluateAll((els) => els.map((e) => e.textContent).join(''))).toContain('sequencer sq');

  // hover over the marked line (the second line of the song)
  const box = (await page.locator('#se-cm .cm-scroller').boundingBox())!;
  await page.mouse.move(box.x + 60, box.y + 6 + 18 * 1.5);
  const tip = page.locator('#se-errtip');
  await expect(tip).toBeVisible();
  await expect(tip).toContainText('AMP5380');
  await expect(tip).toContainText('The sequencer has no sequence');
  await expect(tip.getByRole('button', { name: 'Copy message' })).toBeVisible();
  await page.mouse.move(box.x + 60, box.y + 6 + 18 * 1.5 + 6);   // moving inside the tip's reach keeps it
  await tip.hover();
  await expect(tip).toBeVisible();

  // editing the line takes the mark away
  await page.locator('#se-ta').evaluate((ta: HTMLTextAreaElement) => { ta.value = ta.value.replace('for 2', 'for 3'); ta.dispatchEvent(new Event('input')); });
  await expect(page.locator('.cm-content .sq.build')).toHaveCount(0);
  await expect(tip).toBeHidden();

  // apply and build again: the failure is still there, so the mark is back on the (changed) line
  await page.getByRole('button', { name: /^Apply/ }).click();
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_STATE.doc.text.includes('for 3'))).toBe(true);
  await page.click('#btn-build');
  await expect(page.locator('#btxt')).toHaveText('idle');
  await expect(page.locator('.cm-content .sq.build').first()).toBeAttached();   // split in pieces where another diagnostic crosses the line
});
