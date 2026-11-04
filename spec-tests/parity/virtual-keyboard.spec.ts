import { test, expect, type Page, type Locator } from '@playwright/test';
import { addTemplateBlock, api, freshServerPerTest, node, openApp } from '../../web/tests/support';

// The touch keyboard: in the Jam (with recording), in the keyboard dock of the top bar, and fed by a MIDI input (faked here the way a virtual MIDI cable would show up). DOM only: no audio is heard.
freshServerPerTest();
test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    const input: { onmidimessage: ((m: { data: number[] }) => void) | null; name: string } = { onmidimessage: null, name: 'Virtual cable' };
    (window as unknown as { __fakeMidi: unknown }).__fakeMidi = { send: (data: number[]) => input.onmidimessage?.({ data }) };
    (navigator as unknown as { requestMIDIAccess: () => Promise<unknown> }).requestMIDIAccess = async () => ({ inputs: new Map([['fake', input]]), onstatechange: null });
  });
  await openApp(page);
});

const midi = (page: Page, data: number[]) => page.evaluate((d) => (window as unknown as { __fakeMidi: { send: (x: number[]) => void } }).__fakeMidi.send(d), data);
const notesOf = async (block: string) => ((await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === block).parameters.find((p: { name: string }) => p.name === 'notes')?.value as string | undefined) ?? '';
const blockNames = async () => (await api('/api/doc?path=song.amplet')).blocks.map((b: { name: string }) => b.name) as string[];
const calls = (panel: Locator) => panel.evaluate(((d: any) => d.mlsDebug.calls) as never) as Promise<{ pitch: number }[]>;

async function openJam(page: Page, name: string) {
  await addTemplateBlock(page, 'midi-clip', name);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, name).locator('.nh .t').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  await expect.poll(() => panel.locator('canvas.mls-grid').evaluate((c: HTMLCanvasElement) => c.clientWidth)).toBeGreaterThan(300);
  await panel.locator('select.mls-snap').selectOption('1/4');
  return panel;
}
async function press(page: Page, key: Locator) {
  const box = (await key.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height * 0.6);
  await page.mouse.down();
}

test('the Jam has a touch keyboard: a key lights while it is held and sounds through the clip, and sliding plays the keys it passes', async ({ page }) => {
  const panel = await openJam(page, 'clip');
  await panel.getByRole('button', { name: /Touch keyboard/ }).click();
  const keyboard = panel.locator('.vkb');
  await expect(keyboard).toBeVisible();
  const c4 = keyboard.locator('.vkb-key[data-pitch="60"]'), d4 = keyboard.locator('.vkb-key[data-pitch="62"]');
  await press(page, c4);
  await expect(c4).toHaveClass(/on/);
  await expect.poll(async () => (await calls(panel)).map((c) => c.pitch)).toContain(60);
  // a finger that slides to the next white key lets go of the first and plays the second
  const next = (await d4.boundingBox())!;
  await page.mouse.move(next.x + next.width / 2, next.y + next.height * 0.6, { steps: 4 });
  await expect(d4).toHaveClass(/on/);
  await expect(c4).not.toHaveClass(/on/);
  await page.mouse.up();
  await expect(d4).not.toHaveClass(/on/);
  expect((await calls(panel)).map((c) => c.pitch)).toEqual(expect.arrayContaining([60, 62]));
  // the octave buttons move the range
  await keyboard.getByRole('button', { name: 'Keyboard octave up' }).click();
  await expect(keyboard.locator('.vkb-key[data-pitch="72"]')).toBeVisible();
});

test('Record writes what is played on the keyboard or a MIDI cable into the clip', async ({ page }) => {
  const panel = await openJam(page, 'clip');
  await panel.getByRole('button', { name: /^MIDI in/ }).click();
  await expect(panel).toHaveAttribute('data-midi', '1');
  await panel.getByRole('button', { name: /^Record:/ }).click();
  await expect(panel.getByRole('button', { name: /^Record:/ })).toHaveAttribute('aria-pressed', 'true');
  // the clip must be playing before notes are kept
  await expect.poll(() => panel.evaluate(((d: any) => Boolean(d.querySelector('.mls-btn[aria-pressed="true"][title^="Play"]'))) as never), { timeout: 20000 }).toBe(true);
  await page.waitForTimeout(600);
  await midi(page, [0x90, 64, 100]);
  await page.waitForTimeout(500);
  await midi(page, [0x80, 64, 0]);
  await panel.getByRole('button', { name: /^Stop and return/ }).click();
  await expect.poll(async () => notesOf('clip'), { timeout: 10000 }).toMatch(/:64:/);
});

test('the keyboard dock plays the selected block and shows what a MIDI cable plays', async ({ page }) => {
  await addTemplateBlock(page, 'midi-clip', 'tune');
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.locator('#btn-keys').click();
  const dock = page.locator('#keys-dock');
  await expect(dock).toBeVisible();
  await expect(dock.locator('.vkb-key')).not.toHaveCount(0);
  await node(page, 'tune').locator('.nh .t').click();
  await expect(dock.locator('.keys-target')).toContainText('Playing: tune');
  await dock.getByRole('button', { name: 'MIDI in' }).click();
  await expect(dock.getByRole('button', { name: 'MIDI in' })).toHaveAttribute('aria-pressed', 'true');
  await midi(page, [0x90, 60, 90]);
  await expect(dock.locator('.vkb-key[data-pitch="60"]')).toHaveClass(/on/);
  await midi(page, [0x80, 60, 0]);
  await expect(dock.locator('.vkb-key[data-pitch="60"]')).not.toHaveClass(/on/);
  await page.locator('#btn-keys').click();
  await expect(dock).toHaveCount(0);
});

test('Record waits: the clip loops until the first note, then that pass is written and it stops by itself', async ({ page }) => {
  const panel = await openJam(page, 'waiter');
  await panel.getByRole('button', { name: /^MIDI in/ }).click();
  await panel.getByRole('button', { name: /^Record:/ }).click();
  const record = panel.getByRole('button', { name: /^Record:/ });
  await expect.poll(() => panel.evaluate(((d: any) => Boolean(d.querySelector('.mls-btn[aria-pressed="true"][title^="Play"]'))) as never), { timeout: 20000 }).toBe(true);
  // nothing is played for a few passes of the clip: it keeps going and nothing is written
  await page.waitForTimeout(6000);
  await expect(record).toHaveAttribute('aria-pressed', 'true');
  await expect.poll(() => panel.evaluate(((d: any) => Boolean(d.querySelector('.mls-btn[aria-pressed="true"][title^="Play"]'))) as never)).toBe(true);
  expect(await notesOf('waiter')).not.toMatch(/:64:/);
  await midi(page, [0x90, 64, 100]);
  await page.waitForTimeout(300);
  await midi(page, [0x80, 64, 0]);
  // no Stop is pressed: the pass ends by itself and the note is in the clip
  await expect.poll(async () => notesOf('waiter'), { timeout: 20000 }).toMatch(/:64:/);
  await expect(record).toHaveAttribute('aria-pressed', 'false');
});

test('Pressing Record twice while it waits gives an endless take; it can run past the clip, and the review keeps the bars chosen and makes the clip longer', async ({ page }) => {
  const panel = await openJam(page, 'endless');
  await panel.getByRole('button', { name: /^MIDI in/ }).click();
  const record = panel.getByRole('button', { name: /^Record:/ });
  await record.click();
  await record.click();   // a second press while it waits: endless
  await expect(record).toHaveAttribute('data-mode', 'endless');
  await expect.poll(() => panel.evaluate(((d: any) => Boolean(d.querySelector('.mls-btn[aria-pressed="true"][title^="Play"]'))) as never), { timeout: 20000 }).toBe(true);
  await midi(page, [0x90, 60, 100]);
  await page.waitForTimeout(300);
  await midi(page, [0x80, 60, 0]);
  // past the end of the clip (a bar is two seconds at 120 bpm): a second note, a few bars on
  await page.waitForTimeout(4800);
  await midi(page, [0x90, 67, 100]);
  await page.waitForTimeout(300);
  await midi(page, [0x80, 67, 0]);
  // a take is running: the next press finishes it and shows the review
  await record.click();
  const review = panel.locator('.mls-review');
  await expect(review).toBeVisible();
  await expect(review.locator('.mls-review-info')).toContainText('notes over');
  await expect(review.locator('.mls-review-info')).toContainText('longer than the clip');
  await review.getByRole('button', { name: 'Keep', exact: true }).click();
  await expect(review).toBeHidden();
  await expect.poll(async () => notesOf('endless'), { timeout: 10000 }).toMatch(/:67:/);
  const text = ((await api('/api/doc?path=song.amplet')) as { text: string }).text;
  expect(text).toMatch(/endless[^\n]* for ([3-9]|\d\d)/);
});

test('the dock has a pill, the keyboard and a maximized layout with more octaves and knobs', async ({ page }) => {
  await addTemplateBlock(page, 'midi-clip', 'tune');
  await page.locator('#btn-keys').click();
  const dock = page.locator('#keys-dock');
  await expect(dock).toHaveAttribute('data-size', 'normal');
  const normalKeys = await dock.locator('.vkb-key').count();
  await dock.getByRole('button', { name: 'Maximize the keyboard' }).click();
  await expect(dock).toHaveAttribute('data-size', 'max');
  expect(await dock.locator('.vkb-key').count()).toBeGreaterThan(normalKeys);
  await expect(dock.locator('.keys-extra')).toBeVisible();
  await dock.getByRole('button', { name: 'Transpose up' }).click();
  await expect(dock.locator('.keys-transpose')).toHaveText('+1 st');
  await dock.getByRole('button', { name: 'Minimize the keyboard' }).click();
  await expect(dock).toHaveAttribute('data-size', 'min');
  await expect(dock.locator('.vkb')).toBeHidden();
  await dock.getByRole('button', { name: 'Open the keyboard' }).click();
  await expect(dock).toHaveAttribute('data-size', 'normal');
  await expect(dock.locator('.vkb')).toBeVisible();
});

test('the dock hooks into the Jam that is open: its keys play the Jam and Record is there too', async ({ page }) => {
  const panel = await openJam(page, 'hooked');
  await page.locator('#btn-keys').click();
  const dock = page.locator('#keys-dock');
  await expect(dock.locator('.keys-target')).toContainText('Playing: Jam');
  await expect(dock.getByRole('button', { name: 'Record', exact: true })).toBeVisible();
  const key = dock.locator('.vkb-key[data-pitch="62"]');
  const box = (await key.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height * 0.6);
  await page.mouse.down();
  await expect.poll(async () => (await calls(panel)).map((c) => c.pitch)).toContain(62);
  await page.mouse.up();
  // a transpose of the dock moves what the Jam gets
  await dock.getByRole('button', { name: 'Maximize the keyboard' }).click();
  await dock.getByRole('button', { name: 'Transpose up' }).click();
  const again = dock.locator('.vkb-key[data-pitch="60"]'), box2 = (await again.boundingBox())!;
  await page.mouse.move(box2.x + box2.width / 2, box2.y + box2.height * 0.6);
  await page.mouse.down(); await page.mouse.up();
  await expect.poll(async () => (await calls(panel)).map((c) => c.pitch)).toContain(61);
});

test('the sustain pedal and the channel of a MIDI keyboard are followed, and the wheel moves the strip', async ({ page }) => {
  await addTemplateBlock(page, 'midi-clip', 'tune');
  await page.locator('#btn-keys').click();
  const dock = page.locator('#keys-dock');
  await dock.getByRole('button', { name: 'Maximize the keyboard' }).click();
  await dock.getByRole('button', { name: 'MIDI in' }).click();
  await expect(dock.getByRole('button', { name: 'MIDI in' })).toHaveAttribute('aria-pressed', 'true');
  // the pedal lights while it is down
  await midi(page, [0xb0, 64, 127]);
  await expect(dock.locator('.keys-pedal')).toHaveClass(/on/);
  await midi(page, [0xb0, 64, 0]);
  await expect(dock.locator('.keys-pedal')).not.toHaveClass(/on/);
  // the wheel moves the strip (all the way up is 16383 = lsb 127, msb 127)
  await midi(page, [0xe0, 127, 127]);
  await expect.poll(async () => Number(await dock.locator('.vkb-bend').getAttribute('aria-valuenow'))).toBeGreaterThan(0.9);
  await midi(page, [0xe0, 0, 64]);
  await expect.poll(async () => Number(await dock.locator('.vkb-bend').getAttribute('aria-valuenow'))).toBeCloseTo(0, 1);
  // only channel 2 is listened to
  await dock.getByLabel('MIDI channel').selectOption('2');
  await midi(page, [0x90, 60, 90]);   // channel 1
  await expect(dock.locator('.vkb-key[data-pitch="60"]')).not.toHaveClass(/on/);
  await midi(page, [0x91, 60, 90]);   // channel 2
  await expect(dock.locator('.vkb-key[data-pitch="60"]')).toHaveClass(/on/);
  await midi(page, [0x81, 60, 0]);
  await dock.getByLabel('MIDI channel').selectOption('0');
});

test('the scale lock dims the notes outside the scale and plays the nearest note of it', async ({ page }) => {
  const panel = await openJam(page, 'scaled');
  await page.locator('#btn-keys').click();
  const dock = page.locator('#keys-dock');
  await dock.getByRole('button', { name: 'Maximize the keyboard' }).click();
  await dock.getByLabel('Scale').selectOption('major');
  await expect(dock.locator('.vkb-key[data-pitch="61"]')).toHaveClass(/scale-out/);   // C# is not in C major
  await expect(dock.locator('.vkb-key[data-pitch="60"]')).toHaveClass(/scale-root/);
  await expect(dock.locator('.vkb-key[data-pitch="62"]')).not.toHaveClass(/scale-out/);
  await dock.getByRole('button', { name: 'Lock', exact: true }).click();
  const key = dock.locator('.vkb-key[data-pitch="61"]'), box = (await key.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height * 0.3);
  await page.mouse.down();
  // the C# key sounds as the C below it
  await expect.poll(async () => (await calls(panel)).map((c) => c.pitch)).toContain(60);
  expect((await calls(panel)).map((c) => c.pitch)).not.toContain(61);
  await page.mouse.up();
});

test('the pitch wheel while a Jam take runs is written as a bend on the note', async ({ page }) => {
  const panel = await openJam(page, 'bent');
  await panel.getByRole('button', { name: /^MIDI in/ }).click();
  await panel.getByRole('button', { name: /^Record:/ }).click();
  await expect.poll(() => panel.evaluate(((d: any) => Boolean(d.querySelector('.mls-btn[aria-pressed="true"][title^="Play"]'))) as never), { timeout: 20000 }).toBe(true);
  await midi(page, [0x90, 64, 100]);
  await page.waitForTimeout(200);
  await midi(page, [0xe0, 127, 127]);   // the wheel all the way up: two semitones
  await page.waitForTimeout(300);
  await midi(page, [0x80, 64, 0]);
  await panel.getByRole('button', { name: /^Stop and return/ }).click();
  await expect.poll(async () => notesOf('bent'), { timeout: 10000 }).toMatch(/bend=200/);   // two semitones, in cents
});

test('the record button of the dock makes a take of its own that is saved as a new clip', async ({ page }) => {
  await addTemplateBlock(page, 'midi-clip', 'tune');
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'tune').locator('.nh .t').click();
  await page.locator('#btn-keys').click();
  const dock = page.locator('#keys-dock');
  await dock.getByRole('button', { name: 'MIDI in' }).click();
  const record = dock.getByRole('button', { name: 'Record', exact: true });
  await record.click();
  await expect(record).toHaveAttribute('data-state', 'waiting');
  await midi(page, [0x90, 60, 100]);
  await expect(record).toHaveAttribute('data-state', 'recording');
  await page.waitForTimeout(300);
  await midi(page, [0x80, 60, 0]);
  await page.waitForTimeout(200);
  await midi(page, [0x90, 67, 90]);
  await page.waitForTimeout(300);
  await midi(page, [0x80, 67, 0]);
  await record.click();
  const review = dock.locator('.mls-review');
  await expect(review).toBeVisible();
  await expect(review.locator('.mls-review-info')).toContainText('2 notes');
  await review.getByRole('button', { name: 'Save as clip' }).click();
  await expect(review).toBeHidden();
  await expect.poll(async () => (await blockNames()).includes('take'), { timeout: 10000 }).toBe(true);
  // the block comes first, then its notes and its length (the edits are applied one after the other)
  await expect.poll(async () => notesOf('take'), { timeout: 10000 }).toMatch(/:60:/);
  expect(await notesOf('take')).toMatch(/:67:/);
});

test('the dock can be dragged by its head or its pill, the place is remembered and a double click puts it back', async ({ page }) => {
  await addTemplateBlock(page, 'midi-clip', 'tune');
  await page.locator('#btn-keys').click();
  const dock = page.locator('#keys-dock');
  const start = (await dock.boundingBox())!;
  const head = dock.locator('.keys-head'), hb = (await head.boundingBox())!;
  await page.mouse.move(hb.x + 40, hb.y + hb.height / 2);
  await page.mouse.down();
  await page.mouse.move(hb.x - 160, hb.y - 140, { steps: 8 });
  await page.mouse.up();
  const moved = (await dock.boundingBox())!;
  expect(moved.x).toBeLessThan(start.x - 100);
  expect(moved.y).toBeLessThan(start.y - 100);
  // it stays on the screen
  expect(moved.y).toBeGreaterThanOrEqual(0);
  // the pill moves too, and a drag is not a click (it stays a pill)
  await dock.getByRole('button', { name: 'Minimize the keyboard' }).click();
  await expect(dock).toHaveAttribute('data-size', 'min');
  const pill = dock.locator('.keys-pill'), pb = (await pill.boundingBox())!, before = (await dock.boundingBox())!;
  await page.mouse.move(pb.x + 20, pb.y + pb.height / 2);
  await page.mouse.down();
  await page.mouse.move(pb.x + 120, pb.y - 80, { steps: 6 });
  await page.mouse.up();
  await expect(dock).toHaveAttribute('data-size', 'min');
  expect((await dock.boundingBox())!.y).toBeLessThan(before.y - 40);
  // a plain click on the pill opens it again
  await dock.locator('.keys-pill').click();
  await expect(dock).toHaveAttribute('data-size', 'normal');
  // a double click on the head puts it back where it was
  const head2 = (await dock.locator('.keys-head').boundingBox())!;
  await page.mouse.dblclick(head2.x + 40, head2.y + head2.height / 2);
  await expect.poll(async () => Math.round((await dock.boundingBox())!.y)).toBe(Math.round(start.y));
});

test('the dock can be resized by its corner: wider shows more octaves, taller makes the keys taller, a double click goes back', async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 900 });
  await addTemplateBlock(page, 'midi-clip', 'tune');
  await page.locator('#btn-keys').click();
  const dock = page.locator('#keys-dock');
  const keys = () => dock.locator('.vkb-key').count();
  const keysHeight = async () => (await dock.locator('.vkb-keys').boundingBox())!.height;
  const startKeys = await keys(), startHeight = await keysHeight(), startWidth = (await dock.boundingBox())!.width;
  // make room on the left first: the corner grows to the right
  const head = (await dock.locator('.keys-head').boundingBox())!;
  await page.mouse.move(head.x + 40, head.y + head.height / 2); await page.mouse.down(); await page.mouse.move(head.x - 300, head.y - 100, { steps: 6 }); await page.mouse.up();
  const grip = (await dock.locator('.keys-grip').boundingBox())!;
  await page.mouse.move(grip.x + 10, grip.y + 10);
  await page.mouse.down();
  await page.mouse.move(grip.x + 10 + 330, grip.y + 10 + 70, { steps: 8 });
  await page.mouse.up();
  expect((await dock.boundingBox())!.width).toBeGreaterThan(startWidth + 200);
  expect(await keys()).toBeGreaterThan(startKeys);
  expect(await keysHeight()).toBeGreaterThan(startHeight + 40);
  // a double click on the corner puts the standard size back
  const grip2 = (await dock.locator('.keys-grip').boundingBox())!;
  await page.mouse.dblclick(grip2.x + 10, grip2.y + 10);
  await expect.poll(async () => await keys()).toBe(startKeys);
  expect(Math.abs((await keysHeight()) - startHeight)).toBeLessThan(2);
});

test('on its own the dock plays a piano, and the sound can be changed: another instrument, or always the own sound', async ({ page }) => {
  const renders: string[] = [];
  page.on('request', (request) => { if (request.url().includes('/api/gm/audition') && request.method() === 'POST') renders.push(request.postData() || ''); });
  await page.locator('#btn-keys').click();
  const dock = page.locator('#keys-dock');
  // nothing is open or selected: the own sound is the piano
  await expect(dock.locator('.keys-target')).toContainText('Acoustic Grand Piano');
  await expect(dock.locator('.keys-target')).toContainText('own sound');
  const key = dock.locator('.vkb-key[data-pitch="60"]'), box = (await key.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height * 0.6);
  await page.mouse.down();
  await expect.poll(() => renders.length, { timeout: 10000 }).toBeGreaterThan(0);
  await page.mouse.up();
  expect(renders.some((body) => /"program":0/.test(body))).toBe(true);
  // another instrument: the list of the General MIDI instruments
  await dock.getByRole('button', { name: /Sound/ }).click();
  const input = page.locator('.keys-sound-pop input.keys-sound-program');
  await input.fill('cello');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(dock.locator('.keys-target')).toContainText('Cello');
  // the notes of the keys on the screen are rendered with it in the background
  await expect.poll(() => renders.some((body) => /"program":42/.test(body)), { timeout: 10000 }).toBe(true);
  // always the own sound, also when a clip is selected
  await dock.getByLabel('Plays').selectOption('own');
  await page.locator('#btn-keys').click();   // (adding a block redraws the page: the choice is remembered, so the dock is opened again)
  await addTemplateBlock(page, 'midi-clip', 'tune');
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'tune').locator('.nh .t').click();
  await page.locator('#btn-keys').click();
  await expect(dock.locator('.keys-target')).toContainText('own sound');
  // back to auto: the selected clip is played again
  await dock.getByRole('button', { name: /Sound/ }).click();
  await dock.getByLabel('Plays').selectOption('auto');
  await expect(dock.locator('.keys-target')).toContainText('Playing: tune');
  // and a button puts the piano back
  await dock.getByRole('button', { name: 'Piano', exact: true }).click();
  await dock.getByLabel('Plays').selectOption('own');
  await expect(dock.locator('.keys-target')).toContainText('Acoustic Grand Piano');
});

test('the dock cannot be made too small, and two fingers on it resize it (pinch), not the keys', async ({ page, context }) => {
  await page.setViewportSize({ width: 1500, height: 900 });
  await page.locator('#btn-keys').click();
  const dock = page.locator('#keys-dock');
  // dragging the corner far to the left and up stops at the size the buttons need
  const grip = (await dock.locator('.keys-grip').boundingBox())!;
  await page.mouse.move(grip.x + 10, grip.y + 10); await page.mouse.down(); await page.mouse.move(grip.x - 700, grip.y - 400, { steps: 8 }); await page.mouse.up();
  const small = (await dock.boundingBox())!;
  expect(small.width).toBeGreaterThanOrEqual(411);
  expect((await dock.locator('.vkb-keys').boundingBox())!.height).toBeGreaterThanOrEqual(79);
  // every button of the head is still inside the dock
  for (const name of ['Sound', 'Maximize the keyboard', 'Close the keyboard']) {
    const button = (await dock.getByRole('button', { name: new RegExp(name) }).first().boundingBox())!;
    expect(button.x + button.width).toBeLessThanOrEqual(small.x + small.width + 1);
  }
  // a pinch on the head (two touches that move apart) makes it bigger
  const cdp = await context.newCDPSession(page);
  const top = (await dock.boundingBox())!, y = top.y + 3, x = top.x + 30;   // the padding at the top of the dock: free of buttons
  const touch = (type: string, points: { x: number; y: number; id: number }[]) => cdp.send('Input.dispatchTouchEvent', { type, touchPoints: points.map((p) => ({ x: p.x, y: p.y, id: p.id })) });
  await touch('touchStart', [{ x, y, id: 1 }, { x: x + 30, y, id: 2 }]);
  for (let step = 1; step <= 8; step++) await touch('touchMove', [{ x, y, id: 1 }, { x: x + 30 + step * 10, y, id: 2 }]);
  await touch('touchEnd', []);
  await expect.poll(async () => (await dock.boundingBox())!.width).toBeGreaterThan(small.width + 100);
  // two fingers on the keys are a chord, not a pinch: the size does not change
  const before = (await dock.boundingBox())!.width;
  const keysBox = (await dock.locator('.vkb-keys').boundingBox())!;
  await touch('touchStart', [{ x: keysBox.x + 80, y: keysBox.y + 40, id: 1 }, { x: keysBox.x + 120, y: keysBox.y + 40, id: 2 }]);
  for (let step = 1; step <= 6; step++) await touch('touchMove', [{ x: keysBox.x + 80, y: keysBox.y + 40, id: 1 }, { x: keysBox.x + 120 + step * 30, y: keysBox.y + 40, id: 2 }]);
  await touch('touchEnd', []);
  expect(Math.abs((await dock.boundingBox())!.width - before)).toBeLessThan(3);
});

test('a touch that the system takes away is reported and lets go of the key', async ({ page, context }) => {
  await page.locator('#btn-keys').click();
  const dock = page.locator('#keys-dock');
  const key = dock.locator('.vkb-key[data-pitch="60"]'), box = (await key.boundingBox())!;
  const cdp = await context.newCDPSession(page);
  const x = box.x + box.width / 2, y = box.y + box.height * 0.6;
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x, y, id: 1 }] });
  await expect(key).toHaveClass(/on/);
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchCancel', touchPoints: [] });
  await expect(key).not.toHaveClass(/on/);
  await expect(page.locator('#toast')).toContainText('took over a touch');
});

test('a thin scrollbar under the keys scrolls them, and its ends give fewer or more keys', async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 900 });
  await page.locator('#btn-keys').click();
  const dock = page.locator('#keys-dock');
  const scroll = dock.locator('.vkb-scroll'), handle = dock.locator('.vkb-scroll-handle');
  await expect(scroll).toBeVisible();
  const rangeText = () => dock.locator('.vkb-octave').innerText();
  const keyCount = () => dock.locator('.vkb-key.white').count();   // (the white keys: the black ones between them depend on where the range begins)
  const startRange = await rangeText(), startKeys = await keyCount();
  // dragging the handle scrolls the keys: the same number of keys, other notes
  let hb = (await handle.boundingBox())!;
  await page.mouse.move(hb.x + hb.width / 2, hb.y + hb.height / 2); await page.mouse.down(); await page.mouse.move(hb.x + hb.width / 2 + 90, hb.y + hb.height / 2, { steps: 6 }); await page.mouse.up();
  expect(await rangeText()).not.toBe(startRange);
  expect(await keyCount()).toBe(startKeys);
  // dragging the right end makes more keys
  hb = (await handle.boundingBox())!;
  await page.mouse.move(hb.x + hb.width - 4, hb.y + hb.height / 2); await page.mouse.down(); await page.mouse.move(hb.x + hb.width + 60, hb.y + hb.height / 2, { steps: 6 }); await page.mouse.up();
  expect(await keyCount()).toBeGreaterThan(startKeys);
  // and a drag of the left end towards the right makes fewer again
  hb = (await handle.boundingBox())!;
  const more = await keyCount();
  await page.mouse.move(hb.x + 4, hb.y + hb.height / 2); await page.mouse.down(); await page.mouse.move(hb.x + 90, hb.y + hb.height / 2, { steps: 6 }); await page.mouse.up();
  expect(await keyCount()).toBeLessThan(more);
  // a click on the track jumps there
  const sb = (await scroll.boundingBox())!, before = await rangeText();
  await page.mouse.click(sb.x + 8, sb.y + sb.height / 2);
  expect(await rangeText()).not.toBe(before);
  // the standard size (double-click on the corner) gives the standard keys back
  const grip = (await dock.locator('.keys-grip').boundingBox())!;
  await page.mouse.dblclick(grip.x + 10, grip.y + 10);
  await expect.poll(keyCount).toBe(startKeys);
});

test('the keys can be cut down to a few big ones (not fewer than five white keys), also in a narrow dock', async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 900 });
  await page.locator('#btn-keys').click();
  const dock = page.locator('#keys-dock');
  const whites = () => dock.locator('.vkb-key.white').count();
  const widthOfAKey = async () => (await dock.locator('.vkb-key.white').first().boundingBox())!.width;
  const normal = await widthOfAKey();
  // the right end of the handle all the way to the left: as few keys as allowed
  const hb = (await dock.locator('.vkb-scroll-handle').boundingBox())!;
  await page.mouse.move(hb.x + hb.width - 4, hb.y + hb.height / 2); await page.mouse.down(); await page.mouse.move(hb.x - 200, hb.y + hb.height / 2, { steps: 8 }); await page.mouse.up();
  expect(await whites()).toBe(5);
  expect(await widthOfAKey()).toBeGreaterThan(normal * 2);   // big keys for a finger
  // and the dock itself can be made narrow now: its buttons still fit
  const grip = (await dock.locator('.keys-grip').boundingBox())!;
  await page.mouse.move(grip.x + 10, grip.y + 10); await page.mouse.down(); await page.mouse.move(grip.x - 500, grip.y + 10, { steps: 8 }); await page.mouse.up();
  const box = (await dock.boundingBox())!;
  expect(box.width).toBeLessThan(450);
  for (const name of ['Sound', 'Maximize the keyboard', 'Close the keyboard']) {
    const button = (await dock.getByRole('button', { name: new RegExp(name) }).first().boundingBox())!;
    expect(button.x).toBeGreaterThanOrEqual(box.x - 1);
    expect(button.x + button.width).toBeLessThanOrEqual(box.x + box.width + 1);
  }
});
