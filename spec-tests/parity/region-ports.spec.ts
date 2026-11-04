import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Regions (plan L02/L03): the joins of a loop are their own lane; the port has a friendly name, a tooltip, its own colour, and a region never connects to audio.
freshServer();
test('the loop seam port shows its friendly name and tooltip with the chain, and a region port has its own colour', async ({ page }) => {
  await openApp(page); await page.click('#vs-source'); await expect(page.locator('#se-cm .cm-content')).toBeVisible();
  await page.locator('#se-cm .cm-content').fill('composition "demo" { ample 1  tempo 96  master-seed 424242 }\nimport-audio rain at 1 for 8 {\n  file assets/a.wav\n  loop on\n}\nreverb room at 1 for 8 {\n  from rain.loop-region -> region\n}\n');
  await page.click('#vs-blocks');
  const seam = page.locator('.node[data-id="rain"] .port.out.region');
  await expect(seam).toHaveCount(1);
  await expect(page.locator('.node[data-id="rain"]')).toContainText('Loop seam');
  await expect(seam).toHaveAttribute('title', /Loop seam: Only the joins of the loop/);
  const out = page.locator('.node[data-id="room"] .port.out.region');
  await expect(out).toHaveAttribute('title', /1 effect: room/);
});

test('an output port with a description has a play button that opens a small player with a clear message when there is nothing to hear yet', async ({ page }) => {
  await openApp(page); await page.click('#vs-source'); await expect(page.locator('#se-cm .cm-content')).toBeVisible();
  await page.locator('#se-cm .cm-content').fill('composition "demo" { ample 1  tempo 96  master-seed 424242 }\nimport-audio rain at 1 for 8 {\n  file assets/missing.wav\n  loop on\n  crossfade 2\n}\n');
  await page.click('#vs-blocks');
  const play = page.locator('.node[data-id="rain"] .port.out .pp');
  await expect(play).toHaveCount(2);   // body and the loop seam: a triangle in the dot
  await page.locator('.node[data-id="rain"] .port.out.region').click();
  const player = page.locator('.port-preview');
  await expect(player).toBeVisible();
  await expect(player).toContainText('Loop seam');
  await expect(player.locator('.pp-status')).not.toHaveText('Loading…', { timeout: 20000 });
  await player.getByRole('button', { name: 'Close' }).click();
  await expect(player).toHaveCount(0);
});

test('the player hangs by its port: it moves along with the block and hides when the port is out of sight', async ({ page }) => {
  await openApp(page); await page.click('#vs-source'); await expect(page.locator('#se-cm .cm-content')).toBeVisible();
  await page.locator('#se-cm .cm-content').fill('composition "demo" { ample 1  tempo 96  master-seed 424242 }\nimport-audio rain at 1 for 8 {\n  file assets/missing.wav\n  loop on\n  crossfade 2\n}\n');
  await page.click('#vs-blocks');
  const dot = page.locator('.node[data-id="rain"] .port.out.region');
  await dot.click();
  const player = page.locator('.port-preview');
  await expect(player).toBeVisible();
  await expect(player.locator('.pp-status')).not.toHaveText('Loading…', { timeout: 20000 });
  await page.waitForTimeout(600);   // the block is drawn again once the build is reported; move it after that
  const before = await player.boundingBox();
  await page.locator('.node[data-id="rain"]').evaluate((e: HTMLElement) => { e.style.translate = '-120px 0'; });
  await expect.poll(async () => (await player.boundingBox())!.x).toBeLessThan(before!.x - 60);
  await page.locator('.node[data-id="rain"]').evaluate((e: HTMLElement) => { e.style.translate = '-6000px 0'; });
  await expect(player).toBeHidden();
  await page.locator('.node[data-id="rain"]').evaluate((e: HTMLElement) => { e.style.translate = ''; });
  await expect(player).toBeVisible();
});

test('a press beside the player closes it, and a second click on the same port closes it too', async ({ page }) => {
  await openApp(page); await page.click('#vs-source'); await expect(page.locator('#se-cm .cm-content')).toBeVisible();
  const nl = String.fromCharCode(10);
  await page.locator('#se-cm .cm-content').fill(['composition "demo" { ample 1  tempo 96  master-seed 424242 }', 'import-audio rain at 1 for 8 {', '  file assets/missing.wav', '  loop on', '  crossfade 2', '}', ''].join(nl));
  await page.click('#vs-blocks');
  const dot = page.locator('.node[data-id="rain"] .port.out.region');
  const player = page.locator('.port-preview');
  await dot.click();
  await expect(player).toBeVisible();
  await page.mouse.click(1000, 560);
  await expect(player).toHaveCount(0);
  await dot.click();
  await expect(player).toBeVisible();
  await dot.click();
  await expect(player).toHaveCount(0);
});

test('an effect shows its loop seam ports only when a region is connected, or while a region line is dragged', async ({ page }) => {
  await openApp(page); await page.click('#vs-source'); await expect(page.locator('#se-cm .cm-content')).toBeVisible();
  const nl = String.fromCharCode(10);
  await page.locator('#se-cm .cm-content').fill(['composition "demo" { ample 1  tempo 96  master-seed 424242 }', 'import-audio rain at 1 for 8 {', '  file assets/missing.wav', '  loop on', '  crossfade 2', '}', 'reverb room at 1 for 8 {', '  from rain.audio -> audio', '}', ''].join(nl));
  await page.click('#vs-blocks');
  const seamIn = page.locator('.node[data-id="room"] .port.in.region');
  await expect(page.locator('.node[data-id="rain"] .port.out.region')).toHaveCount(1);
  await expect(seamIn).toHaveCount(0);
  await expect(page.locator('.node[data-id="room"] .port.out.region')).toHaveCount(0);
  const dot = await page.locator('.node[data-id="rain"] .port.out.region').boundingBox();
  await page.mouse.move(dot!.x + dot!.width / 2, dot!.y + dot!.height / 2);
  await page.mouse.down();
  await page.mouse.move(dot!.x + 60, dot!.y + 40, { steps: 4 });
  await expect(seamIn).toHaveCount(1);
  await page.mouse.up();
  await expect(seamIn).toHaveCount(0);
});
