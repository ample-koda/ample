import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The autotune window shows the sung pitch and the notes it is pulled to; the right-click menu of a note writes a fix, and the build runs the block.
const SONG = `composition "demo" { ample 1  tempo 120  master-seed 1 }
midi-clip line at 1 for 2 {
  bars 2
  notes 1:0:4:69:100 2:0:4:72:100
}
synth voice at 1 for 2 {
  wave sine
  from line.notes -> notes
}
autotune tune {
  method spectral
  key chromatic
  from voice.audio -> audio
}
master master {
  from tune.audio -> audio
}
`;
freshServer({ song: SONG });

const docText = async () => ((await api('/api/doc?path=song.amplet')) as { text: string }).text;

test('the window shows the notes and its menu writes a fix', async ({ page }) => {
  await openApp(page, { reset: false });
  const report = (await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } })) as { blocks: { block: string; status: string }[] };
  expect(report.blocks.find((b) => b.block === 'tune')?.status).not.toBe('failed');
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'tune').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const roll = page.locator('#studio[open] canvas.autotune-roll');
  await expect(roll).toBeVisible();
  // the input is decoded and its pitch followed first
  await expect(page.locator('#studio[open] .autotune-readout')).toContainText(/\d+ notes?/, { timeout: 20000 });
  await expect(page.locator('#studio[open] .autotune-key')).toHaveText('Key: chromatic');
  // the longest note the window found is the A4 of the first bar or the C5 of the second: right-click the middle of it
  const runs = JSON.parse((await roll.getAttribute('data-runs'))!) as [number, number, number][];
  const [low, high] = (await roll.getAttribute('data-rows'))!.split(',').map(Number);
  const duration = Number(await roll.getAttribute('data-duration')), keys = Number(await roll.getAttribute('data-keys'));
  const [from, to, note] = runs.reduce((a, b) => (b[1] - b[0] > a[1] - a[0] ? b : a));
  expect([69, 72]).toContain(note);
  const box = (await roll.boundingBox())!;
  // the grid starts after the key strip (keys px wide) and shows the whole sound when the window opens
  await page.mouse.click(box.x + keys + (box.width - keys) * ((from + to) / 2 / duration), box.y + box.height * (1 - (note - low + 0.5) / (high - low + 1)), { button: 'right' });
  await page.getByText('A semitone up', { exact: true }).click();
  const name = note === 69 ? 'A#4' : 'C#5';
  await expect.poll(docText, { timeout: 8000 }).toMatch(new RegExp(`fixes "?[\\d.]+s-[\\d.]+s ${name}`));
  await expect(page.locator('#studio[open] .autotune-row')).toHaveCount(1);
  // the remove button of the row takes it away again
  await page.locator('#studio[open] .autotune-row button').click();
  await expect.poll(async () => (await docText()).includes(name), { timeout: 8000 }).toBe(false);
});

test('the play bar plays the tuned sound', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'tune').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const play = page.locator('#studio[open] .autotune-play');
  await expect(page.locator('#studio[open] .autotune-readout')).toContainText('notes', { timeout: 20000 });
  await expect.poll(async () => { await play.click(); return play.getAttribute('aria-pressed'); }, { timeout: 15000 }).toBe('true');
  await play.click();
  await expect(play).toHaveAttribute('aria-pressed', 'false');
});
