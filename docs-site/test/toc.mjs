import { chromium } from 'playwright-core'; import path from 'node:path'; import fs from 'node:fs'; import { pathToFileURL } from 'node:url';
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe');
const b = await chromium.launch({ executablePath: exe }), pg = await b.newPage({ viewport: { width: 1300, height: 800 } }), u = pathToFileURL(path.resolve('..', 'site', 'index.html')).href;
for (const p of ['#/learn/ample/basic/bars-and-time', '#/blocks/reverb', '#/language/if-else']) {
  await pg.goto(u + p); await pg.waitForTimeout(500); const n = await pg.locator('aside.toc a').count();
  for (let i = 0; i < n; i++) { await pg.locator('aside.toc a').nth(i).click(); await pg.waitForTimeout(900);
    const r = await pg.evaluate((i) => { const a = document.querySelectorAll('aside.toc a')[i], h = document.getElementById(a.dataset.h), top = h.getBoundingClientRect().top; return { h: h.textContent, top: Math.round(top), ok: top >= 50 && top < 400, on: a.classList.contains('on') }; }, i);
    console.log(p, JSON.stringify(r)); }
}
await b.close();
