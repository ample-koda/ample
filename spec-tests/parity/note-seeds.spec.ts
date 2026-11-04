import { test, expect, type Page } from '@playwright/test';
import { api, freshServerPerTest, node, openApp, srcText } from '../../web/tests/support';

// The seed model in the UI: copying a clip with stable random names asks reuse / fork / independent, the editor offers to pick a random-seed() once, completes seeds, and the roll
// tells where the randomness of a pipeline comes from. DOM only, no audio.
freshServerPerTest();
test.use({ viewport: { width: 1400, height: 1200 } });   // the block menu is long

const SONG = `composition "demo" { ample 1  tempo 100  master-seed 1 }

midi-clip a at 1 for 1 {
  notes "1:0:1:60:100 1:1:1:62:100 1:2:1:64:100"
}

midi-clip b at 2 for auto {
  notes a.notes
    |> humanize(timing=20ms, velocity=4, seed=stable("verse"))
}
`;

async function setSong(page: Page, text: string) {
  await page.evaluate(async (t) => { const s = await import('/app/store.js'); await s.applyText(t); }, text);
}
const docText = async () => (await api('/api/doc?path=song.amplet')).text as string;

async function copyDialog(page: Page, choice: string) {
  // the Duplicate item of the block menu (Edit > Duplicate) calls this
  await page.evaluate(async () => { const canvas = await import('/app/canvas.js'); const store = await import('/app/store.js'); void canvas.duplicateBlock(store.flat().nodes.find((n: { id: string }) => n.id === 'b')); });
  const dialog = page.locator('#dlg');
  await expect(dialog).toContainText('stable random names');
  await expect(dialog).toContainText('"verse"');
  await dialog.getByRole('button', { name: choice, exact: true }).click();
}

test.beforeEach(async ({ page }) => {
  await openApp(page);
  await setSong(page, SONG);
});

test('copying a clip with a stable random name asks what the copy should do, and Reuse keeps the name', async ({ page }) => {
  await copyDialog(page, 'Reuse');
  await expect.poll(docText).toContain('midi-clip b-copy');
  const text = await docText();
  expect(text.split('stable("verse")').length - 1).toBe(2);
  expect(text).not.toContain('verse_2');
});

test('Fork gives the copy its own branch of the same name', async ({ page }) => {
  await copyDialog(page, 'Fork');
  await expect.poll(docText).toContain('midi-clip b-copy');
  const text = await docText();
  expect(text).toContain('stable("verse").fork("copy-b-copy")');
  expect(text.split('stable("verse")').length - 1).toBe(2);   // the original keeps its plain name
});

test('Independent gives the copy a new song-global name', async ({ page }) => {
  await copyDialog(page, 'Independent');
  await expect.poll(docText).toContain('midi-clip b-copy');
  const text = await docText();
  expect(text).toContain('stable("verse_2")');
  expect(text.split('stable("verse")').length - 1).toBe(1);
});

test('Cancel makes no copy', async ({ page }) => {
  await copyDialog(page, 'Cancel');
  await page.waitForTimeout(600);
  expect(await docText()).toBe(SONG);
});

test('a randomSeed() is refused until it is picked: the tooltip button writes a number into the song', async ({ page }) => {
  await setSong(page, SONG.replace('stable("verse")', 'randomSeed()'));
  await page.click('#vs-source');
  await expect(page.locator('#se-cm .cm-content')).toBeVisible();
  await expect.poll(() => srcText(page)).toContain('randomSeed()');
  const lines = (await srcText(page)).split('\n');
  const line = lines.findIndex((l) => l.includes('randomSeed()'));
  const box = (await page.locator('#se-cm .cm-scroller').boundingBox())!;
  const tip = page.locator('#se-errtip');
  await page.locator('#se-prob .seerr', { hasText: 'AMP2039' }).first().dblclick({ timeout: 15000 });   // the details of a problem open with a double click on it in the list (a plain hover shows nothing)
  await expect(tip).toBeVisible();
  await expect(tip).toContainText('AMP2039');
  await tip.getByRole('button', { name: 'Pick a seed now' }).click();
  await expect.poll(async () => (await docText()).includes('randomSeed()')).toBe(false);
  expect(await docText()).toMatch(/seed=\d{6,}\)/);
});

test('after seed= the list offers the stable names of the song and the other ways to seed', async ({ page }) => {
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
  await page.locator('#se-cm .cm-content').click();
  await page.keyboard.press('Control+End');
  await page.keyboard.type('midi-clip c at 3 for auto {\n  notes a.notes\n    |> thin(0.5, seed=');
  await expect(async () => { await page.keyboard.press('Control+Space'); await expect(page.locator('#se-comp')).toBeVisible({ timeout: 1500 }); }).toPass({ timeout: 20000 });
  await expect(page.locator('#se-comp')).toContainText('stable("verse")');
  await expect(page.locator('#se-comp')).toContainText('randomSeed()');
});

test('the roll tells where the randomness of a pipeline comes from', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'b').locator('.nh .t').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  await panel.getByRole('button', { name: 'Menu' }).click();
  await page.getByRole('menuitem', { name: /Where does the randomness come from/ }).click();
  const dialog = page.locator('#dlg');
  await expect(dialog).toContainText('humanize');
  await expect(dialog).toContainText('stable("verse")');
  await expect(dialog).toContainText('"verse" (1x)');
});
