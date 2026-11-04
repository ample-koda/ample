import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// A chain of effects on the Scratches region: every link is drawn, also the one into the region-merge.
const SONG = `composition "demo" { ample 1  tempo 92  master-seed 1 }
drums beats at 1 for 4 {
}
rewind pullup {
  from beats.audio -> audio
}
delay echo {
  from pullup.region -> region_in
}
filter tone {
  from echo.region -> region_in
}
reverb room {
  from tone.region -> region_in
}
region-merge back {
  from pullup.audio -> body
  from room.region -> region
}
master master {
  from back.audio -> audio
}
`;
freshServer({ song: SONG });

test('the links of a region chain are drawn', async ({ page }) => {
  await openApp(page, { reset: false });
  await expect(page.locator('path.vis[data-type="region"]')).toHaveCount(4);
});

test('a region line has a colour (its stroke must resolve, otherwise the line is there but cannot be seen)', async ({ page }) => {
  await openApp(page, { reset: false });
  // the canvas redraws its lines while the page settles: read the colour of the line that is on screen now, and try again when it was replaced
  await expect.poll(() => page.evaluate(() => { const p = document.querySelector('path.vis[data-type="region"]'); return p && p.isConnected ? getComputedStyle(p).stroke : ''; }), { timeout: 10000 }).toMatch(/^rgb/);
});
