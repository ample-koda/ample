import { test, expect } from '@playwright/test';
import { freshServer, openApp, setSrc, srcText } from '../../web/tests/support';

// On a phone the browser scrolls ahead of the page's own scripts, so a layer that is moved along with a script (the wavy lines, the line numbers) lagged behind the text.
// They are part of the editor itself now: inside the same scroller as the text, so they cannot be out of step.
freshServer();
test.use({ hasTouch: true, isMobile: true, viewport: { width: 390, height: 844 } });

test('the wavy lines and the line numbers scroll with the text because they are inside the scroller', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
  const filler = Array.from({ length: 150 }, (_, i) => `# line ${i + 1}`).join('\n');
  await setSrc(page, 'composition "p" { ample 1  tempo 100  master-seed 3 }\n\nmidi-clip a at 1 for auto {\n  notes "1:0:1:60:100 9:9:9"\n}\n\n' + filler + '\n');
  await expect(page.locator('#se-prob')).toContainText('AMP2012', { timeout: 20000 });
  await expect(page.locator('.cm-scroller .cm-content .sq.error').first()).toBeAttached();   // the mark is a decoration of the text
  await expect(page.locator('.cm-scroller .cm-ample-gutter').first()).toBeAttached();        // so are the numbers
  const aligned = await page.evaluate(async () => {
    const scroller = document.querySelector('.cm-scroller') as HTMLElement;
    scroller.scrollTop = 600;
    await new Promise((resolve) => requestAnimationFrame(() => resolve(null)));   // the very next frame: a script-synced layer would still be behind
    const numbers = [...document.querySelectorAll('.cm-ample-gutter .cm-gutterElement')] as HTMLElement[];
    const rows = [...document.querySelectorAll('.cm-content .cm-line')] as HTMLElement[];
    const visible = rows.filter((r) => r.getBoundingClientRect().bottom > 0 && r.getBoundingClientRect().top < innerHeight).slice(0, 5);
    return visible.map((row) => { const at = rows.indexOf(row); const gutterRow = numbers.find((n) => Math.abs(n.getBoundingClientRect().top - row.getBoundingClientRect().top) < 1.5); return !!gutterRow && (gutterRow.textContent || '').trim().endsWith(String(Number(row.textContent?.match(/line (\d+)/)?.[1] ?? at + 1) + 6)); });   // six lines of song come before the filler
  });
  expect(aligned.length).toBeGreaterThan(0);
  expect(aligned.every(Boolean)).toBe(true);
});

test('the gutter has the dark background of the editor (not the light grey of the base theme of CodeMirror) and is opaque, so scrolled text does not show through it', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
  const look = await page.locator('.cm-gutters').evaluate((e) => { const s = getComputedStyle(e); return { background: s.backgroundColor, editor: getComputedStyle(e.closest('.cm-editor') as Element).backgroundColor, border: s.borderRightWidth }; });
  expect(look.border).toBe('0px');
  expect(look.background).not.toBe('rgba(0, 0, 0, 0)');
  expect(look.background).toBe(look.editor);
});
