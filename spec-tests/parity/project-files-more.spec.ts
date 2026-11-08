import { test, expect, Page } from '@playwright/test';
import { api, freshServer, openApp, srcText, setSrc } from '../../web/tests/support';

// More of the project panel (a server of its own, apart from project-files.spec.ts): scrolling, audio files in the song, the play button. Plan project-panel PP01 to PP08: the Add and Use menus, refactor renames, drops in the editor and fields, the file switcher and the save control.
const SONG = 'composition "demo" { ample 1  tempo 92  master-seed 424242 }\nimport-audio rain {\n  file samples/rain.wav\n}\ndrums beat at 1 for 1 {\n  kit samples/kit\n  pattern beat-A\n}\ndrums synth at 1 for 1 {\n  pattern beat-A\n}\n';
const playableWav = () => { const wav = Buffer.alloc(44 + 64); wav.write('RIFF', 0); wav.writeUInt32LE(36 + 64, 4); wav.write('WAVEfmt ', 8); wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(8000, 24); wav.writeUInt32LE(8000, 28); wav.writeUInt16LE(1, 32); wav.writeUInt16LE(8, 34); wav.write('data', 36); wav.writeUInt32LE(64, 40); return wav.toString('ascii'); };

freshServer({
  song: SONG,
  files: { 'deep/one/two/three/four/note.txt': 'x', 'sequences/bad.seq': 'sequencer 1\nchannel bass gm file=pack:musescore-general program=38\nchannel kick drum sample=samples/nowhere.wav\npattern p bars=1\n  bass 1:0:1:36:100 1:6:2:36:90\n', 'sequences/loose.seq': 'sequencer 1\n', 'samples/rain.wav': 'RIFFfake', 'samples/storm.wav': playableWav(), 'samples/kit/kick.wav': 'RIFFfake', 'lyrics/old.lrc': '[ti:Old]\n', 'notes/readme.md': '# hi\n' },
});

// The saved layout of the project arrives after the page has loaded and can turn the view to Details while the test clicks: click only while the button says "Details", and check the result.
const ensureDetails = (page: Page) => expect(async () => {
  const button = page.locator('.project-panel-toolbar button', { hasText: /^(Details|Tile view)$/ });
  if ((await button.textContent()) === 'Details') await button.click({ timeout: 2000 });
  await expect(page.locator('.project-details th').first()).toBeVisible({ timeout: 2000 });
}).toPass({ timeout: 15000 });
const openPanel = async (page: Page) => { await page.getByRole('button', { name: 'Project', exact: true }).click(); await expect(page.locator('#b-project')).toBeVisible(); };
// the innermost entry that has the text (a list that opens beside an item holds the entries inside the item)
const menuItem = (page: Page, text: string) => page.locator('#menu div', { hasText: text }).filter({ hasNot: page.locator('div', { hasText: text }) }).first();
const dialogInput = (page: Page) => page.locator('#dlg-extra input').first();
const dialogButton = (page: Page, name: string) => page.locator('#dlg-btns button', { hasText: name });

// the tests share one server: a song that an earlier test changed is saved first, so every test starts clean
// The project panel layout is saved in the project and the tests share it: every test starts on the tiles with the default columns.
const defaultLayout = { view: 'tiles', tileSize: 2, sortColumn: 'name', sortDescending: false, lengthColumn: true, columns: [{ id: 'name', width: 260 }, { id: 'kind', width: 90 }, { id: 'size', width: 100 }, { id: 'length', width: 90 }, { id: 'modified', width: 180 }] };
test.beforeEach(async ({ page }) => { await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } }); await api('/api/project/settings', { method: 'POST', body: { projectPanel: defaultLayout } }); await openApp(page, { reset: false }); });

test('a file and a folder can be duplicated from the right-click menu', async ({ page }) => {
  await openPanel(page);
  await page.locator('.project-panel-tree [role=treeitem]', { hasText: 'samples' }).first().click();
  const tile = page.locator('.project-tile, tr[data-path]').filter({ hasText: 'storm.wav' }).first();
  await tile.click({ button: 'right' });
  await menuItem(page, 'Duplicate...').click();
  expect(await dialogInput(page).inputValue()).toBe('storm copy.wav');
  await dialogButton(page, 'Duplicate').click();
  await expect.poll(async () => JSON.stringify(await api('/api/project/tree?path=samples&depth=0'))).toContain('samples/storm copy.wav');
  const original = (await api('/api/file?path=samples/storm.wav')).text, copy = (await api('/api/file?path=' + encodeURIComponent('samples/storm copy.wav'))).text;
  expect(copy).toBe(original);
  // a folder goes with everything in it
  const folder = page.locator('.project-tile, tr[data-path]').filter({ hasText: /^kit$|\bkit\b/ }).first();
  await folder.click({ button: 'right' });
  await menuItem(page, 'Duplicate...').click();
  await dialogButton(page, 'Duplicate').click();
  await expect.poll(async () => JSON.stringify(await api('/api/project/tree?path=' + encodeURIComponent('samples/kit copy') + '&depth=0'))).toContain('kick.wav');
});

test('an audio file put in the song gets a place on the timeline and a way to the output', async ({ page }) => {
  const { execFileSync } = await import('node:child_process');
  const os = await import('node:os'), fs = await import('node:fs'), path = await import('node:path');
  const mp3 = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'ample-rain-')), 'rain.mp3');
  try { execFileSync(process.env.AMPLE_FFMPEG || 'ffmpeg', ['-y', '-loglevel', 'error', '-f', 'lavfi', '-i', 'anoisesrc=d=12:c=pink:a=0.3', '-ac', '2', '-ar', '44100', mp3]); } catch { test.skip(true, 'ffmpeg is not installed'); }
  const base64 = fs.readFileSync(mp3).toString('base64');
  await page.evaluate(async (data) => {
    const bytes = Uint8Array.from(atob(data), (c) => c.charCodeAt(0));
    const { api } = await import('/app/api.js');
    const up = await api.uploadProject(new File([bytes], 'rain2.mp3'), 'assets', 'refuse', () => {});
    await (await import('/app/canvas.js')).useFileInSong(up.path);
  }, base64);
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'rain2')?.parts?.[0]?.at).toBe(1);
  const block = (await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'rain2');
  expect(block.parts[0].length).toBeGreaterThan(1);   // 12 s at 92 BPM is more than 5 bars
  // and it is not silent: the WAV that ffmpeg made has a LIST chunk before the audio, which the renderer must skip
  const take = await page.evaluate(async () => { const store = await import('/app/store.js'); await store.build('rain2'); return store.S.build?.takes?.rain2; });
  expect(take).toBeTruthy();
  const dataId = (await api(`/api/takes/${encodeURIComponent(take)}`)).audioDataId;
  const peak = await page.evaluate(async (id) => { const b = new Uint8Array(await (await fetch(`/api/data/${id}`)).arrayBuffer()); const v = new DataView(b.buffer); let at = 12; while (at + 8 < b.length && String.fromCharCode(...b.slice(at, at + 4)) !== 'data') at += 8 + v.getUint32(at + 4, true); let m = 0; for (let i = at + 8; i + 1 < b.length; i += 2) m = Math.max(m, Math.abs(v.getInt16(i, true))); return m / 32768; }, dataId);
  expect(peak).toBeGreaterThan(0.05);
});

test('audio files have a play and stop button in front of the name, other files only keep the space', async ({ page }) => {
  await openPanel(page);
  await page.locator('.project-panel-tree [role=treeitem]', { hasText: 'samples' }).first().click();
  await ensureDetails(page);
  const play = page.locator('tr[data-path="samples/storm.wav"] .file-play');
  await play.scrollIntoViewIfNeeded();
  await expect(play).toBeVisible();
  expect(await page.locator('tr[data-path="samples/kit"] .file-play').count()).toBe(0);
  expect(await page.locator('tr[data-path="samples/kit"] .file-play-space').count()).toBe(1);
  await play.click();
  await expect.poll(() => page.evaluate(async () => { const s = (await import('/app/player.js')).playerState(); return s.source; })).toBe('samples/storm.wav');
  await page.evaluate(async () => (await import('/app/player.js')).stop());
  await expect(play).toHaveText('▶');
});

test('the details list can be scrolled up again when a row near the bottom is selected', async ({ page }) => {
  for (let i = 0; i < 45; i++) await api('/api/project/new', { method: 'POST', body: { kind: 'note', name: `scroll-${String(i).padStart(2, '0')}` } });
  await openPanel(page);
  await ensureDetails(page);
  const host = page.locator('#project-panel-content');
  await host.evaluate((h) => { h.style.maxHeight = '260px'; h.style.overflow = 'auto'; });
  const last = page.locator('tr[data-path]').last();
  await last.scrollIntoViewIfNeeded();
  await last.click({ force: true });
  await expect.poll(() => host.evaluate((h) => h.scrollTop)).toBeGreaterThan(20);
  await host.evaluate((h) => { h.scrollTop = 0; });
  await page.waitForTimeout(400);
  expect(await host.evaluate((h) => h.scrollTop)).toBeLessThan(5);   // it stayed at the top; it used to jump back to the selected row
});

test('the column headings of the details list stay in place while the rows scroll', async ({ page }) => {
  for (let i = 0; i < 45; i++) await api('/api/project/new', { method: 'POST', body: { kind: 'note', name: `head-${String(i).padStart(2, '0')}` } });
  await openPanel(page);
  await ensureDetails(page);
  const host = page.locator('#project-panel-content');
  await host.evaluate((h) => { h.style.maxHeight = '260px'; h.style.overflow = 'auto'; });
  const head = page.locator('.project-details th').first();
  const before = (await head.boundingBox())!.y;
  await host.evaluate((h) => { h.scrollTop = 400; });
  await page.waitForTimeout(200);
  expect(await host.evaluate((h) => h.scrollTop)).toBeGreaterThan(20);
  expect(Math.abs((await head.boundingBox())!.y - before)).toBeLessThan(2);
});

test('the project panel layout with the Length column is accepted and kept', async () => {
  const columns = [{ id: 'name', width: 260 }, { id: 'kind', width: 90 }, { id: 'size', width: 100 }, { id: 'length', width: 90 }, { id: 'modified', width: 180 }, { id: 'usedBy', width: 180 }, { id: 'path', width: 250 }, { id: 'sha256', width: 220 }];
  const saved = await api('/api/project/settings', { method: 'POST', body: { projectPanel: { view: 'details', tileSize: 2, sortColumn: 'length', sortDescending: false, columns, lengthColumn: true } } });
  expect(saved.saved).toBe(true);
  const back = await api('/api/project/settings');
  expect(back.projectPanel.columns.map((c) => c.id)).toEqual(columns.map((c) => c.id));
});

test('the details list has a Length column for audio files', async ({ page }) => {
  await openPanel(page);
  await page.locator('.project-panel-tree [role=treeitem]', { hasText: 'samples' }).first().click();
  await ensureDetails(page);
  await expect(page.locator('th', { hasText: /^Length/ })).toBeVisible();
  const row = page.locator('tr[data-path="samples/storm.wav"]');
  await expect(row.locator('td[data-column="length"]')).toBeVisible();
  await expect(page.locator('tr[data-path="samples/kit"] td[data-column="length"]')).toHaveText(/—|…/);   // a folder has no length
});

test('the Length column shows how long a sound file lasts', async ({ page }) => {
  await page.evaluate(async () => {
    const rate = 8000, samples = rate * 2, buffer = new ArrayBuffer(44 + samples * 2), v = new DataView(buffer);
    const text = (at: number, t: string) => [...t].forEach((c, i) => v.setUint8(at + i, c.charCodeAt(0)));
    text(0, 'RIFF'); v.setUint32(4, 36 + samples * 2, true); text(8, 'WAVEfmt '); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
    v.setUint32(24, rate, true); v.setUint32(28, rate * 2, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true); text(36, 'data'); v.setUint32(40, samples * 2, true);
    const { api } = await import('/app/api.js');
    await api.uploadProject(new File([buffer], 'two-seconds.wav'), 'samples', 'refuse', () => {});
  });
  await openPanel(page);
  await page.locator('.project-panel-tree [role=treeitem]', { hasText: 'samples' }).first().click();
  await ensureDetails(page);
  await expect(page.locator('tr[data-path="samples/two-seconds.wav"] td[data-column="length"]')).toHaveText('0:02.0', { timeout: 15000 });
});

test('a sequence file that no block uses opens as text from the project panel', async ({ page }) => {
  await openPanel(page);
  await page.locator('.project-panel-tree [role=treeitem]', { hasText: 'sequences' }).first().click();
  await page.locator('.project-tile, tr[data-path]').filter({ hasText: 'loose.seq' }).first().dblclick();
  await expect(page.locator('#se-title')).toHaveText(/loose.seq/);
  await expect.poll(() => srcText(page)).toMatch(/sequencer 1/);
});

test('a sequence file shows its problems while it is typed: a note past the bar and a sample that is not in the project', async ({ page }) => {
  await openPanel(page);
  await page.locator('.project-panel-tree [role=treeitem]', { hasText: 'sequences' }).first().click();
  await page.locator('.project-tile, tr[data-path]').filter({ hasText: 'bad.seq' }).first().dblclick();
  const problems = page.locator('#se-prob .seerr');
  await expect(problems).toHaveCount(2);
  await expect(problems.first()).toContainText('AMP5380');
  await expect(page.locator('#se-prob')).toContainText("'1:6:2:36:90'");
  await expect(page.locator('#se-prob')).toContainText("samples/nowhere.wav");
  // fixing the row takes its problem away at once
  await setSrc(page, (await srcText(page)).replace('1:6:2:36:90', '1:2:1:36:90'));
  await expect(problems).toHaveCount(1);
  await expect(page.locator('#se-status')).toContainText('1 error');
});

test('a double click on a sound file opens a window that floats and stays, with the file to drag out, and the panel stays', async ({ page }) => {
  await page.evaluate(async () => {
    const rate = 8000, samples = rate * 2, buffer = new ArrayBuffer(44 + samples * 2), v = new DataView(buffer);
    const text = (at: number, t: string) => [...t].forEach((c, i) => v.setUint8(at + i, c.charCodeAt(0)));
    text(0, 'RIFF'); v.setUint32(4, 36 + samples * 2, true); text(8, 'WAVEfmt '); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
    v.setUint32(24, rate, true); v.setUint32(28, rate * 2, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true); text(36, 'data'); v.setUint32(40, samples * 2, true);
    for (let i = 0; i < samples; i++) v.setInt16(44 + i * 2, Math.round(9000 * Math.sin(i / 8)), true);
    const { api } = await import('/app/api.js');
    await api.uploadProject(new File([buffer], 'preview-me.wav'), 'samples', 'refuse', () => {});
  });
  await openPanel(page);
  await page.locator('.project-panel-tree [role=treeitem]', { hasText: 'samples' }).first().click();
  await page.locator('.project-tile, tr[data-path]').filter({ hasText: 'preview-me.wav' }).first().dblclick({ position: { x: 14, y: 10 } });   // not on the waveform: that part has its own scrubbing
  const win = page.locator('.float-preview');
  await expect(win).toBeVisible();
  await expect(win.locator('.fp-play')).toBeVisible();
  await expect(page.locator('#b-project')).toBeVisible();   // the panel is still there
  const before = (await win.boundingBox())!;
  await page.mouse.move(before.x + before.width - 20, before.y + 8);
  await page.mouse.down(); await page.mouse.move(before.x + before.width - 20 - 120, before.y + 8 + 60, { steps: 5 }); await page.mouse.up();
  const after = (await win.boundingBox())!;
  expect(Math.round(before.x - after.x)).toBeGreaterThan(80);
  await page.mouse.click(10, 10);
  await expect(win).toBeVisible();   // it stays when something else is clicked
  await expect(win.locator('.fp-chip')).toHaveAttribute('draggable', 'true');
  const dragged = await win.locator('.fp-chip').evaluate(async (chip) => { const transfer = new DataTransfer(); chip.dispatchEvent(new DragEvent('dragstart', { bubbles: true, dataTransfer: transfer })); return (await import('/app/dragfile.js')).draggedFile()?.path; });
  expect(dragged).toBe('samples/preview-me.wav');
  await win.getByRole('button', { name: /^Close the preview/ }).click();
  await expect(win).toHaveCount(0);
});

test('the open folder has an Up button, clickable parts and Alt+Up to go back up @quarantine', async ({ page }) => {
  await openPanel(page);
  const crumb = page.locator('.project-breadcrumb');
  await expect(crumb.locator('.crumb-up')).toBeDisabled();   // the project folder has nothing above it
  const sequences = page.locator('.project-panel-tree [role=treeitem]', { hasText: 'sequences' }).first();
  await sequences.click();
  await expect(crumb.locator('.crumb-part.current')).toHaveText('sequences');
  await expect(crumb.locator('.crumb-up')).toBeEnabled();
  await crumb.locator('.crumb-up').click();
  await expect(crumb.locator('.crumb-up')).toBeDisabled();
  await expect(crumb.locator('.crumb-part.current')).not.toHaveText('sequences');
  // a part of the path opens that folder, and Alt+Up from the keyboard goes up as well
  await sequences.click();
  await expect(crumb.locator('.crumb-part.current')).toHaveText('sequences');
  await crumb.locator('.crumb-part').first().click();
  await expect(crumb.locator('.crumb-up')).toBeDisabled();
  await sequences.click();
  await crumb.locator('.crumb-part.current').focus();
  await page.keyboard.press('Alt+ArrowUp');
  await expect(crumb.locator('.crumb-up')).toBeDisabled();
});

test('the folder path scales: it folds its early parts and keeps only the Up button when there is no room', async ({ page }) => {
  await openPanel(page);
  await page.locator('.project-panel-tree [role=treeitem]', { hasText: 'sequences' }).first().click();
  const crumb = page.locator('.project-breadcrumb');
  await expect(crumb.locator('.crumb-part.current')).toHaveText('sequences');
  await crumb.evaluate((node) => { (node as HTMLElement).style.flex = '0 0 50px'; });   // far too narrow: no wrapping, only the Up button
  await expect(crumb.locator('.crumb-part')).toHaveCount(0);
  await expect(crumb.locator('.crumb-up')).toBeVisible();
  expect(await crumb.evaluate((node) => node.scrollWidth <= node.clientWidth + 1)).toBe(true);
  await crumb.evaluate((node) => { (node as HTMLElement).style.flex = ''; });
  await expect(crumb.locator('.crumb-part.current')).toHaveText('sequences');
});

test('a deep folder path folds its early parts into a menu before it falls back to the Up button @quarantine', async ({ page }) => {
  await openPanel(page);
  await page.evaluate(() => { const key = Object.keys(localStorage).find((k) => k.startsWith('ample.project-panel.'))!; localStorage.setItem(key, JSON.stringify({ ...JSON.parse(localStorage.getItem(key) || '{}'), folder: 'deep/one/two/three/four' })); });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await openPanel(page);
  const crumb = page.locator('.project-breadcrumb');
  await expect(crumb.locator('.crumb-part.current')).toHaveText('four');
  await expect(crumb.locator('.crumb-more')).toHaveCount(0);   // all of it fits
  const full = await crumb.evaluate((node) => Math.ceil([...node.children].reduce((sum, child) => sum + child.getBoundingClientRect().width + 2, 0)));   // the width the whole path needs
  await crumb.evaluate((node, width) => { (node as HTMLElement).style.flex = `0 0 ${width}px`; }, full - 40);
  await expect(crumb.locator('.crumb-more')).toBeVisible();   // early parts folded
  await expect(crumb.locator('.crumb-part.current')).toHaveText('four');
  expect(await crumb.evaluate((node) => node.scrollWidth <= node.clientWidth + 1)).toBe(true);
  await crumb.locator('.crumb-more').click();
  const folded = ((await crumb.locator('.crumb-more').getAttribute('title')) || '').split(' / ');
  expect(folded.length).toBeGreaterThan(0);
  for (const name of folded) await expect(page.locator('#menu')).toContainText(name);   // the folded parts (first the project itself) are in the menu
});
