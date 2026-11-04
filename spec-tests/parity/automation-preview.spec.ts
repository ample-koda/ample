import { test, expect, type Page } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// Plan automation-preview: hearing the draft of an automation curve before it is applied. The song and its text never change; Preview builds with the curve, Live runs the effect in the browser.
const SONG = 'composition "demo" { ample 1  tempo 120  master-seed 7 }\ndrums drums at 1 for 4 {\n  pattern beat-A\n}\nfilter lp {\n  cutoff 2000\n  from drums.audio -> audio\n}\nmixer mix {\n  from lp.audio -> audio1\n}\nmaster master bake {\n  from mix.audio -> audio\n}\n';

test.use({ launchOptions: { args: ['--autoplay-policy=no-user-gesture-required', '--mute-audio'] } });
freshServer({ song: SONG });
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

const songText = async () => (await api('/api/doc?path=song.amplet')).text as string;
const playing = (page: Page) => page.evaluate(() => (window as unknown as { AMPLE_PLAYER: { state: () => { playing: boolean } } }).AMPLE_PLAYER.state().playing);
const openEditor = (page: Page) => page.evaluate(() => { (window as unknown as { __editor: Promise<string | undefined> }).__editor = import('/app/automation-editor.js').then((m) => m.openAutomationEditor({ type: 'filter', name: 'cutoff', title: 'lp cutoff', text: 'auto(1:2000, 3:200)', bars: 4, tempo: 120, block: 'lp' })); });

test('a draft build with an override uses the draft value, leaves the song as it is and gives a different take', async ({ page }) => {
  const before = await songText();
  const plain = await api('/api/build', { method: 'POST', body: { path: 'song.amplet', intent: 'preview', range: { firstBar: 1, bars: 2 } } });
  const draft = await api('/api/build', { method: 'POST', body: { path: 'song.amplet', intent: 'preview', range: { firstBar: 1, bars: 2 }, override: { block: 'lp', name: 'cutoff', value: 'auto(1:200, 3:200)' } } });
  expect(plain.masterTakeId).toBeTruthy();
  expect(draft.masterTakeId).toBeTruthy();
  expect(draft.masterTakeId).not.toBe(plain.masterTakeId);
  expect(await songText()).toBe(before);
  const again = await api('/api/build', { method: 'POST', body: { path: 'song.amplet', intent: 'preview', range: { firstBar: 1, bars: 2 } } });
  expect(again.masterTakeId).toBe(plain.masterTakeId);
});

test('an override for a block that is not there is refused', async () => {
  const refused = await api('/api/build', { method: 'POST', body: { path: 'song.amplet', override: { block: 'nope', name: 'cutoff', value: '100' } } });
  expect(JSON.stringify(refused)).toContain('AMP5243');
});

test('Preview in the automation dialog plays a draft of the song, writes nothing, and stops when the dialog closes', async ({ page }) => {
  const before = await songText();
  await openEditor(page);
  await expect(page.locator('#auto-preview')).toBeVisible();
  await page.locator('#auto-preview').click();
  await expect(page.locator('.auto-status')).toContainText('Preview · exact', { timeout: 30000 });
  expect(await playing(page)).toBe(true);
  await page.locator('#dlg-btns').getByRole('button', { name: 'Cancel' }).click();
  await expect.poll(() => playing(page)).toBe(false);
  expect(await songText()).toBe(before);
});

test('Live needs a build, then runs the effect in the browser on the sound that feeds the block', async ({ page }) => {
  await openEditor(page);
  await expect(page.locator('#auto-live')).toBeDisabled();   // nothing built yet
  await page.locator('#dlg-btns').getByRole('button', { name: 'Cancel' }).click();
  await page.getByRole('button', { name: 'Build', exact: true }).click();
  await expect.poll(() => page.evaluate(async () => { const s = await import('/app/store.js'); return Boolean(s.S.build.takes.drums); }), { timeout: 60000 }).toBe(true);
  await openEditor(page);
  await expect(page.locator('#auto-live')).toBeEnabled();
  await page.locator('#auto-live').click();
  await expect(page.locator('.auto-status')).toContainText('Live · approximate', { timeout: 30000 });
  expect(await playing(page)).toBe(true);
  await page.locator('#auto-live').click();
  await expect.poll(() => playing(page)).toBe(false);
  await page.locator('#dlg-btns').getByRole('button', { name: 'Cancel' }).click();
});
