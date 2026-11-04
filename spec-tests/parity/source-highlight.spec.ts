import { test, expect } from '@playwright/test';
import { freshServer, openApp, setSrc } from '../../web/tests/support';

// The language layer is coloured in the source editor (docs/language/14): keywords of helpers and control flow, types, calls of helpers and built-ins, units, named arguments, formulas in properties.
freshServer();
const TEXT = [
  'composition "p" { ample 1  tempo 100  master-seed 3 }',
  '',
  'type Steps = Int | Float',
  'helper delayBy(Notes n, Steps s) -> Notes {',
  '  if s is Int { return n |> shift(s * 1beat) }',
  '  return n |> shift(Round(s) * 1beat)',
  '}',
  '',
  'midi-clip at 1 for auto {',
  '  notes walk(C, dorian, steps=16, seed=stable("live"))',
  '    |> transpose(bake delayBy(theme.notes, 2) as +7st)',
  '}',
  '',
].join('\n');

test.beforeEach(async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await setSrc(page, TEXT);
  await expect(page.locator('.cm-content')).toContainText('delayBy');
});

const colorOf = (page, text: string) => page.locator('.cm-content span').evaluateAll((spans, t) => { const hit = spans.find((e) => e.textContent === t); return hit ? getComputedStyle(hit).color : ''; }, text);

test('helper code has its own colours for definitions, control flow, types, calls and units', async ({ page }) => {
  const plain = await page.locator('.cm-content').evaluate((e) => getComputedStyle(e).color);
  const kinds: Record<string, string> = {};
  for (const [label, token] of [['definition', 'helper'], ['control', 'if'], ['operator word', 'is'], ['type', 'Notes'], ['call', 'delayBy'], ['builtin', 'Round'], ['unit', 'beat'], ['number', '1']] as const) kinds[label] = await colorOf(page, token);
  for (const [label, color] of Object.entries(kinds)) expect(color, `${label} is coloured`).not.toBe(plain);
  expect(kinds.control).not.toBe(kinds.type);
  expect(kinds.call).not.toBe(kinds.builtin);
  expect(kinds.unit).not.toBe(kinds.number);
});

test('the expression of a notes line and a bake are coloured like helper code', async ({ page }) => {
  const plain = await page.locator('.cm-content').evaluate((e) => getComputedStyle(e).color);
  expect(await colorOf(page, 'walk')).not.toBe(plain);       // a call in a notes expression
  expect(await colorOf(page, 'dorian')).not.toBe(plain);     // a scale name
  expect(await colorOf(page, 'bake')).not.toBe(plain);       // the bake keyword
  expect(await colorOf(page, 'transpose')).not.toBe(plain);  // an operator after |>
});
