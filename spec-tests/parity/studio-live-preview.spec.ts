import { test, expect } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { addTemplateBlock, api, chooseMenu, freshServerPerTest, openApp, ownServer } from '../../web/tests/support';

freshServerPerTest();

const syntheticTake = 'synthetic-upstream-take';

function wavFixture() {
  const rate = 44_100, frames = rate * 4, bytes = Buffer.alloc(44 + frames * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12);
  bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24);
  bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
  for (let frame = 0; frame < frames; frame++) bytes.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 440 * frame / rate) * 9000), 44 + frame * 2);
  return bytes;
}

async function connect(page, fromId: string, toId: string) {
  const from = page.locator(`.node[data-id="${fromId}"] .port.out`).first();
  const to = page.locator(`.node[data-id="${toId}"] .port.in`).first();
  let a!: { x: number; y: number; width: number; height: number }, b!: typeof a;
  await expect(async () => {   // the canvas can draw the blocks again while this runs: take the ports again until both are there and still
    await from.scrollIntoViewIfNeeded(); await to.scrollIntoViewIfNeeded();
    const first = (await from.boundingBox())!, second = (await to.boundingBox())!;
    await page.waitForTimeout(150);
    const again = (await from.boundingBox())!, secondAgain = (await to.boundingBox())!;
    expect(again.x).toBe(first.x); expect(again.y).toBe(first.y); expect(secondAgain.x).toBe(second.x); expect(secondAgain.y).toBe(second.y);
    a = again; b = secondAgain;
  }).toPass({ timeout: 15000 });
  await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2); await page.mouse.down();
  await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2, { steps: 4 }); await page.mouse.up();
}

function mockSelectedAudio(page, previews: Array<{ block: string; takeId: string; params: Record<string, unknown> }>) {
  const wav = wavFixture();
  page.route('**/api/control/block*', async (route) => route.fulfill({ json: { selected: syntheticTake } }));
  page.route('**/api/effects/preview', async (route) => {
    const body = route.request().postDataJSON();
    previews.push({ block: body.block, takeId: body.takeId, params: body.params });
    await route.fulfill({ status: 200, contentType: 'audio/wav', body: wav });
  });
  page.route('**/api/takes/**', async (route) => {
    const pathname = new URL(route.request().url()).pathname;
    if (pathname.endsWith('/download')) await route.fulfill({ status: 200, contentType: 'audio/wav', body: wav });
    else await route.fulfill({ json: pathname.endsWith('/peaks') ? { peaks: [] } : { audioDataId: 'synthetic-audio-data' } });
  });
  page.route('**/api/data/**', async (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: wav }));
  return wav;
}

test('properties Advanced recognizes a connected convolution input', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'live-source');
  await addTemplateBlock(page, 'convolution', 'chamber');
  await connect(page, 'live-source', 'chamber');
  const openPanel = page.getByRole('button', { name: 'Close advanced controls' });
  if (await openPanel.isVisible()) await openPanel.click();
  await page.locator('.node[data-id="chamber"] .t').click();
  await page.locator('#props .studio-advanced-props').click();
  const footer = page.locator('#studio footer');
  await expect(footer).toContainText('Build audio to play');
  await expect(footer.getByRole('button', { name: 'Build and use input' })).toBeEnabled();
  await expect(footer).not.toContainText('Connect an audio input');
});

test('convolution previews its impulse locally and updates while playing', async ({ page }) => {
  await page.addInitScript(() => {
    (window as any).__ampleParamUpdates = 0;
    const original = AudioParam.prototype.setTargetAtTime;
    AudioParam.prototype.setTargetAtTime = function (...args) { (window as any).__ampleParamUpdates++; return original.apply(this, args); };
  });
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'live-source');
  await addTemplateBlock(page, 'convolution', 'chamber');
  await writeFile(join(ownServer()!.folder, 'preview-ir.wav'), wavFixture());
  for (const [name, value] of [['file', 'preview-ir.wav'], ['ir', 'file']]) {
    const doc = await api('/api/doc?path=song.amplet');
    await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: doc.version, op: { type: 'setParameter', block: 'chamber', name, value } } });
  }
  await page.reload(); await page.waitForSelector('body[data-ready="1"]');
  await connect(page, 'live-source', 'chamber');
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((store) => store.flat().links.some((link) => link.from === 'live-source' && link.to === 'chamber')))).toBe(true);
  const previews: Array<{ block: string; takeId: string; params: Record<string, unknown> }> = [];
  mockSelectedAudio(page, previews);
  let impulseRequests = 0;
  page.on('request', (request) => { if (new URL(request.url()).pathname === '/api/effects/impulse') impulseRequests++; });
  await page.locator('.node[data-id="chamber"]').getByRole('button', { name: 'Advanced', exact: true }).click();
  const dialog = page.locator('#studio');
  const live = dialog.getByRole('button', { name: 'Live local preview' });
  await expect(live).toBeEnabled();
  await live.click();
  await expect(dialog.locator('footer')).toContainText('Live local preview · chamber · approximate');
  expect(impulseRequests).toBe(1);
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_PLAYER.state())).toMatchObject({ playing: true, take: 'audition', auditionBlock: 'chamber' });
  const mix = dialog.getByRole('slider', { name: 'mix', exact: true });
  await mix.scrollIntoViewIfNeeded();
  const before = await page.evaluate(() => (window as any).__ampleParamUpdates);
  const bounds = (await mix.boundingBox())!;
  await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
  await page.mouse.down(); await page.mouse.move(bounds.x + bounds.width / 2 + 35, bounds.y + bounds.height / 2 - 30, { steps: 4 });
  await expect.poll(() => page.evaluate(() => (window as any).__ampleParamUpdates)).toBeGreaterThan(before);
  await page.mouse.up();
  expect(impulseRequests).toBe(1);
  await writeFile(join(ownServer()!.folder, 'preview-ir-next.wav'), wavFixture());
  const file = dialog.getByRole('textbox', { name: 'Project file path' });
  await file.evaluate((element) => { element.dataset.previewControl = 'same'; });
  const beforeIrChange = await page.evaluate(() => (window as any).AMPLE_PLAYER.state().time);
  await file.fill('preview-ir-next.wav'); await file.press('Enter');
  await expect.poll(() => impulseRequests).toBe(2);
  await expect(file).toHaveAttribute('data-preview-control', 'same');
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_PLAYER.state().time)).toBeGreaterThan(beforeIrChange);
  expect(previews).toHaveLength(0);
  await expect(dialog.locator('footer')).toContainText('Live local preview · chamber · approximate');
  await dialog.getByRole('button', { name: 'Close advanced controls' }).click();
  await page.locator('.node[data-id="chamber"] .t').click();
  await page.locator('#props .studio-advanced-props').click();
  await expect(dialog.getByRole('button', { name: 'Live local preview' })).toBeEnabled();
  await dialog.getByRole('button', { name: 'Live local preview' }).click();
  await expect(dialog.locator('footer')).toContainText('Live local preview · chamber · approximate');
  expect(impulseRequests).toBe(3);
});

test('live local preview changes the running EQ during a held knob drag @quarantine', async ({ page }) => {
  await page.addInitScript(() => {
    (window as any).__ampleParamUpdates = 0;
    (window as any).__amplePointerTrace = [];
    window.addEventListener('pointerdown', (event) => {
      const target = event.composedPath()[0] as HTMLElement;
      (window as any).__amplePointerTrace.push(`down:${target?.tagName}.${typeof target?.className === 'string' ? target.className : ''}`);
    }, true);
    window.addEventListener('pointermove', (event) => {
      const target = event.composedPath()[0] as HTMLElement;
      (window as any).__amplePointerTrace.push(`move:${target?.tagName}.${typeof target?.className === 'string' ? target.className : ''}`);
    }, true);
    const original = AudioParam.prototype.setTargetAtTime;
    AudioParam.prototype.setTargetAtTime = function (...args) {
      (window as any).__ampleParamUpdates++;
      return original.apply(this, args);
    };
  });
  const pageErrors: string[] = [];
  page.on('pageerror', (error) => pageErrors.push(`${error.name}: ${error.message}`));
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'live-source');
  await addTemplateBlock(page, 'eq', 'live-eq');
  await connect(page, 'live-source', 'live-eq');
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((store) => store.flat().links.some((link) => link.from === 'live-source' && link.to === 'live-eq')))).toBe(true);

  const previews: Array<{ block: string; takeId: string; params: Record<string, unknown> }> = [];
  mockSelectedAudio(page, previews);
  await page.locator('.node[data-id="live-eq"]').getByRole('button', { name: 'Advanced', exact: true }).click();
  const dialog = page.locator('#studio');
  const compare = dialog.locator('details.studio-preview-more');
  await expect(compare.locator('summary')).toHaveText('Compare / render');
  await compare.locator('summary').click();
  const inputPlay = dialog.locator('footer button').filter({ hasText: /^Play input$/ });
  await expect(inputPlay).toBeEnabled();
  await inputPlay.click();
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_PLAYER.state())).toMatchObject({ playing: true, take: syntheticTake });

  const live = dialog.getByRole('button', { name: 'Live local preview' });
  const previewCountBeforeStart = previews.length;
  await live.click();
  await expect(dialog.locator('.studio-preview-slot')).toContainText('Live local preview · live-eq · approximate');
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_PLAYER.state())).toMatchObject({ playing: true, take: 'audition', auditionBlock: 'live-eq' });
  expect(previews).toHaveLength(previewCountBeforeStart);
  expect(pageErrors).toEqual([]);
  if (process.env.AMPLE_PREVIEW_SCREENSHOT) await page.screenshot({ path: process.env.AMPLE_PREVIEW_SCREENSHOT });
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing)).toBe(true);

  let knob = dialog.getByRole('slider', { name: 'Mid', exact: true });
  await knob.scrollIntoViewIfNeeded();
  knob = dialog.getByRole('slider', { name: 'Mid', exact: true });
  const beforeValue = Number(await knob.getAttribute('aria-valuenow'));
  const beforePlayer = await page.evaluate(() => (window as any).AMPLE_PLAYER.state());
  const beforeAutomation = await page.evaluate(() => (window as any).__ampleParamUpdates);
  const beforePreviewRequests = previews.length;
  const bounds = (await knob.boundingBox())!;
  await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
  await page.mouse.down();
  await page.mouse.move(bounds.x + bounds.width / 2 + 56, bounds.y + bounds.height / 2 - 42, { steps: 6 });
  await expect.poll(async () => Number(await knob.getAttribute('aria-valuenow')), { message: JSON.stringify(await page.evaluate(() => (window as any).__amplePointerTrace.slice(-12))) }).not.toBe(beforeValue);
  await expect.poll(() => page.evaluate(() => (window as any).__ampleParamUpdates)).toBeGreaterThan(beforeAutomation);
  await page.waitForTimeout(200);
  const duringDrag = await page.evaluate(() => (window as any).AMPLE_PLAYER.state());
  expect(duringDrag.playing).toBe(true);
  expect(duringDrag.take).toBe('audition');
  expect(duringDrag.time).toBeGreaterThan(beforePlayer.time);
  expect(previews).toHaveLength(beforePreviewRequests);

  const editResponse = page.waitForResponse((response) => new URL(response.url()).pathname === '/api/doc/edit');
  await page.mouse.up();
  const edited = await editResponse;
  expect(edited.status()).toBe(200);
  expect(edited.request().postDataJSON().op).toMatchObject({ type: 'setParameter', block: 'live-eq', name: 'mid' });
  const afterRelease = await page.evaluate(() => (window as any).AMPLE_PLAYER.state());
  expect(afterRelease.playing).toBe(true);
  expect(afterRelease.time).toBeGreaterThan(duringDrag.time);

  const ab = dialog.getByRole('button', { name: 'A/B' });
  const beforeAb = await page.evaluate(() => (window as any).AMPLE_PLAYER.state());
  const automationBeforeAb = await page.evaluate(() => (window as any).__ampleParamUpdates);
  const previewCountBeforeAb = previews.length;
  await ab.click();
  await expect(ab).toHaveAttribute('aria-pressed', 'true');
  await expect.poll(() => page.evaluate(() => (window as any).__ampleParamUpdates)).toBeGreaterThan(automationBeforeAb);
  await page.waitForTimeout(150);
  const duringAb = await page.evaluate(() => (window as any).AMPLE_PLAYER.state());
  expect(duringAb.playing).toBe(true);
  expect(duringAb.take).toBe('audition');
  expect(duringAb.time).toBeGreaterThan(beforeAb.time);
  expect(previews).toHaveLength(previewCountBeforeAb);
  await ab.click();
  await expect(ab).toHaveAttribute('aria-pressed', 'false');
});

test('the canvas effect picker searches connected branches and focuses the chosen effect', async ({ page }) => {
  const pageErrors: string[] = [];
  page.on('pageerror', (error) => pageErrors.push(`${error.name}: ${error.message}`));
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'picker-source');
  await addTemplateBlock(page, 'eq', 'picker-eq');
  await addTemplateBlock(page, 'reverb', 'picker-reverb');
  const ports = await page.evaluate(() => {
    const output = document.querySelector('.node[data-id="picker-source"] .port.out') as HTMLElement;
    const eqInput = document.querySelector('.node[data-id="picker-eq"] .port.in') as HTMLElement;
    const reverbInput = document.querySelector('.node[data-id="picker-reverb"] .port.in') as HTMLElement;
    return { output: output.title.split(/ [(·]/)[0], eqInput: eqInput.title.split(/ [(·]/)[0], reverbInput: reverbInput.title.split(/ [(·]/)[0] };
  });
  let doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: doc.version, op: { type: 'connect', from: 'picker-source', output: ports.output, to: 'picker-eq', input: ports.eqInput } } });
  doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: doc.version, op: { type: 'connect', from: 'picker-source', output: ports.output, to: 'picker-reverb', input: ports.reverbInput } } });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');

  const source = page.locator('.node[data-id="picker-source"]');
  const arrow = source.getByRole('button', { name: 'Go to effects of picker-source' });
  await expect(arrow).toHaveAttribute('aria-haspopup', 'listbox');
  await arrow.click();
  const search = source.getByRole('combobox', { name: 'Search connected effects' });
  await search.fill('picker-reverb');
  await expect(source.getByRole('option')).toHaveText(['picker-reverb · reverb']);
  await source.getByRole('option').click();
  await expect(page.locator('.node[data-id="picker-reverb"]')).toHaveClass(/sel/);
  await expect(page.locator('#props input').first()).toHaveValue('picker-reverb');
  await page.locator('.node[data-id="picker-reverb"]').getByRole('button', { name: 'Advanced', exact: true }).click();
  const dialog = page.locator('#studio');
  await expect(dialog, JSON.stringify(pageErrors)).toBeVisible();
  await expect(dialog.locator('.studio-chain-manager')).toHaveCount(0);
  await expect(dialog.locator('.studio-dialog-header')).toContainText('block picker-reverb');
  await expect(dialog.getByRole('slider', { name: 'Room size' })).toBeVisible();
});

test('inserting EQ creates a neutral editable effect block after an audio source', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'neutral-source');
  const linksBefore = await page.locator('#links .vis').count();
  await page.locator('.node[data-id="neutral-source"]').click({ button: 'right' });
  await chooseMenu(page, 'Add', 'Effects', 'EQ & filters', 'Equalizer');
  await expect(page.locator('.node[data-id="neutral-source-eq"]')).toBeVisible();
  await page.locator('.node[data-id="neutral-source-eq"]').getByRole('button', { name: 'Advanced', exact: true }).click();
  const dialog = page.locator('#studio');
  await expect(dialog).toBeVisible();
  await expect(dialog.locator('.studio-dialog-header')).toContainText('block neutral-source-eq');
  for (const name of ['Low', 'Mid', 'High']) await expect(dialog.getByRole('slider', { name }).first()).toHaveAttribute('aria-valuenow', '0');
  await expect(page.locator('#links .vis')).toHaveCount(linksBefore + 1);
  await expect(dialog.locator('.studio-chain-manager')).toHaveCount(0);
});

test('an effect node Advanced opens its own controls and an unconnected generator labels its own take @quarantine', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'eq', 'root-eq');
  await addTemplateBlock(page, 'reverb', 'downstream-reverb');
  await connect(page, 'root-eq', 'downstream-reverb');
  await page.locator('.node[data-id="root-eq"]').getByRole('button', { name: 'Advanced', exact: true }).click();
  const effectDialog = page.locator('#studio');
  await expect(effectDialog.locator('.studio-dialog-header')).toContainText('block root-eq');
  await expect(effectDialog.getByRole('slider', { name: 'Low', exact: true })).toBeVisible();
  await expect(effectDialog.locator('.studio-chain-manager')).toHaveCount(0);
  await effectDialog.getByRole('button', { name: 'Close advanced controls' }).click();

  await addTemplateBlock(page, 'drums', 'unconnected-generator');
  await page.locator('.node[data-id="unconnected-generator"]').getByRole('button', { name: 'Advanced', exact: true }).click();
  await expect(page.locator('#props input').first()).toHaveValue('unconnected-generator');
  await expect(page.locator('#studio')).toHaveCount(0);
  await page.locator('#props .studio-advanced-props').click();
  const generatorDialog = page.locator('#studio');
  await expect(generatorDialog.locator('.studio-dialog-header')).toContainText('block unconnected-generator');
  await expect(generatorDialog.getByRole('button', { name: 'Build block' })).toBeVisible();
  await expect(generatorDialog.locator('footer button').filter({ hasText: /^Play block$/ })).toBeDisabled();
  await expect(generatorDialog.getByRole('button', { name: 'Live local preview' })).toHaveCount(0);
  await expect(generatorDialog.getByRole('button', { name: 'Play rendered effect' })).toHaveCount(0);
  await expect(generatorDialog.getByRole('button', { name: 'A/B' })).toBeDisabled();   // nothing to compare before a build
  const buildBlock = generatorDialog.getByRole('button', { name: 'Build block' });
  await buildBlock.click();
  const playBlock = generatorDialog.locator('footer button').filter({ hasText: /^Play block$/ });
  await expect(playBlock).toBeEnabled({ timeout: 20000 });
  const builtTake = await page.evaluate(() => (window as any).AMPLE_STATE.build.takes['unconnected-generator']);
  expect(builtTake).toBeTruthy();
  await playBlock.click();
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_PLAYER.state())).toMatchObject({ playing: true, take: builtTake });
});
