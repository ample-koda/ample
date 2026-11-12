import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Plan local-preview 4.8: the browser renders a General MIDI note with SpessaSynth from the pack SoundFont the server hands out; compared with the server's own render of the same note.
freshServer();
test('browser SoundFont note against the server render (onset and level)', async ({ page }) => {
  test.setTimeout(180000);
  await openApp(page, { reset: false });
  const r = await page.evaluate(async () => {
    const head = await fetch('/api/soundfont/general-midi', { headers: { Range: 'bytes=0-11', 'X-Ample-Token': (await import('/app/api.js')).getToken() } });
    if (head.status !== 206 && head.status !== 200) return { missing: head.status };
    const { getToken } = await import('/app/api.js');
    const response = await fetch('/api/soundfont/general-midi', { headers: { 'X-Ample-Token': getToken() } });
    const sf = await response.arrayBuffer();
    const { createSf2Synth, renderSf2Note } = await import('/lib/sf2-voice.mjs');
    const t0 = performance.now(), synth = createSf2Synth(sf, 44100), loadMs = performance.now() - t0;
    const out: any[] = [];
    for (const [program, channel, pitch] of [[0, 0, 60], [24, 0, 60], [33, 0, 60], [56, 0, 60], [0, 9, 36], [0, 9, 38]]) {
      const t1 = performance.now(), [mine] = renderSf2Note(synth, { program, channel, pitch, velocity: 100, seconds: 0.25, tail: 0.75 }), renderMs = performance.now() - t1;
      const body = { file: 'pack:musescore-general', bank: 0, program, channel, gain: 0, limiter: 'soft', notes: [{ bar: 1, tick: 0, ticks: 500000, ppq: 1000000, pitch, velocity: 100 }] };
      const resp = await fetch('/api/gm/audition', { method: 'POST', headers: { 'X-Ample-Token': getToken(), 'Content-Type': 'application/json' }, body: JSON.stringify(body) }); if (!resp.ok) return { missing: 'audition ' + resp.status + ' ' + (await resp.text()).slice(0, 200) }; const wav = await resp.arrayBuffer();
      const ctx = new OfflineAudioContext(2, 44100, 44100), decoded = await ctx.decodeAudioData(wav), theirs = decoded.getChannelData(0);
      const onset = (a: Float32Array) => { for (let i = 0; i < a.length; i++) if (Math.abs(a[i]) > 0.002) return i; return -1; };
      const rms = (a: Float32Array, n: number) => { let s = 0; for (let i = 0; i < n; i++) s += a[i] * a[i]; return Math.sqrt(s / n); };
      const n = Math.min(mine.length, theirs.length, 30000);
      const peak = (a: Float32Array) => { let p = 0; for (let i = 0; i < 30000; i++) p = Math.max(p, Math.abs(a[i])); return p; }; const win = (a: Float32Array, k: number) => rms(a.subarray(k * 4410, k * 4410 + 4410), 4410).toFixed(4); out.push({ peakMine: peak(mine), peakTheirs: peak(theirs), wm: [0, 1, 3, 6].map((k) => win(mine, k)), wt: [0, 1, 3, 6].map((k) => win(theirs, k)), program, onsetMine: onset(mine), onsetTheirs: onset(theirs), dbDiff: 20 * Math.log10(rms(mine, n) / rms(theirs, n)), renderMs });
    }
    return { loadMs, out };
  });
  if ((r as any).missing) console.log('MISSING', JSON.stringify(r));
  if ((r as any).missing) test.skip(true, 'the General MIDI pack is not installed here');
  console.log('SF2 parity', JSON.stringify(r));
  for (const row of (r as any).out) { expect(Math.abs(row.onsetMine - row.onsetTheirs)).toBeLessThan(100); expect(Math.abs(row.dbDiff), 'level of program ' + row.program).toBeLessThan(1) }
});
