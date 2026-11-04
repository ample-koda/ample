import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp, setSrc, srcText } from '../../web/tests/support';

const COMPONENT = '/** Repeats the notes. */\n#[DisplayName("Triple notes")]\ncomponent koda.notes.tripleNotes version 1.0.0 current {\n  id "demo.koda.notes.triple-notes"\n  input source: Notes {\n    optional true\n    fallback localNotes\n  }\n  #[Editor(jam)]\n  property localNotes: Notes\n  property copies: Int = 3\n  property spacing: Duration = 1bar\n  output notes: Notes\n  process {\n    let n = connected(source) ? source : localNotes\n    for i in 0..copies {\n      emit n |> shift(i * spacing)\n    }\n  }\n}\n';

freshServer({
  song: 'composition "components" { ample 1  tempo 100  master-seed 1 }\nimport "components/triple-notes.amplec"\nmidi-clip intro at 1 for 1 {\n  notes 1:0:1:60:100 1:1:1:62:100\n}\nkoda.notes.tripleNotes alone at 2 for 3 {\n  copies 3\n  localNotes 1:0:1:64:100\n}\n',
  prepare: (_cli, folder) => {
    fs.mkdirSync(path.join(folder, 'components'), { recursive: true });
    fs.writeFileSync(path.join(folder, 'components', 'triple-notes.amplec'), COMPONENT);
  },
});

test('a component instance is a known block with its own ports, not an unknown type', async ({ page }) => {
  await openApp(page, { reset: false });
  const block = node(page, 'alone');
  await expect(block).toBeVisible();
  await expect(block.locator('.tag.missing')).toHaveCount(0);
  await expect(block).toContainText('TRIPLE NOTES');   // the friendly name (DisplayName), the full name is on hover
});

test('the palette lists the component in its own group with the seal status', async ({ page }) => {
  await openApp(page, { reset: false });
  const item = page.locator('#lib .tplitem[data-tpl="koda.notes.tripleNotes"]');
  await expect(item).toBeVisible();
  await expect(item.locator('.tpl-component-badges')).toContainText('unsealed');
  await expect(page.locator('#lib details summary', { hasText: 'Components · koda.notes' })).toBeVisible();
});

test('removing the import makes the block unknown and putting it back makes it known again', async ({ page }) => {
  await openApp(page, { reset: false });
  const doc = await api('/api/doc?path=song.amplet');
  const without = doc.text.replace('import "components/triple-notes.amplec"\n', '');
  await page.evaluate((text) => import('/app/store.js').then((m) => m.applyText(text)), without);
  await expect(node(page, 'alone').locator('.tag.missing')).toHaveCount(1);
  await page.evaluate((text) => import('/app/store.js').then((m) => m.applyText(text)), doc.text);
  await expect(node(page, 'alone').locator('.tag.missing')).toHaveCount(0);
});

// the source view: the language server reads the components too (this was missed once: the board was right, the editor said AMP2001)
const ctrlClickAt = (page, needle: string, extra = 0) => page.locator('#se-ta').evaluate((ta: HTMLTextAreaElement, [n, e]: [string, number]) => {
  const i = ta.value.indexOf(n) + e;
  ta.focus();
  ta.setSelectionRange(i, i);
  ta.dispatchEvent(new MouseEvent('click', { ctrlKey: true, bubbles: true }));
}, [needle, extra] as [string, number]);

test('the source editor knows the component as a block type (no AMP2001)', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page), { timeout: 15000 }).toMatch(/koda\.notes\.tripleNotes alone/);
  await page.waitForTimeout(1500);
  await expect(page.locator('#se-prob')).not.toContainText('AMP2001');
  await expect(page.locator('#se-prob')).not.toContainText('Unknown block type');
});

test('Ctrl+click on the type of a component opens the file that declares it, on its declaration', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page), { timeout: 15000 }).toMatch(/koda\.notes\.tripleNotes alone/);
  await page.waitForTimeout(1500);
  await ctrlClickAt(page, 'koda.notes.tripleNotes alone', 4);
  await expect.poll(() => srcText(page), { timeout: 15000 }).toMatch(/component koda\.notes\.tripleNotes version 1\.0\.0/);
});

test('Ctrl+click on the path of an import opens the component file as text', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page), { timeout: 15000 }).toMatch(/import "components\/triple-notes\.amplec"/);
  await ctrlClickAt(page, 'components/triple-notes.amplec', 8);
  await expect.poll(() => srcText(page), { timeout: 15000 }).toMatch(/component koda\.notes\.tripleNotes version 1\.0\.0/);
});

test('a component shows its friendly name on the card, with the full name and id on hover and in the properties', async ({ page }) => {
  await openApp(page, { reset: false });
  const label = node(page, 'alone').locator('.node-type');
  await expect(label).toHaveText('TRIPLE NOTES');
  await expect(label).toHaveAttribute('title', /full name: koda\.notes\.tripleNotes/);
  await expect(label).toHaveAttribute('title', /id: demo\.koda\.notes\.triple-notes/);
  await node(page, 'alone').click();
  await expect(page.locator('#props .prop', { hasText: 'friendly name' })).toContainText('Triple notes');
  await expect(page.locator('#props .prop', { hasText: 'full name' })).toContainText('koda.notes.tripleNotes');
});

test('a component file in the source editor gets diagnostics while it is typed', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page), { timeout: 15000 }).toMatch(/import "components\/triple-notes\.amplec"/);
  await ctrlClickAt(page, 'components/triple-notes.amplec', 8);
  await expect.poll(() => srcText(page), { timeout: 15000 }).toMatch(/component koda\.notes\.tripleNotes version 1\.0\.0/);
  await expect(page.locator('#se-prob')).not.toContainText('AMP22');
  await setSrc(page, (await srcText(page)).replace('emit n |> shift(i * spacing)', 'emit n |> wobble(i * spacing)\n    emit ghost'));
  await expect(page.locator('#se-prob')).toContainText(/AMP2\d\d\d/, { timeout: 15000 });
});

test('hovering at 2 for 3 on the line of a component shows its start, end and duration', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page), { timeout: 15000 }).toMatch(/koda\.notes\.tripleNotes alone/);
  const line = page.locator('.cm-line', { hasText: 'koda.notes.tripleNotes alone' }).first();
  await line.scrollIntoViewIfNeeded();
  const box = (await line.boundingBox())!;
  const text = 'koda.notes.tripleNotes alone at 2 for 3 {';
  const charW = await line.evaluate((e, n) => { const r = document.createRange(); r.setStart(e.firstChild!, 0); r.setEnd(e.firstChild!, 1); return r.getBoundingClientRect().width || e.getBoundingClientRect().width / n; }, text.length);
  const x = box.x + charW * (text.indexOf('at 2') + 2);
  await expect.poll(async () => { await page.mouse.move(x, box.y - 30); await page.mouse.move(x, box.y + box.height / 2, { steps: 4 }); return page.locator('#se-timetip').isVisible(); }, { timeout: 15000 }).toBe(true);
  await expect(page.locator('#se-timetip')).toContainText('duration');
});

test('Ctrl+Space in a component file suggests the types after a property name and the attributes after #[', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page), { timeout: 15000 }).toMatch(/import "components\/triple-notes\.amplec"/);
  await ctrlClickAt(page, 'components/triple-notes.amplec', 8);
  await expect.poll(() => srcText(page), { timeout: 15000 }).toMatch(/component koda\.notes\.tripleNotes version 1\.0\.0/);
  const caretAfter = (needle: string) => page.locator('#se-ta').evaluate((ta: HTMLTextAreaElement, n: string) => { const i = ta.value.indexOf(n) + n.length; ta.focus(); ta.setSelectionRange(i, i); }, needle);
  // after "property timing:" every type is offered
  await setSrc(page, (await srcText(page)).replace('property copies: Int = 3', 'property timing: \n  property copies: Int = 3'));
  await caretAfter('property timing: ');
  await page.keyboard.press('Control+Space');
  await expect(page.locator('#se-comp')).toBeVisible({ timeout: 10000 });
  await expect(page.locator('#se-comp')).toContainText('Duration');
  await page.keyboard.press('Escape');
  // after #[ the attributes
  await setSrc(page, (await srcText(page)).replace('property timing: \n', '#[\n  property timing: Duration\n'));
  await caretAfter('#[');
  await page.keyboard.press('Control+Space');
  await expect(page.locator('#se-comp')).toContainText('MinValue', { timeout: 10000 });
  await expect(page.locator('#se-comp')).toContainText('Tags');
});

test('in a component file the name of an attribute and its values have their own colours', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page), { timeout: 15000 }).toMatch(/import "components\/triple-notes\.amplec"/);
  await ctrlClickAt(page, 'components/triple-notes.amplec', 8);
  await expect.poll(() => srcText(page), { timeout: 15000 }).toMatch(/#\[DisplayName/);
  const line = page.locator('.cm-line', { hasText: 'DisplayName' }).first();
  const colourOf = (text: string) => line.locator('span', { hasText: text }).last().evaluate((e) => getComputedStyle(e).color);
  const name = await colourOf('DisplayName');
  const value = await colourOf('"Triple notes"');
  const brackets = await line.locator('span').first().evaluate((e) => getComputedStyle(e).color);
  expect(name).not.toBe(value);
  expect(brackets).not.toBe(name);
});

test('a component block is not marked dynamic: no dynamic badge, no variants or bake menu', async ({ page }) => {
  await openApp(page, { reset: false });
  await expect(node(page, 'alone')).toHaveCount(1);
  await expect(node(page, 'alone').locator('.tag.dyn')).toHaveCount(0);
  expect(await page.evaluate(async () => (await import('/app/store.js')).flat().nodes.find((n: { id: string }) => n.id === 'alone')?.dyn)).toBe(false);
});
