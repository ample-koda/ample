import { test, expect } from '@playwright/test';
import { api, chooseMenu, emptySpot, freshServerPerTest, node, openApp } from '../../web/tests/support';

freshServerPerTest();
test.beforeEach(async ({ page }) => { await openApp(page); });
const midiHex = '4D546864000000060000000101E04D54726B0000000D00903C648360803C0000FF2F00';

test('dropping a generated MIDI file at song root imports a selected MIDI block', async ({ page }) => {
  await page.route('**/api/midi/upload**', async route => { await new Promise(resolve => setTimeout(resolve, 250)); await route.continue(); });
  await page.locator('#cv').evaluate((canvas, encoded) => {
    const bytes = Uint8Array.from((encoded as string).match(/../g)!, pair => Number.parseInt(pair, 16));
    const file = new File([bytes], 'generated.mid', { type: 'audio/midi' });
    const data = new DataTransfer(); data.items.add(file);
    canvas.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, clientX: 300, clientY: 220, dataTransfer: data }));
  }, midiHex);

  const dialog = page.locator('#dlg');
  await expect(dialog).toBeVisible();
  await dialog.locator('select').selectOption('single');
  await dialog.getByRole('button', { name: 'Import' }).click();
  const pending = page.locator('.node.midi-pending');
  await expect(pending).toBeVisible();
  await expect.poll(() => pending.evaluate(element => getComputedStyle(element).pointerEvents)).toBe('none');
  await expect(node(page, 'generated')).toBeVisible();
  const projectFiles = await api('/api/project/files');
  expect(projectFiles.map((file: { path: string }) => file.path).some(path => /^assets\/midi\/[a-f0-9]{64}\.mid$/.test(path))).toBe(true);
  expect((await api('/api/doc?path=song.amplet')).text).toContain('track 0');
});

test('dropping MIDI inside an opened module is refused before upload', async ({ page }) => {
  const uploaded = await page.evaluate(async encoded => {
    const bytes = Uint8Array.from((encoded as string).match(/../g)!, pair => Number.parseInt(pair, 16));
    const response = await fetch('/api/midi/upload?extension=.mid&track=all', { method: 'POST', headers: { 'X-Ample-Token': sessionStorage.getItem('ample.token')!, 'Content-Type': 'application/octet-stream' }, body: bytes });
    return response.json();
  }, midiHex);
  await api('/api/midi/module', { method: 'POST', body: { file: uploaded.path, name: 'nested' } });
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', text: `${doc.text}\nuse nested@1.0 as imported {\n}\n`, baseVersion: doc.version } });
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  await node(page, 'imported').dblclick();
  await expect(page.locator('#crumbs span.c')).toHaveCount(2);
  let uploads = 0;
  await page.route('**/api/midi/upload**', async route => { uploads++; await route.continue(); });
  await page.locator('#cv').evaluate((canvas, encoded) => {
    const bytes = Uint8Array.from((encoded as string).match(/../g)!, pair => Number.parseInt(pair, 16));
    const data = new DataTransfer(); data.items.add(new File([bytes], 'nested.mid', { type: 'audio/midi' }));
    canvas.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, clientX: 300, clientY: 220, dataTransfer: data }));
  }, midiHex);
  await expect(page.locator('#toast')).toContainText('song root level');
  expect(uploads).toBe(0);
  await expect(page.locator('#dlg')).toHaveJSProperty('open', false);
});

test('MIDI comparison uses one dialog with recent build take suggestions', async ({ page }) => {
  const uploaded = await page.evaluate(async encoded => {
    const bytes = Uint8Array.from((encoded as string).match(/../g)!, pair => Number.parseInt(pair, 16));
    const response = await fetch('/api/midi/upload?extension=.mid&track=all', { method: 'POST', headers: { 'X-Ample-Token': sessionStorage.getItem('ample.token')!, 'Content-Type': 'application/octet-stream' }, body: bytes });
    return response.json();
  }, midiHex);
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', text: `${doc.text}\nmidi source {\n  file "${uploaded.path}"\n}\n`, baseVersion: doc.version } });
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  await page.waitForTimeout(1000);   // the dry-run status of the song has arrived; the fake takes below come after it
  await page.evaluate(() => Object.assign((window as any).AMPLE_STATE.build.takes, { source: 'a'.repeat(32), convertedMix: 'b'.repeat(32), convertedClip: 'c'.repeat(32) }));
  await node(page, 'source').click({ button: 'right' });
  await chooseMenu(page, 'Block tools', 'Compare A/B...');
  const dialog = page.locator('#dlg');
  await expect(dialog).toHaveJSProperty('open', true);
  await expect(dialog.locator('#dlg-t')).toHaveText('Compare MIDI playback');
  await expect(dialog.locator('#dlg-extra datalist option')).toHaveCount(3);
  await expect(dialog.locator('#dlg-extra input[type="text"]')).toHaveCount(4);
  await expect(dialog.locator('#dlg-extra input[type="number"]')).toHaveValue('0');
  await dialog.getByRole('button', { name: 'Cancel' }).click();
});

test('MIDI conversion name conflicts are rejected before creating another module', async ({ page }) => {
  const uploaded = await page.evaluate(async encoded => {
    const bytes = Uint8Array.from((encoded as string).match(/../g)!, pair => Number.parseInt(pair, 16));
    const response = await fetch('/api/midi/upload?extension=.mid&track=all', { method: 'POST', headers: { 'X-Ample-Token': sessionStorage.getItem('ample.token')!, 'Content-Type': 'application/octet-stream' }, body: bytes });
    return response.json();
  }, midiHex);
  await api('/api/midi/module', { method: 'POST', body: { file: uploaded.path, name: 'existing-midi' } });
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', text: `${doc.text}\nmidi source {\n  file "${uploaded.path}"\n}\n`, baseVersion: doc.version } });
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  let conversionCalls = 0;
  await page.route('**/api/midi/module', async route => { conversionCalls++; await route.continue(); });
  await node(page, 'source').click({ button: 'right' });
  await chooseMenu(page, 'Block tools', 'Convert to song...');
  const dialog = page.locator('#dlg');
  await dialog.locator('#dlg-extra input').nth(0).fill('existing-midi');
  await dialog.getByRole('button', { name: 'Current song' }).click();
  await expect(page.locator('#toast')).toContainText('already exists');
  expect(conversionCalls).toBe(0);
  const paths = (await api('/api/project/files')).map((file: { path: string }) => file.path);
  expect(paths.filter((path: string) => path === 'modules/existing-midi.module')).toHaveLength(1);
  expect((await api('/api/doc?path=song.amplet')).text).toContain('midi source');
});

test('project MIDI picker shows bounded file metadata and track controls', async ({ page }) => {
  await page.evaluate(async encoded => {
    const bytes = Uint8Array.from((encoded as string).match(/../g)!, pair => Number.parseInt(pair, 16));
    await fetch('/api/midi/upload?extension=.mid&track=all', { method: 'POST', headers: { 'X-Ample-Token': sessionStorage.getItem('ample.token')!, 'Content-Type': 'application/octet-stream' }, body: bytes });
  }, midiHex);
  const spot = await emptySpot(page);
  await page.mouse.click(spot.x, spot.y, { button: 'right' });
  await page.locator('#menu').getByText('Import MIDI file...').click();
  const dialog = page.locator('#dlg');
  await expect(dialog).toHaveJSProperty('open', true);
  await expect(dialog.locator('#dlg-extra p')).toContainText('1 track ·');
  await expect(dialog.locator('#dlg-extra p')).toContainText('120 BPM initial tempo');
  await expect(dialog.locator('#dlg-extra p')).toContainText('1 bar ·');
  await expect(dialog.locator('#dlg-extra select')).toHaveCount(2);
  await dialog.locator('#dlg-extra select').nth(1).selectOption('single');
  await expect(dialog.locator('#dlg-extra input[type="number"]')).toHaveAttribute('max', '0');
  await dialog.getByRole('button', { name: 'Cancel' }).click();
  await expect(node(page, 'midi')).toHaveCount(0);
});
