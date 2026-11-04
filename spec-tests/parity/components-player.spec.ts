import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// An audio component instance has a player on its card once it is built: it plays the take of the processor that gives its output.
const DEMO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../demo-projects/components/components');

freshServer({
  song: 'composition "player" { ample 1  tempo 100  master-seed 1 }\nimport "components/dream-echo.amplec"\nmidi-clip melody at 1 for 2 {\n  notes 1:0:1:60:100 1:1:1:64:100\n}\npiano lead at 1 for 2 {\n  from melody.notes -> notes\n}\nkoda.fx.dreamEcho space {\n  from lead.audio -> audio\n}\n',
  prepare: (_cli, folder) => {
    fs.mkdirSync(path.join(folder, 'components'), { recursive: true });
    fs.copyFileSync(path.join(DEMO, 'dream-echo.amplec'), path.join(folder, 'components', 'dream-echo.amplec'));
  },
});

test('a built audio component shows a player on its card', async ({ page }) => {
  await openApp(page, { reset: false });
  await expect(node(page, 'space')).toHaveCount(1);
  const built = await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await page.reload();
  await expect(node(page, 'space').locator('.player')).toHaveCount(1, { timeout: 20000 });
});
