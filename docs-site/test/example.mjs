// Example songs of the block pages: every block with a recorded example has a player; an example song is exclusive (starting it stops loops and music, starting a loop stops it).
// Serves ../site over http itself (the mp3s play only over http).   node example.mjs
import { chromium } from 'playwright-core'; import path from 'node:path'; import fs from 'node:fs'; import http from 'node:http';
const site = path.resolve('..', 'site'), cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe');
const mime = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.mp3': 'audio/mpeg', '.json': 'application/json', '.svg': 'image/svg+xml' };
const srv = http.createServer((q, r) => { const p = path.join(site, decodeURIComponent(q.url.split('?')[0]).replace(/\/$/, '/index.html')); if (!p.startsWith(site)) { r.writeHead(403).end(); return; } fs.readFile(p, (e, d) => { if (e) { r.writeHead(404).end('no'); return; } r.writeHead(200, { 'content-type': mime[path.extname(p)] || 'application/octet-stream' }).end(d); }); });
await new Promise((ok) => srv.listen(8791, '127.0.0.1', ok));
const b = await chromium.launch({ executablePath: exe, args: ['--autoplay-policy=no-user-gesture-required'] }), pg = await b.newPage({ viewport: { width: 1300, height: 900 } }), errs = []; let bad = 0;
const ok = (c, m) => { if (!c) { bad++; console.log('FAIL', m); } else console.log('ok  ', m); };
pg.on('pageerror', (e) => errs.push(e.message));
try {
  const u = 'http://127.0.0.1:8791/index.html';
  await pg.goto(u + '#/blocks/reverb'); await pg.waitForTimeout(2500);
  ok(await pg.locator('.pv-ex .pv-play').count() === 1, 'reverb: example player above the code');
  const loop = pg.locator('.pv:not(.pv-ex) .pv-play').first(), ex = pg.locator('.pv-ex .pv-play');
  await loop.click(); await pg.waitForTimeout(800);
  ok(await pg.locator('.pv:not(.pv-ex).playing').count() === 1, 'a loop plays');
  await ex.click(); await pg.waitForTimeout(1200);
  ok(await pg.locator('.pv-ex.playing').count() === 1 && await pg.locator('.pv:not(.pv-ex).playing').count() === 0, 'example song started: the loop stopped');
  ok(await pg.evaluate(() => AmpleAudio.set.size) === 1, 'only the example song plays');
  await loop.click(); await pg.waitForTimeout(1000);
  ok(await pg.locator('.pv-ex.playing').count() === 0 && await pg.locator('.pv:not(.pv-ex).playing').count() === 1, 'loop started: the example song stopped');
  await ex.click(); await pg.waitForTimeout(800);
  ok(await pg.locator('#t-stop:not([hidden])').count() === 1, 'the Stop button shows while an example song plays');
  await pg.click('#t-stop'); await pg.waitForTimeout(500);
  ok(await pg.evaluate(() => AmpleAudio.set.size) === 0, 'Stop stops everything');
  /* the music and an example song */
  await pg.click('#t-sound'); await pg.waitForTimeout(1500); const mus = await pg.evaluate(() => !!AmplePreviews.music.playing());
  await ex.click(); await pg.waitForTimeout(1000);
  ok(!mus || !(await pg.evaluate(() => AmplePreviews.music.playing())), 'example song started: the music stopped (music was ' + (mus ? 'playing' : 'not started') + ')');
  await pg.click('#t-stop'); await pg.waitForTimeout(300);
  /* every block with a recorded example has a player whose mp3 exists */
  const list = await pg.evaluate(() => BLOCKS.filter((x) => x.exAudio).map((x) => [x.id, x.exAudio.id])); let miss = [];
  for (const [id, aid] of list) { const r = await pg.evaluate(async (a) => (await fetch('audio/' + a + '.mp3', { method: 'HEAD' })).status, aid); if (r !== 200) miss.push(id); }
  ok(list.length > 50 && !miss.length, list.length + ' blocks with a recorded example, mp3 missing: ' + (miss.join(' ') || 'none'));
  for (const id of ['synth', 'delay', 'drums']) { await pg.evaluate((h) => (location.hash = h), '#/blocks/' + id); await pg.waitForTimeout(500); ok(await pg.locator('.pv-ex .pv-play').count() === 1 && await pg.locator('.pv-src[data-audio]').count() === 1, id + ': player above the code box'); }
  await pg.evaluate(() => (location.hash = '#/blocks/reverb')); await pg.waitForTimeout(600); await pg.screenshot({ path: path.resolve('..', 'render', 'example-player.png') });
  ok(!errs.length, 'no page errors ' + errs.slice(0, 2).join(' | '));
} finally { await b.close(); srv.close(); }
process.exit(bad ? 1 : 0);
