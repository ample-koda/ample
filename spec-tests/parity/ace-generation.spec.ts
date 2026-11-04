import { test, expect } from '@playwright/test';
import { freshServer, openApp, state } from '../../web/tests/support';

freshServer({ song: 'composition "cfg test" { ample 1  tempo 120 master-seed 7 }\nsettings { acestep.backend server acestep.synthModel xl-sft }\nmood-scene mood {\n  direction A quiet, spacious scene.\n}\nace-music music at 1 for 1 {\n  music_direction test\n}\nmaster master {\n  from music.audio -> audio\n}\n' });

test('SFT properties display actual CFG seven and persist an explicit override', async ({ page }) => {
  await openApp(page, { reset: false });
  expect(await page.evaluate(() => (window as any).AMPLE_STATE.doc.diagnostics.filter(d => d.severity === 'error'))).toEqual([]);
  await page.locator('#vs-blocks').click();
  expect((await state(page)).text).toContain('ace-music music');
  await page.evaluate(async () => { const store = await import('/app/store.js'); store.select('music'); store.notify(); });
  await page.locator('#props [role="tab"][data-key="parameters"]').click();
  const cfg = page.getByText('CFG (guidance scale)').locator('..').locator('input[type="number"]');
  await expect(cfg).toHaveValue('7');
  await cfg.fill('5');
  await cfg.press('Tab');
  await expect.poll(async () => (await state(page)).text).toMatch(/guidance 5/);
  await page.evaluate(async () => { const store = await import('/app/store.js'); store.select('master'); store.select('music'); store.notify(); });
  await expect(cfg).toHaveValue('5');
});

test('mood and scene direction opens a wide, tall editor with wrapped text', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.locator('#vs-blocks').click();
  await page.evaluate(async () => { const store = await import('/app/store.js'); store.select('mood'); store.notify(); });
  await page.locator('#props [role="tab"][data-key="parameters"]').click();
  await page.getByRole('button', { name: 'Edit Mood / scene direction in a larger editor' }).click();
  const dialog = page.locator('#dlg');
  const editor = dialog.locator('textarea.parameter-modal-editor');
  await expect(editor).toBeVisible();
  const geometry = await page.evaluate(() => {
    const dlg = document.querySelector('#dlg')!.getBoundingClientRect();
    const editor = document.querySelector<HTMLTextAreaElement>('#dlg textarea')!;
    const area = editor.getBoundingClientRect();
    return { dialogWidth: dlg.width, editorWidth: area.width, editorHeight: area.height, scrollWidth: editor.scrollWidth, clientWidth: editor.clientWidth };
  });
  expect(geometry.dialogWidth).toBeGreaterThan(600);
  expect(geometry.editorWidth).toBeGreaterThan(500);
  expect(geometry.editorHeight).toBeGreaterThan(300);
  expect(geometry.scrollWidth).toBeLessThanOrEqual(geometry.clientWidth + 1);
});
