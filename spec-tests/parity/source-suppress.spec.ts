import { test, expect } from '@playwright/test';
import { freshServer, openApp, srcText } from '../../web/tests/support';

// A note (an info like AMP2073) is a remark, not a problem: it shows on Ctrl+hover, and the tooltip can hide it for this session (browser only) or write "# ample-ignore CODE" so the song says it knows.
const SONG = [
  'composition "p" { ample 1  tempo 100  master-seed 3 }',
  '',
  'dynamic helper phase() -> Int {',
  '  return currentTime().second % 4',
  '}',
  '',
  'midi-clip a at 1 for auto {',
  '  notes "1:0:1:60:100"',
  '}',
  '',
  'gm p at 1 for auto {',
  '  file pack:musescore-general',
  '  program phase()',
  '  from a.notes -> notes',
  '}',
  '',
].join('\n');
const LINE = SONG.split('\n').indexOf('  program phase()');

freshServer({ song: SONG });
test.beforeEach(async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/program phase\(\)/);
  await expect(page.locator('#se-prob')).toContainText('AMP2073', { timeout: 20000 });
});

const overLine = async (page, ctrl: boolean) => {
  const box = (await page.locator('#se-cm .cm-scroller').boundingBox())!;
  await page.mouse.move(box.x + 5, box.y + 2);
  if (ctrl) await page.keyboard.down('Control');
  await page.mouse.move(box.x + 140, box.y + 6 + 18 * LINE + 9);
  await page.mouse.move(box.x + 142, box.y + 6 + 18 * LINE + 9);
};

test('a note shows on Ctrl+hover, not on a plain hover', async ({ page }) => {
  await overLine(page, false);
  await page.waitForTimeout(400);
  await expect(page.locator('#se-errtip')).toBeHidden();
  await page.mouse.move(5, 5);
  await overLine(page, true);
  await expect(page.locator('#se-errtip')).toBeVisible();
  await expect(page.locator('#se-errtip')).toContainText('AMP2073');
  await page.keyboard.up('Control');
});

test('Ignore this session hides the note in the browser and leaves the song alone', async ({ page }) => {
  await overLine(page, true);
  await page.locator('#se-errtip').getByRole('button', { name: 'Ignore this session' }).click();
  await expect(page.locator('#se-prob')).not.toContainText('AMP2073');
  expect(await srcText(page)).toBe(SONG);
  await page.locator('#se-unignore').click();
  await expect(page.locator('#se-prob')).toContainText('AMP2073');
  await page.keyboard.up('Control');
});

test('Suppress here writes the comment at the end of the line and the note goes', async ({ page }) => {
  await overLine(page, true);
  await page.locator('#se-errtip').getByRole('button', { name: 'Suppress here' }).click();
  await expect.poll(() => srcText(page)).toMatch(/  program phase\(\)  # ample-ignore AMP2073\n/);
  await expect(page.locator('#se-prob')).not.toContainText('AMP2073', { timeout: 20000 });
  await page.keyboard.up('Control');
});

test('on a block header the comment goes on the line above, because a header cannot end with a comment', async ({ page }) => {
  const header = SONG.split('\n').indexOf('gm p at 1 for auto {');
  const box = (await page.locator('#se-cm .cm-scroller').boundingBox())!;
  await page.keyboard.down('Control');
  await page.mouse.move(box.x + 60, box.y + 6 + 18 * header + 9);
  await page.mouse.move(box.x + 62, box.y + 6 + 18 * header + 9);
  await expect(page.locator('#se-errtip')).toContainText('L001');
  await page.locator('#se-errtip').getByRole('button', { name: 'Suppress here' }).click();
  await expect.poll(() => srcText(page)).toMatch(/# ample-ignore L001\ngm p at 1 for auto \{/);
  await expect(page.locator('#se-prob')).not.toContainText('L001', { timeout: 20000 });
  await page.keyboard.up('Control');
});
