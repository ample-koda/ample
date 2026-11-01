import { chromium } from 'playwright-core'; import http from 'node:http'; import path from 'node:path'; import fs from 'node:fs';
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe'), site = path.resolve('..', 'site');
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.mp3': 'audio/mpeg' };
const srv = http.createServer((q, r) => { const f = path.join(site, decodeURIComponent(q.url.split('?')[0]).replace(/^\/$/, '/index.html')); fs.readFile(f, (e, d) => { if (e) { r.writeHead(404).end(); return; } r.writeHead(200, { 'Content-Type': mime[path.extname(f)] || 'application/octet-stream' }).end(d); }); }).listen(0, '127.0.0.1');
await new Promise((r) => srv.on('listening', r)); const port = srv.address().port, b = await chromium.launch({ executablePath: exe, args: ['--autoplay-policy=no-user-gesture-required'] }), errs = [];
try {
  const pg = await b.newPage({ viewport: { width: 1000, height: 900 } }); pg.on('pageerror', (e) => errs.push(e.message));
  await pg.goto('http://127.0.0.1:' + port + '/index.html#/learn/music/basic/chords'); await pg.waitForTimeout(1500);
  const st = () => pg.evaluate(() => ({ playing: document.querySelectorAll('.pv.playing').length, sticky: !!AmpleAudio.sticky }));
  const press = async (i, ms, type) => { await pg.evaluate(([i, type]) => { const el = document.querySelectorAll('.pv-play')[i]; el.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, pointerType: type })); }, [i, type]); await pg.waitForTimeout(ms); await pg.evaluate(([i, type]) => { const el = document.querySelectorAll('.pv-play')[i]; el.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, pointerType: type })); el.click(); }, [i, type]); await pg.waitForTimeout(500); };
  await press(0, 100, 'touch'); console.log('short tap', JSON.stringify(await st()));
  await press(0, 100, 'touch'); console.log('tap again (stop)', JSON.stringify(await st()));
  await press(0, 700, 'touch'); console.log('long press touch', JSON.stringify(await st()));
  await press(1, 700, 'mouse'); console.log('long press mouse on 2nd', JSON.stringify(await st()));
} finally { console.log(errs.length ? 'ERRORS ' + errs.slice(0, 3).join('|') : 'no errors'); await b.close(); srv.close(); }
