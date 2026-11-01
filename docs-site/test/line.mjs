import { chromium } from 'playwright-core'; import path from 'node:path'; import fs from 'node:fs'; import { pathToFileURL } from 'node:url';
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe');
const b = await chromium.launch({ executablePath: exe }), pg = await b.newPage({ viewport: { width: 1300, height: 800 } });
try { await pg.goto(pathToFileURL(path.resolve('..', 'site', 'index.html')).href + '#/learn/ample/basic/first-blocks'); await pg.waitForTimeout(600);
console.log(await pg.evaluate(() => [...document.querySelectorAll('aside.side,main,aside.toc,.wrap')].map((e) => { const c = getComputedStyle(e), r = e.getBoundingClientRect(); return [e.tagName + '.' + e.className, Math.round(r.left), Math.round(r.right), c.borderLeft, c.borderRight, c.outline].join(' | '); }).join('\n')));
} finally { await b.close(); }
