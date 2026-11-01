import { chromium } from 'playwright-core'; import path from 'node:path'; import fs from 'node:fs'; import { pathToFileURL } from 'node:url';
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe');
const b = await chromium.launch({ executablePath: exe }), pg = await b.newPage({ viewport: { width: 1300, height: 800 } });
const u = pathToFileURL(path.resolve('..', 'site', 'index.html')).href;
await pg.goto(u + '#/learn/ample/advanced/space'); await pg.waitForTimeout(1200); await pg.screenshot({ path: 'dark.png' });
await pg.click('#theme'); await pg.waitForTimeout(300); await pg.screenshot({ path: 'light.png' });
await b.close();
