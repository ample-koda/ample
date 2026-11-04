import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect, Page } from '@playwright/test';
import { api, chooseMenu, freshServer, openApp } from '../../web/tests/support';

// The controls on the blocks that cut and fit audio and the mixer lanes (a server of their own, apart from the rendering specs in audio-cut.spec.ts).
function wav(samples: number[], rate = 44100): Buffer {
  const b = Buffer.alloc(44 + samples.length * 2);
  b.write('RIFF', 0); b.writeInt32LE(36 + samples.length * 2, 4); b.write('WAVEfmt ', 8); b.writeInt32LE(16, 16); b.writeInt16LE(1, 20); b.writeInt16LE(1, 22);
  b.writeInt32LE(rate, 24); b.writeInt32LE(rate * 2, 28); b.writeInt16LE(2, 32); b.writeInt16LE(16, 34); b.write('data', 36); b.writeInt32LE(samples.length * 2, 40);
  samples.forEach((s, i) => b.writeInt16LE(s, 44 + i * 2));
  return b;
}
// one second: the first half is silent, the second half is a tone
const SOURCE = [...Array(44100)].map((_, i) => (i < 22050 ? 0 : Math.round(12000 * Math.sin(i / 10))));

const song = (extra: string) => `composition "cut" { ample 1  tempo 240  master-seed 3 }\nimport-audio src at 1 for 1 {\n  file samples/a.wav\n${extra}}\n`;

freshServer({
  song: song(''),
  prepare: (_cli, folder) => { const p = path.join(folder, 'samples', 'a.wav'); fs.mkdirSync(path.dirname(p), { recursive: true }); fs.writeFileSync(p, wav(SOURCE)); },
});

const setSong = async (page: Page, text: string) => { await openApp(page, { reset: false }); await page.evaluate(async (t) => { const store = await import('/app/store.js'); await store.applyText(t); }, text); };

test('a mixer grows lanes when its grip is dragged down', async ({ page }) => {
  await setSong(page, `${song('')}mixer mix {\n  from src.audio -> audio6\n}\n`);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const block = page.locator('.node[data-id="mix"]');
  // six lanes are in use; growth must retain those input lanes.
  const inputs = () => page.evaluate(async () => { const store = await import('/app/store.js'); return store.flat().nodes.find((n: { id: string }) => n.id === 'mix')!.ins.length; });
  expect(await inputs()).toBe(6);
  await block.hover();
  const box = (await block.locator('.size-grip').boundingBox())!;
  await page.mouse.move(box.x + 7, box.y + 7); await page.mouse.down(); await page.mouse.move(box.x + 7, box.y + 7 + 160, { steps: 24 });
  await page.mouse.up();
  await expect.poll(inputs).toBeGreaterThan(6);
  await expect(block).not.toHaveClass(/resizing/);
});

test('while the grip of a mixer is dragged the new lanes show at once', async ({ page }) => {
  await setSong(page, `${song('')}mixer mix {\n  from src.audio -> audio1\n}\n`);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const block = page.locator('.node[data-id="mix"]');
  await block.hover();
  const box = (await block.locator('.size-grip').boundingBox())!;
  const before = (await block.boundingBox())!.height;
  await page.mouse.move(box.x + 7, box.y + 7); await page.mouse.down(); await page.mouse.move(box.x + 7, box.y + 7 + 160, { steps: 24 });
  await expect(block.locator('.ghost-lane').first()).toBeVisible();
  expect((await block.boundingBox())!.height).toBeGreaterThan(before + 20);
  await page.mouse.up();
  await expect(block.locator('.ghost-lane')).toHaveCount(0);
});

test('the cut block has the same part chooser, on the waveform of its input', async ({ page }) => {
  await setSong(page, `${song('')}cut piece at 1 for 1 {\n  from src.audio -> audio\n  first 0.9\n}\n`);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.locator('.node[data-id="piece"] .studio-part-button').click();
  const dialog = page.locator('dialog.clipper');
  await expect(dialog).toBeVisible({ timeout: 30000 });
  await dialog.getByLabel('Start in seconds').fill('0.5');
  await dialog.getByLabel('Start in seconds').dispatchEvent('change');
  await dialog.getByRole('button', { name: 'Use this part' }).click();
  await expect.poll(async () => JSON.stringify((await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'piece').parameters)).toMatch(/"start","value":"0.5"/);
  await expect.poll(async () => ((await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'piece').parameters as { name: string; value: string }[]).find((p) => p.name === 'first')?.value).toBe('0');   // first would have won over the chosen part
});

test('an import-audio block can be made as long as its audio', async ({ page }) => {
  // the file lasts 1 s; at 240 BPM a bar is 1 s, so make the block longer first and then fit it back
  await setSong(page, `composition "cut" { ample 1  tempo 60  master-seed 3 }\nimport-audio src at 1 for 1 {\n  file samples/a.wav\n}\n`);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.locator('.node[data-id="src"]').click({ button: 'right', position: { x: 20, y: 36 } });
  await chooseMenu(page, 'Block tools', 'Make the block as long as the audio');
  // 1 s at 60 BPM is a quarter of a bar: one bar is the least a block can be
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'src').parts[0].length).toBe(1);
  await setSong(page, `composition "cut" { ample 1  tempo 240  master-seed 3 }\nimport-audio src at 1 for 5 {\n  file samples/a.wav\n  start 0\n}\n`);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.locator('.node[data-id="src"]').click({ button: 'right', position: { x: 20, y: 36 } });
  await chooseMenu(page, 'Block tools', 'Make the block as long as the audio');
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'src').parts[0].length).toBe(1);   // 1 s at 240 BPM: exactly one bar
});

test('Delete refuses a block that is still read, Ctrl+Delete takes its links away and removes it', async ({ page }) => {
  await setSong(page, `${song('')}volume vol at 1 for 1 {\n  from src.audio -> audio\n}\n`);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const names = async () => (await api('/api/doc?path=song.amplet')).blocks.map((b: { name: string }) => b.name);
  await page.evaluate(async () => { const store = await import('/app/store.js'); store.S.selSet.add('src'); store.notify(); });
  await page.locator('.node[data-id="src"]').click({ position: { x: 20, y: 36 } });
  await page.keyboard.press('Delete');
  await page.waitForTimeout(600);
  expect(await names()).toContain('src');   // the volume block still reads it
  await page.keyboard.press('Control+Delete');
  await expect.poll(names).not.toContain('src');
  expect(await names()).toContain('vol');
  const vol = (await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'vol');
  expect(vol.froms).toEqual([]);
});

test('Build this builds with the value that was just typed, at the first click', async ({ page }) => {
  await setSong(page, `${song('')}pitch up at 1 for 1 {\n  from src.audio -> audio\n  semitones 0\n}\n`);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.locator('.node[data-id="up"]').click({ position: { x: 20, y: 36 } });
  await page.locator('button, [role=tab]', { hasText: /^Parameters$/ }).first().click().catch(() => {});
  const semitones = page.locator('.prop', { hasText: /semitones/ }).getByRole('spinbutton');
  await semitones.fill('2.5');
  await page.getByRole('button', { name: 'Build this' }).click();   // no blur first, no second click
  await expect.poll(() => page.evaluate(async () => { const s = await import('/app/store.js'); return [s.S.doc.text.includes('semitones 2.5'), !!s.S.build.takes?.up]; })).toEqual([true, true]);
});

test('a volume block is small, and its faders open in a window', async ({ page }) => {
  await setSong(page, `${song('')}volume vol at 1 for 1 {\n  from src.audio -> audio\n  left_gain -6 dB\n}\n`);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const block = page.locator('.node[data-id="vol"]');
  expect((await block.boundingBox())!.height).toBeLessThan(260);
  await expect(block.locator('.volume-summary')).toContainText('L -6 dB');
  await expect(block.locator('.volume-fader, input[type=range]')).toHaveCount(0);   // the faders are not on the block
  await block.getByRole('button', { name: /^Open the faders of/ }).click();
  const pop = block.locator('.volume-pop');
  await expect(pop).toBeVisible();
  await expect(pop.getByRole('button', { name: /Live preview/ })).toBeVisible();
  const size = (await block.boundingBox())!.height;
  await pop.getByText('Mute L').click();   // using the faders does not close them
  await expect(pop).toBeVisible();
  await expect.poll(async () => (await block.boundingBox())?.height).toBe(size);   // the faders hang over what is below: the block itself keeps its size
  await page.mouse.click(40, 40);   // somewhere else on the canvas
  await expect(pop).toHaveCount(0);
});

test('points of an automation curve can be dragged and removed with a right click', async ({ page }) => {
  await setSong(page, song(''));
  await page.evaluate(() => { (window as unknown as { editorResult: Promise<string | undefined> }).editorResult = import('/app/automation-editor.js').then((m) => m.openAutomationEditor({ type: 'volume', name: 'gain', title: 'volume gain', text: 'auto(1:-60, 5:0, 9:-12)', bars: 16, tempo: 100 })); });
  const dots = page.locator('#dlg circle.auto-dot');
  await expect(dots).toHaveCount(3);
  const readout = () => page.locator('#dlg .auto-source').textContent();
  const before = await readout();
  const box = (await dots.nth(1).boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down(); await page.mouse.move(box.x + box.width / 2 + 60, box.y + box.height / 2 + 30, { steps: 6 }); await page.mouse.up();
  expect(await readout()).not.toBe(before);   // the point followed the pointer all the way
  await expect(dots).toHaveCount(3);
  await dots.nth(0).click({ button: 'right' });
  await page.locator('#menu div', { hasText: /^Delete point$/ }).click();
  await expect(dots).toHaveCount(2);
  await page.locator('#dlg-btns').getByRole('button', { name: /Cancel/ }).click();
});

test('the faders of a volume block stay open while the canvas is zoomed with the wheel', async ({ page }) => {
  await setSong(page, `${song('')}volume vol at 1 for 1 {\n  from src.audio -> audio\n}\n`);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const block = page.locator('.node[data-id="vol"]');
  await block.getByRole('button', { name: /^Open the faders of/ }).click();
  const pop = block.locator('.volume-pop');
  await expect(pop).toBeVisible();
  const before = (await pop.boundingBox())!.width;
  const box = (await block.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + 10);
  await page.mouse.wheel(0, -300);
  await page.waitForTimeout(400);
  await expect(pop).toBeVisible();
  expect((await pop.boundingBox())!.width).toBeGreaterThan(before);   // zoomed in with the faders open
  // with the pointer on the faders themselves
  const faders = (await pop.boundingBox())!;
  await page.mouse.move(faders.x + faders.width / 2, faders.y + faders.height / 2);
  await page.mouse.wheel(0, -200);
  await page.waitForTimeout(400);
  await expect(pop).toBeVisible();
  await page.mouse.wheel(0, 200);
  await page.waitForTimeout(400);
  await expect(pop).toBeVisible();
});

test('the faders stay open while the canvas is panned, and close on a click elsewhere', async ({ page }) => {
  await setSong(page, `${song('')}volume vol at 1 for 1 {\n  from src.audio -> audio\n}\n`);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const block = page.locator('.node[data-id="vol"]');
  await block.getByRole('button', { name: /^Open the faders of/ }).click();
  const pop = block.locator('.volume-pop');
  await expect(pop).toBeVisible();
  await page.mouse.move(60, 400); await page.mouse.down(); await page.mouse.move(160, 440, { steps: 6 }); await page.mouse.up();   // pan the canvas by dragging the background
  await expect(pop).toBeVisible();
  await page.mouse.click(60, 400);
  await expect(pop).toHaveCount(0);
});
