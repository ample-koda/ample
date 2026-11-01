import { chromium } from 'playwright-core'; import path from 'node:path'; import fs from 'node:fs'; import { pathToFileURL } from 'node:url';
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe');
const b = await chromium.launch({ executablePath: exe }), pg = await b.newPage({ viewport: { width: 1300, height: 300 }, deviceScaleFactor: 2 }), u = pathToFileURL(path.resolve('..', 'site', 'index.html')).href;
await pg.goto(u); await pg.waitForTimeout(300); await pg.screenshot({ path: 'g1.png', clip: { x: 0, y: 0, width: 300, height: 56 } });
await pg.waitForTimeout(2500); await pg.screenshot({ path: 'g2.png', clip: { x: 0, y: 0, width: 300, height: 56 } }); await b.close();
