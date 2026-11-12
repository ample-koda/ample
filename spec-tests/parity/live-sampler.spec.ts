import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { freshServer, openApp } from '../../web/tests/support';

// Follow-up F1: the sampler (with its notes passing a MIDI transform) is made in the browser inside the live chain, and a knob of the transform changes the pitch that comes out.
const song = `composition "Live sampler" { ample 1  tempo 120  master-seed 17 }

midi-clip phrase {
  chords C Am F G
  bars 4
}

transpose shifted {
  semitones 0
  from phrase.notes -> notes
}

sampler smp {
  file recordings/live-tone.wav
  root 60
  mode oneshot
  from shifted.notes -> notes
}

eq tone {
  low 0 dB
  from smp.audio -> audio
}

master master {
  from tone.audio -> audio
}
`;

function sampleWav(hz: number) {
  const rate = 8000, frames = rate * 2, data = Buffer.alloc(frames * 2), wav = Buffer.alloc(44 + data.length);
  wav.write('RIFF', 0); wav.writeUInt32LE(36 + data.length, 4); wav.write('WAVE', 8); wav.write('fmt ', 12); wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(rate, 24);
  wav.writeUInt32LE(rate * 2, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(data.length, 40);
  for (let frame = 0; frame < frames; frame++) data.writeInt16LE(Math.round(Math.sin(frame * 2 * Math.PI * hz / rate) * 12000), frame * 2);
  data.copy(wav, 44);
  return wav;
}

freshServer({ song, prepare: (_cli, folder) => { fs.mkdirSync(path.join(folder, 'recordings'), { recursive: true }); fs.writeFileSync(path.join(folder, 'recordings', 'live-tone.wav'), sampleWav(220)); } });

/** The loudest frequency (Hz) seen after the block over `seconds`, from the spectrum of its output. */
async function loudestHz(page, block: string, seconds: number) {
  return page.evaluate(async ({ block, seconds }) => {
    const { chainSpectrum } = await import('/app/live-chain.js');
    let bestDb = -Infinity, bestHz = 0;
    const started = performance.now();
    await new Promise<void>((resolve) => { const timer = setInterval(() => {
      const s = chainSpectrum(block);
      if (s) { const hz = s.rate / 2 / (s.post.length - 1); for (let bin = 2; bin < s.post.length; bin++) if (s.post[bin] > bestDb) { bestDb = s.post[bin]; bestHz = bin * hz; } }
      if (performance.now() - started > seconds * 1000) { clearInterval(timer); resolve(); }
    }, 40); });
    return bestHz;
  }, { block, seconds });
}

test('@audio the sampler is made in the browser from its notes, and a transform knob moves the pitch without a build', async ({ page }) => {
  const builds: string[] = [];
  page.on('request', (request) => { if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(request.url()); });
  await openApp(page, { reset: false });
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_STATE.doc.diagnostics.filter((d: any) => d.severity === 'error').length)).toBe(0);
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('tone'))).toBe(true);
  const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  expect(state.items.map((i) => `${i.id}:${i.kind}`)).toEqual(['smp:local', 'tone:effect']);
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('tone')?.left ?? 0)), { timeout: 15000 }).toBeGreaterThan(0.003);
  const before = await loudestHz(page, 'tone', 3);
  expect(before).toBeGreaterThan(80); expect(before).toBeLessThan(450);   // near the 220 Hz tone, moved by the notes of the chords
  const swaps = state.swaps;
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainPreview('shifted', 'semitones', '12')));
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState().swaps)), { timeout: 15000 }).toBeGreaterThan(swaps);
  await page.waitForTimeout(500);
  const after = await loudestHz(page, 'tone', 3);
  expect(after / before).toBeGreaterThan(1.7);   // an octave up (the loudest note may differ, so not exactly 2)
  expect(builds).toHaveLength(0);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
