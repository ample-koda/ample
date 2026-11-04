import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The mini layout of a component: what the properties panel (and later the card) shows for the properties of a custom component.
const COMPONENT = '/** Repeats the notes. */\ncomponent koda.notes.demoUi version 1.0.0 current {\n  id "demo.koda.notes.demo-ui"\n  input source: Notes\n  #[MinValue(1), MaxValue(16)]\n  property copies: Int = 3\n  #[MinValue(0%), MaxValue(90%)]\n  property amount: Percent = 30%\n  property flip: Bool = false\n  #[Options("soft", "hard")]\n  property shape: String = "soft"\n  output notes: Notes\n  process {\n    for i in 0..copies {\n      emit source |> shift(i * 1bar)\n    }\n  }\n}\n';

freshServer({
  song: 'composition "ui" { ample 1  tempo 100  master-seed 1 }\nimport "components/demo-ui.amplec"\nmidi-clip melody at 1 for 1 {\n  notes 1:0:1:60:100\n}\nkoda.notes.demoUi demo at 2 for 3 {\n  from melody.notes -> source\n}\n',
  prepare: (_cli, folder) => {
    fs.mkdirSync(path.join(folder, 'components'), { recursive: true });
    fs.writeFileSync(path.join(folder, 'components', 'demo-ui.amplec'), COMPONENT);
  },
});

test('a property with a range is a number field with a slider, Bool and Options are choices', async ({ page }) => {
  await openApp(page, { reset: false });
  await node(page, 'demo').click();
  const prop = (name: string) => page.locator('#props .prop', { hasText: name }).first();
  await expect(prop('copies').locator('input[type=number]')).toHaveAttribute('max', '16');
  await expect(prop('copies').locator('input.numslider')).toHaveCount(1);
  await expect(prop('amount').locator('input[type=number]')).toHaveAttribute('max', '90');
  await expect(prop('flip').locator('select option')).toHaveText(['true', 'false']);
  await expect(prop('shape').locator('select option')).toHaveText(['soft', 'hard']);
});

test('a value from the number field is written the way a component writes it (35%, no space)', async ({ page }) => {
  await openApp(page, { reset: false });
  await node(page, 'demo').click();
  const field = page.locator('#props .prop', { hasText: 'amount' }).first().locator('input[type=number]');
  await field.fill('35');
  await field.press('Enter');
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text, { timeout: 10000 }).toContain('amount 35%');
});

// a component with a ui block: rows, a group, a knob, a slider, a toggle, a choice and an xy pad
const LAID = '/** Laid out. */\n#[DisplayName("Laid out")]\ncomponent koda.notes.laidOut version 1.0.0 current {\n  id "demo.koda.notes.laid-out"\n  input source: Notes\n  #[MinValue(0%), MaxValue(100%)]\n  property amount: Percent = 30%\n  #[MinValue(-24dB), MaxValue(6dB)]\n  property level: Gain = -3dB\n  #[MinValue(1), MaxValue(16)]\n  property copies: Int = 3\n  property tone: Percent = 50%\n  property depth: Percent = 10%\n  property flip: Bool = false\n  #[Options("soft", "hard")]\n  property shape: String = "soft"\n  property label: String = "x"\n  output notes: Notes\n  ui {\n    row {\n      knob amount size l\n      slider level\n    }\n    group "Voice" {\n      toggle flip\n      choice shape\n      xy x=tone y=depth\n    }\n    knob copies\n    card { knob amount size s  toggle flip }\n  }\n  process {\n    for i in 0..copies {\n      emit source |> shift(i * 1bar)\n    }\n  }\n}\n';

test.describe('a ui block', () => {
  freshServer({
    song: 'composition "laid" { ample 1  tempo 100  master-seed 1 }\nimport "components/laid-out.amplec"\nmidi-clip melody at 1 for 1 {\n  notes 1:0:1:60:100\n}\nkoda.notes.laidOut laid at 2 for 3 {\n  from melody.notes -> source\n}\n',
    prepare: (_cli, folder) => {
      fs.mkdirSync(path.join(folder, 'components'), { recursive: true });
      fs.writeFileSync(path.join(folder, 'components', 'laid-out.amplec'), LAID);
    },
  });

  test('draws the widgets in the panel and leaves the properties it does not show in the standard rows', async ({ page }) => {
    await openApp(page, { reset: false });
    await node(page, 'laid').click({ position: { x: 24, y: 12 } });   // the head of the card: the middle is the controls
    const cui = page.locator('#props .cui');
    await expect(cui).toBeVisible();
    await expect(cui.locator('.cui-row .studio-knob, .cui-row svg[role=slider]').first()).toBeVisible();
    await expect(cui.locator('.cui-group legend')).toHaveText('Voice');
    await expect(cui.locator('.cui-toggle button[role=switch]')).toHaveCount(1);
    await expect(cui.locator('.cui-choice select option')).toHaveText(['soft', 'hard']);
    await expect(cui.locator('.cui-xy input[type=range]')).toHaveCount(2);
    // "label" is not in the layout: it keeps its standard row, the ones in the layout have none
    await expect(page.locator('#props .prop', { hasText: 'label' }).first()).toBeVisible();
    await expect(page.locator('#props .prop', { hasText: 'amount' })).toHaveCount(0);
  });

  test('a toggle and a choice write the value the way the song writes it, an xy pad both axes in one step', async ({ page }) => {
    await openApp(page, { reset: false });
    await node(page, 'laid').click({ position: { x: 24, y: 12 } });   // the head of the card: the middle is the controls
    await page.locator('#props .cui-toggle button[role=switch]').click();
    await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text, { timeout: 10000 }).toContain('flip true');
    await page.locator('#props .cui-choice select').selectOption('hard');
    await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text, { timeout: 10000 }).toContain('shape "hard"');
    const sliders = page.locator('#props .cui-xy input[type=range]');
    await sliders.nth(0).fill('70');
    await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text, { timeout: 10000 }).toContain('tone 70%');
  });

  test('the card shows its own controls, a knob dragged on it changes the value and never moves the block, and the button opens them in a window', async ({ page }) => {
    await openApp(page, { reset: false });
    const block = node(page, 'laid');
    await expect(block.locator('.cui-card-holder svg[role=slider]')).toBeVisible();
    await expect(block.locator('.cui-card-holder button[role=switch]')).toBeVisible();
    const before = (await block.boundingBox())!;
    const knobBox = (await block.locator('.cui-card-holder svg[role=slider]').boundingBox())!;
    await page.mouse.move(knobBox.x + knobBox.width / 2, knobBox.y + knobBox.height / 2);
    await page.mouse.down();
    await page.mouse.move(knobBox.x + knobBox.width / 2, knobBox.y + knobBox.height / 2 - 40, { steps: 6 });
    await page.mouse.up();
    await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text, { timeout: 10000 }).toMatch(/amount \d+%/);
    // the block is drawn again after the write: measure when it is there; it must be where it was
    await expect.poll(async () => { const now = await block.boundingBox(); return now ? Math.abs(now.x - before.x) + Math.abs(now.y - before.y) : 999; }, { timeout: 10000 }).toBeLessThan(2);
    await block.locator('.cui-open').click();
    await expect(page.locator('#dlg .cui-popup')).toBeVisible();
    await expect(page.locator('#dlg .cui-popup .cui-xy input[type=range]')).toHaveCount(2);   // the whole panel layout, not only the card
    await page.locator('#dlg-btns button', { hasText: 'Close' }).click();
  });
});
