import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// A llama can be resized (0.5 to 1.25 in steps of 0.05, default 1 everywhere); the size is kept in the song's view block.
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

test('the corners scale the llama in steps of 0.05 and the size is written into the song', async ({ page }) => {
  await openApp(page, { reset: false });
  await node(page, 'llama').locator('.studio-llama-node').evaluate((b: HTMLElement) => b.click());
  await expect(page.locator('.llama-avatar')).toHaveCount(1);
  const before = (await page.locator('.llama-avatar').boundingBox())!;
  expect(Math.round(before.width)).toBe(150);   // 1 by default, also on a phone
  await page.locator('.llama-avatar').click({ button: 'right' });
  await page.getByText('Resize…', { exact: true }).click();
  await expect(page.locator('.llama-resize-handle')).toHaveCount(4);
  const handle = (await page.locator('.llama-resize-handle.tl').boundingBox())!;
  await page.mouse.move(handle.x + 10, handle.y + 10);
  await page.mouse.down();
  await page.mouse.move(before.x + before.width * 0.5, before.y + before.height * 0.5, { steps: 6 });
  await page.mouse.up();
  await page.locator('.llama-resize-btn.done').click();
  const after = (await page.locator('.llama-avatar').boundingBox())!;
  expect(after.width).toBeLessThan(before.width);
  await expect.poll(docText, { timeout: 8000 }).toMatch(/size "llama:llama" 0\.\d+/);   // in the song's own view block
});
