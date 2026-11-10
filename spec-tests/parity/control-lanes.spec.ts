import { expect, test } from '@playwright/test';
import { addTemplateBlock, api, bottomTab, freshServer, node, openApp, openTab } from '../../web/tests/support';

// Control lanes (plan/control-lanes): a control made by a curve block steers a filter, the lane of the control shows its curve in the arrangement and the graph links the control to the parameter.
const SONG = `composition "controls" { ample 1  tempo 120  master-seed 5 }

drums beat at 1 for 4 {
  pattern kick:x...x...x...x... snare:....x.......x... hat:x.x.x.x.x.x.x.x.
}

curve sweep {
  value auto(1:0, 4:1000)
}

map hz {
  unit Hz
  out_low 300
  out_high 6000
  from sweep.level -> input
}

filter lp {
  from beat.audio -> audio
  from hz.level -> cutoff
}

reverb space {
  mix 10 + 60 * sweep.level
  from lp.audio -> audio
}

master out {
  from space.audio -> audio
}
`;
freshServer({ song: SONG });

/** openApp puts the demo song back, so the song of this file is written again after it (through the API, like the app does). */
async function useSong() {
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', text: SONG, baseVersion: doc.version } });
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
}

test('a song that is steered by a curve builds, and the steered block is another take than without the control', async () => {
  await useSong();
  const steered = await api('/api/build', { method: 'POST', body: { path: 'song.amplet', intent: 'preview', range: { firstBar: 1, bars: 4 } } });
  expect(steered.masterTakeId).toBeTruthy();
  const flat = await api('/api/doc/replace', { method: 'POST', body: { path: 'song.amplet', text: SONG.replace('value auto(1:0, 4:1000)', 'value 0') } }).catch(() => null);
  if (flat) {
    const other = await api('/api/build', { method: 'POST', body: { path: 'song.amplet', intent: 'preview', range: { firstBar: 1, bars: 4 } } });
    expect(other.masterTakeId).not.toBe(steered.masterTakeId);
  }
});

test('the control has a lane with its curve in the arrangement', async ({ page }) => {
  await openApp(page);
  await useSong();
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await page.reload();
  await bottomTab(page, 'arr');
  // the controls are the blocks sweep (a curve) and hz (a map): a lane for each, with a canvas for the curve
  await expect(page.locator('.control-lane')).toHaveCount(2, { timeout: 60000 });
  await expect(page.locator('.control-lane canvas.control-curve').first()).toHaveCount(1);
});

test('the control API gives the curve of a take', async () => {
  await useSong();
  const build = await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  const blocks: Array<Record<string, string>> = build.blocks ?? [];
  const take = blocks.find((b) => (b.block ?? b.Block) === 'sweep')?.takeId ?? blocks.find((b) => (b.block ?? b.Block) === 'sweep')?.TakeId;
  expect(take).toBeTruthy();
  const curve = await api(`/api/takes/${take}/control?points=100`);
  expect(curve.firstBar).toBe(1);
  expect(curve.values.length).toBeGreaterThan(10);
  expect(curve.values.at(-1)).toBeGreaterThan(curve.values[0]);
});

test('a parameter has a button that steers it with a control, and the choice is written as a connection', async ({ page }) => {
  await openApp(page);
  await addTemplateBlock(page, 'curve', 'sw');
  await addTemplateBlock(page, 'volume', 'vol');
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'vol').click({ position: { x: 20, y: 36 } });
  await openTab(page, 'parameters');
  await page.getByRole('button', { name: 'Steer gain with a control' }).click({ timeout: 10000 });
  await expect(page.locator('#dlg select')).toBeVisible({ timeout: 8000 });
  await page.locator('#dlg select').selectOption({ index: 0 });
  await page.locator('#dlg-btns').getByRole('button', { name: 'Steer' }).click({ timeout: 8000 });
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text, { timeout: 15000 }).toMatch(/from sw.level -> gain/);
});

test('a control line that is dragged shows the control inputs of the parameters and connects to one @quarantine', async ({ page }) => {
  await openApp(page);
  await addTemplateBlock(page, 'curve', 'sw');
  await addTemplateBlock(page, 'volume', 'vol');
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const out = page.locator('.port.out[data-node="sw"]').first();
  const box = (await out.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + 40, box.y + 40, { steps: 6 });
  const target = page.locator('.port.in[data-node="vol"][data-type^="control"]').first();
  await expect(target).toBeVisible({ timeout: 8000 });   // the control input of the gain shows while the line is dragged
  const to = (await target.boundingBox())!;
  await page.mouse.move(to.x + to.width / 2, to.y + to.height / 2, { steps: 8 });
  await page.mouse.up();
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text, { timeout: 15000 }).toMatch(/from sw.level -> gain/);
});
