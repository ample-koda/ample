import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The Delay Llama window: the llama sings along, the pad has four modes, Set writes a vowel and a mouth, the vowel table of the block follows a e i o u.
const SONG = `composition "demo" { ample 1  tempo 120  master-seed 1 }
midi-clip line at 1 for 2 {
  bars 2
  notes 1:0:4:57:100 2:0:4:64:100
}
delay-llama llama {
  from line.notes -> notes
}
master master {
  from llama.audio -> audio
}
`;
freshServer({ song: SONG });
const docText = async () => ((await api('/api/doc?path=song.amplet')) as { text: string }).text;

test('the block builds and its window has a llama and a pad whose Set mode writes vowel and mouth', async ({ page }) => {
  const report = (await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } })) as { blocks: { block: string; status: string }[] };
  expect(report.blocks.find((b) => b.block === 'llama')?.status).not.toBe('failed');
  await openApp(page, { reset: false });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'llama').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  await expect(page.locator('#studio[open] .llama-stage svg')).toBeVisible();
  await expect(page.locator('#studio[open] .llama-modes button')).toHaveCount(4);
  await expect(page.locator('#studio[open] .llama-modes button[aria-pressed="true"]')).toHaveText('View');
  await page.locator('#studio[open] .llama-modes button[data-mode="set"]').click();
  const pad = page.locator('#studio[open] .llama-pad'), box = (await pad.boundingBox())!;
  // the far right is a u (100), the top is an open mouth (100)
  await page.mouse.click(box.x + box.width - 12, box.y + 12);   // inside the rounded corner
  await expect.poll(docText, { timeout: 8000 }).toMatch(/vowel (9[0-9]|100)/);
  await expect.poll(docText).toMatch(/mouth 9\d|mouth 100/);
});

test('the Rec mode says what it does: the main button becomes Record and the steps are written under the pad', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'llama').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  await expect(page.locator('#studio[open] .llama-sing')).toHaveText('▶ Sing');
  await page.locator('#studio[open] .llama-modes button[data-mode="rec"]').click();
  await expect(page.locator('#studio[open] .llama-sing')).toHaveText('● Record');
  await expect(page.locator('#studio[open] .llama-pad-hint')).toContainText('Press Record');
  await expect(page.locator('#studio[open] .llama-undo')).toBeHidden();
  await page.locator('#studio[open] .llama-modes button[data-mode="view"]').click();
  await expect(page.locator('#studio[open] .llama-sing')).toHaveText('▶ Sing');
});

test('a recording sings the clip live, the pad moves are written as curves and Undo brings the old values back', async ({ page }) => {
  test.setTimeout(60000);
  await openApp(page, { reset: false });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'llama').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  await page.mouse.click(300, 300);   // a gesture, so the audio context may run
  await page.locator('#studio[open] .llama-modes button[data-mode="rec"]').click();
  await page.locator('#studio[open] .llama-sing').click();
  await expect(page.locator('#studio[open] .llama-count')).toBeVisible();
  await expect(page.locator('#studio[open] .llama-pad.recording')).toBeVisible({ timeout: 8000 });
  const box = (await page.locator('#studio[open] .llama-pad').boundingBox())!;
  await page.mouse.move(box.x + 30, box.y + box.height - 20);
  await page.mouse.down();
  for (let i = 0; i < 12; i++) { await page.mouse.move(box.x + 30 + (box.width - 80) * (i / 11), box.y + box.height - 20 - (box.height - 50) * (i / 11)); await page.waitForTimeout(250); }
  await page.mouse.up();
  await page.waitForTimeout(500);
  await expect(page.locator('#studio[open] .llama-undo')).toBeVisible({ timeout: 20000 });
  await expect.poll(docText, { timeout: 8000 }).toMatch(/vowel auto\(/);
  await expect.poll(docText).toMatch(/mouth auto\(/);
  await page.locator('#studio[open] .llama-undo').click();
  await expect.poll(async () => /vowel auto\(/.test(await docText()), { timeout: 8000 }).toBe(false);
});

test('under the controls a strip shows the block as built and a click moves the place it sings from', async ({ page }) => {
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'llama').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  await page.mouse.click(300, 300);
  const strip = page.locator('#studio[open] canvas.llama-strip');
  await expect(strip).toBeVisible();
  await expect(page.locator('#studio[open] .studio-preview-slot')).toBeHidden();   // no second Play block: the strip is the transport
  const box = (await strip.boundingBox())!;
  expect(box.width).toBeGreaterThan(300);
  await page.mouse.click(box.x + box.width * 0.5, box.y + box.height / 2);
  await page.locator('#studio[open] .llama-sing').click();
  await page.waitForTimeout(1200);
  if (process.env.LLAMA_SHOT) await page.locator('#studio[open]').screenshot({ path: process.env.LLAMA_SHOT });
  await expect(page.locator('#studio[open] .llama-sing')).toHaveText('■ Stop');
  await page.locator('#studio[open] .llama-sing').click();
  await expect(page.locator('#studio[open] .llama-sing')).toHaveText('▶ Sing');
});
