import { test } from '@playwright/test';
import fs from 'node:fs';
import http from 'node:http';
import { freshServerPerTest, openApp } from '../../web/tests/support';
freshServerPerTest();
// Not part of the suite: runs only with SONG_CASES (and SONG_OUT) set, see docs/clientside-preview/conformance.md 'Second round' for the rig.
// SONG_CASES = path of a json file [{ name, type, params, input, ref, tempo, firstBar }]: the live graph of a block on the server's own input wav, against the server's output wav of that block
test('song blocks live vs server', async ({ page }) => {
  test.skip(!process.env.SONG_CASES, 'a measuring rig: set SONG_CASES to a json file of cases');
  test.setTimeout(600000);
  const cases = JSON.parse(fs.readFileSync(process.env.SONG_CASES!, 'utf8'));
  const server = http.createServer((req, res) => { const f = decodeURIComponent(req.url!.slice(1)); res.setHeader('Access-Control-Allow-Origin', '*'); fs.createReadStream(f).on('error', () => { res.statusCode = 404; res.end(); }).pipe(res); });
  await new Promise<void>((r) => server.listen(0, '127.0.0.1', () => r()));
  const port = (server.address() as any).port;
  await openApp(page);
  const out = await page.evaluate(async ({ cases, port }) => {
    const { renderLive } = await import('/app/live-parity.js');
    const wav = async (file: string) => {
      const b = new DataView(await (await fetch(`http://127.0.0.1:${port}/${encodeURIComponent(file)}`)).arrayBuffer());
      let at = 12, ch = 2, bits = 16, rate = 44100, data = 0, size = 0, tag = 1;
      while (at + 8 <= b.byteLength) { const id = String.fromCharCode(b.getUint8(at), b.getUint8(at + 1), b.getUint8(at + 2), b.getUint8(at + 3)), n = b.getUint32(at + 4, true); if (id === 'fmt ') { tag = b.getUint16(at + 8, true); ch = b.getUint16(at + 10, true); rate = b.getUint32(at + 12, true); bits = b.getUint16(at + 22, true); } if (id === 'data') { data = at + 8; size = n; break; } at += 8 + n + (n % 2); }
      const bytes = bits / 8, frames = Math.floor(size / bytes / ch), chans = Array.from({ length: ch }, () => new Float32Array(frames));
      for (let i = 0; i < frames; i++) for (let c = 0; c < ch; c++) { const o = data + (i * ch + c) * bytes; chans[c][i] = tag === 3 ? b.getFloat32(o, true) : bytes === 2 ? b.getInt16(o, true) / 32768 : (b.getInt32(o, true) >> 8) / 8388608; }
      return { sampleRate: rate, channels: chans.length === 1 ? [chans[0], chans[0]] : chans };
    };
    const res: any = {};
    for (const c of cases) {
      const input = await wav(c.input), ref = await wav(c.ref);
      const live = await renderLive(c.type, c.params, input, c.tempo, { firstBar: c.firstBar || 1 });
      const lat = live.latencySamples, n = Math.min(live.channels[0].length - lat, ref.channels[0].length), skip = Math.floor(input.sampleRate * .05);
      let d = 0, e = 0, dl = 0, el = 0; const windows: number[] = [], win = input.sampleRate * 10;
      let wd = 0, we = 0;
      for (let k = 0; k < 2; k++) for (let i = skip; i < n; i++) { const s = ref.channels[k][i], x = live.channels[k][i + lat] - s; d += x * x; e += s * s; if (k === 0) { wd += x * x; we += s * s; if ((i + 1) % win === 0) { windows.push(we > 0 ? 10 * Math.log10(Math.max(wd, 1e-20) / we) : NaN); wd = 0; we = 0; } } }
      res[c.name] = { residualDb: e > 0 ? 10 * Math.log10(Math.max(d, 1e-20) / e) : NaN, levelDb: 10 * Math.log10(Math.max(e, 1e-20) / Math.max(1e-20, (() => { let q = 0; for (let k = 0; k < 2; k++) for (let i = skip; i < n; i++) q += live.channels[k][i + lat] ** 2; return q; })())), frames: n, win10s: windows.map((v) => Math.round(v)) };
    }
    return res;
  }, { cases, port });
  server.close();
  console.log('SONGRESULT ' + JSON.stringify(out));
  fs.writeFileSync(process.env.SONG_OUT!, JSON.stringify(out, null, 1));
});

// The parity badge of every effect block of a song: BADGE_BLOCKS = json file [{ type, name, params }], BADGE_TEMPO; the badge word is describeDeviation of parityCheck (live graph against the offline render)
test('parity badge per block of a song', async ({ page }) => {
  test.skip(!process.env.BADGE_BLOCKS, 'a measuring rig: set BADGE_BLOCKS to a json file of blocks');
  const blocks = JSON.parse(fs.readFileSync(process.env.BADGE_BLOCKS!, 'utf8'));
  await openApp(page);
  const rows = await page.evaluate(async ({ blocks, tempo }) => {
    const { parityCheck } = await import('/app/live-parity.js');
    const out: any[] = [];
    for (const b of blocks) {
      let r: any = null; try { r = await parityCheck(b.type, b.params, { tempo }); } catch (error) { r = { error: String(error) }; }
      out.push({ name: b.name, type: b.type, db: r?.deviationDb === undefined ? null : Math.round(r.deviationDb * 10) / 10, word: r?.word || r?.error || 'no browser render' });
    }
    return out;
  }, { blocks, tempo: Number(process.env.BADGE_TEMPO || 120) });
  console.log('BADGEROWS ' + JSON.stringify(rows));
  fs.writeFileSync(process.env.BADGE_OUT!, JSON.stringify(rows, null, 1));
});
