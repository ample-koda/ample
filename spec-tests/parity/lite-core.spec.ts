import { spawnSync } from 'node:child_process';
import { expect, Page, test } from '@playwright/test';
import { freshServer, launchUrl, openApp } from '../../web/tests/support';

// AMPLE Remix (plan/lite/PLAN.md): the page, its local storage, the demos, build and play, the reset. Each spec file has a server of its own; the tests run one after the other
// on the same browser profile, so each one starts from "Reset Lite" state by clearing the Lite database first.
freshServer({ env: { AMPLE_LITE_BUILDS_PER_MINUTE: '500', AMPLE_LITE_BUILDS_PER_HOUR: '5000', AMPLE_LITE_DOWNLOADS_PER_MINUTE: '500', AMPLE_LITE_DOWNLOADS_PER_HOUR: '5000' } });

const liteUrl = () => launchUrl().replace('/#token=', '/remix/#token=');
const wipe = async (page: Page) => page.evaluate(() => new Promise<void>((resolve) => { const request = indexedDB.deleteDatabase('ample-remix'); request.onsuccess = request.onerror = request.onblocked = () => resolve(); }));

async function openLite(page: Page) {
  await page.goto(liteUrl());
  await page.waitForSelector('html[data-lite-ready="true"]');
}
async function fresh(page: Page) {
  await page.goto(liteUrl());
  await page.waitForSelector('html[data-lite-ready="true"]');
  await wipe(page);
  await page.reload();
  await page.waitForSelector('html[data-lite-ready="true"]');
}
async function useDemo(page: Page, id: string) {
  await page.click('#lite-demos-btn');
  await expect(page.locator('#lite-demo-dialog')).toBeVisible();
  await page.locator(`#lite-demo-list li[data-demo="${id}"] button`).click();
  await expect(page.locator('#lite-demo-dialog')).toBeHidden();
  await expect(page.locator('#lite-filename')).not.toHaveText('');
}
const rollLayout = async (page: Page) => page.locator('#lite-roll-canvas').evaluate((c) => ({ gutter: Number((c as HTMLCanvasElement).dataset.gutter), row: Number((c as HTMLCanvasElement).dataset.row), pxBeat: Number((c as HTMLCanvasElement).dataset.pxbeat), high: Number((c as HTMLCanvasElement).dataset.high), head: Number((c as HTMLCanvasElement).dataset.head) }));
const editorText = (page: Page) => page.locator('#lite-editor .cm-content').innerText();
const typeAtEnd = async (page: Page, text: string) => { await page.locator('#lite-editor .cm-content').click(); await page.keyboard.press('Control+End'); await page.keyboard.type(text); };

test('/remix opens behind the token, with the AMPLE demo song already there and the other demos one click away', async ({ page }) => {
  await fresh(page);
  await expect(page.locator('#lite-empty')).toBeHidden();   // nothing was kept yet: the demo song is open (not built, not played)
  await expect(page.locator('#lite-projects option')).toHaveText(['Welcome to AMPLE']);
  await expect(page.locator('#lite-filename')).toHaveText('song.amplet');
  await expect(page.locator('#lite-build')).toBeVisible();
  await page.click('#lite-demos-btn');
  await expect(page.locator('#lite-demo-list li').first()).toBeVisible();
  expect(await page.locator('#lite-demo-list li').count()).toBeGreaterThanOrEqual(2);
  // without the token the API refuses
  const refused = await page.evaluate(async () => (await fetch('/api/remix/limits', { credentials: 'omit' })).status);
  expect(refused).toBe(401);
});

test('a demo is copied into an editable project and the demo itself never changes', async ({ page }) => {
  await fresh(page);
  const original = await (await fetch(new URL('/remix/demos/hello-melody/song.amplet', launchUrl()).toString())).text();
  await useDemo(page, 'hello-melody');
  await expect(page.locator('#lite-tree .lite-row.file')).toHaveCount(1);
  await expect(page.locator('#lite-editor .cm-content')).toContainText('midi-clip tune');
  await typeAtEnd(page, '\n# my own change');
  await expect(page.locator('#lite-saved')).toHaveText('saved on this browser');
  // the demo as the server serves it is the same, and using it again gives the original text
  expect(await (await fetch(new URL('/remix/demos/hello-melody/song.amplet', launchUrl()).toString())).text()).toBe(original);
  await useDemo(page, 'hello-melody');
  expect(await editorText(page)).not.toContain('my own change');
  await expect(page.locator('#lite-projects option')).toHaveCount(3);   // the AMPLE demo song of an empty workspace, and the two copies
});

test('a change survives a reload, and the project and the open file come back', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'chords-bass');
  // at the start: the editor draws only the lines in view, so a line at the end of a long text is not in the page
  await page.locator('#lite-editor .cm-content').click();
  await page.keyboard.press('Control+Home');
  await page.keyboard.type('# kept after reload\n');
  await expect(page.locator('#lite-saved')).toHaveText('saved on this browser');
  await page.reload();
  await page.waitForSelector('html[data-lite-ready="true"]');
  await expect(page.locator('#lite-editor .cm-content')).toContainText('# kept after reload');
  await expect(page.locator('#lite-filename')).toHaveText('song.amplet');
});

test('new files, a component file and deleting a file', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'hello-melody');
  await page.click('#lite-new-file');
  await page.fill('#lite-dialog-field', 'components/echo.amplec');
  await page.click('#lite-dialog-ok');
  await expect(page.locator('#lite-filename')).toHaveText('components/echo.amplec');
  await expect(page.locator('#lite-editor .cm-content')).toContainText('component koda.lite.echo');
  await expect(page.locator('#lite-tree .lite-row[data-path="components"]')).toBeVisible();
  await page.click('#lite-new-file');
  await page.fill('#lite-dialog-field', '../escape.amplet');
  await page.click('#lite-dialog-ok');
  await expect(page.locator('#lite-toasts .error')).toContainText('names are letters');
  await page.click('#lite-delete');
  await page.click('#lite-dialog-ok');
  await expect(page.locator('#lite-tree .lite-row[data-path="components/echo.amplec"]')).toHaveCount(0);
  await expect(page.locator('#lite-tree .lite-row.file')).toHaveCount(1);
});

test('the language tooling: an error is marked and listed, and completion offers block types', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'hello-melody');
  await expect(page.locator('#lite-problem-count')).toHaveText('none');
  await typeAtEnd(page, '\nsynthh broken {\n}\n');
  await expect(page.locator('#lite-problems li.error').first()).toBeVisible();
  await expect(page.locator('#lite-editor .lite-diag-error').first()).toBeVisible();
  await page.keyboard.press('Control+z');
  await typeAtEnd(page, '\nsyn');
  await page.keyboard.press('Control+Space');
  await expect(page.locator('.lite-complete .lite-complete-row', { hasText: 'synth' }).first()).toBeVisible();
});

test('a sample can be uploaded, stays after a reload, and a wrong file is refused', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'hello-melody');
  const wav = Buffer.concat([Buffer.from('RIFF'), Buffer.alloc(4), Buffer.from('WAVE'), Buffer.alloc(32)]);
  await page.setInputFiles('#lite-upload', [{ name: 'my kick.wav', mimeType: 'audio/wav', buffer: wav }, { name: 'virus.exe', mimeType: 'application/octet-stream', buffer: Buffer.from('MZ') }]);
  await expect(page.locator('#lite-tree .lite-row[data-path="samples/my kick.wav"]')).toBeVisible();
  await expect(page.locator('#lite-toasts .error')).toContainText('virus.exe');
  await page.reload();
  await page.waitForSelector('html[data-lite-ready="true"]');
  await expect(page.locator('#lite-tree .lite-row[data-path="samples/my kick.wav"]')).toBeVisible();
  await page.locator('#lite-tree .lite-row[data-path="samples/my kick.wav"]').click();
  await expect(page.locator('#lite-binary audio')).toBeVisible();
});

test('a song builds, the sound is there, and several demos build one after the other', async ({ page }) => {
  await fresh(page);
  for (const id of ['hello-melody', 'chords-bass']) {
    await useDemo(page, id);
    await page.click('#lite-build');
    await expect(page.locator('#lite-output-info')).toContainText('seconds', { timeout: 60000 });
    const duration = await page.evaluate(async () => { const a = document.querySelector('#lite-output') as HTMLAudioElement; for (let i = 0; i < 50 && !(a.duration > 0); i++) await new Promise((r) => setTimeout(r, 100)); return a.duration; });
    expect(duration).toBeGreaterThan(1);
    await page.click('#lite-export-btn');   // the sound is in the Export as menu
    await expect(page.locator('#lite-download')).toBeVisible();
    await page.keyboard.press('Escape');
    await page.evaluate(() => (document.querySelector('#lite-output') as HTMLAudioElement).pause());
  }
});

test('a song with an error does not build and says why, with no paths of the server', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'hello-melody');
  await typeAtEnd(page, '\nsynthh broken {\n}\n');
  await expect(page.locator('#lite-saved')).toHaveText('saved on this browser');
  await page.click('#lite-build');
  await expect(page.locator('#lite-status')).toHaveText('The build did not work.');
  const problems = await page.locator('#lite-problems').innerText();
  expect(problems).toMatch(/AMP/);
  expect(problems).not.toMatch(/[A-Za-z]:\\|\/home\/|\/tmp\//);
});

test('Reset Lite deletes the Lite data only: the Studio keys and the sign-in stay', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'hello-melody');
  await page.evaluate(() => { localStorage.setItem('ample.studio.setting', 'keep'); sessionStorage.setItem('ample-remix.tab', 'x'); });
  const tokenBefore = await page.evaluate(() => Object.entries(sessionStorage).filter(([k]) => !k.startsWith('ample-remix.')));
  await page.click('#lite-reset');
  await expect(page.locator('#lite-dialog')).toContainText('Studio, its projects, your sign-in');
  await page.click('#lite-dialog-ok');
  await page.waitForSelector('html[data-lite-ready="true"]');
  await expect(page.locator('#lite-empty')).toBeHidden();   // after a reset the AMPLE demo song is loaded again by itself
  await expect(page.locator('#lite-projects option')).toHaveText(['Welcome to AMPLE']);
  expect(await page.evaluate(() => localStorage.getItem('ample.studio.setting'))).toBe('keep');
  expect(await page.evaluate(() => sessionStorage.getItem('ample-remix.tab'))).toBeNull();
  expect(await page.evaluate(() => Object.entries(sessionStorage).filter(([k]) => !k.startsWith('ample-remix.')))).toEqual(tokenBefore);
});

test('component demos with imports build (.amplec files next to the song)', async ({ page }) => {
  await fresh(page);
  for (const id of ['comp-notes', 'comp-audio']) {
    await useDemo(page, id);
    await page.click('#lite-build');
    await expect(page.locator('#lite-output-info')).toContainText('seconds', { timeout: 90000 });
    await page.evaluate(() => (document.querySelector('#lite-output') as HTMLAudioElement).pause());
  }
});

test('hostile input through the page: a path out of the project builds nothing and shows no server path', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'hello-melody');
  await typeAtEnd(page, ['', 'gm evil {', '  file ../../secret.sf2', '}', ''].join(String.fromCharCode(10)));
  await expect(page.locator('#lite-saved')).toHaveText('saved on this browser');
  await page.click('#lite-build');
  // the server refuses the path before anything is built: an error toast, no audio, and no server path in the words
  const refused = page.locator('#lite-toasts .error').first();
  await expect(refused).toBeVisible({ timeout: 60000 });
  expect(await refused.innerText()).not.toMatch(/[A-Za-z]:[\\]|\/home\/|\/tmp\//);
  // a file name that climbs out of the project is refused on upload as well
  await page.setInputFiles('#lite-upload', [{ name: '../evil.wav', mimeType: 'audio/wav', buffer: Buffer.from('RIFF') }]);
  await expect(page.locator('#lite-toasts .error').last()).toBeVisible();
});

test('a build shows its log step by step with a progress bar, and it can be cancelled', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'house');
  await page.click('#lite-build');
  await expect(page.locator('#lite-cancel')).toBeVisible();
  await expect(page.locator('#lite-progress')).toBeVisible();
  await expect(page.locator('#lite-log')).toContainText('Checked', { timeout: 20000 });
  await expect(page.locator('#lite-log')).toContainText('Rendering the blocks', { timeout: 30000 });
  await page.click('#lite-cancel');
  await expect(page.locator('#lite-status')).toHaveText('Build cancelled.');
  await expect(page.locator('#lite-log')).toContainText('Build cancelled');
  await expect(page.locator('#lite-cancel')).toBeHidden();
  await expect(page.locator('#lite-build')).toBeEnabled();
  // after a cancel the next build works and ends with its summary line
  await useDemo(page, 'hello-melody');
  await page.click('#lite-build');
  await expect(page.locator('#lite-log')).toContainText('Done in', { timeout: 60000 });
});

test('the note about unsealed components is not listed, and the list says why', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'ambient');
  await page.click('#lite-build');
  await expect(page.locator('#lite-log')).toContainText('Done in', { timeout: 90000 });
  await expect(page.locator('#lite-problems .lite-hidden-note')).toContainText('not listed');
  await expect(page.locator('#lite-problems .lite-hidden-note')).toContainText('sealing is for components that are shared');
  await expect(page.locator('#lite-problems')).not.toContainText('is not sealed');
  await expect(page.locator('#lite-problem-count')).toHaveText('none');
});

test('completion right after typing, hover and ctrl+click to a definition work in a song', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'hello-melody');
  await expect(page.locator('#lite-problem-count')).toHaveText('none');
  // completion straight after typing, with no pause: the server must have the text that was just typed
  await page.locator('#lite-editor .cm-content').click();
  await page.keyboard.press('Control+End');
  await page.keyboard.type(String.fromCharCode(10) + 'syn');
  await page.keyboard.press('Control+Space');
  await expect(page.locator('.lite-complete .lite-complete-row').first()).toBeVisible({ timeout: 10000 });
  await page.keyboard.press('Escape');
  await page.keyboard.press('Control+z');
  // hover and ctrl+click on the name of a block in a from line
  const line = page.locator('#lite-editor .cm-line', { hasText: 'from lead.audio' }).first();
  // the middle of the word "lead" as drawn on this machine (the width of a letter depends on the font, so a fixed distance from the start of the line hits another word on another machine)
  const wordCentre = () => line.evaluate((element) => {
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const at = (node.textContent ?? '').indexOf('lead');
      if (at < 0) continue;
      const range = document.createRange(); range.setStart(node, at); range.setEnd(node, at + 4);
      const r = range.getBoundingClientRect();
      return { x: r.x + r.width / 2, y: r.y + r.height / 2 };
    }
    return null;
  });
  let spot = (await wordCentre())!;
  await expect(async () => {   // the hover needs the answer of the language server and a pointer that moves over the name: move away and over it again until the tip shows
    spot = (await wordCentre())!;
    await page.mouse.move(spot.x - 40, spot.y);
    await page.mouse.move(spot.x, spot.y, { steps: 4 });
    await expect(page.locator('.lite-tip')).toBeVisible({ timeout: 2500 });
  }).toPass({ timeout: 20000 });
  await page.keyboard.down('Control');
  await page.mouse.click(spot.x, spot.y);
  await page.keyboard.up('Control');
  await expect.poll(() => page.evaluate(() => getSelection()?.anchorNode?.textContent ?? ''), { timeout: 8000 }).toBe('lead');
});

test('ctrl+click on an import line opens the component file it names', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'house');
  const line = page.locator('#lite-editor .cm-line', { hasText: 'import "components/' }).first();
  await expect(line).toBeVisible();
  await line.click({ modifiers: ['Control'], position: { x: 100, y: 8 } });   // Playwright waits until the line has stopped being redrawn
  await expect(page.locator('#lite-filename')).toHaveText(/^components\/.+\.amplec$/, { timeout: 8000 });
});

test('ctrl+click on the type of a component instance opens the file of that component', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'house');
  await expect(page.locator('#lite-problem-count')).toHaveText(/none|[0-9]/, { timeout: 20000 });
  await page.waitForTimeout(2500);
  await page.locator('#lite-editor .cm-content').click();
  await page.keyboard.press('Control+End');
  const line = page.locator('#lite-editor .cm-line', { hasText: /^koda[.]house[.]/ }).first();
  await expect(line).toBeVisible();
  const box = (await line.boundingBox())!;
  await page.keyboard.down('Control');
  await page.mouse.click(box.x + 40, box.y + box.height / 2);
  await page.keyboard.up('Control');
  await expect(page.locator('#lite-filename')).toHaveText(/^components[/].+[.]amplec$/, { timeout: 8000 });
});

test('the notes of a clip are edited on a roll: add, undo, delete, and the song text follows', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'delay-llama');
  await page.click('#lite-notes');
  await expect(page.locator('#lite-roll')).toBeVisible();
  await expect(page.locator('#lite-roll-info')).toHaveText(/^13 notes/);
  const canvas = page.locator('#lite-roll-canvas');
  const box = (await canvas.boundingBox())!;
  const L = await rollLayout(page);
  const cell = (beat: number, pitch: number) => [box.x + L.gutter + beat * L.pxBeat + 6, box.y + L.head + (L.high - pitch) * L.row + L.row / 2] as const;
  // an empty cell: bar 1, beat 3, pitch 70
  await page.mouse.click(...cell(3, 70));
  await expect(page.locator('#lite-roll-info')).toHaveText(/^14 notes/);
  await page.click('#lite-roll-undo');
  await expect(page.locator('#lite-roll-info')).toHaveText(/^13 notes/);
  await page.click('#lite-roll-redo');
  await expect(page.locator('#lite-roll-info')).toHaveText(/^14 notes/);
  // select the added note (a click on it) and take it away with Delete
  await page.mouse.click(...cell(3, 70));
  await page.keyboard.press('Delete');
  await expect(page.locator('#lite-roll-info')).toHaveText(/^13 notes/);
  await page.mouse.click(...cell(3, 70));
  await page.click('#lite-roll-close');
  await expect(page.locator('#lite-roll')).toBeHidden();
  // the notes line of the song has the new note (bar 1, beat 3, pitch 70) and the rest is as it was
  await expect(page.locator('#lite-editor .cm-content')).toContainText('1:0:2:57:92 1:2:2:60:96');
  await expect(page.locator('#lite-editor .cm-content')).toContainText('1:3:0.25:70:100');
  await expect(page.locator('#lite-saved')).toHaveText('saved on this browser');
});

test('a song without a notes line says so when the note editor is opened', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'piano');
  await page.click('#lite-notes');
  await expect(page.locator('#lite-toasts .error')).toBeVisible();
  await expect(page.locator('#lite-roll')).toBeHidden();
});

test('ctrl+click on the notes line of a clip opens the note editor on that clip, and a drag moves a note', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'delay-llama');
  const line = page.locator('#lite-editor .cm-line', { hasText: 'notes 1:0:2:57:92' }).first();
  await expect(line).toBeVisible();
  await line.click({ modifiers: ['Control'], position: { x: 80, y: 8 } });
  await expect(page.locator('#lite-roll')).toBeVisible();
  await expect(page.locator('#lite-roll-clip')).toHaveValue('chant');
  // drag the first note (bar 1, beat 0, pitch 57) two beats later: its entry becomes 1:2:...
  const canvas = page.locator('#lite-roll-canvas');
  const box = (await canvas.boundingBox())!;
  const L = await rollLayout(page);
  const x = box.x + L.gutter + 20, y = box.y + L.head + (L.high - 57) * L.row + L.row / 2;
  await page.mouse.move(x, y);
  await page.mouse.down();
  await page.mouse.move(x + L.pxBeat, y, { steps: 4 });
  await page.mouse.up();
  await page.click('#lite-roll-close');
  await expect(page.locator('#lite-editor .cm-content')).toContainText('1:1:2:57:92');
});

test('the note editor selects several notes, duplicates them and sets a velocity', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'delay-llama');
  await page.click('#lite-notes');
  await expect(page.locator('#lite-roll-info')).toHaveText(/^13 notes/);
  await page.locator('#lite-roll-canvas').click({ position: { x: 120, y: 4 } });   // the bar ruler: focus only (a click on the grid adds a note)
  await page.keyboard.press('Control+a');
  await expect(page.locator('#lite-roll-info')).toHaveText(/13 selected/);
  await page.keyboard.press('Control+d');
  await expect(page.locator('#lite-roll-info')).toHaveText(/^26 notes, 13 selected/);
  await page.keyboard.press('Control+z');
  await expect(page.locator('#lite-roll-info')).toHaveText(/^13 notes/);
  await page.click('#lite-roll-all');
  await page.click('#lite-roll-oct-up');
  await page.click('#lite-roll-close');
  await expect(page.locator('#lite-editor .cm-content')).toContainText('1:0:2:69:92');
});

test('the build can be downloaded as FLAC and MP3 (encoded on the server), when an encoder is installed', async ({ page }) => {
  const encoder = spawnSync(process.env.AMPLE_FFMPEG || 'ffmpeg', ['-version']);
  test.skip(encoder.error !== undefined || encoder.status !== 0, 'no ffmpeg on this machine');
  await fresh(page);
  await useDemo(page, 'hello-melody');
  await expect(page.locator('#lite-download-flac')).toBeHidden();
  await page.click('#lite-build');
  await expect(page.locator('#lite-log')).toContainText('Done in', { timeout: 60000 });
  await page.click('#lite-export-btn');
  await expect(page.locator('#lite-download-flac')).toBeVisible();
  for (const format of ['flac', 'mp3']) {
    const [download] = await Promise.all([page.waitForEvent('download', { timeout: 60000 }), page.click(`#lite-download-${format}`)]);
    if (!(await page.locator('#lite-export-menu').isVisible())) await page.click('#lite-export-btn');
    expect(download.suggestedFilename()).toBe(`song.${format}`);
  }
});

test('Studio keeps working next to Lite', async ({ page }) => {
  await openApp(page);
  await expect(page.locator('.node').first()).toBeVisible();
});

test('a shared demo link opens a fresh copy, builds it and plays; Share copies the link only while the copy is unchanged', async ({ page, context }) => {
  await fresh(page);
  await expect(page.locator('#lite-share')).toBeVisible();   // the AMPLE demo song of an empty workspace is open, an unchanged demo copy that Share can link
  await useDemo(page, 'hello-melody');
  await expect(page.locator('#lite-share')).toBeVisible();
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await page.click('#lite-share');
  await expect(page.locator('#lite-share-dialog')).toBeVisible();
  const link = await page.locator('#lite-share-link').inputValue();
  expect(link).toMatch(/\/remix\/#demo=hello-melody$/);
  await expect(page.locator('#lite-share-result')).toContainText('on your clipboard');   // the copy on opening
  await page.click('#lite-share-copy');
  await expect(page.locator('#lite-share-copy')).toContainText('Copied');
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(link);
  await page.click('#lite-share-close');
  // a changed copy cannot be shared
  await typeAtEnd(page, '\n# my change\n');
  await page.waitForTimeout(700);
  await page.click('#lite-share');
  await expect(page.locator('#lite-publish-dialog')).toBeVisible();   // a changed copy is your own song now: the form of a share, not the link of the demo
  await page.click('#lite-publish-cancel');
  // the link itself builds and plays: a new page on the link, with the sign-in token kept in the page storage
  const fresh2 = await context.newPage();
  await fresh2.goto(liteUrl());
  await fresh2.waitForSelector('html[data-lite-ready="true"]');
  await fresh2.evaluate(() => { location.hash = '#demo=hello-melody'; });
  await expect(fresh2.locator('#lite-dialog')).toBeVisible();   // the first page holds a changed copy: Lite asks
  await fresh2.click('#lite-dialog-cancel');   // New fresh copy
  await expect(fresh2.locator('#lite-output-info')).toContainText('seconds', { timeout: 60000 });
  await expect(fresh2.locator('#lite-filename')).toContainText('song.amplet');
  await fresh2.close();
});

test('a language connection that the browser closes (a phone that sleeps) comes back by itself, without the offline message', async ({ page }) => {
  await page.addInitScript(() => {
    const Original = window.WebSocket;
    const made: WebSocket[] = [];
    (window as unknown as { __sockets: WebSocket[] }).__sockets = made;
    window.WebSocket = class extends Original { constructor(url: string | URL, protocols?: string | string[]) { super(url, protocols); made.push(this); } } as typeof WebSocket;
  });
  await fresh(page);
  await useDemo(page, 'hello-melody');
  await expect.poll(() => page.evaluate(() => (window as unknown as { __sockets: WebSocket[] }).__sockets.filter((s) => s.readyState === 1).length)).toBe(1);
  // the browser drops the connection
  await page.evaluate(() => (window as unknown as { __sockets: WebSocket[] }).__sockets.forEach((s) => s.close()));
  await expect.poll(() => page.evaluate(() => (window as unknown as { __sockets: WebSocket[] }).__sockets.filter((s) => s.readyState === 1).length), { timeout: 8000 }).toBe(1);
  expect(await page.evaluate(() => (window as unknown as { __sockets: WebSocket[] }).__sockets.length)).toBeGreaterThanOrEqual(2);
  await expect(page.locator('#lite-status')).not.toContainText('offline');
  // and the new connection works: a mistake is still marked
  await typeAtEnd(page, '\nnonsense block here {\n');
  await expect(page.locator('#lite-problems li').first()).toBeVisible({ timeout: 8000 });
});

test('on a phone the editor has a real height and Build & play floats inside the screen', async ({ page }) => {
  await page.setViewportSize({ width: 412, height: 839 });
  await fresh(page);
  await useDemo(page, 'hello-melody');
  const editor = await page.locator('#lite-editor').boundingBox();
  expect(editor!.height).toBeGreaterThanOrEqual(300);
  const build = await page.locator('#lite-build').boundingBox();
  expect(build!.y + build!.height).toBeLessThanOrEqual(839);
  expect(build!.y).toBeGreaterThan(839 / 2);
  await page.mouse.wheel(0, 800);
  await page.waitForTimeout(200);
  const after = await page.locator('#lite-build').boundingBox();
  expect(after!.y).toBeCloseTo(build!.y, 0);   // it stays where the thumb is while the page scrolls
});

test('when the browser storage never answers Lite still works, without saving, and says so', async ({ page }) => {
  // an IndexedDB whose open never finishes (a tab frozen in the middle of a write, or an in-app browser)
  await page.addInitScript(() => { indexedDB.open = (() => ({})) as unknown as typeof indexedDB.open; });
  await page.goto(liteUrl());
  await page.waitForSelector('html[data-lite-ready="true"]', { timeout: 20000 });
  await expect(page.locator('#lite-notice')).toBeVisible();
  await expect(page.locator('#lite-notice')).toContainText('Not saved');
  await useDemo(page, 'hello-melody');
  await page.click('#lite-build');
  await expect(page.locator('#lite-output-info')).toContainText('seconds', { timeout: 60000 });
});

test('a shared link opens a window with the steps, ends in a Play button that plays, and Cancel works while it builds', async ({ page, context }) => {
  await fresh(page);
  // the link: steps one by one, then Play
  const linked = await context.newPage();
  await linked.goto(liteUrl());
  await linked.waitForSelector('html[data-lite-ready="true"]');
  await linked.evaluate(() => { location.hash = '#demo=chords-bass'; });
  await expect(linked.locator('#lite-tour')).toBeVisible();
  await expect(linked.locator('#lite-tour-steps li')).toHaveCount(4);
  await expect(linked.locator('#lite-tour-play')).toBeVisible({ timeout: 60000 });
  await expect(linked.locator('#lite-tour-steps li[data-state="done"]')).toHaveCount(4);
  await expect(linked.locator('#lite-tour-title')).toContainText('Chords and a bass');   // the name of the demo
  await linked.click('#lite-tour-play');
  await expect(linked.locator('#lite-tour')).toBeHidden();
  await expect.poll(() => linked.evaluate(() => !(document.querySelector('#lite-output') as HTMLAudioElement).paused), { timeout: 10000 }).toBe(true);
  await linked.close();
  // Cancel while it works: the window says so and offers Try again
  const cancelled = await context.newPage();
  await cancelled.route('**/api/remix/build**', async (route) => { await new Promise((r) => setTimeout(r, 4000)); await route.continue(); });
  await cancelled.goto(liteUrl());
  await cancelled.waitForSelector('html[data-lite-ready="true"]');
  await cancelled.evaluate(() => { location.hash = '#demo=hello-melody'; });
  await expect(cancelled.locator('#lite-tour-cancel')).toBeVisible();
  await cancelled.waitForTimeout(800);
  await cancelled.click('#lite-tour-cancel');
  await expect(cancelled.locator('#lite-tour-line')).toContainText('Cancelled');
  await expect(cancelled.locator('#lite-tour-retry')).toBeVisible();
  await expect(cancelled.locator('#lite-tour-play')).toBeHidden();
  await cancelled.click('#lite-tour-close');
  await expect(cancelled.locator('#lite-tour')).toBeHidden();
  await cancelled.close();
});

test('a build sends only what the server does not have: demo files never, an own change once', async ({ page }) => {
  await fresh(page);
  const bodies: number[] = [];
  page.on('request', (request) => { if (request.url().includes('/api/remix/build')) bodies.push(request.postDataBuffer()?.length ?? 0); });
  await useDemo(page, 'basic-drums');   // a song and the five sounds of the kit
  await page.click('#lite-build');
  await expect(page.locator('#lite-output-info')).toContainText('seconds', { timeout: 60000 });
  await expect(page.locator('#lite-log')).toContainText('already cached on the server');
  const untouched = bodies[0];
  expect(untouched).toBeLessThan(5000);   // the manifest only: the song and the sounds are the demo's
  // an own change is sent once ...
  await typeAtEnd(page, '\n# my change\n');
  await page.waitForTimeout(700);
  await page.click('#lite-build');
  await expect(page.locator('#lite-log')).toContainText('Sending 1 of', { timeout: 60000 });
  await expect(page.locator('#lite-output-info')).toContainText('seconds', { timeout: 60000 });
  // ... and the next build with the same text sends nothing (the button says Play from start now: Ctrl+Enter builds again)
  await expect(page.locator('#lite-build')).not.toHaveText('Building...');
  await page.keyboard.press('Control+Enter');
  await expect(page.locator('#lite-log')).toContainText('Sending 0 of', { timeout: 60000 });
  await expect(page.locator('#lite-output-info')).toContainText('seconds', { timeout: 60000 });
  expect(bodies.length).toBe(3);
  expect(bodies[2]).toBeLessThan(5000);
});

test('the main button follows the sound: Play from start or Stop after a build, and Build & play again at any change', async ({ page }) => {
  await fresh(page);
  const mode = () => page.locator('#lite-build').getAttribute('data-mode');
  await useDemo(page, 'hello-melody');
  await expect(page.locator('#lite-build')).toHaveText('Build & play');
  await page.click('#lite-build');
  await expect(page.locator('#lite-output-info')).toContainText('seconds', { timeout: 60000 });
  await expect.poll(mode, { timeout: 10000 }).not.toBe('build');   // Stop while it plays, Play from start when the browser kept it from starting
  await page.evaluate(() => (document.querySelector('#lite-output') as HTMLAudioElement).pause());
  await expect(page.locator('#lite-build')).toContainText('Play from start');
  await expect(page.locator('#lite-status')).toHaveText('Paused');   // paused by the player itself: the line says so
  await page.click('#lite-build');   // plays again from the start
  await expect(page.locator('#lite-build')).toContainText('Stop');
  await expect(page.locator('#lite-status')).toHaveText('Playing');
  expect(await page.evaluate(() => (document.querySelector('#lite-output') as HTMLAudioElement).currentTime)).toBeLessThan(1.5);
  await page.click('#lite-build');   // stops
  await expect(page.locator('#lite-build')).toContainText('Play from start');
  // looking at another file and back is not a change
  await page.evaluate(() => (document.querySelector('#lite-output') as HTMLAudioElement).pause());
  await expect(page.locator('#lite-build')).toContainText('Play from start');
  // the first edit makes it an old sound: Build & play again
  await typeAtEnd(page, '\n# my change\n');
  await expect(page.locator('#lite-build')).toHaveText('Build & play');
  // build again, then a new file is a change too
  await page.click('#lite-build');
  await expect(page.locator('#lite-build')).not.toHaveText('Build & play', { timeout: 60000 });
  await page.evaluate(() => (document.querySelector('#lite-output') as HTMLAudioElement).pause());
  await expect(page.locator('#lite-build')).toContainText('Play from start');
  await page.click('#lite-new-file');
  await page.locator('#lite-dialog input').fill('extra.txt');
  await page.click('#lite-dialog-ok');
  await expect(page.locator('#lite-build')).toHaveText('Build & play');
  // another demo is a change as well
  await page.click('#lite-build');
  await expect(page.locator('#lite-build')).not.toHaveText('Build & play', { timeout: 60000 });
  await useDemo(page, 'chords-bass');
  await expect(page.locator('#lite-build')).toHaveText('Build & play');
});

test('the window of a shared link shows how many files are cached on the server', async ({ page, context }) => {
  await fresh(page);
  const linked = await context.newPage();
  await linked.goto(liteUrl());
  await linked.waitForSelector('html[data-lite-ready="true"]');
  await linked.evaluate(() => { location.hash = '#demo=basic-drums'; });
  await expect(linked.locator('#lite-tour-play')).toBeVisible({ timeout: 60000 });
  await expect(linked.locator('#lite-tour-files')).toContainText('cached on the server');
  await expect(linked.locator('#lite-tour-files')).toContainText('nothing to send');
  await linked.close();
});

test('Try again in the window of a shared link goes through all the steps again and ends in Play', async ({ page, context }) => {
  await fresh(page);
  const linked = await context.newPage();
  let calls = 0;
  await linked.route('**/api/remix/build**', async (route) => {
    calls++;
    if (calls === 1) await route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ error: { code: 'AMPL931', message: 'The build failed inside Lite (reference test).' } }) });
    else await route.continue();
  });
  await linked.goto(liteUrl());
  await linked.waitForSelector('html[data-lite-ready="true"]');
  await linked.evaluate(() => { location.hash = '#demo=hello-melody'; });
  await expect(linked.locator('#lite-tour-retry')).toBeVisible({ timeout: 30000 });
  await expect(linked.locator('#lite-tour-line')).toContainText('failed');
  await linked.click('#lite-tour-retry');
  // the window shows the steps of the second run (not stuck on the first step while the build runs behind it)
  await expect(linked.locator('#lite-tour-play')).toBeVisible({ timeout: 60000 });
  await expect(linked.locator('#lite-tour-steps li[data-state="done"]')).toHaveCount(4);
  expect(calls).toBe(2);
  await linked.click('#lite-tour-play');
  await expect(linked.locator('#lite-tour')).toBeHidden();
  await linked.close();
});

test('Share says visibly when copying did not work, and what to do', async ({ page }) => {
  await fresh(page);
  await page.addInitScript(() => { Object.defineProperty(navigator, 'clipboard', { value: { writeText: () => Promise.reject(new Error('blocked')) } }); document.execCommand = () => false; });
  await page.reload();
  await page.waitForSelector('html[data-lite-ready="true"]');
  await useDemo(page, 'hello-melody');
  await page.click('#lite-share');
  await expect(page.locator('#lite-share-result')).toContainText('did not work');
  await expect(page.locator('#lite-share-result')).toHaveAttribute('data-kind', 'error');
  await expect(page.locator('#lite-share-link')).toBeFocused();   // the link is selected, ready for Ctrl+C
});

test('the word ample in the composition line has a colour of its own (a gradient), and no other word has', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'hello-melody');
  const egg = page.locator('#lite-editor .cm-ample-word');
  await expect(egg).toHaveCount(1);
  await expect(egg).toHaveText('ample');
  expect(await egg.evaluate((e) => getComputedStyle(e).backgroundImage)).toContain('linear-gradient');
});

test('a link that was opened before and whose copy holds changes asks what to do: your copy or a fresh one', async ({ page }) => {
  await fresh(page);
  await page.goto(page.url().split('#')[0] + '#demo=hello-melody');
  await page.reload();
  await expect(page.locator('#lite-tour')).toBeVisible();
  await expect(async () => {   // the question about an earlier copy can come up just now and covers the tour: answer it (a new fresh copy) and skip again
    if (await page.locator('#lite-dialog').isVisible()) await page.click('#lite-dialog-cancel', { timeout: 2000 });
    await page.click('#lite-tour-skip', { timeout: 2000 });   // open it without building
  }).toPass({ timeout: 20000 });
  await expect(page.locator('#lite-tour')).toBeHidden();
  await typeAtEnd(page, '\n# my own change');
  await expect(page.locator('#lite-saved')).toHaveText('saved on this browser');
  await page.reload();   // the link is still in the address bar: Lite asks
  await expect(page.locator('#lite-dialog')).toBeVisible();
  await expect(page.locator('#lite-dialog-title')).toHaveText('You already have a copy');
  await page.click('#lite-dialog-ok');   // Open my copy
  await expect(page.locator('#lite-editor .cm-content')).toContainText('my own change');
  await page.click('#lite-tour-skip', { timeout: 3000 }).catch(() => {});   // the tour may not be there: without a short limit this click waits for the whole test
  await page.reload();
  await expect(page.locator('#lite-dialog')).toBeVisible();
  await page.click('#lite-dialog-cancel');   // New fresh copy
  await expect(page.locator('#lite-editor .cm-content')).not.toContainText('my own change', { timeout: 20000 });
  // the fresh copy is left over now, unchanged: it must not hide the copy with the changes
  await page.reload();
  await expect(page.locator('#lite-dialog')).toBeVisible();
  await page.click('#lite-dialog-ok');   // Open my copy
  await expect(page.locator('#lite-editor .cm-content')).toContainText('my own change', { timeout: 20000 });
});

test('the demo Cars passing the music builds: rain and a car make a control that steers a filter, a reverb and a note', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'cars-and-music');
  await expect(page.locator('#lite-editor .cm-content')).toContainText('cars passing the music');   // the first line: the editor only draws the lines in view
  await page.click('#lite-build');
  await expect(page.locator('#lite-output-info')).toContainText('seconds', { timeout: 120000 });
});

test('Tab indents the lines you selected by one step each time, and Shift+Tab takes a step back', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'hello-melody');
  await typeAtEnd(page, '\nalpha\nbeta');
  await page.keyboard.press('Shift+ArrowUp');   // beta and alpha are selected
  const text = () => page.locator('#lite-editor .cm-content').innerText();
  await page.keyboard.press('Tab');
  await expect.poll(text).toMatch(/\n {2}alpha\n {2}beta/);
  await page.keyboard.press('Tab');
  await expect.poll(text).toMatch(/\n {4}alpha\n {4}beta/);
  await page.keyboard.press('Shift+Tab');
  await expect.poll(text).toMatch(/\n {2}alpha\n {2}beta/);
});
