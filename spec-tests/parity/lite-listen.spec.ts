import { expect, Page, Route, test } from '@playwright/test';
import { freshServer, launchUrl } from '../../web/tests/support';

// Live Preview in Remix: listen to the song while editing it. The song loops, a change builds again after a pause (and never sooner than 12 seconds after the
// last build), the code that plays lights up, and Stop stops everything, also a sound that is still being fetched. Pixel Quest needs no sound bank.
freshServer({ env: { AMPLE_LITE_BUILDS_PER_MINUTE: '500', AMPLE_LITE_BUILDS_PER_HOUR: '5000' } });

const liteUrl = () => launchUrl().replace('/#token=', '/remix/#token=');
const bar = (page: Page) => page.locator('#lite-listen');
const status = (page: Page) => page.locator('#lite-listen-status');
const OUTPUT = /\/api\/remix\/output\//;

async function fresh(page: Page) {
  await page.goto(liteUrl());
  await page.waitForSelector('html[data-lite-ready="true"]');
  await page.evaluate(() => new Promise<void>((resolve) => { const request = indexedDB.deleteDatabase('ample-remix'); request.onsuccess = request.onerror = request.onblocked = () => resolve(); }));
  await page.reload();
  await page.waitForSelector('html[data-lite-ready="true"]');
}

async function openPixelQuest(page: Page) {
  await fresh(page);
  await page.click('#lite-demos-btn');
  await page.locator('#lite-demo-list li[data-demo="pixel-quest"] button').click();
  await expect(page.locator('#lite-demo-dialog')).toBeHidden();
  await expect(page.locator('#lite-editor .cm-content')).toContainText('PIXEL QUEST');
}

/** The start time (ms) of every build request of the page. */
function countBuilds(page: Page) {
  const builds: number[] = [];
  page.on('request', (request) => { if (request.url().includes('/api/remix/build') && request.method() === 'POST') builds.push(Date.now()); });
  return builds;
}

test('Listen builds the song, loops it without the page player, lights up what plays, and Stop ends it all', async ({ page }) => {
  await openPixelQuest(page);
  const builds = countBuilds(page);
  await expect(bar(page)).toBeVisible();
  await page.click('#lite-listen-play');
  await expect(status(page)).toContainText('hearing build #1', { timeout: 90000 });
  await expect(bar(page)).toHaveAttribute('data-playing', 'true');
  expect(await page.evaluate(() => (document.querySelector('#lite-output') as HTMLAudioElement).paused), 'the page player stays quiet: one sound at a time').toBe(true);
  await expect(page.locator('.cm-content .lp-now').first()).toBeAttached({ timeout: 15000 });   // a literal note or a step of the clip that plays
  expect(builds.length).toBe(1);
  await page.click('#lite-listen-play');
  await expect(status(page)).toHaveText('Stopped');
  await expect(bar(page)).toHaveAttribute('data-playing', 'false');
  await expect(page.locator('.cm-content .lp-now')).toHaveCount(0);
});

test('a range of bars is cut from the render of the whole song: another range needs no new build', async ({ page }) => {
  await openPixelQuest(page);
  const builds = countBuilds(page);
  await page.selectOption('#lite-listen-target', 'bars');
  await page.fill('#lite-listen-first', '2'); await page.locator('#lite-listen-first').dispatchEvent('change');
  await page.fill('#lite-listen-last', '3'); await page.locator('#lite-listen-last').dispatchEvent('change');
  await page.click('#lite-listen-play');
  await expect(status(page)).toContainText('hearing build #1 (bars 2-3)', { timeout: 90000 });
  const heard = () => page.evaluate(() => (document.querySelector('#lite-listen') as HTMLElement).dataset.playing);
  expect(await heard()).toBe('true');
  await page.fill('#lite-listen-last', '4'); await page.locator('#lite-listen-last').dispatchEvent('change');
  await expect(status(page)).toContainText('(bars 2-4)');
  await page.waitForTimeout(500);
  expect(builds.length, 'the same render, cut again').toBe(1);
  expect(await heard()).toBe('true');
  await page.fill('#lite-listen-first', '200'); await page.locator('#lite-listen-first').dispatchEvent('change');
  await expect(page.locator('#lite-toasts')).toContainText('has no bars', { timeout: 5000 });   // beyond the end of the song: said, and the sound that plays is kept
  await page.click('#lite-listen-play');
  await expect(status(page)).toHaveText('Stopped');
});

test('a change builds again after a pause, never sooner than 12 seconds after the last build; a broken song keeps the last good build playing', async ({ page }) => {
  test.setTimeout(240000);
  await openPixelQuest(page);
  const builds = countBuilds(page);
  await page.click('#lite-listen-play');
  await expect(status(page)).toContainText('hearing build #1', { timeout: 90000 });
  // break the song: the last good build keeps playing, the problem is said
  await page.locator('#lite-editor .cm-content').click();
  await page.keyboard.press('Control+End');
  const broken = '\nthis is not a line of a song\n';
  await page.keyboard.type(broken);
  await expect(status(page)).toContainText('you still hear the last good build', { timeout: 90000 });
  expect(builds.length).toBe(2);
  expect(builds[1] - builds[0], 'the second build started at least 12 seconds after the first').toBeGreaterThan(11500);
  await expect(bar(page)).toHaveAttribute('data-playing', 'true');
  // mend it: the next build is heard
  await page.keyboard.press('Control+End');
  for (let i = 0; i < broken.length; i++) await page.keyboard.press('Backspace');
  await expect(status(page)).toContainText('hearing build #3', { timeout: 90000 });
  await page.click('#lite-listen-play');
});

test('Stop while the sound is still being fetched: when it arrives nothing plays and nothing changes', async ({ page }) => {
  await openPixelQuest(page);
  const held: Route[] = [];
  await page.route(OUTPUT, (route) => { held.push(route); });
  await page.click('#lite-listen-play');
  await expect.poll(() => held.length, { timeout: 90000 }).toBeGreaterThan(0);   // the build is done and the sound is being fetched
  await page.click('#lite-listen-play');   // Stop
  await expect(status(page)).toHaveText('Stopped');
  for (const route of held) await route.continue();
  await page.waitForTimeout(1500);   // the old task has had every chance to start the sound
  await expect(bar(page)).toHaveAttribute('data-playing', 'false');
  await expect(status(page)).toHaveText('Stopped');
});

test('the Listen bar is one line high while listening and typing, and Play of the page stops the listening', async ({ page }) => {
  await openPixelQuest(page);
  const high = () => bar(page).evaluate((el) => Math.round(el.getBoundingClientRect().height));
  const before = await high();
  await page.click('#lite-listen-play');
  await expect(status(page)).toContainText('hearing build #1', { timeout: 90000 });
  expect(await high(), 'the long status does not make the bar taller').toBe(before);
  await page.click('#lite-build');   // the page's own build and play
  await expect(bar(page)).toHaveAttribute('data-playing', 'false', { timeout: 120000 });
  await expect(page.locator('#lite-listen-play')).toHaveText(/Listen/);
});

test('a component that is open while listening: the song is still the one that is heard, and its marks stay out of the component', async ({ page }) => {
  await openPixelQuest(page);
  await page.click('#lite-listen-play');
  await expect(status(page)).toContainText('hearing build #1', { timeout: 90000 });
  await expect(page.locator('.cm-content .lp-now').first()).toBeAttached({ timeout: 15000 });
  await page.click('#lite-new-file');
  await page.fill('#lite-dialog-field', 'components/echo.amplec');
  await page.click('#lite-dialog-ok');
  await expect(page.locator('#lite-filename')).toHaveText('components/echo.amplec');
  await expect(bar(page), 'still listening to the song').toHaveAttribute('data-playing', 'true');
  await expect(status(page)).toContainText(/build/);   // hearing build 1, or building 2 (a new file changes the project)
  await expect(page.locator('.cm-content .lp-now'), 'no marks of the song in a component').toHaveCount(0);
  await page.locator('#lite-tree .lite-row[data-path="song.amplet"]').click();
  await expect(page.locator('.cm-content .lp-now').first()).toBeAttached({ timeout: 15000 });   // back in the song: its marks come back
  await page.click('#lite-listen-play');
});

test('another song, or another project, ends the listening', async ({ page }) => {
  await openPixelQuest(page);
  await page.click('#lite-listen-play');
  await expect(status(page)).toContainText('hearing build #1', { timeout: 90000 });
  await page.click('#lite-new-file');
  await page.fill('#lite-dialog-field', 'second.amplet');
  await page.click('#lite-dialog-ok');
  await expect(page.locator('#lite-filename')).toHaveText('second.amplet');
  await expect(status(page)).toHaveText('Stopped');
  await expect(bar(page)).toHaveAttribute('data-playing', 'false');
  await expect(page.locator('#lite-toasts')).toContainText('another song was opened');
  await expect(page.locator('.cm-content .lp-now')).toHaveCount(0);
  await page.click('#lite-listen-play');   // listen to the new song
  await expect(status(page)).toContainText('hearing build', { timeout: 90000 });
  await page.click('#lite-new-project');
  await page.fill('#lite-dialog-field', 'Another project');
  await page.click('#lite-dialog-ok');
  await expect(status(page)).toHaveText('Stopped');
  await expect(bar(page)).toHaveAttribute('data-playing', 'false');
});
