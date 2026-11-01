import { chromium } from 'playwright-core'; import http from 'node:http'; import path from 'node:path'; import fs from 'node:fs';
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe'), site = path.resolve('..', 'site');
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.mp3': 'audio/mpeg' };
const srv = http.createServer((q, r) => { const f = path.join(site, decodeURIComponent(q.url.split('?')[0]).replace(/^\/$/, '/index.html')); fs.readFile(f, (e, d) => { if (e) { r.writeHead(404).end(); return; } r.writeHead(200, { 'Content-Type': mime[path.extname(f)] || 'application/octet-stream' }).end(d); }); }).listen(0, '127.0.0.1');
await new Promise((r) => srv.on('listening', r)); const port = srv.address().port, b = await chromium.launch({ executablePath: exe, args: ['--autoplay-policy=no-user-gesture-required'] }), errs = [];
try {
  const pg = await b.newPage({ viewport: { width: 1200, height: 900 } }); pg.on('pageerror', (e) => errs.push(e.message));
  await pg.goto('http://127.0.0.1:' + port + '/index.html#/learn/music/basic/chords'); await pg.waitForTimeout(1500);
  const btn = pg.locator('.pv-play'); const n = Math.min(3, await btn.count());
  for (let i = 0; i < n; i++) { await btn.nth(i).click({ modifiers: ['Control'] }); await pg.waitForTimeout(900); }
  await pg.waitForTimeout(3500);
  console.log(JSON.stringify(await pg.evaluate(() => ({ playing: [...document.querySelectorAll('.pv.playing')].length, durs: AmpleAudio ? [...AmpleAudio.set.keys()].length : 0 }))));
  const pos = await pg.evaluate(() => [...document.querySelectorAll('.pv')].slice(0, 3).map((e) => { const c = e.querySelector('canvas'); return c ? c.toDataURL().length : 0; }));
  console.log('canvases', pos.join(','));
  const real = await pg.evaluate(async () => { const out = []; for (const e of [...document.querySelectorAll('.pv')].slice(0, 3)) { const s = JSON.parse(e.dataset.pv); const bf = await AmplePreviews.render(s, true); out.push(bf._real + ':' + bf.duration.toFixed(2)); } return out; });
  console.log('buffers', real.join(' '));
} finally { console.log(errs.length ? 'ERRORS ' + errs.slice(0, 3).join('|') : 'no errors'); await b.close(); srv.close(); }
