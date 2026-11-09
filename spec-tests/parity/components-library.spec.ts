import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp, ownServer } from '../../web/tests/support';

// The Library tab: every component of every .amplec file of the project (also the ones the song does not import), a quick search that also looks in #tags and
// descriptions, and one-click adding that shows the lines first.
const DEMO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../demo-projects/components/components');
const TAGGED = '/** Repeats the notes a few times. */\n#[DisplayName("Triple notes")]\n#[Tags("repeat", "Triplet")]\ncomponent koda.notes.tripleNotes version 1.0.0 current {\n  id "demo.koda.notes.triple-notes"\n  input source: Notes\n  property copies: Int = 3\n  output notes: Notes\n  process {\n    for i in 0..copies {\n      emit source |> shift(i * 1bar)\n    }\n  }\n}\n';

freshServer({
  song: 'composition "library" { ample 1  tempo 100  master-seed 1 }\nimport "components/triple-notes.amplec"\nmidi-clip melody at 1 for 2 {\n  notes 1:0:1:60:100 1:1:1:64:100\n}\n',
  prepare: (_cli, folder) => {
    fs.mkdirSync(path.join(folder, 'components'), { recursive: true });
    fs.writeFileSync(path.join(folder, 'components', 'triple-notes.amplec'), TAGGED);
    for (const f of ['dream-echo.amplec', 'swing-line.amplec']) fs.copyFileSync(path.join(DEMO, f), path.join(folder, 'components', f));
  },
});

const openLibrary = async (page) => {
  await page.locator('#ltabs button[data-l="clib"]').click();
  await expect(page.locator('#pane-clib')).toBeVisible();
  await expect(page.locator('#clib-list .clib-entry')).toHaveCount(5);   // the three of the project and the two that ship with AMPLE
};

test('the library lists every component of the project, the imported one marked', async ({ page }) => {
  await openApp(page, { reset: false });
  await openLibrary(page);
  const triple = page.locator('#clib-list .clib-entry', { hasText: 'Triple notes' });
  await expect(triple.locator('.clib-st')).toBeVisible();   // imported
  await expect(page.locator('#clib-list .clib-entry', { hasText: 'Dream echo' }).locator('.clib-st')).toHaveCount(0);
  await expect(page.locator('#clib-list .clib-entry', { hasText: 'Dream echo' }).locator('.clib-ic')).toHaveText('∿');
  await expect(triple.locator('.tplitem')).toHaveAttribute('title', /Repeats the notes a few times/);
});

test('the search finds by name, #tag and description, and a tag chip filters', async ({ page }) => {
  await openApp(page, { reset: false });
  await openLibrary(page);
  await page.fill('#clib-q', '#triplet');   // the tag was written with a capital: tags are lower case
  await expect(page.locator('#clib-list .clib-entry')).toHaveCount(1);
  await expect(page.locator('#clib-list .clib-entry')).toContainText('Triple notes');
  await page.fill('#clib-q', 'repeats');   // in the description
  await expect(page.locator('#clib-list .clib-entry')).toContainText('Triple notes');
  await page.fill('#clib-q', 'echo');
  await expect(page.locator('#clib-list .clib-entry', { hasText: 'Dream echo' })).toHaveCount(1);
  await page.fill('#clib-q', 'nothing like this');
  await expect(page.locator('#clib-info')).toContainText('Nothing found');
});

test('adding shows the lines first, writes the import and a block, and the entry is marked imported', async ({ page }) => {
  await openApp(page, { reset: false });
  await openLibrary(page);
  const echo = page.locator('#clib-list .clib-entry', { hasText: 'Dream echo' });
  await echo.hover();
  await echo.getByRole('button', { name: 'Import and add Dream echo' }).click();
  await expect(page.locator('#dlg')).toBeVisible();
  await expect(page.locator('#dlg-extra')).toContainText('+ import "components/dream-echo.amplec"');
  await expect(page.locator('#dlg-extra')).toContainText('+ koda.fx.dreamEcho dreamEcho {');
  await page.locator('#dlg-btns button', { hasText: 'Write import and block' }).click();
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text, { timeout: 15000 }).toContain('import "components/dream-echo.amplec"');
  await expect(node(page, 'dreamEcho')).toHaveCount(1);
  await expect(page.locator('#clib-list .clib-entry', { hasText: 'Dream echo' }).locator('.clib-st')).toBeVisible({ timeout: 15000 });
});

test('the library reads only the files that changed (the rest comes from the cache) and sees a new file after a scan', async ({ page }) => {
  await openApp(page, { reset: false });
  const first = await api('/api/components/library?path=song.amplet');
  expect(first.files.length).toBe(5);   // three of the project and two that ship with AMPLE (not counted as scanned or cached)
  const second = await api('/api/components/library?path=song.amplet');
  expect(second.scanned).toBe(0);
  expect(second.cached).toBe(3);
  fs.writeFileSync(path.join(ownServer()!.folder, 'components', 'extra.amplec'), TAGGED.replace('tripleNotes', 'extraNotes').replace('triple-notes', 'extra-notes'));
  const third = await api('/api/components/library?path=song.amplet');
  expect(third.files.length).toBe(6);
  expect(third.scanned).toBe(1);   // only the new file was read
  expect(third.cached).toBe(3);
  expect(third.files.flatMap((f: { components: { tags: string[] }[] }) => f.components.flatMap((c) => c.tags))).toContain('triplet');
  fs.rmSync(path.join(ownServer()!.folder, 'components', 'extra.amplec'));   // the other tests count three entries
  const fourth = await api('/api/components/library?path=song.amplet');
  expect(fourth.files.length).toBe(5);
});

test('a component whose name is taken by other code offers an alias, and the import then works', async ({ page }) => {
  // another file with the same exported name, but another id
  fs.writeFileSync(path.join(ownServer()!.folder, 'components', 'triple-copy.amplec'), TAGGED.replace('demo.koda.notes.triple-notes', 'com.other.triple').replace('Triple notes', 'Other triple'));
  await openApp(page, { reset: false });
  await page.locator('#ltabs button[data-l="clib"]').click();
  await page.locator('#clib-scan').click();
  const other = page.locator('#clib-list .clib-entry', { hasText: 'Other triple' });
  await expect(other.locator('.clib-taken')).toBeVisible();
  await other.hover();
  await other.getByRole('button', { name: 'Import Other triple' }).click();
  await expect(page.locator('#dlg')).toContainText('Import it as "tripleCopy"');
  await page.locator('#dlg-btns button', { hasText: 'Import as tripleCopy' }).click();
  await expect(page.locator('#dlg-extra')).toContainText('+ import "components/triple-copy.amplec" as tripleCopy');
  await page.locator('#dlg-btns button', { hasText: 'Write import' }).click();
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text, { timeout: 15000 }).toContain('as tripleCopy');
});

test('an entry dragged from the library onto the board becomes a block, a component the song does not import is imported first', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.locator('#ltabs button[data-l="clib"]').click();
  await expect(page.locator('#clib-list .clib-entry', { hasText: 'swingLine' })).toHaveCount(1);   // (an earlier test left another file in the project: not counted)
  const board = page.locator('#cv');
  const blocks = async () => ((await api('/api/doc?path=song.amplet')).blocks as { type: string }[]).map((b) => b.type);
  // the component the song imports: one drop, one block
  const before = (await blocks()).filter((t) => t === 'koda.notes.tripleNotes').length;
  await page.locator('#clib-list .clib-entry', { hasText: 'Triple notes' }).locator('.tplitem').dragTo(board, { targetPosition: { x: 420, y: 260 } });
  await expect.poll(async () => (await blocks()).filter((t) => t === 'koda.notes.tripleNotes').length, { timeout: 15000 }).toBe(before + 1);
  // one it does not import: the lines are shown first (the import only: the block follows on the board), then the block
  await page.locator('#clib-list .clib-entry', { hasText: 'swingLine' }).locator('.tplitem').dragTo(board, { targetPosition: { x: 520, y: 360 } });
  await expect(page.locator('#dlg-extra')).toContainText('+ import "components/swing-line.amplec"');
  await expect(page.locator('#dlg-extra')).not.toContainText('swingLine {');
  await page.locator('#dlg-btns button', { hasText: 'Write import' }).click();
  await expect.poll(async () => (await blocks()).includes('koda.notes.swingLine'), { timeout: 20000 }).toBe(true);
  expect((await api('/api/doc?path=song.amplet')).text).toContain('import "components/swing-line.amplec"');
});
