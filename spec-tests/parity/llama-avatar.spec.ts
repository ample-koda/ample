import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The linked llama avatar: only a Delay Llama block has the llama button; it calls out a floating llama that can be dragged and is put away with a double click.
const SONG = `composition "demo" { ample 1  tempo 120  master-seed 1 }
midi-clip line at 1 for 2 {
  bars 2
  notes 1:0:4:57:100 2:0:4:64:100
}
delay-llama llama {
  from line.notes -> notes
}
reverb room {
  from llama.audio -> audio
}
master master {
  from room.audio -> audio
}
`;
freshServer({ song: SONG });

test('the llama button is only on a Delay Llama block, calls the llama out, it can be dragged and a double click puts it away', async ({ page }) => {
  await openApp(page, { reset: false });
  await expect(node(page, 'llama').locator('.studio-llama-node')).toHaveCount(1);
  await expect(node(page, 'room').locator('.studio-llama-node')).toHaveCount(0);
  await node(page, 'llama').locator('.studio-llama-node').evaluate((b: HTMLElement) => b.click());
  const avatar = page.locator('.llama-avatar[data-block="llama"]');
  await expect(avatar).toBeVisible();
  const size = (await avatar.boundingBox())!;
  expect(size.width).toBeGreaterThan(100); expect(size.height).toBeGreaterThan(100);
  if (process.env.LLAMA_SHOT) await page.screenshot({ path: process.env.LLAMA_SHOT });
  // drag it to the upper left
  const before = (await avatar.boundingBox())!;
  await page.mouse.move(before.x + before.width / 2, before.y + before.height / 2);
  await page.mouse.down();
  await page.mouse.move(200, 200, { steps: 6 });
  await page.mouse.up();
  const after = (await avatar.boundingBox())!;
  expect(after.x).toBeLessThan(before.x - 100);
  // it remembers its place; a double click puts it away
  await avatar.dblclick();
  await expect(avatar).toHaveCount(0);
  await expect(node(page, 'llama').locator('.studio-llama-node')).toHaveAttribute('aria-pressed', 'false');
  void api;
});
