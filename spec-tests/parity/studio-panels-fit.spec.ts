import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, node, openApp } from '../../web/tests/support';

// The window of an effect panel is as tall as the panel needs (up to the screen), so the knobs of compressor and peq are not cut off under the play bar,
// and the shape of a reverb room, a de-esser band or the stereo image is tinted instead of a black hole.
freshServer();
test.beforeEach(async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 1100 });
  await openApp(page, { reset: false });
});

async function open(page, type: string) {
  await addTemplateBlock(page, type, `p-${type}`);
  await node(page, `p-${type}`).getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const dialog = page.locator('#studio[open]');
  await expect(dialog).toBeVisible();
  return dialog;
}

for (const type of ['compressor', 'peq']) {
  test(`the ${type} panel shows all its knobs without scrolling on a 1100 px screen`, async ({ page }) => {
    const dialog = await open(page, type);
    await expect.poll(() => dialog.locator('.studio-dialog-body').evaluate((el) => el.scrollHeight - el.clientHeight)).toBeLessThanOrEqual(2);
  });
}

for (const type of ['reverb', 'deesser', 'width']) {
  test(`the shape in the ${type} panel is tinted, not the colour of the empty graph`, async ({ page }) => {
    const dialog = await open(page, type);
    const [shape, plain] = await dialog.locator('.studio-room-shape').first().evaluate((el) => {
      const probe = document.createElement('div'); probe.style.background = 'var(--panel3)'; document.body.append(probe);
      const out = [getComputedStyle(el).fill, getComputedStyle(probe).backgroundColor]; probe.remove(); return out;
    });
    expect(shape).not.toBe(plain);
  });
}

test('the six bands of the parametric EQ are on one row', async ({ page }) => {
  const dialog = await open(page, 'peq');
  const tops = await dialog.locator('.studio-peq-band').evaluateAll((bands) => bands.map((band) => Math.round(band.getBoundingClientRect().top)));
  expect(tops).toHaveLength(6);
  expect(new Set(tops).size).toBe(1);
});

test('the six knobs of the compressor are on one row', async ({ page }) => {
  const dialog = await open(page, 'compressor');
  const tops = await dialog.locator('.studio-eq-controls > *').evaluateAll((cells) => cells.map((cell) => Math.round(cell.getBoundingClientRect().top)));
  expect(tops.length).toBeGreaterThanOrEqual(6);
  expect(new Set(tops).size).toBe(1);
});
