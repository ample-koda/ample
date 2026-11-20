import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// The edge tab of the properties lies over the scrollbar of the code editor, so it is hidden while the code editor is open;
// the properties still open and close with the toolbar button, and the tab comes back with the blocks.
const SONG = `composition "demo" { ample 1  tempo 100  master-seed 1 }
speech voice at 1 for 1 {
  text "I am a robot."
}
master master {
  from voice.audio -> audio
}
`;
freshServer({ song: SONG });

test('the properties tab is hidden while the code editor is open, and the toolbar still toggles the properties', async ({ page }) => {
  await page.setViewportSize({ width: 1400, height: 900 });
  await openApp(page, { reset: false });
  const lip = page.locator('#lip-right');
  await expect(lip).toBeVisible();

  await page.click('#vs-source');
  await expect(page.locator('#se .cm-content, #se-ta').first()).toBeVisible();
  await expect(lip).toBeHidden();

  // the toolbar button still closes and opens the properties
  const toggle = page.locator('#tg-right');
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-pressed', 'false');
  await expect(page.locator('#work')).toHaveClass(/noR/);
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');

  await page.click('#vs-blocks');
  await expect(lip).toBeVisible();
});
