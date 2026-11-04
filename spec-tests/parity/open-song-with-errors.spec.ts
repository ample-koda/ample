import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { freshServer, openApp } from '../../web/tests/support';

// Opening another song of the project that has errors goes straight to the source editor, as the start of the app does.
const GOOD = `composition "demo" { ample 1  tempo 120  master-seed 1 }
midi-clip line at 1 for 2 {
  bars 2
  notes 1:0:4:57:100 2:0:4:60:100
}
synth voice at 1 for 2 {
  wave saw
  from line.notes -> notes
}
synth pad at 1 for 2 {
  wave saw
  from line.notes -> notes
}
vocoder talk {
  bands 12
  from voice.audio -> modulator
  from pad.audio -> carrier
}
master master {
  from talk.audio -> audio
}
`;
const BROKEN = `composition "broken" { ample 1  tempo 120  master-seed 1 }
nonsense thing {
}
master master {
}
`;
freshServer({ song: GOOD, prepare: (_cli, folder) => fs.writeFileSync(path.join(folder, 'zz-broken.amplet'), BROKEN) });

test('a song with errors opens in the source editor', async ({ page }) => {
  await openApp(page, { reset: false });
  await expect(page.locator('#se')).toBeHidden();
  await page.evaluate(() => document.dispatchEvent(new CustomEvent('ample:open', { detail: 'zz-broken.amplet' })));
  await expect(page.locator('#vs-source')).toHaveClass(/on/, { timeout: 10000 });
  await expect(page.locator('#se')).toBeVisible();
});
