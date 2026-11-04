import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The Scratches output of a rewind can be dragged by hand to the region input of an effect; that input is there before the drag starts.
const SONG = `composition "demo" { ample 1  tempo 92  master-seed 1 }
drums beats at 1 for 4 {
}
rewind pullup {
  from beats.audio -> audio
}
delay echo {
  from pullup.audio -> audio
}
master master {
  from echo.audio -> audio
}
`;
freshServer({ song: SONG });

test('dragging the Scratches output to the region input of an effect connects them', async ({ page }) => {
  await openApp(page, { reset: false });
  const dot = node(page, 'pullup').locator('.port.out.region');
  await expect(dot).toHaveCount(1);
  const target = node(page, 'echo').locator('.port.in.region');
  await expect(target).toHaveCount(1);   // visible without dragging: there is a free Scratches output
  const box = (await dot.boundingBox())!, t = (await target.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(t.x + t.width / 2, t.y + t.height / 2, { steps: 10 });
  await page.mouse.up();
  await expect.poll(async () => ((await api('/api/doc?path=song.amplet')) as { text: string }).text, { timeout: 8000 }).toMatch(/pullup\.region -> region\b/);
  await expect(page.locator('path.vis[data-type="region"]')).toHaveCount(1);   // and the line is drawn
});
