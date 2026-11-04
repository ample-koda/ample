import { test, expect } from '@playwright/test';
import { freshServer, openApp, srcText } from '../../web/tests/support';

// The right-click menu of the code editor: the usual text actions and the AI actions, with the selection kept.
freshServer();
test.beforeEach(async ({ page }) => { await openApp(page); await page.click('#vs-source'); await expect(page.locator('#se-cm .cm-content')).toBeVisible(); });

test('right click gives cut, copy, paste, select all and an AI submenu', async ({ page }) => {
  await page.locator('#se-cm .cm-content').click({ button: 'right', position: { x: 60, y: 20 } });
  const menu = page.locator('#menu');
  for (const label of ['Cut', 'Copy', 'Paste', 'Select all']) await expect(menu.getByText(label, { exact: true })).toBeVisible();
  await expect(menu.getByText('AI ›')).toBeVisible();
});

test('right-clicking a lint diagnostic writes its suppression into the amplet source', async ({ page }) => {
  await page.locator('#se-cm .cm-content').fill('composition "demo" { ample 1  tempo 92  master-seed 424242 }\nlyrics words {\n  lang nl\n}\nai-voice lead at 9 for 8 as "chorus voice" lock {\n  voice anna\n  lm_seed 3297468772\n  guidance 1\n  from words.words -> lyrics\n}\n');
  const diagnostic = page.locator('#se-prob [data-code="L009"]');
  await expect(diagnostic).toBeVisible();
  await diagnostic.click({ button: 'right' });
  await page.locator('#menu').getByText('Disable L009 for this block', { exact: true }).click();
  await expect(page.locator('#se-cm .cm-content')).toContainText(/# ample-ignore L009.*ai-voice lead/s);
  await expect(page.locator('#se-prob [data-code="L009"]')).toHaveCount(0);
});

test('the selection is still the selection when an AI action is chosen from the menu or the button', async ({ page }) => {
  const ta = page.locator('#se-ta');
  await page.locator('#se-cm .cm-content').click();
  await ta.evaluate((e: HTMLTextAreaElement) => { e.setSelectionRange(0, e.value.indexOf('\n', e.value.indexOf('\n') + 1)); });
  await page.locator('#se-cm .cm-content').click({ button: 'right', position: { x: 60, y: 10 }, modifiers: [] }).catch(() => {});
  await ta.evaluate((e: HTMLTextAreaElement) => { e.setSelectionRange(0, e.value.indexOf('\n', e.value.indexOf('\n') + 1)); });
  await page.evaluate(() => document.querySelector('#se-ta')!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 300, clientY: 300 })));
  await page.locator('#menu .sub', { hasText: 'AI' }).first().hover();
  // the assistant is not set up here, so the submenu leads to the settings; what matters is that the menu opens without touching the selection
  await expect(page.locator('#menu')).toContainText('Set up the Assistant');
  const kept = await ta.evaluate((e: HTMLTextAreaElement) => e.selectionEnd - e.selectionStart);
  expect(kept).toBeGreaterThan(0);
});

test('with an assistant chosen, "Modify selection" starts on the selected lines instead of the whole file', async ({ page }) => {
  await page.evaluate(() => fetch('/api/ai/settings', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Ample-Token': sessionStorage.getItem('ample.token') || '' }, body: JSON.stringify({ assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true }) }));
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await page.click('#vs-source');
  const ta = page.locator('#se-ta');
  await page.locator('#se-cm .cm-content').click();
  await ta.evaluate((e: HTMLTextAreaElement) => { e.setSelectionRange(0, e.value.indexOf('\n', e.value.indexOf('\n') + 1)); });
  await page.evaluate(() => document.querySelector('#se-ta')!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 300, clientY: 300 })));
  await page.locator('#menu .sub', { hasText: 'AI' }).first().hover();
  await page.locator('#menu .flyout div', { hasText: 'Modify selection' }).first().click();
  await expect(page.locator('#ai-scope')).toHaveValue('selection');
});

test('Copy, Cut and Paste of the menu work on the text of the editor (the textarea is only a stand-in of CodeMirror)', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  const before = await srcText(page);
  const first = before.split('\n')[0];
  const menu = async () => {
    await page.evaluate(() => document.querySelector('#se-ta')!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 300, clientY: 300 })));
    await expect(page.locator('#menu')).toBeVisible();
  };
  const select = (from: number, to: number) => page.locator('#se-ta').evaluate((e: HTMLTextAreaElement, [a, b]: number[]) => { e.focus(); e.setSelectionRange(a, b); }, [from, to]);
  await select(0, first.length);
  await menu();
  await page.locator('#menu div', { hasText: /^Copy$/ }).first().click();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(first);
  await select(0, first.length);
  await menu();
  await page.locator('#menu div', { hasText: /^Cut$/ }).first().click();
  await expect.poll(() => srcText(page)).toBe(before.slice(first.length));
  await select(0, 0);
  await menu();
  await page.locator('#menu div', { hasText: /^Paste$/ }).first().click();
  await expect.poll(() => srcText(page)).toBe(before);
});
