import { expect, Page, test } from '@playwright/test';
import { freshServer, launchUrl } from '../../web/tests/support';

// Sharing your own song on Lite (plan: shares): the form, the link and the code, the (i) buttons, the checks of the form, and the .zip.
// where complaints about a share go is a setting of the server: the test names its own address
const REPORT = ['complaints', 'remix.example.test'].join(String.fromCharCode(64));
freshServer({ env: { AMPLE_REMIX_REPORT_EMAIL: REPORT, AMPLE_LITE_BUILDS_PER_MINUTE: '500', AMPLE_LITE_BUILDS_PER_HOUR: '5000', AMPLE_LITE_DOWNLOADS_PER_MINUTE: '500', AMPLE_LITE_DOWNLOADS_PER_HOUR: '5000', AMPLE_LITE_SHARES_PER_MINUTE: '500', AMPLE_LITE_SHARES_PER_HOUR: '5000' } });

const liteUrl = () => launchUrl().replace('/#token=', '/remix/#token=');
const wipe = async (page: Page) => page.evaluate(() => new Promise<void>((resolve) => { const request = indexedDB.deleteDatabase('ample-remix'); request.onsuccess = request.onerror = request.onblocked = () => resolve(); }));
async function fresh(page: Page) {
  await page.goto(liteUrl());
  await page.waitForSelector('html[data-lite-ready="true"]');
  await wipe(page);
  await page.evaluate(() => localStorage.clear());
  await page.reload();
  await page.waitForSelector('html[data-lite-ready="true"]');
}
async function useDemo(page: Page, id: string) {
  await page.click('#lite-demos-btn');
  await page.locator(`#lite-demo-list li[data-demo="${id}"] button`).click();
  await expect(page.locator('#lite-demo-dialog')).toBeHidden();
  await expect(page.locator('#lite-filename')).not.toHaveText('');
}
const typeAtEnd = async (page: Page, text: string) => { await page.locator('#lite-editor .cm-content').click(); await page.keyboard.press('Control+End'); await page.keyboard.type(text); };

async function ownSong(page: Page) {
  await fresh(page);
  await page.click('#lite-new-project');
  await page.fill('#lite-dialog input', 'My little tune');
  await page.click('#lite-dialog-ok');
  await expect(page.locator('#lite-filename')).toHaveText('song.amplet');
}

test('your own song is shared: the form, then the link to share and the code to manage it', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await ownSong(page);
  await page.click('#lite-share');
  await expect(page.locator('#lite-publish-dialog')).toBeVisible();
  await expect(page.locator('#lite-publish-dialog input[name="lite-expires"]:checked')).toHaveValue('1w');   // a week is the default
  await expect(page.locator('#lite-publish-dialog input[name="lite-expires"]')).toHaveCount(5);
  await page.fill('#lite-publish-name', 'My little tune');
  await page.fill('#lite-publish-author', 'koda');
  await page.fill('#lite-publish-desc', 'a test of the share form');
  await page.check('#lite-publish-rights');
  await page.click('#lite-publish-go');
  await expect(page.locator('#lite-publish-done')).toBeVisible({ timeout: 30000 });
  const link = await page.locator('#lite-publish-link').inputValue();
  const manage = await page.locator('#lite-publish-manage').inputValue();
  expect(link).toMatch(/\/mix\/[0-9A-Z]{8}$/);
  expect(manage).toMatch(/\/mix\/[0-9A-Z]{8}#manage=[0-9A-Z]{26}$/);
  expect(manage.startsWith(link + '#manage=')).toBe(true);
  await expect(page.locator('#lite-publish-link-result')).toContainText('clipboard');
  await page.click('#lite-publish-copy-manage');
  await expect(page.locator('#lite-publish-copy-manage')).toContainText('Copied');
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(manage);
  // the share exists for everybody, and shows what was filled in (and nothing of the maker's address)
  const id = link.split('/mix/')[1];
  const info = await page.evaluate(async (shareId) => { const r = await fetch(`/api/remix/shares/${shareId}`); return { status: r.status, text: await r.text() }; }, id);
  expect(info.status).toBe(200);
  expect(JSON.parse(info.text)).toMatchObject({ title: 'My little tune', author: 'koda', description: 'a test of the share form' });
  expect(info.text).not.toMatch(/"ip"|userAgent|cookie|codeHash|127\.0\.0\.1/i);
  // the maker remembers it
  const mine = await page.evaluate(() => JSON.parse(localStorage.getItem('ample-remix-shares') ?? '[]'));
  expect(mine[0]).toMatchObject({ id, title: 'My little tune' });
});

test('the form asks for what is needed: the Share button looks off until the rights box is ticked, and a click shows what is missing', async ({ page }) => {
  await ownSong(page);
  await page.click('#lite-share');
  await expect(page.locator('#lite-publish-go')).toHaveAttribute('aria-disabled', 'true');
  await page.fill('#lite-publish-name', '');
  await page.click('#lite-publish-go', { force: true });   // looks switched off but a click shows what is missing (force: Playwright treats aria-disabled as disabled)
  await expect(page.locator('#lite-publish-error')).toContainText('title and an author');
  await expect(page.locator('#lite-publish-name')).toHaveAttribute('aria-invalid', 'true');
  await expect(page.locator('#lite-publish-author')).toHaveAttribute('aria-invalid', 'true');
  await expect(page.locator('#lite-publish-rights-row')).toHaveAttribute('data-need', '1');
  await expect(page.locator('.lite-need-hint')).toBeVisible();
  await page.fill('#lite-publish-name', 'x');
  await expect(page.locator('#lite-publish-name')).not.toHaveAttribute('aria-invalid', 'true');   // typing takes the mark away
  await page.fill('#lite-publish-author', 'y');
  await page.click('#lite-publish-go', { force: true });
  await expect(page.locator('#lite-publish-error')).toContainText('Tick the box');
  await expect(page.locator('#lite-publish-rights-row')).toHaveAttribute('data-need', '1');
  await page.check('#lite-publish-rights');
  await expect(page.locator('#lite-publish-go')).toHaveAttribute('aria-disabled', 'false');
  await expect(page.locator('#lite-publish-rights-row')).not.toHaveAttribute('data-need', '1');
  await expect(page.locator('#lite-publish-done')).toBeHidden();
});

test('the (i) button shows its text on hover, stays on a click and goes away with Escape or a click elsewhere', async ({ page }) => {
  await ownSong(page);
  await page.click('#lite-share');
  const wrap = page.locator('#lite-publish-dialog .lite-info-wrap').first();
  const pop = wrap.locator('.lite-pop');
  await expect(pop).toBeHidden();
  await wrap.locator('.lite-info').hover();
  await expect(pop).toBeVisible();
  await expect(pop).toContainText('best-effort');
  await expect(pop).toContainText(REPORT);
  await page.mouse.move(5, 5);
  await expect(pop).toBeHidden();   // a hover goes away when the pointer leaves
  await wrap.locator('.lite-info').click();
  await page.mouse.move(5, 5);
  await expect(pop).toBeVisible();   // a click (or a tap) keeps it
  await page.keyboard.press('Escape');
  await expect(pop).toBeHidden();
  await expect(page.locator('#lite-publish-dialog')).toBeVisible();   // Escape closed the text, not the form
  await wrap.locator('.lite-info').click();
  await expect(pop).toBeVisible();
  await page.locator('#lite-publish-title').click();
  await expect(pop).toBeHidden();
});

test('the whole project can be downloaded as a .zip from the form', async ({ page }) => {
  await ownSong(page);
  await page.click('#lite-share');
  await page.fill('#lite-publish-name', 'My little tune');
  const download = page.waitForEvent('download');
  await page.click('#lite-publish-zip');
  const file = await download;
  expect(file.suggestedFilename()).toBe('my-little-tune.zip');
});

async function shareWithApi(page: Page, title = 'Shared tune', author = 'koda') {
  await page.goto(liteUrl());
  await page.waitForSelector('html[data-lite-ready="true"]');
  return page.evaluate(async ({ title: t, author: a }) => {
    const song = 'composition "t" { ample 1  tempo 120  master-seed 1 }\n\nmidi-clip m at 1 for 1 {\n  notes 1:0:1:60:100 1:1:1:64:100\n}\n\nsynth s at 1 for 1 {\n  from m.notes -> notes\n}\n\nmaster out {\n  from s.audio -> audio\n}\n# ' + Math.random() + '\n';
    const bytes = new TextEncoder().encode(song);
    const hash = [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))].map((b) => b.toString(16).padStart(2, '0')).join('');
    const form = new FormData();
    form.append('manifest', JSON.stringify({ title: t, author: a, description: 'a <b>test</b> description', expires: '1w', rights: true, entry: 'song.amplet', files: [{ path: 'song.amplet', sha256: hash }] }));
    form.append('f0', new Blob([bytes]), 'file');
    const token = sessionStorage.getItem('ample.token') ?? '';
    const r = await fetch('/api/remix/shares', { method: 'POST', headers: { 'X-Ample-Token': token }, body: form });
    return r.json();
  }, { title, author });
}

test('a link to a shared song opens a window that says who made it, builds it and ends in Play', async ({ page, context }) => {
  await fresh(page);
  const made = await shareWithApi(page);
  expect(made.id).toMatch(/^[0-9A-Z]{8}$/);
  const viewer = await context.newPage();
  await viewer.goto(liteUrl());
  await viewer.waitForSelector('html[data-lite-ready="true"]');
  await viewer.goto(launchUrl().replace('/#token=', '/s/' + made.id + '#token='));
  await expect(viewer.locator('#lite-tour')).toBeVisible();
  await expect(viewer.locator('#lite-tour-play')).toBeVisible({ timeout: 60000 });
  await expect(viewer.locator('#lite-tour-title')).toHaveText('Shared tune');
  await expect(viewer.locator('#lite-about-by')).toHaveText('By koda');
  await expect(viewer.locator('#lite-about-desc')).toHaveText('a <b>test</b> description');   // plain text: nothing is read as HTML
  await expect(viewer.locator('.lite-about-banner')).toContainText('third party');
  const report = await viewer.locator('#lite-about-report').getAttribute('href');
  expect(report).toContain('mailto:' + REPORT);
  expect(decodeURIComponent(report!)).toContain(made.id);
  await expect(viewer.locator('#lite-about-id')).toHaveText('ID ' + made.id);
  await viewer.click('#lite-tour-play');
  await expect(viewer.locator('#lite-tour')).toBeHidden();
  await viewer.close();
});

test('a link to a share that does not exist says that it is no longer available', async ({ page }) => {
  await page.goto(liteUrl());
  await page.waitForSelector('html[data-lite-ready="true"]');
  await page.goto(launchUrl().replace('/#token=', '/s/ZZZZZZZZ#token='));
  await expect(page.locator('#lite-tour-line')).toContainText('no longer available', { timeout: 20000 });
  await expect(page.locator('#lite-tour-play')).toBeHidden();
});

test('the management link: the maker changes how long it stays, and deletes it after a question', async ({ page, context }) => {
  await fresh(page);
  const made = await shareWithApi(page, 'Manage me');
  const maker = await context.newPage();
  await maker.goto(liteUrl());
  await maker.waitForSelector('html[data-lite-ready="true"]');
  await maker.goto(new URL(launchUrl()).origin + '/s/' + made.id + '#manage=' + made.code);
  await expect(maker.locator('#lite-manage-dialog')).toBeVisible();
  await expect(maker.locator('#lite-manage-title')).toHaveText('Manage me');
  await expect(maker.locator('#lite-manage-body')).toBeVisible();
  await expect(maker.locator('#lite-manage-change')).toBeDisabled();   // choose a duration first
  await maker.check('input[name="lite-manage-expires"][value="1h"]');
  await maker.click('#lite-manage-change');
  await expect(maker.locator('#lite-manage-message')).toContainText('Done');
  const hours = await maker.evaluate(async ({ id, code }) => { const r = await fetch('/api/remix/shares/' + id + '/manage', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Ample-Token': sessionStorage.getItem('ample.token') ?? '' }, body: JSON.stringify({ code, action: 'info' }) }); const j = await r.json(); return (new Date(j.expires).getTime() - Date.now()) / 3600000; }, { id: made.id, code: made.code });
  expect(hours).toBeGreaterThan(0.9);
  expect(hours).toBeLessThan(1.1);
  await expect(maker.locator('#lite-manage-linkbox')).toBeHidden();   // only the link to share, when asked for
  await maker.click('#lite-manage-showlink');
  await expect(maker.locator('#lite-manage-link')).toHaveValue(new RegExp('/mix/' + made.id + '$'));
  const download = maker.waitForEvent('download');   // a copy to keep, made from the files of the share itself
  await maker.click('#lite-manage-zip');
  expect((await download).suggestedFilename()).toBe('manage-me.zip');
  await expect(maker.locator('#lite-manage-message')).toContainText('downloaded');
  await maker.click('#lite-manage-delete');   // delete asks first
  await expect(maker.locator('#lite-manage-confirm')).toBeVisible();
  await maker.click('#lite-manage-delete-no');
  await expect(maker.locator('#lite-manage-confirm')).toBeHidden();
  await maker.click('#lite-manage-delete');
  await maker.click('#lite-manage-delete-yes');
  await expect(maker.locator('#lite-manage-message')).toContainText('deleted');
  const after = await maker.evaluate(async (id) => (await fetch('/api/remix/shares/' + id)).status, made.id);
  expect(after).toBe(410);
  await maker.close();
});

test('a wrong management code is refused and shows no share', async ({ page }) => {
  await page.goto(liteUrl());
  await page.waitForSelector('html[data-lite-ready="true"]');
  const made = await shareWithApi(page, 'Not yours');
  await page.goto(new URL(launchUrl()).origin + '/s/' + made.id + '#manage=AAAAAAAAAAAAAAAAAAAAAAAAAA');
  await expect(page.locator('#lite-manage-message')).toContainText('does not belong');
  await expect(page.locator('#lite-manage-body')).toBeHidden();
});

test('a share gives all its files as ONE zip, and a share with many files opens', async ({ page, context }) => {
  await fresh(page);
  const made = await page.evaluate(async () => {
    const token = sessionStorage.getItem('ample.token') ?? '';
    const files: Array<{ path: string; bytes: Uint8Array; sha256: string }> = [];
    const hex = async (b: Uint8Array) => [...new Uint8Array(await crypto.subtle.digest('SHA-256', b))].map((x) => x.toString(16).padStart(2, '0')).join('');
    const song = new TextEncoder().encode('composition "many" { ample 1  tempo 120  master-seed 1 }\n# ' + Math.random() + '\n');
    files.push({ path: 'song.amplet', bytes: song, sha256: await hex(song) });
    for (let i = 0; i < 30; i++) { const b = new TextEncoder().encode('note ' + i + ' ' + Math.random()); files.push({ path: 'notes/n' + i + '.txt', bytes: b, sha256: await hex(b) }); }
    const form = new FormData();
    form.append('manifest', JSON.stringify({ title: 'Many files', author: 'koda', description: '', expires: '1w', rights: true, entry: 'song.amplet', files: files.map((f) => ({ path: f.path, sha256: f.sha256 })) }));
    files.forEach((f, i) => form.append('f' + i, new Blob([f.bytes]), 'file'));
    return (await fetch('/api/remix/shares', { method: 'POST', headers: { 'X-Ample-Token': token }, body: form })).json();
  });
  expect(made.id).toMatch(/^[0-9A-Z]{8}$/);
  const listed = await page.evaluate(async (id) => {
    const token = sessionStorage.getItem('ample.token') ?? '';
    const response = await fetch('/api/remix/shares/' + id + '/archive', { headers: { 'X-Ample-Token': token } });
    const { readZip } = await import('/remix/lite-unzip.mjs');
    const { entries } = await readZip(await response.blob());
    return { type: response.headers.get('content-type'), paths: entries.map((e: { path: string }) => e.path).sort() };
  }, made.id);
  expect(listed.type).toContain('zip');
  expect(listed.paths.length).toBe(31);
  expect(listed.paths).toContain('song.amplet');
  // the link opens it (with a request count that a proxy accepts: the page asks for the archive once)
  const viewer = await context.newPage();
  let archives = 0, singles = 0;
  viewer.on('request', (r) => { if (r.url().includes('/archive')) archives++; if (r.url().includes('/files/')) singles++; });
  await viewer.goto(liteUrl());
  await viewer.waitForSelector('html[data-lite-ready="true"]');
  await viewer.goto(new URL(launchUrl()).origin + '/s/' + made.id);
  await expect.poll(() => archives, { timeout: 20000 }).toBe(1);   // the song of this share has no blocks, so it never builds: the page only has to ask for the archive once
  await expect(viewer.locator('#lite-filename')).toHaveText('song.amplet', { timeout: 20000 });   // and the share opened
  expect(singles).toBe(0);
  await viewer.close();
});

test('the (i) text of a shared song sits above its button and inside the screen, also on a phone; and Skip building only opens the song', async ({ page, context }) => {
  await fresh(page);
  const made = await shareWithApi(page, 'Skip me');
  for (const viewport of [{ width: 1280, height: 800 }, { width: 390, height: 700 }]) {
    const viewer = await context.newPage();
    await viewer.setViewportSize(viewport);
    await viewer.goto(liteUrl());
    await viewer.waitForSelector('html[data-lite-ready="true"]');
    await viewer.goto(new URL(launchUrl()).origin + '/s/' + made.id);
    await expect(viewer.locator('#lite-tour-play')).toBeVisible({ timeout: 60000 });
    const info = viewer.locator('#lite-tour-about .lite-info');
    await info.click();
    const pop = viewer.locator('#lite-tour-about .lite-pop');
    await expect(pop).toBeVisible();
    const button = (await info.boundingBox())!;
    const box = (await pop.boundingBox())!;
    expect(box.x).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width).toBeLessThanOrEqual(viewport.width);   // inside the screen: no cut off text, no scroll bar
    expect(box.y + box.height).toBeLessThanOrEqual(button.y + 1);    // above the button
    expect(await viewer.evaluate(() => document.querySelector('#lite-tour')!.scrollWidth <= document.querySelector('#lite-tour')!.clientWidth + 1)).toBe(true);
    await viewer.close();
  }

  // Skip building: the song is opened and nothing is built
  const skipper = await context.newPage();
  await skipper.route('**/api/remix/build**', async (route) => { await new Promise((r) => setTimeout(r, 3000)); await route.continue(); });   // keep it busy long enough to press Skip
  await skipper.goto(liteUrl());
  await skipper.waitForSelector('html[data-lite-ready="true"]');
  await skipper.goto(new URL(launchUrl()).origin + '/s/' + made.id);
  await expect(skipper.locator('#lite-tour-skip')).toBeVisible();
  await skipper.click('#lite-tour-skip');
  await expect(skipper.locator('#lite-tour')).toBeHidden();
  await expect(skipper.locator('#lite-filename')).toHaveText('song.amplet');   // the song is open
  await skipper.close();
});
