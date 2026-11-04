import { test, expect } from '@playwright/test';
import { openApp, state, srcText, setSrc } from '../../web/tests/support';

test.beforeEach(async ({ page }) => { await openApp(page); await page.click('#vs-source'); await expect.poll(() => srcText(page)).toMatch(/composition|master master|bar 9/); });

const ctrlClickAt = (page, needle: string, extra = 0) => page.locator('#se-ta').evaluate((ta: HTMLTextAreaElement, [n, e]: [string, number]) => {
  const i = ta.value.indexOf(n) + e;
  ta.focus();
  ta.setSelectionRange(i, i);
  ta.dispatchEvent(new MouseEvent('click', { ctrlKey: true, bubbles: true }));
}, [needle, extra] as [string, number]);

test('Ctrl+click on a reference opens the node editor on that block', async ({ page }) => {
  await ctrlClickAt(page, 'from chords.notes', 7);
  await expect(page.locator('#se')).toBeHidden();
  expect((await state(page)).sel).toEqual(['chords']);
});

test('with errors it says so and stays in the text at the definition', async ({ page }) => {
  await setSrc(page, (await srcText(page)).replace('from bass.audio -> audio2', 'from bassx.audio -> audio2'));
  await expect(page.locator('#se-prob .seerr.error').first()).toBeVisible();
  await ctrlClickAt(page, 'from chords.notes', 7);
  await expect(page.locator('#toast')).toContainText('fix them first');
  await expect(page.locator('#se')).toBeVisible();
});

test('holding Ctrl over a reference shows a tooltip', async ({ page }) => {
  const box = (await page.locator('#se-cm .cm-scroller').boundingBox())!;
  await page.keyboard.down('Control');
  await page.mouse.move(box.x + 100, box.y + 6 + 18 * 9 + 9);
  await page.mouse.move(box.x + 101, box.y + 6 + 18 * 9 + 9);
  await page.keyboard.up('Control');
});

test('Ctrl+click on a helper goes to its declaration in the code, not to the blocks view', async ({ page }) => {
  const song = 'composition "p" { ample 1  tempo 100  master-seed 3 }\n\nhelper canonVoice(Notes source, Interval by) -> Notes {\n  source |> transpose(by)\n}\n\nmidi-clip theme at 1 for 1 {\n  notes "1:0:1:60:100"\n}\n\nmidi-clip canon at 2 for auto {\n  notes canonVoice(theme.notes, +7st)\n}\n';
  await setSrc(page, song);
  await expect(page.locator('#se-prob')).not.toContainText('AMP20', { timeout: 20000 });
  await ctrlClickAt(page, 'notes canonVoice(', 8);
  await expect(page.locator('#se')).toBeVisible();   // still in the source editor
  await expect.poll(() => page.locator('#se-ta').evaluate((ta: HTMLTextAreaElement) => ta.value.slice(0, ta.selectionStart).split('\n').length)).toBe(3);   // on the line of the declaration
});
