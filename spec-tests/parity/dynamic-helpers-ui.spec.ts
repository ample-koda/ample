import { test, expect } from '@playwright/test';
import { api, chooseMenu, freshServer, node, openApp, srcText, state } from '../../web/tests/support';

// Dynamic helpers in the app (docs/language/14): Jam says when the notes it shows depend on the clock, and after a build
// "Bake clock values" writes the values the build used into the source: in this song (one undo step) or in a NEW song (this one stays dynamic).
const SONG = [
  'composition "p" { ample 1  tempo 100  master-seed 3 }',
  '',
  'dynamic helper phase() -> Int {',
  '  return currentTime().second % 4',
  '}',
  '',
  'dynamic helper shiftBy(Int base) -> Int {',
  '  return base + currentTime().minute % 3',
  '}',
  '',
  'midi-clip theme at 1 for 1 {',
  '  notes "1:0:1:60:100"',
  '}',
  '',
  'midi-clip live at 2 for auto {',
  '  notes theme.notes |> transpose(phase())',
  '}',
  '',
  'midi-clip direct at 3 for auto {',
  '  notes theme.notes |> transpose(currentTime().second % 4)',
  '}',
  '',
  'midi-clip param at 4 for auto {',
  '  notes theme.notes |> transpose(shiftBy(2))',
  '}',
  '',
].join('\n');

freshServer({ song: SONG });
// the server keeps the working copy between tests: every test starts from the dynamic song
test.beforeEach(async ({ page }) => {
  await openApp(page, { reset: false });
  await page.evaluate(async (t) => { const store = await import('/app/store.js'); await store.applyText(t); }, SONG);
});

const built = async (page) => { await page.click('#btn-build'); await expect(page.locator('#btxt')).toHaveText('idle', { timeout: 60000 }); };

test('Jam shows a banner for notes that depend on the clock, with the bake actions', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'live').locator('.nh .t').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  await expect(page.locator('dialog.mls-panel')).toBeVisible();
  const banner = page.locator('.mls-dynamic-banner');
  await expect(banner).toBeVisible({ timeout: 20000 });
  await expect(banner).toContainText('Dynamic');
  await expect(banner).toContainText('currentTime()');
  await expect(banner.getByRole('button', { name: 'Bake resulting notes' })).toBeVisible();
  await expect(banner.getByRole('button', { name: 'Make editable' })).toBeVisible();
});

test('an open roll follows a build: the new clock gives new notes, with the same clock as the build', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'live').locator('.nh .t').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  const pitch = () => panel.evaluate((d: any) => d.mlsDebug.model().notes[0].note.pitch as number);
  await expect(panel.locator('.mls-dynamic-banner')).toContainText('this moment');
  await page.waitForTimeout(1300);                       // the clock is read in seconds: a later build reads another one
  await page.evaluate(async () => { const store = await import('/app/store.js'); await store.build(); });
  await expect(panel.locator('.mls-dynamic-banner')).toContainText('the last build');
  const clockAt = await page.evaluate(async () => (await import('/app/store.js')).S.build.clockAt as string);
  expect(clockAt).toMatch(/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d$/);
  const second = Number(clockAt.slice(-2));
  await expect.poll(pitch, { timeout: 15000 }).toBe(60 + (second % 4));   // what the roll shows is what the build made
});

test('Bake clock values asks where, bakes helpers, direct clock reads and calls with parameters, and undo takes it back', async ({ page }) => {
  await expect(page.locator('#btn-bake-dyn')).toBeHidden();
  await built(page);
  const bake = page.locator('#btn-bake-dyn');
  await expect(bake).toBeVisible();
  await bake.click();
  await expect(page.locator('#dlg')).toContainText('In a new song');
  await page.locator('#dlg').getByRole('button', { name: 'In this song' }).click();
  await expect.poll(async () => (await state(page)).text).toMatch(/dynamic helper phase\(\) -> Int bake as [0-3] \{/);
  const text = (await state(page)).text as string;
  expect(text).toMatch(/transpose\(bake currentTime\(\)\.second % 4 as [0-3]\)/);   // read the clock directly: baked where it stands
  expect(text).toMatch(/transpose\(bake shiftBy\(2\) as [0-9]+\)/);                   // a call with parameters
  expect(text).toMatch(/dynamic helper shiftBy\(Int base\) -> Int \{/);               // its helper stays dynamic
  await expect(bake).toBeHidden();
  await page.keyboard.press('Control+z');
  await expect.poll(async () => (await state(page)).text).toMatch(/dynamic helper phase\(\) -> Int \{/);
});

test('a block can bake its own clock values from its menu and the helpers stay dynamic', async ({ page }) => {
  await built(page);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'live').click({ button: 'right' });
  await chooseMenu(page, 'Edit', 'Bake clock values of this block…');
  await page.locator('#dlg').getByRole('button', { name: 'In this song' }).click();
  await expect.poll(async () => (await state(page)).text).toMatch(/transpose\(bake phase\(\) as [0-3]\)/);
  expect((await state(page)).text).toMatch(/dynamic helper phase\(\) -> Int \{/);
  expect((await state(page)).text).toMatch(/transpose\(currentTime\(\)\.second % 4\)/);   // the other blocks were not touched
});

test('Bake this call freezes one dynamic call in the source and leaves the helper dynamic', async ({ page }) => {
  await built(page);
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/transpose\(phase\(\)\)/);
  const button = page.locator('#se-bakecall');
  await expect(button).toBeHidden();
  // the caret goes inside the name phase() in the clip (the editor is CodeMirror: #se-ta mirrors its selection)
  await page.locator('#se-ta').evaluate((e) => { const ta = e as HTMLTextAreaElement & { onselectionchange?: () => void }; const at = ta.value.indexOf('transpose(phase())') + 'transpose(pha'.length; ta.focus(); ta.setSelectionRange(at, at); ta.onselectionchange?.(); });
  await expect(button).toBeVisible();
  await button.click();
  await expect.poll(() => srcText(page)).toMatch(/transpose\(bake phase\(\) as [0-3]\)/);
  expect(await srcText(page)).toMatch(/dynamic helper phase\(\) -> Int \{/);
  // a call with a parameter too
  await page.locator('#se-ta').evaluate((e) => { const ta = e as HTMLTextAreaElement & { onselectionchange?: () => void }; const at = ta.value.indexOf('shiftBy(2)') + 3; ta.focus(); ta.setSelectionRange(at, at); ta.onselectionchange?.(); });
  await expect(button).toBeVisible();
  await button.click();
  await expect.poll(() => srcText(page)).toMatch(/transpose\(bake shiftBy\(2\) as [0-9]+\)/);
});

// last: it opens the new song, so the tests above all start from the dynamic song
test('Bake clock values can write a new song and leaves the open one dynamic', async ({ page }) => {
  await built(page);
  await page.locator('#btn-bake-dyn').click();
  await page.locator('#dlg input').fill('p-baked');
  await page.locator('#dlg').getByRole('button', { name: 'In a new song' }).click();
  await expect.poll(async () => (await api('/api/docs')).some((f: string) => f.endsWith('p-baked.amplet'))).toBe(true);
  const made = (await api('/api/docs')).find((f: string) => f.endsWith('p-baked.amplet')) as string;
  const baked = (await api(`/api/doc?path=${encodeURIComponent(made)}`)).text as string;
  expect(baked).toMatch(/dynamic helper phase\(\) -> Int bake as [0-3] \{/);
  expect(baked).toMatch(/transpose\(bake shiftBy\(2\) as [0-9]+\)/);
  const original = (await api('/api/doc?path=song.amplet')).text as string;                 // the seed is untouched
  expect(original).toMatch(/dynamic helper phase\(\) -> Int \{/);
});
