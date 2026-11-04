import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// More options of the layout: a tint for a widget or a group, a friendly name for a property, and the curve widget (x:y points from 0 to 1 in a text property).
const COMPONENT = '/** Options. */\ncomponent koda.notes.options version 1.0.0 current {\n  id "demo.koda.notes.options"\n  input source: Notes\n  #[DisplayName("How many"), MinValue(1), MaxValue(16)]\n  property copies: Int = 3\n  #[MinValue(0%), MaxValue(100%)]\n  property amount: Percent = 30%\n  property shape: String = "0:0 1:1"\n  output notes: Notes\n  ui {\n    group "Repeat" tint blue {\n      knob copies tint red\n      slider amount tint "#ff8800"\n    }\n    curve shape label "Swing curve"\n  }\n  process {\n    for i in 0..copies {\n      emit source |> shift(i * 1bar)\n    }\n  }\n}\n';

freshServer({
  song: 'composition "options" { ample 1  tempo 100  master-seed 1 }\nimport "components/options.amplec"\nmidi-clip melody at 1 for 1 {\n  notes 1:0:1:60:100\n}\nkoda.notes.options opts at 2 for 3 {\n  from melody.notes -> source\n}\n',
  prepare: (_cli, folder) => {
    fs.mkdirSync(path.join(folder, 'components'), { recursive: true });
    fs.writeFileSync(path.join(folder, 'components', 'options.amplec'), COMPONENT);
  },
});

test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); await node(page, 'opts').click({ position: { x: 24, y: 12 } }); });

test('a tint colours the knob, the slider and the group, a friendly name is the label with the name in the tooltip', async ({ page }) => {
  const cui = page.locator('#props .cui');
  await expect(cui.locator('svg[role=slider]').first()).toHaveAttribute('aria-label', 'How many');
  expect(await cui.locator('svg[role=slider]').first().evaluate((e) => (e as SVGElement).style.getPropertyValue('--knob-accent'))).toBe('var(--bad)');
  expect(await cui.locator('.cui-slider').evaluate((e) => (e as HTMLElement).style.getPropertyValue('--cui-tint'))).toBe('#ff8800');
  expect(await cui.locator('.cui-group').evaluate((e) => (e as HTMLElement).style.getPropertyValue('--cui-tint'))).toBe('var(--audio)');
  await expect(cui.locator('.cui-knob')).toHaveAttribute('title', /How many \(copies\)/);   // the full name stays visible on hover
});

test('the curve widget draws the points of the text and a double click adds one, written as x:y pairs', async ({ page }) => {
  const curve = page.locator('#props .cui-curve');
  await expect(curve.locator('.cui-label')).toHaveText('Swing curve');
  await expect(curve.locator('circle')).toHaveCount(2);   // 0:0 and 1:1
  const box = (await curve.locator('svg').boundingBox())!;
  await curve.locator('svg').dblclick({ position: { x: box.width / 2, y: box.height / 4 } });
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text, { timeout: 10000 }).toMatch(/shape "0:0 0\.\d+:0\.\d+ 1:1"/);
  await expect(curve.locator('circle')).toHaveCount(3);
});
