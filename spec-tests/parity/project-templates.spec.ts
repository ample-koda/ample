import { test, expect } from '@playwright/test';
import { freshServer, openApp, srcText } from '../../web/tests/support';

// Project templates and suggestions in the Templates tab: save the selected blocks as a template (with a setting), put it into the song at another bar under free names, delete it; suggestions from what the song lacks; similar beats.
freshServer({ song: 'composition "p" { ample 1  tempo 100  master-seed 3 }\nmidi-clip chords {\n  chords Am F C G\n  bars 8\n}\npiano keys pinned@2.1 at 1 for 8 {\n  gain -9\n  humanize 18\n  from chords.notes -> notes\n}\n' });
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); await page.locator('#ltabs button[data-l="tpl"]').click(); });

// the song may still be loading when this runs, and loading it again clears the selection: select again until the selection is still there a moment later
const select = (page, ids: string[]) => expect(async () => {
  await page.evaluate(async (names) => { const s = await import('/app/store.js'); names.forEach((n, i) => s.select(n, i > 0)); s.notifySelection(); s.notify(); }, ids);   // the Save button listens to these two, as the canvas calls them after a click
  await page.waitForTimeout(400);
  expect(await page.evaluate(async (names) => { const s = await import('/app/store.js'); return names.every((n) => s.S.selSet.has(n)); }, ids)).toBe(true);
}).toPass({ timeout: 20000 });

test('selected blocks are saved as a template with a setting, added again under free names at another bar, and deleted @quarantine', async ({ page }) => {
  await select(page, ['chords', 'keys']);
  await expect(page.locator('#tpl-save')).toBeEnabled();
  await page.locator('#tpl-save').click();
  const win = page.locator('dialog.ample-win');
  await expect(win).toBeVisible();
  await win.locator('input[aria-label="Template name"]').fill('Chords and keys');
  await win.locator('input[aria-label="Make humanize of keys a setting"]').check();
  await win.locator('input[aria-label="Label of humanize of keys"]').fill('Human feel');
  await win.getByRole('button', { name: 'Save template' }).click();
  await expect(win).toHaveCount(0);
  const saved = await page.evaluate(async () => (await (await fetch('/api/file?path=templates/chords-and-keys.json')).json()).text);
  expect(JSON.parse(saved).source).toContain('humanize {{keys-humanize}}');
  expect(JSON.parse(saved).settings[0]).toMatchObject({ label: 'Human feel', default: '18' });
  // the Project list shows it; a double click adds it again
  await page.locator('#pane-tpl .tpl-source button[data-source="project"]').click();
  const card = page.locator('#tpl-list .tpl-card', { hasText: 'Chords and keys' });
  await expect(card).toBeVisible();
  await card.dblclick();
  await expect(win).toBeVisible();
  await win.locator('input[aria-label="Starts at bar"]').fill('9');
  await win.locator('input[aria-label="Human feel"]').fill('30');
  await win.getByRole('button', { name: 'Add to song' }).click();
  await expect(win).toHaveCount(0);
  await page.click('#vs-source');
  const text = await (async () => { await expect.poll(() => srcText(page)).toMatch(/keys-2/); return srcText(page); })();
  expect(text).toMatch(/midi-clip chords-2 /);
  expect(text).toMatch(/piano keys-2 pinned@2\.1 at 9 for 8/);
  expect(text).toMatch(/humanize 30/);
  expect(text).toMatch(/from chords-2\.notes -> notes/);
  // delete it
  await page.click('#vs-blocks');
  await card.locator('.tpl-x').click();
  await page.getByRole('button', { name: 'Delete', exact: true }).click();
  await expect(card).toHaveCount(0);
});

test('a block can be saved as a switch (on or off when the template is used), and the windows can be resized', async ({ page }) => {
  if (!(await page.locator('#pane-tpl').isVisible())) await page.locator('#ltabs button[data-l="tpl"]').click();   // the tab is remembered: the beforeEach click may have closed it
  await select(page, ['chords', 'keys']);
  await page.evaluate(async () => { (await import('/app/project-templates.js')).openSaveTemplate(); });   // the same window as the button (its enabled state is not what is tested here)
  const win = page.locator('dialog.ample-win');
  await expect(win).toBeVisible();
  const before = await win.boundingBox();
  const grip = win.locator('.aw-grip');
  await expect(grip).toBeVisible();
  const g = await grip.boundingBox();
  await page.mouse.move(g!.x + 8, g!.y + 8);
  await page.mouse.down();
  await page.mouse.move(g!.x + 128, g!.y + 78, { steps: 4 });
  await page.mouse.up();
  const after = await win.boundingBox();
  expect(after!.width).toBeGreaterThan(before!.width + 80);
  expect(after!.height).toBeGreaterThan(before!.height + 40);
  await win.locator('input[aria-label="Template name"]').fill('Switchable');
  await win.locator('input[aria-label="Let people switch keys on or off"]').check();
  await win.locator('input[aria-label="Label of the switch of keys"]').fill('Keys');
  await win.getByRole('button', { name: 'Save template' }).click();
  await expect(win).toHaveCount(0);
  const saved = JSON.parse(await page.evaluate(async () => (await (await fetch('/api/file?path=templates/switchable.json')).json()).text));
  expect(saved.source).toContain('{{use-keys}} {');
  expect(saved.settings.find((x: any) => x.name === 'use-keys')).toMatchObject({ kind: 'switch', label: 'Keys', default: '' });
  await page.locator('#pane-tpl .tpl-source button[data-source="project"]').click();
  await page.locator('#tpl-list .tpl-card', { hasText: 'Switchable' }).dblclick();
  await expect(win).toBeVisible();
  await win.locator('input[aria-label="Keys"]').uncheck();
  await win.getByRole('button', { name: 'Add to song' }).click();
  await expect(win).toHaveCount(0);
  expect(await page.evaluate(async () => (await import('/app/store.js')).S.doc.text)).toMatch(/piano keys-\d+ pinned@2\.1 at 1 for 8 disabled \{/);
});

test('a project template can be dragged onto the board: a preview hangs at the pointer, letting go opens the form, which can be cancelled or confirmed @quarantine', async ({ page }) => {
  await select(page, ['chords', 'keys']);
  await expect(page.locator('#tpl-save')).toBeEnabled();
  await page.locator('#tpl-save').click();
  const win = page.locator('dialog.ample-win');
  await win.locator('input[aria-label="Template name"]').fill('Dragged');
  await win.getByRole('button', { name: 'Save template' }).click();
  await expect(win).toHaveCount(0);
  await page.locator('#pane-tpl .tpl-source button[data-source="project"]').click();
  const card = page.locator('#tpl-list .tpl-card', { hasText: 'Dragged' });
  const c = await card.boundingBox();
  const cv = await page.locator('#cv').boundingBox();
  const text = () => page.evaluate(async () => (await import('/app/store.js')).S.doc.text);
  const before = await text();
  await page.mouse.move(c!.x + 30, c!.y + 20);
  await page.mouse.down();
  await page.mouse.move(c!.x + 60, c!.y + 20, { steps: 3 });
  await page.mouse.move(cv!.x + cv!.width / 2, cv!.y + cv!.height / 2, { steps: 6 });
  await expect(page.locator('.tpl-drag.on')).toContainText('adds 2 blocks');
  expect(await text()).toBe(before);
  await page.mouse.up();
  await expect(page.locator('.tpl-drag')).toHaveCount(0);
  await expect(win).toBeVisible();
  await win.getByRole('button', { name: 'Cancel' }).click();
  await expect(win).toHaveCount(0);
  expect(await text()).toBe(before);
  await page.mouse.move(c!.x + 30, c!.y + 20);
  await page.mouse.down();
  await page.mouse.move(cv!.x + 200, cv!.y + 200, { steps: 8 });
  await page.mouse.up();
  await win.getByRole('button', { name: 'Add to song' }).click();
  await expect(win).toHaveCount(0);
  expect(await text()).not.toBe(before);
  // the blocks that came in are selected
  expect(await page.evaluate(async () => (await import('/app/store.js')).S.selSet.size)).toBeGreaterThanOrEqual(2);
  expect(await page.locator('.node.sel').count()).toBeGreaterThanOrEqual(2);
});

test('a song with chords and no drums or bass is offered a groove and a bass, and a suggestion filters the beats', async ({ page }) => {
  const suggestions = page.locator('#tpl-suggest .tpl-suggestion');
  await expect(suggestions.first()).toBeVisible();
  await expect(suggestions.filter({ hasText: 'Put a bass under the chords' })).toBeVisible();
  const cards = page.locator('#tpl-list .tpl-card');
  await expect(cards.first()).toBeVisible();
  const all = await page.locator('#tpl-count').textContent();
  await suggestions.filter({ hasText: 'Put a bass under the chords' }).click();
  await expect.poll(() => page.locator('#tpl-count').textContent()).not.toBe(all);
  await expect(page.locator('#tpl-tempo')).toBeChecked();
});

test('the wizard of a beat offers similar beats and a click opens that beat', async ({ page }) => {
  await page.locator('#tpl-q').fill('house');
  await page.locator('#tpl-list .tpl-card').filter({ hasText: 'four on the floor' }).first().dblclick();
  const win = page.locator('dialog.ample-win');
  await expect(win.locator('.bw-similar button').first()).toBeVisible();
  const title = await win.locator('.aw-title').textContent();
  await win.locator('.bw-similar button').first().click();
  await expect(win.locator('.aw-title')).not.toHaveText(title!);
});
