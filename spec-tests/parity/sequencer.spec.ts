import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// Plan fl-block FL04 to FL08: the sequencer workspace, its playlist and lanes, and the lanes of the built block.
function wav(samples: number[], rate = 44100): Buffer {
  const b = Buffer.alloc(44 + samples.length * 2);
  b.write('RIFF', 0); b.writeInt32LE(36 + samples.length * 2, 4); b.write('WAVEfmt ', 8); b.writeInt32LE(16, 16); b.writeInt16LE(1, 20); b.writeInt16LE(1, 22);
  b.writeInt32LE(rate, 24); b.writeInt32LE(rate * 2, 28); b.writeInt16LE(2, 32); b.writeInt16LE(16, 34); b.write('data', 36); b.writeInt32LE(samples.length * 2, 40);
  samples.forEach((s, i) => b.writeInt16LE(s, 44 + i * 2));
  return b;
}
const tone = (n: number, level = 12000) => wav([...Array(n)].map((_, i) => Math.round(level * Math.exp(-i / 1500) * Math.sin(i / 12))));

const SEQ = 'sequencer 1\nchannel kick drum sample=samples/kick.wav vol=-3\nchannel hat drum sample=samples/hat.wav pan=10\nchannel lead notes wave=square vol=-8\npattern verse bars=2\n  kick x...x...x...x...|x...x...x..xx...\n  hat x.x.x.x.x.x.x.x.\n  lead 1:0:1:60:96 1:1:1:64:96 2:0:2:67:96\nplaylist\n  clip verse track=1 at=1 for=4\n  section Intro at=1 for=2\n';

freshServer({
  song: 'composition "band" { ample 1  tempo 120  master-seed 3 }\nsequencer sq at 1 for 4 {\n  file sequences/a.seq\n  lanes lane1=kick lane2=hat+lead\n}\n',
  files: { 'sequences/a.seq': SEQ },
  prepare: (_cli, folder) => { for (const [n, len] of [['kick', 6000], ['hat', 1500]] as const) { const p = path.join(folder, 'samples', `${n}.wav`); fs.mkdirSync(path.dirname(p), { recursive: true }); fs.writeFileSync(p, tone(len)); } },
});

const seqText = async () => (await api('/api/file?path=sequences/a.seq')).text as string;
const param = async (name: string) => (await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'sq').parameters.find((p: { name: string }) => p.name === name)?.value;

test.beforeEach(async ({ page }) => { await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } }); await openApp(page, { reset: false }); });

async function openWorkspace(page) {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  await page.locator('#open-sequencer').click();
  await expect(page.locator('#sq')).toBeVisible();
  await expect(page.locator('#sq-rack .sqchannel')).toHaveCount(3);
}

test('the built block carries the mix and the lanes it names, and a lane that carries nothing stops what is connected to it', async () => {
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: doc.version, op: { type: 'addBlock', blockType: 'volume', name: 'v1' } } });
  const built = await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  const sq = built.blocks.find((b: { block: string }) => b.block === 'sq');
  expect(sq.status === 'rendered' || sq.status === 'cached').toBe(true);
  const take = await api(`/api/takes/${sq.takeId}`);
  const roles = (take.take.data as { role: string }[]).map((d) => d.role).sort();
  expect(roles).toEqual(['lane1', 'lane2', 'mix']);   // only the lanes that name channels
  const lane1 = await api(`/api/takes/${sq.takeId}/export`, { method: 'POST', body: { name: 'lane1-test', folder: 'renders', format: 'wav', role: 'lane1' } });
  const mix = await api(`/api/takes/${sq.takeId}/export`, { method: 'POST', body: { name: 'mix-test', folder: 'renders', format: 'wav' } });
  expect(lane1.sizeBytes).toBe(mix.sizeBytes);
  const none = await api(`/api/takes/${sq.takeId}/export`, { method: 'POST', body: { name: 'lane5-test', folder: 'renders', format: 'wav', role: 'lane5' } });
  expect(JSON.stringify(none)).toContain('lanes');
});

test('the workspace lists the channels, edits a step, a clip and the lanes, and writes the file on Apply and back', async ({ page }) => {
  const textBefore = await seqText();
  await openWorkspace(page);
  await expect(page.locator('#sq-patterns .sqtab.on')).toHaveText('verse');
  // a step of the hat row: bar 1, step 1 was a rest and becomes a hit
  const cell = page.locator('#sq-patterns .sqgridrow[data-voice="hat"] .seqc[data-bar="0"][data-step="1"]');
  await expect(cell).toHaveAttribute('data-value', '.');
  await cell.click();
  await expect(cell).toHaveAttribute('data-value', 'x');
  await expect(page.locator('#sq .savedot')).toContainText('typing');
  // the playlist: a second clip of the pattern on track 2, then moved by the keyboard
  await page.locator('#sq-playlist .sqtrack[data-track="2"]').click({ position: { x: 5 * 30 + 4, y: 10 } });
  await expect(page.locator('#sq-playlist .sqclip')).toHaveCount(2);
  await page.locator('#sq-playlist .sqclip[data-clip="1"]').focus();
  await page.keyboard.press('ArrowRight');
  // the lanes: the lead goes in lane 3
  await page.locator('#sq-lanes .sqlane[data-lane="3"] .sqchip', { hasText: 'lead' }).click();
  await expect.poll(() => param('lanes')).toBe('lane1=kick lane2=hat+lead lane3=lead');
  // nothing is written to the file until Apply and back
  expect(await seqText()).toBe(textBefore);
  await page.locator('#sq-apply').click();
  await expect(page.locator('#sq')).toBeHidden();
  const text = await seqText();
  expect(text).toContain('  hat xxx.x.x.x.x.x.x.|x.x.x.x.x.x.x.x.');
  expect(text).toContain('  clip verse track=2 at=7 for=2');
  expect(text).toContain('channel lead notes wave=square vol=-8');
  await api('/api/doc/revert', { method: 'POST', body: { path: 'song.amplet' } });
});

test('a clip that was added by mistake is removed with Delete, the button or Ctrl+Z, whatever has the focus', async ({ page }) => {
  await openWorkspace(page);
  const clips = page.locator('#sq-playlist .sqclip');
  const before = await clips.count();
  // a clip added by a click on an empty spot of track 2
  await page.locator('#sq-playlist .sqtrack[data-track="2"]').click({ position: { x: 10 * 30 + 4, y: 10 } });
  await expect(clips).toHaveCount(before + 1);
  // Ctrl+Z takes it back, Ctrl+Y puts it back
  await page.keyboard.press('Control+z');
  await expect(clips).toHaveCount(before);
  await page.keyboard.press('Control+y');
  await expect(clips).toHaveCount(before + 1);
  // the clip is chosen by a click and removed with the button
  await clips.last().click();
  await expect(page.locator('#sq-clip-delete')).toBeEnabled();
  await page.locator('#sq-clip-delete').click();
  await expect(clips).toHaveCount(before);
  await expect(page.locator('#sq-clip-delete')).toBeDisabled();
  // and with the Delete key, without the clip having the focus
  await page.locator('#sq-playlist .sqtrack[data-track="2"]').click({ position: { x: 12 * 30 + 4, y: 10 } });
  await expect(clips).toHaveCount(before + 1);
  await page.locator('body').click({ position: { x: 2, y: 2 } });
  await page.keyboard.press('Delete');
  await expect(clips).toHaveCount(before);
  await page.locator('#sq-undoback').click();
});

test('Undo and back leaves the file as it was, and a pattern can be made, renamed and deleted', async ({ page }) => {
  const before = await seqText();
  await openWorkspace(page);
  await page.locator('#sq-pattern-new').click();
  await page.locator('#dlg-extra input').first().fill('fill');
  await page.locator('#dlg-btns button', { hasText: 'OK' }).click();
  await expect(page.locator('#sq-patterns .sqtab')).toHaveCount(2);
  await expect(page.locator('#sq-patterns .sqtab.on')).toHaveText('fill');
  await page.locator('#sq-pattern-rename').click();
  await page.locator('#dlg-extra input').first().fill('break');
  await page.locator('#dlg-btns button', { hasText: 'OK' }).click();
  await expect(page.locator('#sq-patterns .sqtab.on')).toHaveText('break');
  await page.locator('#sq-pattern-delete').click();
  await expect(page.locator('#sq-patterns .sqtab')).toHaveCount(1);
  await page.locator('#sq-undo').click();
  await expect(page.locator('#sq-patterns .sqtab')).toHaveCount(2);
  await page.locator('#sq-undoback').click();
  await page.locator('#dlg-btns button', { hasText: 'Undo and back' }).click();
  await expect(page.locator('#sq')).toBeHidden();
  expect(await seqText()).toBe(before);
});

test('the piano roll of a notes channel writes the row of the pattern', async ({ page }) => {
  await openWorkspace(page);
  const row = page.locator('#sq-patterns .sqgridrow[data-voice="lead"]');
  await expect(row).toContainText('3 notes');
  await row.locator('.sqlite').click();
  await expect(page.locator('dialog.mls-panel canvas').first()).toBeVisible();
  await page.getByRole('button', { name: 'Close AMPLE Jam' }).click();
  await expect(row).toContainText('3 notes');
});

test('a variation inherits its base, a loop and a marker are written to the file, and guitar and gm channels get their fields', async ({ page }) => {
  await openWorkspace(page);
  await page.locator('#sq-pattern-variation').click();
  await expect(page.locator('#sq-patterns .sqtab.on')).toHaveText('verse-b');
  await expect(page.locator('#sq-from')).toHaveText('variation of verse');
  await expect(page.locator('#sq-patterns .sqgridrow[data-voice="kick"]')).toHaveClass(/inherited/);
  await page.locator('#sq-loop').click();
  await page.locator('dialog[open] input').nth(0).fill('2');
  await page.locator('dialog[open] input').nth(1).fill('2');
  await page.locator('dialog[open] button', { hasText: 'OK' }).click();
  await page.locator('#sq-add-marker').click();
  await page.locator('dialog[open] input').nth(0).fill('Drop');
  await page.locator('dialog[open] input').nth(1).fill('3');
  await page.locator('dialog[open] button', { hasText: 'OK' }).click();
  await page.locator('#sq-add-channel').selectOption('guitar');
  await expect(page.locator('#sq-rack .sqchannel[data-channel="guitar"] select').nth(1)).toBeVisible();   // the voicing
  await page.locator('#sq-add-channel').selectOption('gm');
  await expect(page.locator('#sq-rack .sqchannel[data-channel="gm"]')).toContainText('soundfont');
  await page.locator('#sq-apply').click();
  await expect(page.locator('#sq')).toBeHidden();
  const text = await seqText();
  expect(text).toContain('pattern verse-b bars=2 from=verse');
  expect(text).toContain('  loop at=2 for=2');
  expect(text).toContain('  marker Drop at=3');
  expect(text).toContain('channel guitar guitar');
  expect(text).toContain('channel gm gm');
  await api('/api/doc/revert', { method: 'POST', body: { path: 'song.amplet' } });
});

test('a file with comments is not changed by opening it, and Apply and back keeps the comments', async ({ page }) => {
  const commented = '# the band\n' + SEQ.replace('channel kick', '# the kick\nchannel kick').replace('pattern verse', '# the verse\npattern verse').replace('  hat x.x', '  # hats on the off beat\n  hat x.x');
  await api('/api/file', { method: 'PUT', body: { path: 'sequences/a.seq', text: commented } });
  await openWorkspace(page);
  await expect(page.locator('#sq .savedot')).not.toContainText('typing');
  const cell = page.locator('#sq-patterns .sqgridrow[data-voice="hat"] .seqc[data-bar="0"][data-step="1"]');
  await cell.click();
  await page.locator('#sq-apply').click();
  await expect(page.locator('#sq')).toBeHidden();
  const text = await seqText();
  for (const line of ['# the band', '# the kick', '# the verse', '  # hats on the off beat']) expect(text).toContain(line);
  expect(text).toContain('  hat xxx.x.x.x.x.x.x.');
  await api('/api/file', { method: 'PUT', body: { path: 'sequences/a.seq', text: SEQ } });
  await api('/api/doc/revert', { method: 'POST', body: { path: 'song.amplet' } });
});

test('the transport plays the block, a dragged range or a section repeats, and a change is built while it plays', async ({ page }) => {
  await openWorkspace(page);
  await expect(page.locator('#sq-play')).toBeVisible();
  await expect(page.locator('#sq-stop')).toBeDisabled();
  // drag over bars 2 and 3 of the ruler: that stretch is the repeat range
  const ruler = page.locator('#sq-playlist .sqruler');
  const box = (await ruler.boundingBox())!;
  await page.mouse.move(box.x + 30 + 4, box.y + 6);
  await page.mouse.down();
  await page.mouse.move(box.x + 3 * 30 - 4, box.y + 6, { steps: 4 });
  await page.mouse.up();
  await expect(page.locator('#sq-range')).toContainText('repeat bars 2 to 3');
  await expect(page.locator('#sq-playlist .sqrange')).toHaveCount(1);
  await page.locator('#sq-play').click();
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_LOOP.state().playing), { timeout: 30000 }).toBe(true);
  const loop = await page.evaluate(() => (window as any).AMPLE_LOOP.state().loop);
  expect(loop.end - loop.start).toBeCloseTo(4, 1);   // two bars at 120 BPM
  await expect(page.locator('#sq-playhead')).toBeVisible();
  // a change while it plays is built in the background
  const cell = page.locator('#sq-patterns .sqgridrow[data-voice="hat"] .seqc[data-bar="0"][data-step="1"]');
  await cell.click();
  await expect(page.locator('#sq-status')).not.toHaveText('', { timeout: 20000 });
  await page.locator('#sq-stop').click();
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_LOOP.state().playing)).toBe(false);
  // a section repeats with its own button
  await page.locator('.sqsecloop').first().click();
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_LOOP.state().playing), { timeout: 30000 }).toBe(true);
  await page.locator('#sq-stop').click();
});

test('the looping player makes sound', async ({ page }) => {
  await openWorkspace(page);
  await page.locator('#sq-play').click();
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_LOOP.state().playing), { timeout: 30000 }).toBe(true);
  await page.waitForTimeout(1500);
  expect(await page.evaluate(() => (window as any).AMPLE_LOOP.context())).toBe("audio-element");
  const peak = await page.evaluate(async () => { let m = 0; for (let i = 0; i < 40; i++) { m = Math.max(m, (window as any).AMPLE_LOOP.level()); await new Promise((r) => setTimeout(r, 50)); } return m; });
  console.log('PEAK', peak);
  // the audio that plays is the mix of the block, not one of its lanes
  const take = await page.evaluate(() => (window as any).AMPLE_LOOP.info().take);
  const info = await api(`/api/takes/${encodeURIComponent(take)}`);
  expect((info.take.data as { role: string; dataId: string }[]).find((d) => d.dataId === info.audioDataId)?.role).toBe('mix');
  expect(peak).toBeGreaterThan(0);
});

test('Play pattern repeats the first place of the pattern', async ({ page }) => {
  await openWorkspace(page);
  await page.locator('#sq-pattern-play').click();
  await expect(page.locator('#sq-range')).toContainText('repeat bars 1 to 2');
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_LOOP.state().playing), { timeout: 30000 }).toBe(true);
  // again while it plays, after another range was chosen on the ruler
  const box = (await page.locator('#sq-playlist .sqruler').boundingBox())!;
  await page.mouse.move(box.x + 64, box.y + 6); await page.mouse.down(); await page.mouse.move(box.x + 120, box.y + 6, { steps: 3 }); await page.mouse.up();
  await expect(page.locator('#sq-range')).toContainText('repeat bars 3');
  await page.locator('#sq-pattern-play').click();
  await expect(page.locator('#sq-range')).toContainText('repeat bars 1 to 2');
  await expect.poll(() => page.evaluate(() => { const s = (window as any).AMPLE_LOOP.state(); return s.playing && s.loop.start < 0.01; })).toBe(true);
  await page.locator('#sq-stop').click();
});

test('a channel can be heard in the rack: a sample, and an instrument with its program', async ({ page }) => {
  // the sound bank of the machine is not in the test server: the audition is answered with a short sound
  const requests: Array<Record<string, unknown>> = [];
  await page.route('**/api/gm/audition', async (route) => { requests.push(route.request().postDataJSON()); await route.fulfill({ status: 200, contentType: 'audio/wav', body: wav([...Array(2000)].map((_, i) => Math.round(9000 * Math.sin(i / 8)))) }); });
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  await page.locator('#open-sequencer').click();
  await expect(page.locator('#sq')).toBeVisible();
  await page.locator('#sq-rack .sqhear').first().click();   // the first channel is a drum with a sample
  await expect.poll(() => page.evaluate(async () => (await import('/app/player.js')).playerState().source)).toBe('samples/kick.wav');
  // an instrument channel: the program that is set goes to the audition
  await page.locator('#sq-add-channel').selectOption('gm');
  const gm = page.locator('#sq-rack .sqchannel').last();
  await gm.getByLabel(/^Program/).fill('38');
  await gm.getByLabel(/^Program/).dispatchEvent('change');
  await page.locator('#sq-rack .sqchannel').last().getByRole('button', { name: /^Hear / }).click();
  await expect.poll(() => requests.length).toBeGreaterThan(0);
  expect(requests.at(-1)).toMatchObject({ program: 38, file: 'pack:musescore-general' });
  expect((requests.at(-1)!.notes as Array<{ pitch: number }>)[0].pitch).toBe(36);   // a bass program is tried low
  // the rack keeps its scroll place when something is changed
  await page.addStyleTag({ content: '#sq .sqbody { max-height: 120px; overflow: auto; }' });   // a style sheet, so the workspace is still short when it is drawn again (the rack and the rows are one grid in .sqbody)
  await page.evaluate(() => { (document.querySelector('#sq .sqbody') as HTMLElement).scrollTop = 80; });
  await page.locator('#sq-rack .sqchannel').last().getByLabel(/^Bank/).fill('0');
  await page.locator('#sq-rack .sqchannel').last().getByLabel(/^Bank/).dispatchEvent('change');
  await expect.poll(() => page.evaluate(() => (document.querySelector('#sq .sqbody') as HTMLElement).scrollTop)).toBeGreaterThan(0);
});

test('a sound dropped on a channel replaces its sample, and on the rack it makes a new channel', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  await page.locator('#open-sequencer').click();
  await expect(page.locator('#sq-rack .sqchannel')).toHaveCount(3);
  const drop = (selector: string, path: string) => page.evaluate(async ({ selector, path }) => {
    const { beginFileDrag } = await import('/app/dragfile.js');
    const transfer = new DataTransfer();
    beginFileDrag({ dataTransfer: transfer } as DragEvent, { path, isDir: false });
    const target = document.querySelector(selector)!;
    const over = new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: transfer });
    target.dispatchEvent(over);
    target.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: transfer }));
    return over.defaultPrevented;
  }, { selector, path });
  // on a channel: the hat plays the kick sample now
  expect(await drop('#sq-rack .sqchannel[data-channel="hat"]', 'samples/kick.wav')).toBe(true);
  await expect(page.locator('#sq-rack .sqchannel[data-channel="hat"]')).toContainText('sample: kick.wav');
  // on the rack itself: a new drum channel made of the sound
  expect(await drop('#sq-rack', 'samples/hat.wav')).toBe(true);
  await expect(page.locator('#sq-rack .sqchannel')).toHaveCount(4);
  await expect(page.locator('#sq-rack .sqchannel[data-channel="hat-2"]')).toContainText('sample: hat.wav');
  // a sound on a notes channel is not taken: it needs a folder of samples
  expect(await drop('#sq-rack .sqchannel[data-channel="lead"]', 'samples/hat.wav')).toBe(true);
  await expect(page.locator('#toast')).toContainText('folder');
});

test('the transport says what it does, and Space plays and stops', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  await page.locator('#open-sequencer').click();
  await expect(page.locator('#sq-hint')).toContainText('The whole song repeats');
  await expect(page.locator('#sq-hint')).toContainText('Space plays and stops');
  await page.locator('#sq-repeat').click();
  await expect(page.locator('#sq-hint')).toContainText('plays once');
  await page.locator('#sq-repeat').click();
  const playlist = page.locator('#sq-playlist');
  await playlist.click({ position: { x: 4, y: 4 } });
  await page.keyboard.press('Space');
  await expect.poll(() => page.evaluate(() => (window as unknown as { AMPLE_LOOP: { state: () => { playing: boolean } } }).AMPLE_LOOP.state().playing), { timeout: 30000 }).toBe(true);
  await expect(page.locator('#sq-hint')).toContainText('▶');
  await page.keyboard.press('Space');
  await expect.poll(() => page.evaluate(() => (window as unknown as { AMPLE_LOOP: { state: () => { playing: boolean } } }).AMPLE_LOOP.state().playing)).toBe(false);
});

test('AMPLE Jam opens a notes channel row, writes it back, undoes by itself and switches to Advanced and back (LSQ)', async ({ page }) => {
  await openWorkspace(page);
  const row = page.locator('#sq-patterns .sqgridrow[data-voice="lead"]');
  await expect(row).toContainText('3 notes');
  await row.locator('.sqlite').click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  const count = () => panel.evaluate((d: any) => d.mlsDebug.rows().length as number);
  await expect.poll(count).toBe(3);
  const grid = panel.locator('canvas.mls-grid');
  await expect.poll(() => grid.evaluate((c: HTMLCanvasElement) => c.clientWidth)).toBeGreaterThan(300);
  const box = (await grid.boundingBox())!;
  await page.mouse.click(box.x + box.width - 80, box.y + 30);
  await expect.poll(count).toBe(4);
  await grid.focus();
  await page.keyboard.press('Control+z');
  await expect.poll(count).toBe(3);
  await page.keyboard.press('Control+Shift+z');
  await expect.poll(count).toBe(4);
  await expect.poll(count).toBe(4);
  await panel.getByRole('button', { name: 'Close AMPLE Jam' }).click();
  await expect(row).toContainText('4 notes');
});

test('dragging the AMPLE Jam title moves the panel with a transform that stays after the drop (no relayout on release)', async ({ page }) => {
  await openWorkspace(page);
  await page.locator('#sq-patterns .sqgridrow[data-voice="lead"] .sqlite').click();
  const panel = page.locator('dialog.mls-panel');
  const title = (await panel.locator('.mls-title').boundingBox())!, before = (await panel.boundingBox())!;
  await page.mouse.move(title.x + 200, title.y + 10);
  await page.mouse.down();
  await page.mouse.move(title.x + 260, title.y + 50, { steps: 5 });
  expect(await panel.evaluate((e) => (e as HTMLElement).style.transform)).toContain('translate3d');
  await page.mouse.up();
  const after = (await panel.boundingBox())!;
  expect(Math.round(after.x - before.x)).toBe(60);
  expect(Math.round(after.y - before.y)).toBe(40);
  expect(await panel.evaluate((e) => (e as HTMLElement).style.transform)).toContain('translate3d');
});

test('the AMPLE Jam panel opens wider than the generic 520 px dialog cap', async ({ page }) => {
  await openWorkspace(page);
  await page.locator('#sq-patterns .sqgridrow[data-voice="lead"] .sqlite').click();
  expect((await page.locator('dialog.mls-panel').boundingBox())!.width).toBeGreaterThan(600);
});

test('AMPLE Jam window management: double-click and the minus button roll it up, maximize locks it in full screen and Esc restores size and place', async ({ page }) => {
  await openWorkspace(page);
  await page.locator('#sq-patterns .sqgridrow[data-voice="lead"] .sqlite').click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel.locator('.mls-title strong')).toHaveText('AMPLE Jam');
  const before = (await panel.boundingBox())!;
  await panel.locator('.mls-title').dblclick({ position: { x: 150, y: 8 } });
  await expect(panel).toHaveAttribute('data-window', 'collapsed');
  expect((await panel.boundingBox())!.height).toBeLessThan(60);
  expect((await panel.boundingBox())!.width).toBeLessThan(420);
  const rightGap = await panel.evaluate((p) => p.getBoundingClientRect().right - (p.querySelector('.mls-close') as HTMLElement).getBoundingClientRect().right);
  expect(rightGap).toBeLessThan(20);
  await panel.getByRole('button', { name: 'Expand' }).click();
  expect(await panel.getAttribute('data-window')).toBeNull();
  expect(Math.round((await panel.boundingBox())!.height)).toBe(Math.round(before.height));
  expect(Math.round((await panel.boundingBox())!.width)).toBe(Math.round(before.width));
  await panel.getByRole('button', { name: 'Collapse' }).click();
  await expect(panel).toHaveAttribute('data-window', 'collapsed');
  await expect(panel.getByRole('button', { name: 'Expand' })).toHaveText('▁');
  await panel.getByRole('button', { name: 'Expand' }).click();
  // double-click: the collapsed bar slides under the mouse, so a second double-click in the same place expands it again
  const at = { x: before.x + 150, y: before.y + 14 };
  await page.mouse.dblclick(at.x, at.y);
  await expect(panel).toHaveAttribute('data-window', 'collapsed');
  const hit = await page.evaluate(([x, y]) => { const e = document.elementFromPoint(x, y) as HTMLElement; return { inPanel: !!e.closest('dialog.mls-panel'), button: !!e.closest('button') }; }, [at.x, at.y]);
  expect(hit).toEqual({ inPanel: true, button: false });
  await page.mouse.dblclick(at.x, at.y);
  expect(await panel.getAttribute('data-window')).toBeNull();
  await panel.getByRole('button', { name: 'Maximize' }).click();
  await expect(panel).toHaveAttribute('data-window', 'max');
  await expect(page.locator('.mls-backdrop')).toBeVisible();
  const full = (await panel.boundingBox())!;
  expect(full.width).toBeGreaterThan(before.width - 1);
  expect(Math.round(full.width)).toBe(page.viewportSize()!.width - 16);
  await page.keyboard.press('Escape');
  expect(await panel.getAttribute('data-window')).toBeNull();
  await expect(page.locator('.mls-backdrop')).toHaveCount(0);
  const after = (await panel.boundingBox())!;
  expect([Math.round(after.x), Math.round(after.y), Math.round(after.width), Math.round(after.height)]).toEqual([Math.round(before.x), Math.round(before.y), Math.round(before.width), Math.round(before.height)]);
});

test('a General MIDI program: typed as number or name, or picked from a dark list where hovering plays it and leaving stops it', async ({ page }) => {
  const requests: Array<Record<string, unknown>> = [];
  await page.route('**/api/gm/audition', async (route) => { requests.push(route.request().postDataJSON()); await route.fulfill({ status: 200, contentType: 'audio/wav', body: wav([...Array(2000)].map((_, i) => Math.round(9000 * Math.sin(i / 9)))) }); });
  await openWorkspace(page);
  await page.locator('#sq-add-channel').selectOption('gm');
  const gm = page.locator('#sq-rack .sqchannel').last();
  await expect(gm.locator('.sqgmname')).toContainText('Acoustic Grand Piano (GM 0)');
  const field = gm.getByLabel(/^Program/);
  await field.click();
  await expect(page.locator('.gm-picker .gm-row')).toHaveCount(128);
  await field.fill('cello');
  await expect(page.locator('.gm-picker .gm-row:not([hidden])')).toHaveCount(1);
  await field.fill('');
  const cello = page.locator('.gm-picker .gm-row[data-program="42"]');
  await cello.hover();
  await expect(cello).toHaveClass(/playing/);
  await expect.poll(() => requests.at(-1)?.program).toBe(42);
  await page.mouse.move(5, 5);
  await expect(cello).not.toHaveClass(/playing/);
  await cello.click();
  await expect(page.locator('.gm-picker')).toHaveCount(0);
  await expect(page.locator('#sq-rack .sqchannel').last().locator('.sqgmname')).toContainText('Cello (GM 42)');
  await expect(page.locator('#sq-rack .sqchannel').last().getByLabel(/^Program/)).toHaveValue('42');
  await page.locator('#sq-rack .sqchannel').last().getByLabel(/^Program/).fill('trumpet');
  await page.locator('#sq-rack .sqchannel').last().getByLabel(/^Program/).dispatchEvent('change');
  await expect(page.locator('#sq-rack .sqchannel').last().locator('.sqgmname')).toContainText('Trumpet (GM 56)');
});

test('the properties of a sequencer block read Actions, then Parameters, then Management (build, source, delete)', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  const order = await page.evaluate(() => {
    const heads = [...document.querySelectorAll('#props h4, #props .prop-actions button, #props #open-sequencer')].map((e) => e.tagName === 'H4' ? (e.textContent || '').trim() : `[${(e.textContent || '').trim()}]`);
    return heads;
  });
  const at = (text: string) => order.indexOf(text);
  expect(at('Actions')).toBeGreaterThanOrEqual(0);
  expect(at('[Open the sequencer]')).toBeGreaterThan(at('Actions'));
  expect(at('Parameters')).toBeGreaterThan(at('[Open the sequencer]'));
  expect(at('Management')).toBeGreaterThan(at('Parameters'));
  expect(at('[Build this block]')).toBeGreaterThan(at('Management'));
});

test('the Info tab of a block links to the manual page of its block type', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  await page.getByRole('tab', { name: 'Info', exact: true }).click();
  const link = page.getByRole('link', { name: 'Open the manual page of this block' });
  await expect(link).toHaveAttribute('href', '/help#template-sequencer');
  await expect(link).toHaveAttribute('target', '_blank');
});

test('Source of a block puts the caret on that block even the first time the source view opens', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  await page.locator('.prop-actions').getByRole('button', { name: 'Source', exact: true }).click();
  await expect.poll(() => page.evaluate(() => {
    const ta = document.querySelector('#se-ta') as HTMLTextAreaElement | null;
    if (!ta) return '';
    const line = ta.value.slice(0, ta.selectionStart).split('\n').length - 1;
    return ta.value.split('\n')[line];
  })).toContain('sequencer sq');
});

test('text typed in the source editor while the song changed elsewhere: Keep both merges, nothing is dropped silently', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  await page.locator('.prop-actions').getByRole('button', { name: 'Source', exact: true }).click();
  await expect.poll(() => page.evaluate(() => !!document.querySelector('#se-ta'))).toBe(true);
  // type a line at the end of the song (not applied), then change the song elsewhere (as a block editor or the assistant would)
  await page.evaluate(() => { const ta = document.querySelector('#se-ta') as HTMLTextAreaElement; ta.value = ta.value.replace(/\n*$/, '\n') + '# typed note\n'; ta.dispatchEvent(new Event('input', { bubbles: true })); });
  await page.evaluate(async () => { const store = await import('/app/store.js'); await store.edit([{ type: 'setParameter', block: 'sq', name: 'channels', value: 'auto' }]); });
  await page.evaluate(async () => { (window as any).__applied = (await import('/app/source.js')).apply(); });
  const dialog = page.locator('dialog.typed-conflict-dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog.locator('.tc-status.good')).toBeVisible();
  await dialog.getByRole('button', { name: 'Keep both' }).click();
  await expect.poll(() => page.evaluate(async () => (await import('/app/store.js')).S.doc.text)).toContain('# typed note');
  const text = await page.evaluate(async () => (await import('/app/store.js')).S.doc.text as string);
  expect(text).toContain('channels auto');
});

test('AMPLE Jam: a double-click far to the right of the title bar still leaves the mouse on the rolled-up bar, and maximized keeps the window buttons at the right', async ({ page }) => {
  await openWorkspace(page);
  await page.locator('#sq-patterns .sqgridrow[data-voice="lead"] .sqlite').click();
  const panel = page.locator('dialog.mls-panel');
  const before = (await panel.boundingBox())!;
  const at = { x: before.x + before.width - 160, y: before.y + 14 };
  await page.mouse.dblclick(at.x, at.y);
  await expect(panel).toHaveAttribute('data-window', 'collapsed');
  const hit = await page.evaluate(([x, y]) => { const e = document.elementFromPoint(x, y) as HTMLElement; return { inPanel: !!e.closest('dialog.mls-panel'), button: !!e.closest('button') }; }, [at.x, at.y]);
  expect(hit).toEqual({ inPanel: true, button: false });
  await page.mouse.dblclick(at.x, at.y);
  expect(await panel.getAttribute('data-window')).toBeNull();
  await panel.getByRole('button', { name: 'Maximize' }).click();
  const gap = await panel.evaluate((p) => p.getBoundingClientRect().right - (p.querySelector('.mls-close') as HTMLElement).getBoundingClientRect().right);
  expect(gap).toBeLessThan(20);
  const maxRight = await panel.evaluate((p) => (p.querySelector('.mls-maximize') as HTMLElement).getBoundingClientRect().left - p.getBoundingClientRect().left);
  expect(maxRight).toBeGreaterThan(300);
  await page.keyboard.press('Escape');
});

test('AMPLE Jam opens in the middle of the screen and the next ones step down and to the right', async ({ page }) => {
  await openWorkspace(page);
  const open = (id: string) => page.evaluate(async (blockId) => { (await import('/app/pianoroll-lite.js')).openPianoRollLite(blockId, { pseudo: { id: blockId, title: blockId, params: {}, flags: [], tpl: 'midi-clip', parts: [] }, text: '', ppq: 1_000_000, write: async () => true }); }, id);
  await open('c1');
  const first = (await page.locator('dialog.mls-panel').first().boundingBox())!, view = page.viewportSize()!;
  expect(Math.abs(first.x + first.width / 2 - view.width / 2)).toBeLessThan(40);
  expect(Math.abs(first.y + first.height / 2 - view.height / 2)).toBeLessThan(60);
  await open('c2');
  await open('c3');
  const boxes = await page.locator('dialog.mls-panel').evaluateAll((l) => l.map((e) => { const r = e.getBoundingClientRect(); return [Math.round(r.left), Math.round(r.top)]; }));
  expect(boxes.length).toBe(3);
  expect(boxes[1][0]).toBeGreaterThan(boxes[0][0]);
  expect(boxes[1][1]).toBeGreaterThan(boxes[0][1]);
  expect(boxes[2][0]).toBeGreaterThan(boxes[1][0]);
});

test('a click or a drag in an AMPLE Jam window brings it to the front', async ({ page }) => {
  await openWorkspace(page);
  const open = (id: string) => page.evaluate(async (blockId) => { (await import('/app/pianoroll-lite.js')).openPianoRollLite(blockId, { pseudo: { id: blockId, title: blockId, params: {}, flags: [], tpl: 'midi-clip', parts: [] }, text: '', ppq: 1_000_000, write: async () => true }); }, id);
  await open('w1');
  await open('w2');
  const z = () => page.locator('dialog.mls-panel').evaluateAll((l) => l.map((e) => Number(getComputedStyle(e).zIndex)));
  let zs = await z();
  expect(zs[1]).toBeGreaterThan(zs[0]);
  const title = (await page.locator('dialog.mls-panel').first().locator('.mls-title').boundingBox())!;
  await page.mouse.move(title.x + 120, title.y + 12);
  await page.mouse.down();
  await page.mouse.move(title.x + 130, title.y + 12);
  await page.mouse.up();
  zs = await z();
  expect(zs[0]).toBeGreaterThan(zs[1]);
});

test('AMPLE Jam opens where the last one was in this session (nothing is stored)', async ({ page }) => {
  await openWorkspace(page);
  const open = (id: string) => page.evaluate(async (blockId) => { (await import('/app/pianoroll-lite.js')).openPianoRollLite(blockId, { pseudo: { id: blockId, title: blockId, params: {}, flags: [], tpl: 'midi-clip', parts: [] }, text: '', ppq: 1_000_000, write: async () => true }); }, id);
  await open('s1');
  const panel = page.locator('dialog.mls-panel');
  const title = (await panel.locator('.mls-title').boundingBox())!;
  await page.mouse.move(title.x + 300, title.y + 12); await page.mouse.down(); await page.mouse.move(title.x + 360, title.y + 72, { steps: 5 }); await page.mouse.up();
  const moved = (await panel.boundingBox())!;
  await panel.getByRole('button', { name: 'Close AMPLE Jam' }).click();
  await expect(panel).toHaveCount(0);
  await open('s2');
  const again = (await panel.boundingBox())!;
  expect(Math.abs(again.x - moved.x)).toBeLessThan(3);
  expect(Math.abs(again.y - moved.y)).toBeLessThan(3);
});
