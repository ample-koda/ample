import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { freshServer, openApp, ownServer } from '../../web/tests/support';

function wav(samples: number[], rate = 44100): Buffer {
  const b = Buffer.alloc(44 + samples.length * 2);
  b.write('RIFF', 0); b.writeInt32LE(36 + samples.length * 2, 4); b.write('WAVEfmt ', 8); b.writeInt32LE(16, 16); b.writeInt16LE(1, 20); b.writeInt16LE(1, 22);
  b.writeInt32LE(rate, 24); b.writeInt32LE(rate * 2, 28); b.writeInt16LE(2, 32); b.writeInt16LE(16, 34); b.write('data', 36); b.writeInt32LE(samples.length * 2, 40);
  samples.forEach((s, i) => b.writeInt16LE(s, 44 + i * 2));
  return b;
}
const put = (folder: string, rel: string, data: Buffer) => { const p = path.join(folder, rel); fs.mkdirSync(path.dirname(p), { recursive: true }); fs.writeFileSync(p, data); };
const tone = (n: number, level = 6000) => wav([...Array(n)].map((_, i) => Math.round(level * Math.exp(-i / 1000) * Math.sin(i / 9))));

freshServer({
  song: 'composition "band" { ample 1  tempo 100  master-seed 3 }\nmidi-clip chords {\n  chords Am F\n  bars 2\n}\ndrums kit at 1 for 2 {\n  kit samples/kit\n  pattern beat-A\n}\ndrums synth at 1 for 2 {\n}\nguitar gtr at 1 for 2 {\n  set samples/gtr\n  strum D...D.u.\n  from chords.notes -> notes\n}\n',
  prepare: (_cli, folder) => {
    for (const v of ['kick', 'snare', 'hat', 'tom']) put(folder, `samples/kit/${v}.wav`, tone(2000));
    for (const n of ['e2', 'a2', 'c3']) put(folder, `samples/gtr/${n}.wav`, tone(8000));
  },
});

test('replacing a sample file makes only the blocks that play it, and what reads them, out of date', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#btn-build');
  await expect(page.locator('#btxt')).toHaveText('idle');
  const built = () => page.evaluate(async () => Object.keys((await import('/app/store.js')).S.build.takes).sort());
  const refresh = () => page.evaluate(async () => { await (await import('/app/store.js')).refreshInputs(); });
  expect(await built()).toEqual(expect.arrayContaining(['chords', 'kit', 'synth', 'gtr']));
  await refresh();   // the stamps of the files as built
  put(ownServer()!.folder, 'samples/kit/kick.wav', tone(2000, 9000));
  await refresh();
  const after = await built();
  expect(after).not.toContain('kit');
  expect(after).toEqual(expect.arrayContaining(['chords', 'synth', 'gtr']));   // the other blocks keep their build
  put(ownServer()!.folder, 'samples/gtr/e2.wav', tone(8000, 9000));
  await refresh();
  expect(await built()).not.toContain('gtr');
});
