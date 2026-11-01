import { chromium } from 'playwright-core'; import path from 'node:path'; import fs from 'node:fs'; import { pathToFileURL } from 'node:url';
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe');
const b = await chromium.launch({ executablePath: exe }), pg = await b.newPage({ viewport: { width: 390, height: 720 } });
try { await pg.goto(pathToFileURL(path.resolve('..', 'site', 'index.html')).href + '#/music'); await pg.waitForTimeout(500);
await pg.click('#menu'); await pg.waitForTimeout(300); await pg.screenshot({ path: 'drawer.png' });
await pg.click('.secs a:has-text("Blocks")'); await pg.waitForTimeout(400);
console.log('went to', await pg.textContent('h1'), 'drawer open:', await pg.evaluate(() => document.body.classList.contains('menu')));
await pg.setViewportSize({ width: 1300, height: 800 }); console.log('desktop secs visible:', await pg.locator('.secs').isVisible());
} finally { await b.close(); }
