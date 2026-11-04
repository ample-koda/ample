import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// "at X for Y" of a block header is underlined in the source; on hover it shows the start, end and duration in seconds (from the tempo).
freshServer();
test('the time of a block in the source: underlined, and on hover start, end and duration in seconds', async ({ page }) => {
  await openApp(page); await page.click('#vs-source'); await expect(page.locator('#se-cm .cm-content')).toBeVisible();
  await page.locator('#se-cm .cm-content').fill('composition "demo" { ample 1  tempo 96  master-seed 424242 }\nimport-audio rain-source at 1 for 72 {\n  file assets/a.wav\n}\n');
  await expect(page.locator('.cm-content .tm').filter({ hasText: 'at 1 for 72' })).toHaveCount(1);
  const box = await page.locator('#se-cm .cm-line').nth(1).boundingBox();
  const charW = await page.evaluate(() => { const p = document.createElement('span'); p.textContent = 'x'; p.style.cssText = 'position:absolute;visibility:hidden;font:12px ui-monospace,Consolas,monospace'; document.body.append(p); const w = p.getBoundingClientRect().width; p.remove(); return w; });
  const at = 'import-audio rain-source at 1 for 72'.indexOf('at 1');
  const y = box!.y + box!.height / 2;
  await page.mouse.move(box!.x + 8 + charW * (at + 3), y);
  await page.mouse.move(box!.x + 8 + charW * (at + 4), y);
  await expect(page.locator('#se-timetip')).toHaveText('start 0 s · end 180 s (3:00) · duration 180 s (3:00)');
  await page.mouse.move(box!.x + 8 + charW * 3, y);
  await expect(page.locator('#se-timetip')).toBeHidden();
});
