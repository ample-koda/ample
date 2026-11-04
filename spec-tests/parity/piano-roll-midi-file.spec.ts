import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// A tiny Standard MIDI File: format 0, 120 ticks per quarter, tempo 86.5 BPM, three notes (C4, E4 and a G4 that goes on over a bar line).
function smf(): Buffer {
  const track: number[] = [];
  const at = (delta: number, ...bytes: number[]) => { track.push(...vlq(delta), ...bytes); };
  function vlq(value: number): number[] { const out = [value & 0x7f]; while ((value >>= 7)) out.unshift((value & 0x7f) | 0x80); return out; }
  at(0, 0xff, 0x51, 0x03, 0x0a, 0xa3, 0x8a);
  at(0, 0x90, 60, 100); at(120, 0x80, 60, 0);          // C4 on beat 1, one beat long
  at(0, 0x90, 64, 90); at(120, 0x80, 64, 0);           // E4 on beat 2
  at(120, 0x90, 67, 80); at(240, 0x80, 67, 0);         // G4 from the last beat of bar 1 into bar 2 (ticks 360 to 600, the bar line is at 480)
  at(0, 0xff, 0x2f, 0x00);
  const header = Buffer.from([0x4d, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 0, 120]);
  const body = Buffer.from(track);
  const length = Buffer.alloc(4); length.writeUInt32BE(body.length);
  return Buffer.concat([header, Buffer.from('MTrk'), length, body]);
}

freshServer({
  song: 'composition "t" { ample 1  tempo 86  master-seed 1 }\nmidi m at 1 for 4 {\n  file assets/tiny.mid\n}\n',
  prepare: (_cli, folder) => { fs.mkdirSync(path.join(folder, 'assets'), { recursive: true }); fs.writeFileSync(path.join(folder, 'assets', 'tiny.mid'), smf()); },
});

test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

test('a MIDI file block opens read-only in AMPLE Jam and can be copied into an editable midi-clip', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'm').locator('.nh .t').click();
  await page.getByRole('button', { name: /Show notes/ }).click();
  const dialog = page.locator('dialog.mls-panel');
  await expect(dialog).toBeVisible();
  // the G4 over the bar line arrives as two pieces, so there are four entries
  await expect(dialog.getByText(/4 notes/)).toBeVisible();
  await expect(dialog.getByText(/read-only/)).toBeVisible();
  await page.screenshot({ path: process.env.PIANOROLL_SHOT || 'test-results/piano-roll-midi-file.png' });
  // read-only: drawing on the grid changes nothing
  const canvas = dialog.locator('canvas.mls-grid');
  const box = (await canvas.boundingBox())!;
  await page.mouse.move(box.x + 300, box.y + 200); await page.mouse.down(); await page.mouse.move(box.x + 360, box.y + 200); await page.mouse.up();
  await expect(dialog.getByText(/4 notes/)).toBeVisible();
  await dialog.getByRole('button', { name: 'Menu' }).click();
  await page.getByRole('menuitem', { name: /Make an editable copy/ }).click();
  await expect(node(page, 'm-notes')).toBeVisible();
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).blocks.some((b: { name: string }) => b.name === 'm-notes')).toBe(true);
  const doc = await api('/api/doc?path=song.amplet');
  const copy = doc.blocks.find((b: { name: string }) => b.name === 'm-notes')!;
  const value = (name: string) => copy.parameters.find((p: { name: string }) => p.name === name)?.value;
  expect(value('ppq')).toBe('120');
  expect(value('notes')).toBe('"1:0:120:60:100:0:0:0 1:120:120:64:90:0:0:0 1:360:120:67:80:0:0:0:0 2:0:120:67:80:0:0:0:0"');
});
