import { chromium } from 'playwright-core'; import path from 'node:path'; import fs from 'node:fs'; import { pathToFileURL } from 'node:url';
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe');
const b = await chromium.launch({ executablePath: exe }), u = pathToFileURL(path.resolve('..', 'site', 'index.html')).href;
for (const w of [360, 414]) {
  const pg = await b.newPage({ viewport: { width: w, height: 700 } }); await pg.goto(u + '#/studio'); await pg.waitForTimeout(500);
  const r = await pg.evaluate(() => ({ sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth, over: [...document.querySelectorAll('header *')].filter((e) => e.getBoundingClientRect().right > innerWidth + 1 && getComputedStyle(e).display !== 'none').map((e) => e.id || e.className || e.tagName).slice(0, 8) }));
  console.log(w, JSON.stringify(r)); await pg.screenshot({ path: 'm' + w + '.png' });
}
await b.close();
