import { expect, Page, test } from '@playwright/test';
import { freshServer, launchUrl } from '../../web/tests/support';

// Remix: saving and building through the normal controls. The store is slowed or broken with the test seam (?hooks=1) so that the order of events is fixed.
freshServer({ env: { AMPLE_LITE_BUILDS_PER_MINUTE: '500', AMPLE_LITE_BUILDS_PER_HOUR: '5000' } });

const liteUrl = () => launchUrl().replace('/#token=', '/remix/?hooks=1#token=');
// every test types its own marker: the server keeps a file of a build for an hour by its hash and does not take it again

async function openPixelQuest(page: Page) {
  await page.goto(liteUrl());
  await page.waitForSelector('html[data-lite-ready="true"]');
  await page.evaluate(() => new Promise<void>((resolve) => { const request = indexedDB.deleteDatabase('ample-remix'); request.onsuccess = request.onerror = request.onblocked = () => resolve(); }));
  await page.reload();
  await page.waitForSelector('html[data-lite-ready="true"]');
  await page.click('#lite-demos-btn');
  await page.locator('#lite-demo-list li[data-demo="pixel-quest"] button').click();
  await expect(page.locator('#lite-editor .cm-content')).toContainText('PIXEL QUEST');
}

/** The build requests of the page, with the time and whether the text of the song in them has the marker. */
function watchBuilds(page: Page, mark: string) {
  const builds: { at: number; marker: boolean }[] = [];
  void page.route('**/api/remix/build*', async (route) => {   // the route sees the whole body of the upload
    const request = route.request();
    if (request.method() === 'POST') builds.push({ at: Date.now(), marker: (request.postDataBuffer()?.toString('latin1') ?? '').includes(mark) });
    await route.continue();
  });
  return builds;
}

async function typeMarker(page: Page, mark: string) {
  await page.locator('#lite-editor .cm-content').click();
  await page.keyboard.press('Control+End');
  await page.keyboard.type(`\n${mark}`);
}

test('an autosave that is on its way: Build waits for it, and the build has the typed text; two quick clicks make one build @quarantine', async ({ page }) => {
  await openPixelQuest(page);
  const MARK = '# typed marker 4711';
  const builds = watchBuilds(page, MARK);
  await page.evaluate(() => {
    const w = window as any, store = w.__remixTest.app.store, real = store.writeFile.bind(store);
    w.__writes = 0;
    store.writeFile = (...args: unknown[]) => { w.__writes++; return new Promise((resolve) => { w.__release = () => resolve(real(...args)); }); };
    w.__restore = () => { store.writeFile = real; };
  });
  await typeMarker(page, MARK);
  await page.waitForFunction(() => (window as any).__writes >= 1);   // the autosave runs and is held in the store
  const heldAt = Date.now();
  await page.evaluate(() => { const b = document.querySelector('#lite-build') as HTMLButtonElement; b.click(); b.click(); });   // two quick clicks
  await page.waitForTimeout(1500);
  expect(builds.length, 'Build waits for the save that is on its way').toBe(0);
  await page.evaluate(() => (window as any).__release());
  await expect.poll(() => builds.length, { timeout: 60000 }).toBe(1);
  expect(builds[0].at).toBeGreaterThan(heldAt + 1000);
  expect(builds[0].marker, 'the build has the text that was typed').toBe(true);
  await page.waitForTimeout(1500);
  expect(builds.length, 'two quick clicks gave one build').toBe(1);
  await page.evaluate(() => (window as any).__restore());
});

test('a save that fails: the text stays, the build takes it, and a click on the label tries again', async ({ page }) => {
  await openPixelQuest(page);
  const MARK = '# typed marker 4712';
  const builds = watchBuilds(page, MARK);
  await page.evaluate(() => {
    const w = window as any, store = w.__remixTest.app.store, real = store.writeFile.bind(store);
    store.writeFile = async () => { throw new Error('the browser storage is full'); };
    w.__restore = () => { store.writeFile = real; };
  });
  await typeMarker(page, MARK);
  await expect(page.locator('#lite-saved')).toContainText('not saved', { timeout: 10000 });
  await page.click('#lite-build');
  await expect.poll(() => builds.length, { timeout: 60000 }).toBe(1);
  expect(builds[0].marker, 'the build took the text in the editor, not the old saved one').toBe(true);
  await expect(page.locator('#lite-output-info')).toContainText('seconds', { timeout: 90000 });
  await expect(page.locator('#lite-saved'), 'still not saved: the build did not hide it').toContainText('not saved');
  await page.evaluate(() => (window as any).__restore());
  await page.click('#lite-saved');   // try again
  await expect(page.locator('#lite-saved')).toHaveText('saved on this browser', { timeout: 10000 });
  const stored = await page.evaluate(async () => { const app = (window as any).__remixTest.app; return (await app.store.readFile(app.project.id, 'song.amplet')).text; });
  expect(stored).toContain(MARK);
});

test('renaming a song to an audio name is refused and the file stays as it was', async ({ page }) => {
  await openPixelQuest(page);
  await page.click('#lite-rename');
  await page.fill('#lite-dialog-field', 'song.wav');
  await page.click('#lite-dialog-ok');
  await expect(page.locator('.lite-toast, #lite-toasts')).toContainText('holds text');
  await expect(page.locator('#lite-filename')).toHaveText('song.amplet');
  await expect(page.locator('#lite-editor .cm-content')).toContainText('PIXEL QUEST');
  const files = await page.evaluate(async () => { const t = (window as any).__remixTest.app; return (await t.store.listFiles(t.project.id)).map((f: any) => `${f.path}:${f.kind}`); });
  expect(files).toContain('song.amplet:text');
  expect(files.some((f: string) => f.startsWith('song.wav'))).toBe(false);
});
