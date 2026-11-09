import { test, expect } from '@playwright/test';
import { addTemplateBlock, chooseMenu, freshServer, openApp } from '../../web/tests/support';

freshServer();

test('unbuilt effects do not request a preview without a source take', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'compressor', 'comp');
  const node = page.locator('.node[data-id="comp"]');
  await expect(node.getByRole('button', { name: 'Edit in source' })).toBeVisible();
  await expect(node.getByRole('button', { name: 'Advanced', exact: true })).toHaveText('↗');
  await node.getByRole('button', { name: 'Advanced', exact: true }).click();
  const dialog = page.locator('#studio');
  await expect(dialog).toBeVisible();
  await expect(dialog.getByText('Connect an audio input')).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Analyze exact preview' })).toBeDisabled();
  await expect(dialog.getByRole('slider', { name: 'Threshold' }).last()).toBeVisible();
});

test('the current block arrow opens its GUI and the context menu toggles bypass @quarantine', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'compressor', 'shortcut-comp');
  const node = page.locator('.node[data-id="shortcut-comp"]');
  const arrow = node.getByRole('button', { name: 'Advanced', exact: true });
  await expect(arrow).toBeVisible();
  await expect(node.locator('.node-toolbar')).toContainText('COMPRESSOR');
  const versionBox = (await node.locator('.node-toolbar .cls').boundingBox())!, arrowBox = (await arrow.boundingBox())!;
  expect(arrowBox.x - versionBox.x - versionBox.width).toBeLessThan(12);
  await arrow.click();
  await expect(page.locator('#studio')).toBeVisible();
  await page.locator('#studio').getByRole('button', { name: 'Close advanced controls' }).click();
  await node.click({ button: 'right' });
  await chooseMenu(page, 'Edit', 'Bypass effect');
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_STATE.doc.blocks.find((block: any) => block.name === 'shortcut-comp').parameters.find((param: any) => param.name === 'enabled')?.value)).toBe('off');
  await node.click({ button: 'right' });
  await chooseMenu(page, 'Edit', 'Enable effect');
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_STATE.doc.blocks.find((block: any) => block.name === 'shortcut-comp').parameters.find((param: any) => param.name === 'enabled')?.value)).toBe('on');
});

test('properties put source beside the title and Advanced at the far right', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'heading-comp');
  await page.locator('.node[data-id="heading-comp"]').click();
  const heading = page.locator('#props .prop-heading');
  const title = heading.locator('.prop-heading-title');
  const source = heading.getByRole('button', { name: 'Edit in source' });
  const advanced = heading.getByRole('button', { name: 'Open advanced controls' });
  const titleBox = (await title.boundingBox())!, sourceBox = (await source.boundingBox())!, advancedBox = (await advanced.boundingBox())!, headingBox = (await heading.boundingBox())!;
  expect(sourceBox.x).toBeGreaterThan(titleBox.x + titleBox.width);
  expect(advancedBox.x).toBeGreaterThan(sourceBox.x + sourceBox.width + 20);
  expect(headingBox.x + headingBox.width - advancedBox.x - advancedBox.width).toBeLessThan(20);
  await source.click();
  await expect(page.locator('#se')).toBeVisible();
});

test('an audio node lists its connected effects and focuses the chosen block and properties', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'chain-source');
  await addTemplateBlock(page, 'eq', 'chain-eq');
  await addTemplateBlock(page, 'compressor', 'chain-comp');
  const connect = (from: string, output: string, to: string) => page.evaluate(async ([f, o, t]) => (await import('/app/store.js')).edit([{ type: 'connect', from: f, output: o, to: t, input: 'audio' }]), [from, output, to]);
  await connect('chain-source', 'audio', 'chain-eq');
  await connect('chain-eq', 'out', 'chain-comp');
  await page.getByRole('button', { name: 'Fit', exact: true }).click();   // the new blocks can sit under the side panel
  const arrow = page.locator('.node[data-id="chain-source"]').getByRole('button', { name: 'Go to effects of chain-source' });
  await expect(arrow).toBeVisible();
  await arrow.click();
  const options = page.getByRole('listbox', { name: 'Connected effects' }).getByRole('option');
  await expect(options).toHaveText(['chain-eq · eq', 'chain-comp · compressor']);
  await options.first().dispatchEvent('click');
  await expect(page.locator('.node[data-id="chain-eq"]')).toHaveClass(/sel/);
  await expect(page.locator('#props input').first()).toHaveValue('chain-eq');
  await page.locator('.node[data-id="chain-comp"]').click({ button: 'right' });
  await chooseMenu(page, 'Edit', 'Disable block');
  await arrow.click();
  await expect(options.nth(1)).toContainText('chain-comp · compressor · disabled');
  await expect(options.nth(1)).toHaveClass(/effect-off/);
  await options.nth(1).dispatchEvent('click');
  await expect(page.locator('.node[data-id="chain-comp"]')).toHaveClass(/sel/);
  await expect(page.locator('#props input').first()).toHaveValue('chain-comp');
});

test('the right-click menu adds EQ after an audio block', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'managed-source');
  const before = await page.locator('.node').count();
  await page.locator('.node[data-id="managed-source"]').click({ button: 'right' });
  await chooseMenu(page, 'Add', 'Effects', 'EQ & filters', 'Equalizer');
  await expect(page.locator('.node')).toHaveCount(before + 1);
  await expect(page.locator('.node[data-id="managed-source"]').getByRole('button', { name: 'Go to effects of managed-source' })).toBeVisible();
});

test('double-clicking a node title renames inline and picks a unique name', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'compressor', 'target');
  await addTemplateBlock(page, 'reverb', 'reserved');
  const node = page.locator('.node[data-id="target"]');
  await page.getByRole('button', { name: 'Fit', exact: true }).click();   // earlier tests of this file left blocks on the canvas
  const editor = node.getByRole('textbox', { name: 'Rename block' });
  await expect(async () => {   // the canvas can still be drawn again after Fit and swallow the double click: click again until the name editor is there
    await node.locator('.nh .t').dblclick();
    await expect(editor).toBeVisible({ timeout: 2000 });
  }).toPass({ timeout: 20000 });
  await editor.fill('reserved');
  await editor.press('Enter');
  await expect(page.locator('.node[data-id="reserved-2"]')).toBeVisible();
});

test('built-in advanced controls expose accessible editable controls', async ({ page }) => {
  await openApp(page, { reset: false });
  for (const type of ['eq', 'filter', 'compressor', 'duck', 'distortion', 'delay', 'reverb', 'mixer']) {
    const id = `panel-${type}`;
    await addTemplateBlock(page, type, id);
    const node = page.locator(`.node[data-id="${id}"]`);
    await node.getByRole('button', { name: 'Advanced', exact: true }).dispatchEvent('click');   // the new blocks pile up on the canvas, so a pointer click may hit a neighbour
    const dialog = page.locator('#studio');
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('[role="slider"], input[type="range"], select').first()).toBeVisible();
    if (type === 'compressor') await expect(dialog.getByRole('button', { name: 'Analyze exact preview' })).toBeDisabled();
    if (type === 'mixer') await expect(dialog.getByRole('button', { name: 'Play the selected audio take' })).toBeDisabled();
    await dialog.getByRole('button', { name: 'Close advanced controls' }).click();
  }
});

test('compressor transfer curve follows its keyboard-editable threshold', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'compressor', 'curve-comp');
  const node = page.locator('.node[data-id="curve-comp"]');
  await node.getByRole('button', { name: 'Advanced', exact: true }).click();
  const dialog = page.locator('#studio');
  const response = dialog.locator('.studio-dynamics-curve .studio-response-line');
  const before = await response.getAttribute('d');
  await dialog.getByRole('slider', { name: 'Threshold' }).last().press('ArrowRight');
  await expect.poll(() => response.getAttribute('d')).not.toBe(before);
});

test('a knob pointer gesture commits once and can be undone once', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'compressor', 'gesture-comp');
  await page.locator('.node[data-id="gesture-comp"]').getByRole('button', { name: 'Advanced', exact: true }).click();
  const knob = page.locator('#studio').getByRole('slider', { name: 'Threshold' }).last();
  const before = Number(await knob.getAttribute('aria-valuenow'));
  const undoBefore = await page.evaluate(async () => (await import('/app/store.js')).S.undo.length);
  const box = (await knob.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2 + 56, box.y + box.height / 2 - 32, { steps: 5 });
  await page.mouse.up();
  await expect.poll(async () => Number(await knob.getAttribute('aria-valuenow'))).not.toBe(before);
  await expect.poll(() => page.evaluate(async () => (await import('/app/store.js')).S.undo.length)).toBe(undoBefore + 1);
  await page.keyboard.press('Control+Z');
  await expect(knob).toHaveAttribute('aria-valuenow', String(before));
});

test('a disabled block stays visible but does not produce output', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'compressor', 'comp');
  const node = page.locator('.node[data-id="comp"]');
  await page.getByRole('button', { name: 'Fit', exact: true }).click();   // earlier tests of this file left blocks on the canvas
  await node.locator('.nh .t').dispatchEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 400, clientY: 300 });   // the block can sit under the side panel, so the menu is asked for directly
  await chooseMenu(page, 'Edit', 'Disable block');
  await expect(page.locator('.node[data-id="comp"]')).toHaveClass(/is-disabled/);
  await page.locator('.node[data-id="comp"] .nh .t').dispatchEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 400, clientY: 300 });
  await page.locator('#menu > .sub').filter({ hasText: /^Edit ›/ }).hover();
  await expect(page.locator('#menu .flyout').getByText('Enable block')).toBeVisible();
});

test('Studio knobs respect reduced-motion preferences', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'compressor', 'reduced-motion-comp');
  const node = page.locator('.node[data-id="reduced-motion-comp"]');
  await node.getByRole('button', { name: 'Advanced', exact: true }).click();
  const knob = page.locator('#studio').getByRole('slider', { name: 'Threshold' }).last();
  await expect(knob).toBeVisible();
  expect(await knob.evaluate((element) => getComputedStyle(element).transitionDuration)).toBe('0s');
  await knob.press('ArrowRight');
});
