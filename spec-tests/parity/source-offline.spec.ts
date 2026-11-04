import { test, expect } from '@playwright/test';
import { freshServer, openApp, setSrc, srcText } from '../../web/tests/support';

// Without the language server the source editor still shows what the browser can find in the helpers by itself (the same checks as the server: spec-tests/note-lang/static.json).
freshServer();

test('"re turn 3" and an unknown name are found by the browser when the language server cannot be reached', async ({ page }) => {
  await page.routeWebSocket('**/lsp', (ws) => { ws.close(); });   // the server of the language is not there
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
  const song = [
    'composition "p" { ample 1  tempo 100  master-seed 3 }',
    '',
    'helper octaveFor(Int p) -> Octave {',
    '  if p % 2 == 0 { return 5 }',
    '  else { re turn 3 }',
    '}',
    '',
    'helper g(Int p) -> Int {',
    '  if p == 0 { return 1 }',
    '  else { return nothingHere }',
    '}',
    '',
  ].join('\n');
  await setSrc(page, song);
  await expect(page.locator('#se-prob')).toContainText('AMP1111', { timeout: 20000 });
  await expect(page.locator('#se-prob')).toContainText('AMP2082');
  await expect(page.locator('#toast')).toContainText('language server is not reachable');
});

test('a helper that nothing calls (AMP2086) is also found by the browser when the language server cannot be reached', async ({ page }) => {
  await page.routeWebSocket('**/lsp', (ws) => { ws.close(); });
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
  const song = [
    'composition "p" { ample 1  tempo 100  master-seed 3 }',
    '',
    'helper up(Notes source) -> Notes {',
    '  source |> transpose(+12)',
    '}',
    '',
    'midi-clip theme at 1 for 4 {',
    '  notes 1:0:1:60:100',
    '}',
    '',
  ].join('\n');
  await setSrc(page, song);
  await expect(page.locator('#se-prob')).toContainText('AMP2086', { timeout: 20000 });
});

test('# ample-ignore silences a note of the browser too, without the language server', async ({ page }) => {
  await page.routeWebSocket('**/lsp', (ws) => { ws.close(); });
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
  const song = [
    'composition "p" { ample 1  tempo 100  master-seed 3 }',
    '',
    '# ample-ignore AMP2086',
    'helper up(Notes source) -> Notes {',
    '  source |> transpose(+12)',
    '}',
    '',
    'helper down(Notes source) -> Notes {',
    '  source |> transpose(-12)',
    '}',
    '',
    'midi-clip theme at 1 for 4 {',
    '  notes 1:0:1:60:100',
    '}',
    '',
  ].join('\n');
  await setSrc(page, song);
  await expect(page.locator('#se-prob')).toContainText('AMP2086', { timeout: 20000 });   // down is not silenced
  await expect(page.locator('#se-prob')).toContainText('"down"');
  await expect(page.locator('#se-prob')).not.toContainText('"up"');   // up is
});
