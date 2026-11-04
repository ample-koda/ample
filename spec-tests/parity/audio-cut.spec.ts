import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect, Page } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// import-audio can use a stretch of its file (start, end, loop) and the cut block keeps a stretch of its input (start, end, first, last, loop).
function wav(samples: number[], rate = 44100): Buffer {
  const b = Buffer.alloc(44 + samples.length * 2);
  b.write('RIFF', 0); b.writeInt32LE(36 + samples.length * 2, 4); b.write('WAVEfmt ', 8); b.writeInt32LE(16, 16); b.writeInt16LE(1, 20); b.writeInt16LE(1, 22);
  b.writeInt32LE(rate, 24); b.writeInt32LE(rate * 2, 28); b.writeInt16LE(2, 32); b.writeInt16LE(16, 34); b.write('data', 36); b.writeInt32LE(samples.length * 2, 40);
  samples.forEach((s, i) => b.writeInt16LE(s, 44 + i * 2));
  return b;
}
// one second: the first half is silent, the second half is a tone
const SOURCE = [...Array(44100)].map((_, i) => (i < 22050 ? 0 : Math.round(12000 * Math.sin(i / 10))));

const song = (extra: string) => `composition "cut" { ample 1  tempo 240  master-seed 3 }\nimport-audio src at 1 for 1 {\n  file samples/a.wav\n${extra}}\n`;

freshServer({
  song: song(''),
  prepare: (_cli, folder) => { const p = path.join(folder, 'samples', 'a.wav'); fs.mkdirSync(path.dirname(p), { recursive: true }); fs.writeFileSync(p, wav(SOURCE)); fs.writeFileSync(path.join(folder, 'samples', 'v.wav'), wav([...Array(44100)].map((_, i) => Math.round(9000 * Math.sin((2 * Math.PI * 200 * i) / 44100) + 3000 * Math.sin((2 * Math.PI * 400 * i) / 44100))))); },
});

/** The peak (0 to 1) of the first `ms` milliseconds of the audio of a block, after building it. */
async function peakAtStart(page: Page, block: string, ms: number, from = 0): Promise<number> {
  return page.evaluate(async ({ block, ms, from }) => {
    const store = await import('/app/store.js');
    await store.build(block);
    const take = store.S.build?.takes?.[block];
    const { api } = await import('/app/api.js');
    const id = (await api.get(`/api/takes/${encodeURIComponent(take)}`)).audioDataId;
    const b = new Uint8Array(await (await fetch(`/api/data/${id}`)).arrayBuffer());
    const v = new DataView(b.buffer);
    let at = 12; while (at + 8 < b.length && String.fromCharCode(...b.slice(at, at + 4)) !== 'data') at += 8 + v.getUint32(at + 4, true);
    const channels = v.getUint16(22, true), rate = v.getUint32(24, true);
    const first = at + 8 + Math.floor(rate * from / 1000) * 2 * channels, count = Math.floor(rate * ms / 1000);
    let m = 0; for (let i = 0; i < count; i++) m = Math.max(m, Math.abs(v.getInt16(first + i * 2 * channels, true)));
    return m / 32768;
  }, { block, ms, from });
}

const setSong = async (page: Page, text: string) => { await openApp(page, { reset: false }); await page.evaluate(async (t) => { const store = await import('/app/store.js'); await store.applyText(t); }, text); };

test('import-audio starts at its start time, and loops the stretch when asked', async ({ page }) => {
  await setSong(page, song(''));
  expect(await peakAtStart(page, 'src', 100)).toBeLessThan(0.01);   // the whole file: it starts with silence
  await setSong(page, song('  start 0.5\n'));
  expect(await peakAtStart(page, 'src', 100)).toBeGreaterThan(0.2);   // only the second half: the tone is at the start
  await setSong(page, song('  start 0.5\n  end 0.75\n'));
  expect(await peakAtStart(page, 'src', 100, 400)).toBeLessThan(0.01);   // a quarter second of tone, then silence
  await setSong(page, song('  start 0.5\n  end 0.75\n  loop on\n'));
  expect(await peakAtStart(page, 'src', 100, 400)).toBeGreaterThan(0.2);   // the stretch repeats
});

test('the cut block keeps the first or the last seconds of its input', async ({ page }) => {
  const withCut = (params: string) => `${song('')}cut piece at 1 for 1 {\n  from src.audio -> audio\n${params}}\n`;
  await setSong(page, withCut('  last 0.5\n'));
  expect(await peakAtStart(page, 'piece', 100)).toBeGreaterThan(0.2);   // the last half is the tone
  await setSong(page, withCut('  first 0.5\n'));
  expect(await peakAtStart(page, 'piece', 100)).toBeLessThan(0.01);   // the first half is silence
});

test('the shift block starts its input later without moving the block', async ({ page }) => {
  await setSong(page, `${song('  start 0.5\n')}shift late at 1 for 1 {\n  from src.audio -> audio\n  seconds 0.25\n}\n`);
  expect(await peakAtStart(page, 'late', 100)).toBeLessThan(0.01);   // the first quarter second is silence
  expect(await peakAtStart(page, 'late', 100, 300)).toBeGreaterThan(0.2);   // then the sound
});

test('a file name with a space is written in quotes', async ({ page }) => {
  await setSong(page, song(''));
  await page.evaluate(async () => { const store = await import('/app/store.js'); await store.edit([{ type: 'setParameter', block: 'src', name: 'file', value: "samples/Rain (it's) wet.wav" }]); });
  const live = await page.evaluate(async () => (await import('/app/store.js')).S.doc.text as string);
  expect(live).toContain('file "samples/Rain (it\'s) wet.wav"');
});


test('the scissors button of import-audio opens the part chooser and writes start and end', async ({ page }) => {
  await setSong(page, song(''));
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.locator('.node[data-id="src"] .studio-part-button').click();
  const dialog = page.locator('dialog.clipper');
  await expect(dialog).toBeVisible();
  await dialog.getByLabel('Start in seconds').fill('0.25');
  await dialog.getByLabel('Start in seconds').dispatchEvent('change');
  await dialog.getByLabel('End in seconds').fill('0.75');
  await dialog.getByLabel('End in seconds').dispatchEvent('change');
  await dialog.getByRole('button', { name: '▶ Preview part' }).click();
  await expect(dialog.getByRole('button', { name: '■ Stop preview' })).toBeVisible();
  await dialog.getByRole('button', { name: '■ Stop preview' }).click();
  await dialog.getByRole('button', { name: 'Use this part' }).click();
  // the block also gets the length of the chosen part (half a second: one bar at this tempo is 1 s)
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'src').parts?.[0]?.length).toBe(1);
  await expect.poll(async () => JSON.stringify((await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'src').parameters)).toMatch(/"start","value":"0.25"[\s\S]*"end","value":"0.75"|"end","value":"0.75"[\s\S]*"start","value":"0.25"/);
});

test('the part chooser has handles for the start and the end, and the wheel zooms the waveform', async ({ page }) => {
  await setSong(page, song(''));
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.locator('.node[data-id="src"] .studio-part-button').click();
  const dialog = page.locator('dialog.clipper');
  const canvas = dialog.locator('canvas');
  const box = (await canvas.boundingBox())!;
  const startValue = () => dialog.getByLabel('Start in seconds').inputValue();
  const endValue = () => dialog.getByLabel('End in seconds').inputValue();
  expect(await startValue()).toBe('0.00');
  // the end handle is at the right edge of the whole file: drag it to the middle
  await page.mouse.move(box.x + box.width - 2, box.y + 40);
  await page.mouse.down(); await page.mouse.move(box.x + box.width / 2, box.y + 40, { steps: 5 }); await page.mouse.up();
  expect(Number(await endValue())).toBeGreaterThan(0.4); expect(Number(await endValue())).toBeLessThan(0.6);
  // then the start handle, at the left edge: drag it a quarter in
  await page.mouse.move(box.x + 1, box.y + 40);
  await page.mouse.down(); await page.mouse.move(box.x + box.width / 4, box.y + 40, { steps: 5 }); await page.mouse.up();
  expect(Number(await startValue())).toBeGreaterThan(0.15); expect(Number(await startValue())).toBeLessThan(0.35);
  expect(Number(await endValue())).toBeGreaterThan(0.4);   // dragging a handle left the other one alone
  // the wheel zooms in on the part under the pointer
  const before = await dialog.locator('.note').last().textContent();
  await page.mouse.move(box.x + box.width / 2, box.y + 40);
  await page.mouse.wheel(0, -300);
  await expect.poll(async () => dialog.locator('.note').last().textContent()).not.toBe(before);
  await dialog.getByRole('button', { name: 'Cancel' }).click();
});

/** How many times the first channel of the audio of a block rises through zero in the middle half second. */
async function risingCrossings(page: Page, block: string): Promise<number> {
  return page.evaluate(async (block) => {
    const store = await import('/app/store.js');
    await store.build(block);
    const take = store.S.build?.takes?.[block];
    const { api } = await import('/app/api.js');
    const id = (await api.get(`/api/takes/${encodeURIComponent(take)}`)).audioDataId;
    const b = new Uint8Array(await (await fetch(`/api/data/${id}`)).arrayBuffer());
    const v = new DataView(b.buffer);
    let at = 12; while (at + 8 < b.length && String.fromCharCode(...b.slice(at, at + 4)) !== 'data') at += 8 + v.getUint32(at + 4, true);
    const channels = v.getUint16(22, true), rate = v.getUint32(24, true);
    const first = at + 8 + Math.floor(rate * 0.25) * 2 * channels, count = Math.floor(rate * 0.5);
    let crossings = 0, last = v.getInt16(first, true);
    for (let i = 1; i < count; i++) { const now = v.getInt16(first + i * 2 * channels, true); if (last < 0 && now >= 0) crossings++; last = now; }
    return crossings;
  }, block);
}

test('the pitch block moves a voice up an octave with every method', async ({ page }) => {
  const song = (method: string) => `composition "cut" { ample 1  tempo 240  master-seed 3 }
import-audio v at 1 for 1 {
  file samples/v.wav
}
pitch up at 1 for 1 {
  from v.audio -> audio
  semitones 12
  method ${method}
}
`;
  await setSong(page, song('wsola').replace(/pitch up[sS]*$/, '') );
  const original = await risingCrossings(page, 'v');
  for (const method of ['wsola', 'voice', 'granular']) {
    await setSong(page, song(method));
    const shifted = await risingCrossings(page, 'up');
    // 200 Hz becomes 400 Hz: twice as many rises in the same half second, allowing for the seams of the method
    expect(shifted / original, method).toBeGreaterThan(1.7);
    // the granular method adds crossings of its own at the seams of its grains (that is the metallic sound); the other two stay close to the octave
    if (method !== 'granular') expect(shifted / original, method).toBeLessThan(2.3);
  }
});

test('the pitch can be moved by a part of a semitone', async ({ page }) => {
  const song = (semitones: string) => `composition "cut" { ample 1  tempo 240  master-seed 3 }\nimport-audio v at 1 for 1 {\n  file samples/v.wav\n}\npitch up at 1 for 1 {\n  from v.audio -> audio\n  semitones ${semitones}\n  method wsola\n}\n`;
  await setSong(page, song('0'));
  const original = await risingCrossings(page, 'v');
  for (const [semitones, ratio] of [['1.5', 1.0905], ['-0.5', 0.9715], ['6.25', 1.4375]] as const) {
    await setSong(page, song(semitones));
    expect((await page.evaluate(async () => (await import('/app/store.js')).S.doc.diagnostics.filter((d: { severity: string }) => d.severity === 'error').length)), semitones).toBe(0);
    const shifted = await risingCrossings(page, 'up');
    expect(shifted / original, semitones).toBeGreaterThan(ratio * 0.93);
    expect(shifted / original, semitones).toBeLessThan(ratio * 1.07);
  }
});

test('a shift block longer than its input moves the sound later than the input is long', async ({ page }) => {
  await setSong(page, `composition "cut" { ample 1  tempo 240  master-seed 3 }\nimport-audio src at 1 for 1 {\n  file samples/a.wav\n}\nshift late at 1 for 3 {\n  from src.audio -> audio\n  seconds 1.5\n}\n`);
  expect(await peakAtStart(page, 'late', 200, 500)).toBeLessThan(0.01);   // where the tone was
  expect(await peakAtStart(page, 'late', 200, 2100)).toBeGreaterThan(0.2);   // 1.5 s later, past the end of the 1 s input
});

test('the master slice cuts a part of the song out: only those bars come out, starting at the beginning', async ({ page }) => {
  const song = (slice: string) => `composition "cut" { ample 1  tempo 240  master-seed 3 }\nimport-audio src at 1 for 3 {\n  file samples/a.wav\n}\nmaster master {\n  from src.audio -> audio\n${slice}}\n`;
  await setSong(page, song(''));
  const length = (block: string) => page.evaluate(async (b) => {
    const store = await import('/app/store.js');
    await store.build(b);
    const take = store.S.build?.takes?.[b];
    const { api } = await import('/app/api.js');
    const id = (await api.get(`/api/takes/${encodeURIComponent(take)}`)).audioDataId;
    const bytes = new Uint8Array(await (await fetch(`/api/data/${id}`)).arrayBuffer());
    const v = new DataView(bytes.buffer);
    let at = 12; while (at + 8 < bytes.length && String.fromCharCode(...bytes.slice(at, at + 4)) !== 'data') at += 8 + v.getUint32(at + 4, true);
    return { seconds: v.getUint32(at + 4, true) / v.getUint32(28, true) };
  }, block);
  expect((await length('master')).seconds).toBeCloseTo(3, 1);   // the whole song: three bars of a second
  await setSong(page, song('  slice_from 1\n  slice_to 1\n'));
  expect((await length('master')).seconds).toBeCloseTo(1, 1);   // one bar
  expect(await peakAtStart(page, 'master', 200, 600)).toBeGreaterThan(0.1);   // the tone of the first bar
  await setSong(page, song('  slice_from 2\n  slice_to 3\n'));
  expect((await length('master')).seconds).toBeCloseTo(2, 1);   // bars 2 and 3
  expect(await peakAtStart(page, 'master', 1500, 0)).toBeLessThan(0.01);   // the file is over by then
});
