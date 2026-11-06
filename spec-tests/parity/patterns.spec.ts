import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// Plan patterns P03 and P04: the chooser, the section strip and the Patterns view of a drums block.
function wav(samples: number[], rate = 44100): Buffer {
  const b = Buffer.alloc(44 + samples.length * 2);
  b.write('RIFF', 0); b.writeInt32LE(36 + samples.length * 2, 4); b.write('WAVEfmt ', 8); b.writeInt32LE(16, 16); b.writeInt16LE(1, 20); b.writeInt16LE(1, 22);
  b.writeInt32LE(rate, 24); b.writeInt32LE(rate * 2, 28); b.writeInt16LE(2, 32); b.writeInt16LE(16, 34); b.write('data', 36); b.writeInt32LE(samples.length * 2, 40);
  samples.forEach((s, i) => b.writeInt16LE(s, 44 + i * 2));
  return b;
}
const tone = (n: number) => wav([...Array(n)].map((_, i) => Math.round(6000 * Math.exp(-i / 800) * Math.sin(i / 9))));

freshServer({
  song: 'composition "band" { ample 1  tempo 100  master-seed 3 }\ndrums kit at 1 for 4 {\n  kit samples/kit\n  pattern kick:x...x...x...x... snare:....x.......x...\n}\n',
  prepare: (_cli, folder) => { for (const v of ['kick', 'snare', 'hat']) { const p = path.join(folder, 'samples/kit', v + '.wav'); fs.mkdirSync(path.dirname(p), { recursive: true }); fs.writeFileSync(p, tone(2000)); } },
});

const param = async (name: string) => (await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'kit').parameters.find((p: { name: string }) => p.name === name)?.value;
const dialogInput = (page) => page.locator('#dlg-extra input').first();

test.beforeEach(async ({ page }) => { await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } }); await openApp(page, { reset: false }); });

test('a new pattern, a section song and a changed grid are written as text and the block builds with them', async ({ page }) => {
  await node(page, 'kit').locator('.nh .t').click();
  await page.locator('#seq-pattern-new').click();
  await dialogInput(page).fill('fill');
  await page.locator('#dlg-btns button', { hasText: 'OK' }).click();
  await expect.poll(() => param('patterns')).toBe('fill=kick:................');
  await expect(page.locator('#seq-pattern')).toHaveValue('fill');
  // the grid now edits the fill: a click on the first kick step writes into patterns, the plain pattern is not touched
  await page.locator('#seq-drums [data-voice="kick"] .seqc[data-step="0"]').click();
  await expect.poll(() => param('patterns')).toBe('fill=kick:x...............');
  expect(await param('pattern')).toBe('kick:x...x...x...x... snare:....x.......x...');
  // sections: pattern for two bars, then the fill
  await page.locator('#seq-song .sectl-add').click();
  await page.locator('#menu').getByText('main', { exact: true }).click();
  await expect.poll(() => param('song')).toBe('pattern');
  await expect(page.locator('#seq-song .sectl-block')).toHaveCount(1);
  await page.locator('#seq-song .sectl-block').first().focus();
  await page.keyboard.press('+');
  await expect.poll(() => param('song')).toBe('pattern*2');
  await expect(page.locator('#seq-song .sectl-bars').first()).toHaveText('2');
  await page.locator('#seq-song .sectl-add').click();
  await page.locator('#menu').getByText('fill', { exact: true }).click();
  await expect.poll(() => param('song')).toBe('pattern*2 fill');
  await expect(page.locator('#seq-song .sectl-block')).toHaveCount(2);
  await page.click('#btn-build');
  await expect(page.locator('#btxt')).toHaveText('idle');
  const status = await page.evaluate(() => (window as any).AMPLE_STATE.build.status.kit.status);
  expect(['rendered', 'cached']).toContain(status);   // it builds; a take of the same plan may already be there
  // rename follows the song, delete takes the sections away
  await page.locator('#seq-pattern').selectOption('fill');
  await page.locator('#seq-pattern-rename').click();
  await dialogInput(page).fill('chorus');
  await page.locator('#dlg-btns button', { hasText: 'OK' }).click();
  await expect.poll(() => param('song')).toBe('pattern*2 chorus');
  await page.locator('#seq-pattern-delete').click();
  await expect.poll(async () => String(await param('song'))).toBe('pattern*2');
});

test('the Patterns view shows the same grid, chooser and sections, and a sample dropped on a lane sets the map', async ({ page }) => {
  await node(page, 'kit').locator('.nh .t').click();
  await page.locator('#seq-open-view').click();
  await expect(page.locator('#pt')).toBeVisible();
  await expect(page.locator('#pt-grid #seq-drums')).toBeVisible();
  await expect(page.locator('#pt-grid #seq-pattern')).toBeVisible();
  const out = await page.evaluate(async () => {
    const { beginFileDrag, endFileDrag } = await import('/app/dragfile.js');
    const row = document.querySelector('#pt-grid .seqrow[data-voice="snare"]') as HTMLElement;
    const dt = new DataTransfer();
    beginFileDrag({ dataTransfer: dt }, { path: 'samples/kit/hat.wav', isDir: false });
    row.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: dt }));
    row.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt }));
    endFileDrag();
    return true;
  });
  expect(out).toBe(true);
  await expect.poll(() => param('map')).toBe('snare:samples/kit/hat');
  await page.click('#pt-back');
  await expect(page.locator('#pt')).toBeHidden();
});

// Plan patterns P07: the sections as a small timeline; the edge changes the bars, a drag moves a block, the menu and the keyboard do the rest, and the text stays the source.
test('the section timeline: drag an edge to change the bars, drag a block to move it, remove and duplicate from the keyboard and the menu @quarantine', async ({ page }) => {
  await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', text: 'composition "band" { ample 1  tempo 100  master-seed 3 }\ndrums kit at 1 for 8 {\n  kit samples/kit\n  pattern kick:x...x...x...x... snare:....x.......x...\n  patterns fill=kick:xxxxxxxxxxxxxxxx / verse=kick:x...x...x...x...\n  song verse*3 fill verse*2\n}\n', baseVersion: (await api('/api/doc?path=song.amplet')).version } });
  await page.reload(); await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'kit').locator('.nh .t').click();
  const blocks = page.locator('#seq-song .sectl-block');
  await expect(blocks).toHaveCount(3);
  await expect(blocks.nth(0)).toContainText('verse');
  // the edge of the first block, three bars wide, dragged two bars to the right
  const b0 = (await blocks.nth(0).boundingBox())!, px = b0.width / 3;
  await page.mouse.move(b0.x + b0.width - 3, b0.y + b0.height / 2); await page.mouse.down(); await page.mouse.move(b0.x + b0.width - 3 + px * 2, b0.y + b0.height / 2, { steps: 6 }); await page.mouse.up();
  await expect.poll(() => param('song')).toBe('verse*5 fill verse*2');
  // the fill dragged in front of the first block
  const first = (await blocks.nth(0).boundingBox())!, fill = (await blocks.nth(1).boundingBox())!;
  await page.mouse.move(fill.x + fill.width / 2, fill.y + fill.height / 2); await page.mouse.down(); await page.mouse.move(first.x + 4, fill.y + fill.height / 2, { steps: 8 }); await page.mouse.up();
  await expect.poll(() => param('song')).toBe('fill verse*5 verse*2');
  await expect(blocks.nth(0)).toContainText('fill');
  // keyboard: Alt+Right moves it back, minus takes a bar off, Delete removes (each time after the timeline has been drawn again)
  await blocks.nth(0).focus(); await page.keyboard.press('Alt+ArrowRight');
  await expect.poll(() => param('song')).toBe('verse*5 fill verse*2');
  await expect(blocks.nth(0)).toContainText('verse');
  await blocks.nth(0).focus(); await page.keyboard.press('-');
  await expect.poll(() => param('song')).toBe('verse*4 fill verse*2');
  await expect(blocks.nth(0).locator('.sectl-bars')).toHaveText('4');
  // the menu: duplicate the fill, then remove one of them
  await blocks.nth(1).click({ button: 'right' });
  await page.locator('#menu').getByText('Duplicate').click();
  await expect.poll(() => param('song')).toBe('verse*4 fill fill verse*2');
  await expect(blocks).toHaveCount(4);
  await blocks.nth(2).focus(); await page.keyboard.press('Delete');
  await expect.poll(() => param('song')).toBe('verse*4 fill verse*2');
  // a click chooses the pattern the grid shows
  await blocks.nth(1).click();
  await expect(page.locator('#seq-pattern')).toHaveValue('fill');
});
