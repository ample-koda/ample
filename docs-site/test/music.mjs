import { chromium } from 'playwright-core'; import path from 'node:path'; import fs from 'node:fs'; import { pathToFileURL } from 'node:url';
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe');
const b = await chromium.launch({ executablePath: exe }), pg = await b.newPage({ viewport: { width: 1300, height: 900 } }), errs = [];
pg.on('pageerror', (e) => errs.push(e.message)); pg.on('console', (m) => m.type() === 'error' && errs.push(m.text()));
try { await pg.goto(pathToFileURL(path.resolve('..', 'site', 'index.html')).href + '#/music'); await pg.waitForTimeout(600);
console.log('h1', await pg.textContent('h1'), 'cards', await pg.locator('.card').count(), 'nav', await pg.locator('nav.top a[data-s=music]').count());
await pg.goto(pg.url().split('#')[0] + '#/learn/music/basic/chords'); await pg.waitForTimeout(500);
console.log('previews', await pg.locator('.pv').count(), 'ample-box', await pg.locator('.ample-box a').count());
await pg.screenshot({ path: 'music.png' });
} finally { console.log(errs.length ? 'ERRORS ' + errs.join('|') : 'no errors'); await b.close(); }
