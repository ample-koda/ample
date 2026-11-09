import { test, expect } from '@playwright/test';
import { api, bottomTab, dialog, freshServer, node, openApp } from '../../web/tests/support';

freshServer();
test.beforeEach(async ({ page }) => { await openApp(page); });

test('the Project history tab works again after Edit history was opened', async ({ page }) => {
  await bottomTab(page, 'hist');
  await page.click('#hist-edit-tab');
  await expect(page.locator('#hist-edit-view')).toBeVisible();
  await expect(page.locator('#hist-project-view')).toBeHidden();
  await page.click('#hist-project-tab');
  await expect(page.locator('#hist-project-view')).toBeVisible();
  await expect(page.locator('#hist-edit-view')).toBeHidden();
  await expect(page.locator('#hist-project-tab')).toHaveAttribute('aria-pressed', 'true');
});

test('both history view toggles state their action and carry aria-pressed', async ({ page }) => {
  await bottomTab(page, 'hist');
  await expect(page.locator('#h-adv')).toHaveText('Advanced view');
  await expect(page.locator('#h-adv')).toHaveAttribute('aria-pressed', 'false');
  await page.click('#h-adv');
  await expect(page.locator('#h-adv')).toHaveAttribute('aria-pressed', 'true');
  await page.click('#h-adv');
  await expect(page.locator('#h-adv')).toHaveAttribute('aria-pressed', 'false');
});

test('canvas keys do nothing while a dialog is open', async ({ page }) => {
  await expect(node(page, 'chords')).toBeVisible();   // earlier tests of this file share the song: wait until the block is drawn
  await node(page, 'chords').locator('.nh .t').click();
  const x0 = (await node(page, 'chords').boundingBox())!.x;
  await page.click('#btn-cp');
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('Control+z');
  await page.waitForTimeout(300);
  expect((await node(page, 'chords').boundingBox())!.x).toBe(x0);
  await dialog(page, 'Cancel');
  await expect(page.locator('#dlg')).toHaveJSProperty('open', false);
  await page.locator('body').focus();
  await page.keyboard.press('ArrowRight');
  await expect.poll(async () => (await node(page, 'chords').boundingBox())?.x ?? 0).toBeGreaterThan(x0);
});

test('graph revisions are keyboard reachable: Enter selects, the menu key opens the menu', async ({ page }) => {
  await bottomTab(page, 'hist');
  const g = page.locator('#hist g[data-node]').first();
  await expect(g).toHaveAttribute('tabindex', '0');
  await expect(g).toHaveAttribute('role', 'button');
  await expect(g).toHaveAttribute('aria-label', /Revision/);
  await g.focus();
  await page.keyboard.press('Enter');
  await expect.poll(() => page.evaluate(() => document.activeElement?.hasAttribute('data-node'))).toBe(true);
  await page.keyboard.press('ContextMenu');
  await expect(page.locator('#menu')).toBeVisible();
  await expect(page.locator('#menu')).toContainText('Look only');
});

test('a second ask waits for the first and no promise hangs', async ({ page }) => {
  const outcome = await page.evaluate(async () => {
    const { ask } = await import('/app/ui.js');
    const first = ask('First question', '', [{ label: 'One', value: 1, primary: true }]);
    const second = ask('Second question', '', [{ label: 'Two', value: 2, primary: true }]);
    const title = () => document.querySelector('#dlg-t')!.textContent;
    const seen = [title()];
    (document.querySelector('#dlg-btns button') as HTMLButtonElement).click();
    const a = await first;
    await new Promise((r) => setTimeout(r, 50));
    seen.push(title());
    (document.querySelector('#dlg-btns button') as HTMLButtonElement).click();
    const b = await second;
    return { seen, a, b };
  });
  expect(outcome).toEqual({ seen: ['First question', 'Second question'], a: 1, b: 2 });
});

test('project settings: unsaved tabs are marked, a save keeps the other tab, leaving asks first', async ({ page }) => {
  await page.click('#btn-projects');
  await page.click('#pm [data-page="settings"]');
  await expect(page.locator('#ps-tabbtn-general .ps-dirty')).toHaveCount(0);
  await page.locator('#ps-budget').fill('77');
  await expect(page.locator('#ps-tabbtn-general .ps-dirty')).toHaveCount(1);
  await page.click('#ps-tabbtn-audio');
  await page.locator('#ps-faer-song').selectOption('faer-else-local');
  await expect(page.locator('#ps-tabbtn-audio .ps-dirty')).toHaveCount(1);
  await page.click('#ps-save-audio');
  await expect.poll(async () => (await api('/api/project/settings')).faer.song).toBe('faer-else-local');
  await expect(page.locator('#toast')).toContainText('Saved: Audio routing.');
  // the rebuilt page still holds the unsaved General edit and still marks it; the saved tab is clean
  await expect(page.locator('#ps-tabbtn-audio .ps-dirty')).toHaveCount(0);
  await expect(page.locator('#ps-tabbtn-general .ps-dirty')).toHaveCount(1);
  await expect(page.locator('#ps-budget')).toHaveValue('77');
  // leaving asks; Keep editing stays
  await page.click('#pm-close');
  await expect(page.locator('#dlg-t')).toHaveText('Leave without saving?');
  await dialog(page, 'Keep editing');
  await expect(page.locator('#pm')).toBeVisible();
  await expect(page.locator('#dlg')).toHaveJSProperty('open', false);
  await page.keyboard.press('Escape');
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await dialog(page, 'Keep editing');
  await page.click('#pm [data-page="health"]');
  await dialog(page, 'Keep editing');
  await expect(page.locator('#ps-tab-audio')).toBeVisible();
  await page.click('#pm-close-top');
  await dialog(page, 'Discard changes');
  await expect(page.locator('#pm')).toBeHidden();
  await api('/api/project/settings', { method: 'POST', body: { faer: { song: 'local', voice: 'local', separation: 'local' } } });
});

test('project settings: the tabs know their panels and Home and End move between them', async ({ page }) => {
  await page.click('#btn-projects');
  await page.click('#pm [data-page="settings"]');
  await expect(page.locator('#ps-tabbtn-general')).toHaveAttribute('aria-controls', 'ps-tab-general');
  await page.locator('#ps-tabbtn-general').focus();
  await page.keyboard.press('End');
  await expect(page.locator('#ps-tab-seed')).toBeVisible();
  await page.keyboard.press('Home');
  await expect(page.locator('#ps-tab-general')).toBeVisible();
});

test('the projects view stacks nav above the body at 390 px', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 800 });
  await page.click('#btn-projects');
  await page.locator('#pm [data-page="settings"]').evaluate((b) => (b as HTMLElement).click());
  const nav = (await page.locator('.pmnav').boundingBox())!, body = (await page.locator('#pm-body').boundingBox())!;
  for (const b of [nav, body]) { expect(b.x).toBeGreaterThanOrEqual(0); expect(b.x + b.width).toBeLessThanOrEqual(391); }
  expect(nav.y + nav.height).toBeLessThanOrEqual(body.y + 1);
  await expect(page.locator('#pm-close')).toBeHidden();
  await expect(page.locator('#pm-close-top')).toBeVisible();
  const label = (await page.locator('#ps-tab-general .ps-row label').first().boundingBox())!, input = (await page.locator('#ps-budget').boundingBox())!;
  expect(input.y).toBeGreaterThan(label.y);
  expect(input.x + input.width).toBeLessThanOrEqual(391);
});
