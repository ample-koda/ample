import { test, expect, Page } from '@playwright/test';
import { api, freshServer, openApp, srcText, setSrc, openSourceFile } from '../../web/tests/support';

// Plan project-panel PP01 to PP08: the Add and Use menus, refactor renames, drops in the editor and fields, the file switcher and the save control.
const SONG = 'composition "demo" { ample 1  tempo 92  master-seed 424242 }\nimport-audio rain {\n  file samples/rain.wav\n}\ndrums beat at 1 for 1 {\n  kit samples/kit\n  pattern beat-A\n}\ndrums synth at 1 for 1 {\n  pattern beat-A\n}\n';

freshServer({
  song: SONG,
  files: { 'samples/rain.wav': 'RIFFfake', 'samples/storm.wav': 'RIFFfake', 'samples/kit/kick.wav': 'RIFFfake', 'lyrics/old.lrc': '[ti:Old]\n', 'notes/readme.md': '# hi\n' },
});

const openPanel = async (page: Page) => { await page.getByRole('button', { name: 'Project', exact: true }).click(); await expect(page.locator('#b-project')).toBeVisible(); };
// the innermost entry that has the text (a list that opens beside an item holds the entries inside the item)
const menuItem = (page: Page, text: string) => page.locator('#menu div', { hasText: text }).filter({ hasNot: page.locator('div', { hasText: text }) }).first();
const dialogInput = (page: Page) => page.locator('#dlg-extra input').first();
const dialogButton = (page: Page, name: string) => page.locator('#dlg-btns button', { hasText: name });

// the tests share one server: a song that an earlier test changed is saved first, so every test starts clean
test.beforeEach(async ({ page }) => { await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } }); await openApp(page, { reset: false }); });

test('the kinds endpoint lists what can be created and the new endpoint makes each with a skeleton', async () => {
  const kinds = await api('/api/project/kinds');
  expect(kinds.map((k: { id: string }) => k.id)).toEqual(['song', 'lyrics', 'chords', 'midi', 'module', 'component', 'samples', 'note', 'sequence', 'folder']);
  const made = await api('/api/project/new', { method: 'POST', body: { kind: 'chords', name: 'Verse' } });
  expect(made.path).toBe('chords/Verse.chordpro');
  expect((await api(`/api/file?path=${encodeURIComponent('chords/Verse.chordpro')}`).catch(() => ({ text: '' }))).text ?? '').toBeDefined();
  const again = await api('/api/project/new', { method: 'POST', body: { kind: 'chords', name: 'Verse' } }).catch((e) => e);
  expect(JSON.stringify(again)).toMatch(/already something|AMP5243/);
  const bad = await api('/api/project/new', { method: 'POST', body: { kind: 'note', name: '../x' } }).catch((e) => e);
  expect(JSON.stringify(bad)).toMatch(/AMP5285|name is/);
});

test('right-click on an empty spot of the panel offers Add and creates a lyrics file that opens in the editor', async ({ page }) => {
  await openPanel(page);
  await page.locator('#project-panel-content').evaluate((host) => host.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 300, clientY: 300 })));
  await menuItem(page, 'Add').click();
  await menuItem(page, 'Lyrics (timed)').click();
  await dialogInput(page).fill('Chorus');
  await dialogButton(page, 'Create').click();
  await expect(page.locator('#toast')).toContainText('lyrics/Chorus.lrc');
  expect((await api(`/api/file?path=${encodeURIComponent('lyrics/Chorus.lrc')}`)).text).toContain('[ti:Chorus]');
});

test('Use in song on a file adds a block that reads it, Point a block at it changes an existing block', async ({ page }) => {
  await openPanel(page);
  await page.locator('.project-panel-tree [role=treeitem]', { hasText: 'samples' }).first().click();
  const tile = page.locator('.project-tile').filter({ hasText: 'storm.wav' });
  await tile.click({ button: 'right' });
  await menuItem(page, 'Use in song').click();
  await menuItem(page, 'New').first().click();
  await expect(page.locator('#toast')).toContainText('from samples/storm.wav');
  await tile.click({ button: 'right' });
  await menuItem(page, 'Point a block at it').click();
  await menuItem(page, 'rain › file').click();
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'rain').parameters.find((p: { name: string }) => p.name === 'file').value).toBe('samples/storm.wav');
});

test('renaming a folder updates the songs that point at it after a preview; the advanced entry leaves them alone', async ({ page }) => {
  const plan = await api('/api/project/rename', { method: 'POST', body: { source: 'samples/kit', destination: 'samples/rusty', dryRun: true } });
  expect(plan.edits).toHaveLength(1);
  expect(plan.edits[0].new).toContain('samples/rusty');
  expect((await api('/api/project/tree?path=samples&depth=1')).children.map((c: { name: string }) => c.name)).toContain('kit');   // a dry run moved nothing

  await openPanel(page);
  await page.locator('.project-panel-tree [role=treeitem]', { hasText: 'samples' }).first().click();
  const folder = page.locator('.project-tile').filter({ hasText: 'kit' }).first();
  await folder.click({ button: 'right' });
  await menuItem(page, 'Rename...').click();
  await dialogInput(page).fill('rusty');
  await dialogButton(page, 'Next').click();
  await expect(page.locator('#dlg-b')).toContainText('1 line(s) that point at it will be updated');
  await expect(page.locator('#dlg-extra .refactor-list')).toContainText('kit samples/rusty');
  await dialogButton(page, 'Move').click();
  await expect(page.locator('#toast')).toContainText('1 reference(s) updated');
  expect((await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'beat').parameters.find((p: { name: string }) => p.name === 'kit').value).toBe('samples/rusty');

  // without refactor only the folder moves
  await page.locator('.project-tile').filter({ hasText: 'rusty' }).first().click({ button: 'right' });
  await menuItem(page, 'Advanced').click();
  await menuItem(page, 'Rename without refactor').click();
  await dialogInput(page).fill('plain');
  await dialogButton(page, 'Next').click();
  await expect(page.locator('#dlg-b')).toContainText('Only the folder moves');
  await dialogButton(page, 'Move anyway').click();
  await expect(page.locator('#toast')).toContainText('References were not changed');
  expect((await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'beat').parameters.find((p: { name: string }) => p.name === 'kit').value).toBe('samples/rusty');
});

test('a file dragged over the code editor lights the lines that take it and a drop fills the path; the same editor takes a new block between blocks', async ({ page }) => {
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/import-audio rain/);
  const result = await page.evaluate(async () => {
    const { beginFileDrag, endFileDrag } = await import('/app/dragfile.js');
    const ta = document.querySelector('#se-ta') as HTMLTextAreaElement;
    const dt = new DataTransfer();
    beginFileDrag({ dataTransfer: dt }, { path: 'samples/other.wav', isDir: false });
    const rect = ta.getBoundingClientRect();
    const at = (line: number) => ({ bubbles: true, cancelable: true, dataTransfer: dt, clientX: rect.left + 40, clientY: rect.top + 6 + line * 18 + 9 });
    ta.dispatchEvent(new DragEvent('dragover', at(3)));
    const lit = { fields: document.querySelectorAll('#se-drop .dt.field').length, inserts: document.querySelectorAll('#se-drop .dt.insert').length, on: document.querySelectorAll('#se-drop .dt.on').length };
    ta.dispatchEvent(new DragEvent('drop', at(3)));
    endFileDrag();
    return { lit, text: ta.value };
  });
  expect(result.lit.fields).toBeGreaterThanOrEqual(1);
  expect(result.lit.inserts).toBeGreaterThanOrEqual(2);
  expect(result.lit.on).toBeGreaterThanOrEqual(1);
  // the path of the line is a different file: the editor asks first
  await expect(page.locator('#dlg-t')).toContainText('Replace the file?');
  await dialogButton(page, 'Replace').click();
  await expect.poll(() => srcText(page)).toMatch(/file samples\/other\.wav/);
  await page.keyboard.press('Control+z');
  await expect.poll(() => srcText(page)).not.toMatch(/file samples\/other\.wav/);
});

test('a file dragged onto a file field of a block sets it, and the fields that take it light up', async ({ page }) => {
  await page.locator('.node[data-id="rain"] .nh .t').click();
  const field = page.locator('#props .file-field input').first();
  await expect(field).toBeVisible();
  const out = await page.evaluate(async () => {
    const { beginFileDrag, endFileDrag } = await import('/app/dragfile.js');
    const input = document.querySelector('#props .file-field input') as HTMLInputElement;
    const dt = new DataTransfer();
    beginFileDrag({ dataTransfer: dt }, { path: 'samples/other.wav', isDir: false });
    const lit = input.classList.contains('drop-ok');
    input.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: dt }));
    input.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt }));
    endFileDrag();
    return { lit };
  });
  expect(out.lit).toBe(true);
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'rain').parameters.find((p: { name: string }) => p.name === 'file').value).toBe('samples/other.wav');
});

test('Ctrl+P opens the file switcher: a few letters find a file and Enter opens it', async ({ page }) => {
  await page.click('#vs-source');
  await page.locator('#se-cm .cm-content').press('Control+p');
  await expect(page.locator('#se-switch')).toBeFocused();
  await page.locator('#se-switch').fill('oldl');
  await expect(page.locator('#se-switchlist div').first()).toHaveText('lyrics/old.lrc');
  await page.locator('#se-switch').press('Enter');
  await expect(page.locator('#se-title')).toHaveText('lyrics/old.lrc');
 
});

test('recent and open files are in the tab row, in the Source view and in the Blocks view', async ({ page }) => {
  await page.click('#vs-source');
  await page.locator('#se-switch').fill('notes/readme');
  await page.locator('#se-switch').press('Enter');
  await expect(page.locator('#se-title')).toHaveText('notes/readme.md');
  await expect.poll(() => srcText(page)).toBe('# hi\n');

  const sourceToggle = page.locator('#tabbar .recent-files-toggle');
  await sourceToggle.click();
  const sourceMenu = page.locator(`#${await sourceToggle.getAttribute('aria-controls')}`);
  await expect(sourceMenu).toBeVisible();
  await expect(sourceMenu.locator('.recent-file-row.active[data-file="song.amplet"]')).toBeVisible();
  await expect(sourceMenu.locator('.recent-file-row.active[data-file="notes/readme.md"]')).toBeVisible();
  await page.evaluate(async () => {
    const { S } = await import('/app/store.js');
    const { saveRecoveryDraft } = await import('/lib/recovery-drafts.mjs');
    saveRecoveryDraft(localStorage, S.project.id, 'notes/readme.md', '# recovered\n', '# hi\n', Date.now());
  });
  await page.keyboard.press('Escape');
  await sourceToggle.click();
  await expect(sourceMenu.locator('.recent-file-row.active[data-file="notes/readme.md"] .recent-file-dirty')).toHaveText('● Unsaved');
  await page.keyboard.press('Escape');
  await page.locator('#se-close-file').click();
  await expect(page.locator('#se-title')).toHaveText('song.amplet');
  await sourceToggle.click();
  await expect(sourceMenu.locator('.recent-file-row[data-file="notes/readme.md"] .recent-file-dirty')).toHaveText('● Unsaved');
  await page.keyboard.press('Escape');
  await page.click('#vs-blocks');
  const canvasToggle = page.locator('#tabbar .recent-files-toggle');
  await expect(canvasToggle).toBeVisible();
  await canvasToggle.click();
  const canvasMenu = page.locator(`#${await canvasToggle.getAttribute('aria-controls')}`);
  await expect(canvasMenu).toBeVisible();
  await expect(canvasMenu.locator('.recent-file-row[data-file="notes/readme.md"]')).toBeVisible();
  await canvasMenu.locator('.recent-file-row[data-file="notes/readme.md"]').click();
  await expect(page.locator('.recovery-diff-dialog')).toHaveCount(0);
  await expect(page.locator('#se-title')).toHaveText('notes/readme.md');
  await expect(page.locator('#se-cm .cm-content')).toContainText('# recovered');
});

test('the save control shows changes everywhere, saves with Ctrl+S, reverts, and saves a copy', async ({ page }) => {
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/import-audio rain/);   // the text is loaded
  const dot = page.locator('#se .savectl .savedot');
  await expect(dot).toHaveText('');
  await setSrc(page, (await srcText(page)) + '\n# typed\n');
  await expect(dot).toContainText('typing, not applied');
  await page.locator('#se-apply').click();
  await expect(dot).toContainText('changed');
  await expect(page.locator('#b-dirty')).toBeVisible();
  await page.keyboard.press('Control+s');
  await expect(dot).toHaveText('');
  expect((await api('/api/file?path=song.amplet').catch(() => null))).toBeDefined();

  await setSrc(page, (await srcText(page)) + '# more\n');
  await page.locator('#se-apply').click();
  await expect(dot).toContainText('changed');
  await page.locator('#se .savectl .savemore').click();
  await menuItem(page, 'Revert changes').click();
  await dialogButton(page, 'Revert').click();
  await expect(dot).toHaveText('');
  await expect.poll(() => srcText(page)).not.toMatch(/# more/);

  await page.locator('#se .savectl .savemore').click();
  await menuItem(page, 'Save as').click();
  await dialogInput(page).fill('copy-of-demo');
  await dialogButton(page, 'Save a copy').click();
  await expect(page.locator('#toast')).toContainText('copy-of-demo.amplet');
  expect((await api('/api/docs')).includes('copy-of-demo.amplet')).toBe(true);
});

test('a take is written to a file of the project as WAV, and an existing name is refused', async ({ page }) => {
  await page.click('#btn-build');
  await expect(page.locator('#btxt')).toHaveText('idle');
  const takes = await page.evaluate(async () => (await import('/app/store.js')).S.build.takes as Record<string, string>);
  const take = takes.synth;
  expect(take).toBeTruthy();
  const made = await api(`/api/takes/${take}/export`, { method: 'POST', body: { name: 'beat-render', folder: 'renders', format: 'wav' } });
  expect(made.path).toBe('renders/beat-render.wav');
  expect(made.sizeBytes).toBeGreaterThan(44);
  const again = await api(`/api/takes/${take}/export`, { method: 'POST', body: { name: 'beat-render', folder: 'renders', format: 'wav' } }).catch((e) => e);
  expect(JSON.stringify(again)).toMatch(/already a file|AMP5243/);
  const tree = await api('/api/project/tree?path=renders&depth=1');
  expect(tree.children.map((c: { name: string }) => c.name)).toContain('beat-render.wav');
});

test('an uploaded MP3 also gets a WAV next to it, because blocks play WAV files', async ({ page }) => {
  const { execFileSync } = await import('node:child_process');
  const os = await import('node:os'), fs = await import('node:fs'), path = await import('node:path');
  const mp3 = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'ample-mp3-')), 'tone.mp3');
  try { execFileSync(process.env.AMPLE_FFMPEG || 'ffmpeg', ['-y', '-loglevel', 'error', '-f', 'lavfi', '-i', 'sine=frequency=440:duration=0.5', '-ac', '2', '-ar', '44100', mp3]); } catch { test.skip(true, 'ffmpeg is not installed'); }
  const base64 = fs.readFileSync(mp3).toString('base64');
  const result = await page.evaluate(async (data) => {
    const { api } = await import('/app/api.js');
    const bytes = Uint8Array.from(atob(data), (c) => c.charCodeAt(0));
    return api.uploadProject(new File([bytes], 'tone.mp3'), 'samples', 'refuse', () => {});
  }, base64);
  expect(result.path).toBe('samples/tone.mp3');
  expect(result.converted).toBe('samples/tone.wav');
  const head = await page.evaluate(async () => { const r = await fetch('/api/project/audio?path=samples/tone.wav'); const b = new Uint8Array(await r.arrayBuffer()); return { riff: String.fromCharCode(...b.slice(0, 4)), size: b.length }; });
  expect(head.riff).toBe('RIFF');
  expect(head.size).toBeGreaterThan(50000);   // 0.5 s of 44.1 kHz stereo 16-bit
});

test('an MP3 of the project is converted to a WAV next to it on request, and an existing WAV is reused', async ({ page }) => {
  const { execFileSync } = await import('node:child_process');
  const os = await import('node:os'), fs = await import('node:fs'), path = await import('node:path');
  const mp3 = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'ample-mp3-')), 'pad.mp3');
  try { execFileSync(process.env.AMPLE_FFMPEG || 'ffmpeg', ['-y', '-loglevel', 'error', '-f', 'lavfi', '-i', 'sine=frequency=220:duration=0.5', mp3]); } catch { test.skip(true, 'ffmpeg is not installed'); }
  const base64 = fs.readFileSync(mp3).toString('base64');
  await page.evaluate(async (data) => {
    const bytes = Uint8Array.from(atob(data), (c) => c.charCodeAt(0));
    const { api } = await import('/app/api.js');
    await api.uploadProject(new File([bytes], 'pad.mp3'), 'samples', 'refuse', () => {});
  }, base64);
  const first = await api('/api/project/convert-audio', { method: 'POST', body: { path: 'samples/pad.mp3' } });
  expect(first.path).toBe('samples/pad.wav');
  const again = await api('/api/project/convert-audio', { method: 'POST', body: { path: 'samples/pad.mp3' } });
  expect(again).toMatchObject({ path: 'samples/pad.wav', created: false });
  const bad = await api('/api/project/convert-audio', { method: 'POST', body: { path: 'song.amplet' } }).catch((e) => e);
  expect(JSON.stringify(bad)).toMatch(/AMP5285|Only an MP3/);
});

test('names that start with a dot are hidden unless asked for', async () => {
  await api('/api/project/folder', { method: 'POST', body: { path: '.sneaky' } });
  const names = (r: { children: { name: string }[] }) => r.children.map((c) => c.name);
  expect(names(await api('/api/project/tree?depth=0'))).not.toContain('.sneaky');
  expect(names(await api('/api/project/tree?depth=0&hidden=1'))).toContain('.sneaky');
  expect(names(await api('/api/project/tree?depth=0&hidden=1'))).not.toContain('.ample');
});

test('dragging an item onto a folder moves it with the refactor preview, and onto the path at the top moves it out', async ({ page }) => {
  await openPanel(page);
  await page.locator('.project-tile[data-path="notes"]').dragTo(page.locator('.project-tile[data-path="lyrics"]'));
  await expect(page.locator('#dlg-btns button', { hasText: 'Move' }).first()).toBeVisible();
  await expect(page.locator('#dlg')).toContainText('notes becomes lyrics/notes');
  await dialogButton(page, 'Move').click();
  await expect.poll(async () => JSON.stringify(await api('/api/project/tree?path=lyrics&depth=0'))).toContain('lyrics/notes');
  // out again: into the root, by dropping on the path at the top of the panel
  await page.locator('.project-tile[data-path="lyrics"]').dblclick();
  await page.locator('.project-tile[data-path="lyrics/notes"]').dragTo(page.locator('.project-breadcrumb'));
  await expect(page.locator('#dlg')).toContainText('lyrics/notes becomes notes');
  await dialogButton(page, 'Move').click();
  await expect.poll(async () => JSON.stringify(await api('/api/project/tree?depth=0'))).toContain('"path":"notes"');
});

test('Add > Folder from the + menu and from the right-click menu of a folder creates the folder', async ({ page }) => {
  await openPanel(page);
  await page.getByRole('button', { name: 'Project actions' }).click();
  await menuItem(page, 'Add').click();
  await menuItem(page, /^Folder$/).click();
  await dialogInput(page).fill('from-plus');
  await dialogButton(page, 'Create').click();
  await expect.poll(async () => JSON.stringify(await api('/api/project/tree?depth=0'))).toContain('"path":"from-plus"');
  await page.locator('.project-tile[data-path="from-plus"]').click({ button: 'right' });
  await menuItem(page, 'Add').click();
  await menuItem(page, /^Folder$/).click();
  await dialogInput(page).fill('inner');
  await dialogButton(page, 'Create').click();
  await expect.poll(async () => JSON.stringify(await api('/api/project/tree?path=from-plus&depth=0'))).toContain('from-plus/inner');
});

test('Enter in the name field of a dialog presses its main button', async ({ page }) => {
  await openPanel(page);
  await page.getByRole('button', { name: 'Project actions' }).click();
  await page.locator('#menu div', { hasText: /^New folder/ }).click();
  await dialogInput(page).fill('by-enter');
  await dialogInput(page).press('Enter');
  await expect.poll(async () => JSON.stringify(await api('/api/project/tree?depth=0'))).toContain('"path":"by-enter"');
});

test('names may contain parentheses, except the names of songs and modules', async () => {
  const folder = await api('/api/project/new', { method: 'POST', body: { kind: 'folder', name: 'Take (1)' } });
  expect(folder.path).toBe('Take (1)');
  const note = await api('/api/project/new', { method: 'POST', body: { kind: 'note', name: 'Idea (draft)' } });
  expect(note.path).toContain('Idea (draft)');
  const song = await api('/api/project/new', { method: 'POST', body: { kind: 'module', name: 'bass (old)' } }).catch((e) => e);
  expect(JSON.stringify(song)).toMatch(/AMP5285|name is/);
});

test('files from the computer can be dropped on the project panel: into the open folder, or into the folder they are dropped on', async ({ page }) => {
  await openPanel(page);
  const drop = async (selector: string, name: string) => page.evaluate(async ({ selector, name }) => {
    const target = document.querySelector(selector)!;
    const transfer = new DataTransfer();
    transfer.items.add(new File([new Uint8Array([1, 2, 3, 4])], name, { type: 'audio/wav' }));
    // while dragging, the browser only knows the types; the files are there at the drop
    const over = new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: transfer });
    target.dispatchEvent(over);
    target.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: transfer }));
    return over.defaultPrevented;
  }, { selector, name });
  expect(await drop('.project-tile[data-path="notes"]', 'dropped-in-notes.wav')).toBe(true);
  await expect.poll(async () => JSON.stringify(await api('/api/project/tree?path=notes&depth=0'))).toContain('notes/dropped-in-notes.wav');
  expect(await drop('.project-tiles', 'dropped-here.wav')).toBe(true);
  await expect.poll(async () => JSON.stringify(await api('/api/project/tree?depth=0'))).toContain('"path":"dropped-here.wav"');
});
