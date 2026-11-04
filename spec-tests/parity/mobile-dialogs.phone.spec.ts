import { test, expect, Page } from '@playwright/test';
import { addTemplateBlock, freshServer, node, openApp } from '../../web/tests/support';

// MUI05 + MUI07: dialogs and panels are full-screen pages on a phone with a Back bar, and none of the main ones is wider than 390 px.
freshServer();
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

async function fits(page: Page, selector: string) {
  const r = await page.evaluate((sel) => {
    const d = document.querySelector<HTMLElement>(sel)!;
    const b = d.getBoundingClientRect();
    // anything visible that sticks out of the viewport and is not inside a scroll container of its own
    const out: string[] = [];
    for (const e of d.querySelectorAll<HTMLElement>('*')) {
      const r = e.getBoundingClientRect();
      if (!r.width || !r.height || getComputedStyle(e).visibility === 'hidden') continue;
      if (r.right <= innerWidth + 1 && r.left >= -1) continue;
      let p: HTMLElement | null = e.parentElement, scrolls = false;
      while (p && p !== d.parentElement) { const o = getComputedStyle(p).overflowX; if (o === 'auto' || o === 'scroll' || o === 'hidden') { scrolls = true; break; } p = p.parentElement; }
      if (!scrolls) out.push(`${e.tagName.toLowerCase()}.${String(e.className).slice(0, 30)}#${e.id}`);
    }
    return { left: b.left, right: b.right, top: b.top, bottom: b.bottom, scrollW: d.scrollWidth, clientW: d.clientWidth, doc: document.documentElement.scrollWidth, out: out.slice(0, 5) };
  }, selector);
  expect(r.left).toBeGreaterThanOrEqual(-1);
  expect(r.right).toBeLessThanOrEqual(391);
  expect(r.scrollW).toBeLessThanOrEqual(r.clientW + 1);
  expect(r.doc).toBeLessThanOrEqual(390);
  expect(r.out).toEqual([]);
  return r;
}

async function fullScreen(page: Page, selector: string) {
  const r = await fits(page, selector);
  expect(r.top).toBeLessThanOrEqual(1);
  expect(r.bottom).toBeGreaterThanOrEqual(843);
  const bar = page.locator(`${selector} > .mob-back`);
  await expect(bar).toBeVisible();
  const back = bar.locator('.mob-back-btn');
  const box = (await back.boundingBox())!;
  expect(box.height).toBeGreaterThanOrEqual(44);
  expect(box.width).toBeGreaterThanOrEqual(44);
}

test('an ask dialog is a full-screen page; Back, Escape and the system Back all close it', async ({ page }) => {
  const home = page.url();
  const open = () => page.evaluate(() => { (window as any).__ans = undefined; import('/app/ui.js').then((u) => u.ask('Delete everything?', 'This is a long explanation. '.repeat(20), [{ label: 'Cancel', value: 'no' }, { label: 'Delete', value: 'yes', primary: true }]).then((v: unknown) => { (window as any).__ans = v ?? 'closed'; })); });
  await open();
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await fullScreen(page, '#dlg');
  await expect(page.locator('#dlg > .mob-back .mob-back-title')).toHaveText('Delete everything?');
  await page.locator('#dlg .mob-back-btn').tap();
  await expect(page.locator('#dlg')).toHaveJSProperty('open', false);
  await expect.poll(() => page.evaluate(() => (window as any).__ans)).toBe('closed');
  await open();
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await page.keyboard.press('Escape');
  await expect(page.locator('#dlg')).toHaveJSProperty('open', false);
  // the system Back button (history back) closes the dialog and does not leave the app
  await open();
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await page.goBack();
  await expect(page.locator('#dlg')).toHaveJSProperty('open', false);
  expect(page.url()).toBe(home);
  await expect(page.locator('.node').first()).toBeVisible();
  // a dialog that was closed by its own button leaves no history entry behind
  await open();
  await page.locator('#dlg-btns button.primary').tap();
  await expect(page.locator('#dlg')).toHaveJSProperty('open', false);
  expect(await page.evaluate(() => (window as any).__ans)).toBe('yes');
  await page.waitForTimeout(150);
  expect(page.url()).toBe(home);
  await expect(page.locator('.node').first()).toBeVisible();
});

test('the checkpoint dialog and the about dialog fit 390 px', async ({ page }) => {
  await page.locator('#btn-more').tap();
  await page.getByRole('menuitem', { name: 'Checkpoint...' }).tap();
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await fullScreen(page, '#dlg');
  await page.locator('#dlg .mob-back-btn').tap();
  await page.locator('#btn-more').tap();
  await page.getByRole('menuitem', { name: 'About AMPLE' }).tap();
  await expect(page.locator('dialog.about-dialog')).toHaveJSProperty('open', true);
  await expect(page.locator('dialog.about-dialog .about-copy')).toBeVisible();
  await fullScreen(page, 'dialog.about-dialog');
});

test('the projects view (settings and storage pages) fits 390 px and keeps its Back button', async ({ page }) => {
  await page.locator('#btn-projects').tap();
  for (const p of ['recent', 'settings', 'storage', 'health', 'finals', 'backup', 'new']) {
    await page.evaluate((k) => (document.querySelector(`#pm [data-page="${k}"]`) as HTMLElement).click(), p);
    await expect(page.locator('#pm-body')).toBeVisible();
    await page.waitForTimeout(250);
    const w = await page.evaluate(() => ({ pm: document.getElementById('pm')!.scrollWidth, doc: document.documentElement.scrollWidth }));
    expect(w.pm, p).toBeLessThanOrEqual(391);
    expect(w.doc, p).toBeLessThanOrEqual(390);
  }
  await expect(page.locator('#pm-close-top')).toBeVisible();
  await page.locator('#pm-close-top').tap();
  await expect(page.locator('#pm')).toBeHidden();
});

test('the Studio (advanced effect panel) is a full-screen page with a scrolling body', async ({ page }) => {
  await addTemplateBlock(page, 'compressor', 'comp');
  await page.locator('#z-100').tap();
  await node(page, 'comp').getByRole('button', { name: 'Advanced', exact: true }).evaluate((e) => (e as HTMLElement).click());
  await expect(page.locator('#studio')).toBeVisible();
  await fullScreen(page, '#studio');
  const body = page.locator('#studio .studio-dialog-body');
  expect(await body.evaluate((e) => getComputedStyle(e).overflowY)).toBe('auto');
  await page.locator('#studio > .mob-back .mob-back-btn').tap();
  await expect(page.locator('#studio')).toBeHidden();
});

test('the history compare dialog and AMPLE Jam fit 390 px', async ({ page }) => {
  await page.locator('#btabs button[data-t="hist"]').tap();
  await page.locator('#h-compare').evaluate((e) => (e as HTMLElement).click());
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await fullScreen(page, '#dlg');
  await page.keyboard.press('Escape');
  await expect(page.locator('#dlg')).toHaveJSProperty('open', false);
  await page.locator('#btabs button[data-t="hist"]').tap();

  await addTemplateBlock(page, 'midi-clip', 'adv');
  await page.evaluate(async () => { const s = await import('/app/store.js'); await s.edit([{ type: 'setParameter', block: 'adv', name: 'notes', value: '"1:0:1:60:100 1:1:1:64:100 1:2:1:67:100 1:3:1:72:100"' }]); });
  await page.getByRole('button', { name: 'Fit', exact: true }).tap();
  await node(page, 'adv').locator('.nh .t').tap();
  await page.locator('button', { hasText: /AMPLE Jam/ }).first().evaluate((e) => (e as HTMLElement).click());
  const roll = page.locator('dialog.mls-panel');
  await expect(roll).toBeVisible();
  const rollBox = (await roll.boundingBox())!;
  expect(rollBox.x).toBeGreaterThanOrEqual(0);
  expect(rollBox.x + rollBox.width).toBeLessThanOrEqual(390);
  await roll.getByRole('button', { name: 'Close AMPLE Jam' }).tap();
  await expect(roll).toBeHidden();
});

test('more dialogs fit 390 px: sound check and the export dialog', async ({ page }) => {
  await page.evaluate(() => import('/app/soundcheck.js').then((m) => { m.openSoundCheck(); }));
  const sound = page.locator('dialog.soundcheck');
  await expect(sound).toHaveJSProperty('open', true);
  await page.waitForTimeout(300);
  await fullScreen(page, 'dialog.soundcheck');
  await sound.locator('> .mob-back .mob-back-btn').tap();
  await expect(sound).toHaveJSProperty('open', false);
  await page.evaluate(() => import('/app/package.js').then((m) => { m.openExportDialog(); }));
  const exp = page.locator('dialog[open]').last();
  await expect(exp).toBeVisible();
  await page.waitForTimeout(300);
  await fullScreen(page, 'dialog[open]');
  await page.keyboard.press('Escape');
  await expect(page.locator('dialog[open]')).toHaveCount(0);
});
