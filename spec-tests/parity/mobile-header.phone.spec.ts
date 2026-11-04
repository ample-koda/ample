import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

// MUI01: at phone width the header is one row with a "more" menu. Run with --project=phone.
test.beforeEach(async ({ page }) => { await openApp(page); });

test('the header is a single row of at most 56 px and does not scroll sideways', async ({ page }) => {
  const box = (await page.locator('header').boundingBox())!;
  expect(box.height).toBeLessThan(56);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(await page.locator('meta[name=viewport]').getAttribute('content')).toContain('viewport-fit=cover');
});

test('Projects, Undo, Redo, Build-and-play and the more button are visible; the rest is in the menu', async ({ page }) => {
  for (const id of ['#btn-projects', '#btn-undo', '#btn-redo', '#btn-preview', '#btn-more']) await expect(page.locator(id)).toBeVisible();
  for (const id of ['#btn-build', '#btn-save', '#btn-cp', '#tg-left', '#tg-right', '#tg-bottom', '#btn-help']) await expect(page.locator(id)).toBeHidden();
  await expect(page.locator('#bprog')).toBeVisible();
  // all visible header buttons stay inside the screen
  const right = await page.locator('#btn-more').evaluate((e) => e.getBoundingClientRect().right);
  expect(right).toBeLessThanOrEqual(390);
  const pill = (await page.locator('#bprog').boundingBox())!;
  expect(pill.width).toBeGreaterThanOrEqual(30);
  expect(await page.locator('#btn-preview').evaluate((e) => e.getBoundingClientRect().height)).toBeGreaterThanOrEqual(30);
});

test('the more menu holds Save, Checkpoint, panels and help, works by touch and closes with Escape', async ({ page }) => {
  const more = page.locator('#btn-more');
  await expect(more).toHaveAttribute('aria-expanded', 'false');
  await more.tap();
  await expect(more).toHaveAttribute('aria-expanded', 'true');
  const items = page.locator('#more-popup [role^="menuitem"]');
  const names = await items.allTextContents();
  for (const n of ['Build', 'Save', 'Checkpoint...', 'Block library', 'Properties', 'Panel', 'Help']) expect(names).toContain(n);
  await page.keyboard.press('Escape');
  await expect(page.locator('#more-popup')).toBeHidden();
  await expect(more).toBeFocused();
});

test('Checkpoint from the menu opens the checkpoint dialog, and the menu is keyboard operable', async ({ page }) => {
  await page.locator('#btn-more').focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('#more-popup [role=menuitem]').first()).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect(page.locator('#more-popup [role=menuitem]').nth(1)).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(page.locator('#more-popup')).toBeHidden();
  await expect(page.locator('dialog[open]')).toBeVisible();
});

test('Play and Stop are one button: it says Stop while audio plays, Cancel while a build runs, and Play again afterwards', async ({ page }) => {
  await expect(page.locator('#btn-stop')).toBeHidden();
  const set = (mode: string) => page.evaluate((m) => {
    const stop = document.querySelector('#btn-stop') as HTMLButtonElement;
    stop.disabled = m === 'none';
    stop.dataset.mode = m;
    stop.classList.toggle('playing', m === 'audio');
    (window as any).__stops = (window as any).__stops || 0;
    if (!(stop as any).__spy) { (stop as any).__spy = true; stop.addEventListener('click', () => { (window as any).__stops++; }); }
  }, mode);
  const play = page.locator('#btn-preview');
  await set('audio');
  await expect(play).toHaveClass(/as-stop/);
  expect(await play.evaluate((e) => getComputedStyle(e, '::after').content)).toContain('Stop');
  await play.click();
  expect(await page.evaluate(() => (window as any).__stops)).toBe(1);
  await set('build');
  expect(await play.evaluate((e) => getComputedStyle(e, '::after').content)).toContain('Cancel');
  await set('none');
  await expect(play).not.toHaveClass(/as-stop/);
});

test('the assistant panel head uses icon-only buttons that keep their name, and the toast clears the tab bar', async ({ page }) => {
  await page.locator('#b-more').click();
  await page.getByRole('menuitem', { name: 'Assistant' }).click();
  await expect(page.locator('.assistant-panel-head')).toHaveCount(1);
  for (const name of ['New session', 'Refresh']) {
    const b = page.locator('.assistant-panel-head').getByRole('button', { name });
    await expect(b).toBeVisible();
    const box = (await b.boundingBox())!;
    expect(box.width).toBeLessThanOrEqual(48);
    expect(box.height).toBeGreaterThanOrEqual(36);
  }
  await expect(page.locator('.assistant-panel-brand p')).toBeHidden();
  const toastBottom = await page.evaluate(() => parseFloat(getComputedStyle(document.querySelector('#toast')!).bottom));
  expect(toastBottom).toBeGreaterThanOrEqual(56);
});

test('assistant session cards keep their action buttons on one row inside the card', async ({ page }) => {
  await page.route('**/api/ai/sessions*', (r) => r.fulfill({ json: [{ id: 's1', title: 'A session', archiveConversation: false, updated: Date.now(), tasks: [] }] }));
  await page.locator('#b-more').click();
  await page.getByRole('menuitem', { name: 'Assistant' }).click();
  const card = page.locator('.assistant-session-card').first();
  await expect(card).toBeVisible();
  const c = (await card.boundingBox())!;
  const tops = new Set<number>();
  for (const b of await card.locator('.assistant-session-action').all()) {
    const r = (await b.boundingBox())!;
    tops.add(Math.round(r.y));
    expect(r.x + r.width).toBeLessThanOrEqual(c.x + c.width + 0.5);
  }
  expect(tops.size).toBe(1);
});

test('the arrangement tools are one row of icons above the transport and the lane labels stay narrow', async ({ page }) => {
  await page.locator('#btabs button[data-t="arr"]').click();
  const ctl = page.locator('#arr-ctl');
  await expect(ctl).toBeVisible();
  for (const name of ['Sound check', 'Record', 'Separate and transcribe audio']) {
    const b = ctl.getByRole('button', { name });
    const box = (await b.boundingBox())!;
    expect(box.width).toBeLessThanOrEqual(44);
  }
  const bars = await ctl.locator('>button').evaluateAll((l) => new Set(l.map((e) => Math.round((e.getBoundingClientRect().top + e.getBoundingClientRect().bottom) / 10))).size);
  expect(bars).toBe(1);
  expect((await page.locator('.arr-labels').boundingBox())!.width).toBeLessThanOrEqual(100);
});
