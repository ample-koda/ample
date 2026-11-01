import { chromium } from 'playwright-core'; import http from 'node:http'; import path from 'node:path'; import fs from 'node:fs'; import { pathToFileURL } from 'node:url';
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe'), site = path.resolve('..', 'site');
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.mp3': 'audio/mpeg' };
const srv = http.createServer((q, r) => { const f = path.join(site, decodeURIComponent(q.url.split('?')[0]).replace(/^\/$/, '/index.html')); fs.readFile(f, (e, d) => { if (e) { r.writeHead(404); r.end(); } else { r.writeHead(200, { 'content-type': mime[path.extname(f)] || 'application/octet-stream' }); r.end(d); } }); }).listen(0, '127.0.0.1');
await new Promise((r) => srv.on('listening', r)); const port = srv.address().port, b = await chromium.launch({ executablePath: exe, args: ['--autoplay-policy=no-user-gesture-required'] }), errs = [];
try {
  for (const [label, url] of [['file', pathToFileURL(path.join(site, 'index.html')).href], ['http', 'http://127.0.0.1:' + port + '/index.html']]) {
    const pg = await b.newPage({ viewport: { width: 1300, height: 900 } }); pg.on('pageerror', (e) => errs.push(label + ' ' + e.message));
    await pg.goto(url + '#/learn/music/basic/chords'); await pg.waitForTimeout(1500);
    await pg.locator('.pv-code').first().click(); await pg.waitForTimeout(1200);
    const cm = await pg.evaluate(() => ({ lines: document.querySelectorAll('.pv-cm .cm-line').length, colored: new Set([...document.querySelectorAll('.pv-cm .cm-line span')].map((s) => getComputedStyle(s).color)).size, first: document.querySelector('.pv-cm .cm-line')?.textContent }));
    const real = await pg.evaluate(async () => { const s = JSON.parse(document.querySelector('.pv').dataset.pv); const b = await AmplePreviews.render(s, true); return !!b._real; });
    console.log(label, JSON.stringify(cm), 'real audio:', real);
    if (label === 'http') { await pg.locator('.pv-src').first().screenshot({ path: 'code.png' }); }
    await pg.close();
  }
} finally { console.log(errs.length ? 'ERRORS ' + errs.slice(0, 3).join('|') : 'no errors'); await b.close(); srv.close(); }
