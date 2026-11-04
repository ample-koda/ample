import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// A sequence kept in the song itself (a sequence { ... } part of the sequencer block) instead of a .ampleseq file.
function wav(samples: number[], rate = 44100): Buffer {
  const b = Buffer.alloc(44 + samples.length * 2);
  b.write('RIFF', 0); b.writeInt32LE(36 + samples.length * 2, 4); b.write('WAVEfmt ', 8); b.writeInt32LE(16, 16); b.writeInt16LE(1, 20); b.writeInt16LE(1, 22);
  b.writeInt32LE(rate, 24); b.writeInt32LE(rate * 2, 28); b.writeInt16LE(2, 32); b.writeInt16LE(16, 34); b.write('data', 36); b.writeInt32LE(samples.length * 2, 40);
  samples.forEach((s, i) => b.writeInt16LE(s, 44 + i * 2));
  return b;
}
const SONG = 'composition "band" { ample 1  tempo 120  master-seed 3 }\nsequencer sq at 1 for 4 {\n  lanes lane1=kick\n  sequence {\n    sequencer 1\n    channel kick drum sample=samples/kick.wav\n    pattern verse bars=2\n      kick x...x...x...x...|x...x...x...x...\n    playlist\n      clip verse track=1 at=1 for=4\n  }\n}\n';

freshServer({
  song: SONG,
  prepare: (_cli, folder) => { const p = path.join(folder, 'samples', 'kick.wav'); fs.mkdirSync(path.dirname(p), { recursive: true }); fs.writeFileSync(p, wav([...Array(6000)].map((_, i) => Math.round(12000 * Math.exp(-i / 1500) * Math.sin(i / 12))))); },
});

test('a sequencer block with the sequence in the song has no errors and builds sound', async ({ page }) => {
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  const doc = await api('/api/doc?path=song.amplet');
  expect(doc.diagnostics.filter((d: { severity: string }) => d.severity === 'error')).toEqual([]);
  const block = doc.blocks.find((b: { name: string }) => b.name === 'sq');
  const sequence = block.parameters.find((p: { name: string }) => p.name === 'sequence')?.value as string;
  expect(sequence).toContain('channel kick drum sample=samples/kick.wav');
  expect(sequence.split('\n')[0]).toBe('sequencer 1');   // the text of a sequence file, without the indent of the song
  const peak = await page.evaluate(async () => {
    const store = await import('/app/store.js');
    await store.build('sq');
    const take = store.S.build?.takes?.sq;
    const { api } = await import('/app/api.js');
    const id = (await api.get(`/api/takes/${encodeURIComponent(take)}`)).audioDataId;
    const b = new Uint8Array(await (await fetch(`/api/data/${id}`)).arrayBuffer());
    const v = new DataView(b.buffer);
    let at = 12; while (at + 8 < b.length && String.fromCharCode(...b.slice(at, at + 4)) !== 'data') at += 8 + v.getUint32(at + 4, true);
    let m = 0; for (let i = at + 8; i + 1 < b.length; i += 2) m = Math.max(m, Math.abs(v.getInt16(i, true)));
    return m / 32768;
  });
  expect(peak).toBeGreaterThan(0.05);
  void node;
});

test('the sequencer opens a sequence that is kept in the song, writes changes into it, and moves it out to a file and back', async ({ page }) => {
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  await page.locator('#open-sequencer').click();
  await expect(page.locator('#sq')).toBeVisible();
  await expect(page.locator('#sq-rack .sqchannel')).toHaveCount(1);
  await expect(page.locator('#sq-extract')).toBeVisible();
  // a step changed in the workspace is written into the song itself
  const cell = page.locator('#sq-patterns .sqgridrow[data-voice="kick"] .seqc[data-bar="0"][data-step="1"]');
  await cell.click();
  await page.locator('#sq-apply').click();
  await expect.poll(async () => ((await api('/api/doc?path=song.amplet')).text as string)).toMatch(/kick xx\.\.x\.\.\.x/);
  // extract it to a file: the block uses the file and the sequence is gone from the song
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  await page.locator('#open-sequencer').click();
  await page.locator('#sq-extract').click();
  await page.locator('#dlg-extra input').fill('sequences/out.ampleseq');
  await page.locator('#dlg-btns').getByRole('button', { name: 'Extract' }).click();
  await expect.poll(async () => ((await api('/api/doc?path=song.amplet')).text as string)).toMatch(/file sequences\/out\.ampleseq/);
  const song = (await api('/api/doc?path=song.amplet')).text as string;
  expect(song).not.toContain('sequence {');
  expect((await api('/api/file?path=sequences/out.ampleseq')).text).toContain('channel kick drum');
  // and kept in the song again: the file stays
  await expect(page.locator('#sq-embed')).toBeVisible();
  await page.locator('#sq-embed').click();
  await expect.poll(async () => ((await api('/api/doc?path=song.amplet')).text as string)).toContain('sequence {');
  expect((await api('/api/file?path=sequences/out.ampleseq')).text).toContain('channel kick drum');
});
