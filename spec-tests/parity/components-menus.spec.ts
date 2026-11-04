import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test, expect } from '@playwright/test';
import { freshServer, node, openApp } from '../../web/tests/support';

// The board menus of a component instance that were never clicked in a real browser: the Audio badge in the palette,
// "Show internal graph" (audio) and "Migrate to version" (notes). They use the real demo components.
const DEMO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../demo-projects/components/components');

freshServer({
  song: 'composition "menus" { ample 1  tempo 100  master-seed 1 }\nimport "components/wet-dry.amplec"\nimport "components/swing-line.amplec"\nmidi-clip melody at 1 for 2 {\n  notes 1:0:1:60:100 1:1:1:64:100\n}\npiano lead at 1 for 2 {\n  from melody.notes -> notes\n}\nkoda.fx.wetDry blend {\n  from lead.audio -> audio\n}\nkoda.notes.swingLine oldWay pinned@1.0 at 2 for 4 {\n  copies 3\n  spacing 1bar\n  from melody.notes -> source\n}\n',
  prepare: (_cli, folder) => {
    fs.mkdirSync(path.join(folder, 'components'), { recursive: true });
    for (const f of ['wet-dry.amplec', 'swing-line.amplec']) fs.copyFileSync(path.join(DEMO, f), path.join(folder, 'components', f));
  },
});

// runs the menu entry the way the board does, without depending on where the context menu opens
const runItem = (page, id: string, label: string, child?: string) => page.evaluate(async ([blockId, text, sub]) => {
  const { componentMenuItems } = await import('/app/components.js');
  const { flat } = await import('/app/store.js');
  const n = flat().nodes.find((x) => x.id === blockId);
  const items = componentMenuItems(n);
  const item = items.find((x) => x.label.startsWith(text));
  if (!item) throw new Error('no menu item ' + text + ' in ' + items.map((x) => x.label).join(' | '));
  const target = sub ? item.children.find((c) => c.label.startsWith(sub)) : item;
  target.fn();
}, [id, label, child ?? ''] as [string, string, string]);

test('the palette marks an audio component with the audio badge', async ({ page }) => {
  await openApp(page, { reset: false });
  const item = page.locator('#lib .tplitem[data-tpl="koda.fx.wetDry"]');
  await expect(item).toBeVisible();
  await expect(item.locator('.tpl-component-badges')).toContainText('audio');
});

test('Show internal graph lists the processors, the wiring and the latency of an audio component', async ({ page }) => {
  await openApp(page, { reset: false });
  await expect(node(page, 'blend')).toHaveCount(1);
  await runItem(page, 'blend', 'Show internal graph');
  const body = page.locator('#dlg-b');
  await expect(body).toContainText('delay1');
  await expect(body).toContainText('reverb1');
  await expect(body).toContainText('output audio');
  await expect(body).toContainText('latency');
  await page.locator('#dlg-btns button', { hasText: 'Close' }).click();
});

test('Migrate to version shows the impact and the steps before anything changes', async ({ page }) => {
  await openApp(page, { reset: false });
  await expect(node(page, 'oldWay')).toHaveCount(1);
  await runItem(page, 'oldWay', 'Migrate to version', '2.0.0');
  const body = page.locator('#dlg-b');
  await expect(body).toContainText('Impact: Equivalent');
  await expect(body).toContainText('1.0.0 → 2.0.0');
  await expect(body).toContainText('copies');
  await page.locator('#dlg-btns button', { hasText: 'Cancel' }).click();
});
