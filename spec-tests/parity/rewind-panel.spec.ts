import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The rewind panel shows the sound as it is heard; a click on the wave adds a rewind, a drag moves its stop, a double-click takes it away, all written to the events text.
const SONG = `composition "demo" { ample 1  tempo 92  master-seed 1 }
drums beats at 1 for 4 {
}
rewind pullup {
  from beats.audio -> audio
}
master master {
  from pullup.audio -> audio
}
`;
freshServer({ song: SONG });

const docText = async () => ((await api('/api/doc?path=song.amplet')) as { text: string }).text;

test('clicking, dragging and double-clicking the wave write the rewinds', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'pullup').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const wave = page.locator('#studio[open] canvas.rewind-wave');
  await expect(wave).toBeVisible();
  // the input is decoded first; a click before that does nothing, so click again until a rewind is written
  for (let attempt = 0; attempt < 8 && !/scratch {/.test(await docText()); attempt++) {
    await page.waitForTimeout(700);
    const box = (await wave.boundingBox())!;
    await page.mouse.dblclick(box.x + box.width * 0.5, box.y + box.height / 2);
    await page.waitForTimeout(1500);
  }
  await expect.poll(docText, { timeout: 15000 }).toMatch(/scratch \{\s+at [\d.]+s \{ duration 1s; back 2s \}/);
  const rows = page.locator('#studio[open] .rewind-row');
  await expect(rows).toHaveCount(1);
  // drag its flag to the left: the stop moves to an earlier time
  const before = Number(/at ([\d.]+)s/.exec(await docText())![1]);
  const box = (await wave.boundingBox())!;
  const startX = box.x + box.width * 0.5;   // the click above put the flag in the middle
  await page.mouse.move(startX, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(startX - box.width * 0.2, box.y + box.height / 2, { steps: 5 });
  await page.mouse.up();
  await expect.poll(async () => Number(/at ([\d.]+)s/.exec(await docText())?.[1] ?? before), { timeout: 8000 }).toBeLessThan(before);
  // the remove button of the row takes it away again
  await page.locator('#studio[open] .rewind-row button').click();
  await expect.poll(async () => /scratch {/.test(await docText()), { timeout: 8000 }).toBe(false);
});

test('the live preview plays the rewound sound and follows a change', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'pullup').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const live = page.locator('#studio[open] .rewind-play');
  await expect(live).toHaveText(/Play/);
  // the sound is decoded first; press until the preview runs
  await expect.poll(async () => { await live.click(); return live.getAttribute('aria-pressed'); }, { timeout: 15000 }).toBe('true');
  const wave = page.locator('#studio[open] canvas.rewind-wave');
  const box = (await wave.boundingBox())!;
  await page.mouse.dblclick(box.x + box.width * 0.5, box.y + box.height / 2);   // a change while it plays keeps it playing
  await expect.poll(docText, { timeout: 8000 }).toMatch(/scratch \{/);
  await expect(live).toHaveAttribute('aria-pressed', 'true');
  await live.click();
  await expect(live).toHaveAttribute('aria-pressed', 'false');
  // leave the song as it was found (the tests of this file share it)
  await expect(page.locator('#studio[open] .rewind-row button')).toHaveCount(1, { timeout: 15000 });
  await page.locator('#studio[open] .rewind-row button').click();
  await expect.poll(async () => /scratch {/.test(await docText()), { timeout: 8000 }).toBe(false);
});

test('the wave scrubs, moves a whole rewind with its inside and has a right-click menu', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'pullup').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const wave = page.locator('#studio[open] canvas.rewind-wave');
  await expect(wave).toBeVisible();
  const at = async (fraction: number) => { await expect(wave).toBeVisible(); let box = await wave.boundingBox(); for (let tries = 0; !box && tries < 20; tries++) { await page.waitForTimeout(100); box = await wave.boundingBox(); }   // the board redraws after a write: the box is there again a moment later
    if (!box) throw new Error('the wave has no box'); return { x: box.x + box.width * fraction, y: box.y + box.height / 2 }; };
  // add through the menu
  for (let attempt = 0; attempt < 8 && !/scratch \{/.test(await docText()); attempt++) {
    await page.waitForTimeout(700);
    const p = await at(0.3);
    await page.mouse.click(p.x, p.y, { button: 'right' });
    await page.getByText(/Add a rewind here/).click({ timeout: 2000 }).catch(() => undefined);
    await page.waitForTimeout(1500);
  }
  await expect(page.locator('#studio[open] .rewind-row')).toHaveCount(1, { timeout: 15000 });   // the panel is built again and has decoded its input
  await expect.poll(docText, { timeout: 15000 }).toMatch(/scratch \{\s+at [\d.]+s \{ duration 1s; back 2s \}/);
  const before = Number(/at ([\d.]+)s/.exec(await docText())![1]);
  // drag the inside of the bracket to the right: the whole rewind moves
  const inside = await at(0.3 + 0.01), moved = await at(0.3 + 0.2);
  await page.mouse.move(inside.x, inside.y); await page.mouse.down(); await page.mouse.move(moved.x, moved.y, { steps: 6 }); await page.mouse.up();
  await expect.poll(async () => Number(/at ([\d.]+)s/.exec(await docText())?.[1] ?? before), { timeout: 8000 }).toBeGreaterThan(before);
  // a click on the empty wave moves the playhead (the time readout follows)
  const empty = await at(0.9);
  await page.mouse.click(empty.x, empty.y);
  await expect(page.locator('#studio[open] .rewind-time')).not.toHaveText(/^0:00\.0 /);
  // the menu of a rewind removes it
  const flag = await at(0.3 + 0.2);
  await page.mouse.click(flag.x + 12, flag.y, { button: 'right' });
  await page.getByText('Remove this rewind').click();
  await expect.poll(async () => /scratch \{/.test(await docText()), { timeout: 8000 }).toBe(false);
});

test('closing the window stops the preview (it does not play on under the song)', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  const open = () => node(page, 'pullup').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  await open();
  const live = page.locator('#studio[open] .rewind-play');
  await expect.poll(async () => { await live.click(); return live.getAttribute('aria-pressed'); }, { timeout: 15000 }).toBe('true');
  await page.keyboard.press('Escape');
  await expect(page.locator('#studio[open]')).toHaveCount(0);
  await page.waitForTimeout(1200);   // the preview notices that its window is gone
  await open();
  await expect(page.locator('#studio[open] .rewind-play')).toHaveAttribute('aria-pressed', 'false');
});
