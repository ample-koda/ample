import { chromium } from 'playwright-core'; import path from 'node:path'; import fs from 'node:fs'; import { pathToFileURL } from 'node:url';
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe');
const b = await chromium.launch({ executablePath: exe }), pg = await b.newPage({ viewport: { width: 390, height: 600 } });
try { await pg.goto(pathToFileURL(path.resolve('..', 'site', 'index.html')).href); await pg.waitForTimeout(500);
console.log(await pg.evaluate(() => ['#menu', '.logo b', '#t-alive', '#t-sound', '#theme', '#open-search'].map((s) => { const e = document.querySelector(s), v = e.querySelector('svg'); if (!v) return s + ' no svg'; const a = e.getBoundingClientRect(), c = v.getBoundingClientRect(); return s + ' dx=' + ((a.left + a.width / 2) - (c.left + c.width / 2)).toFixed(1) + ' dy=' + ((a.top + a.height / 2) - (c.top + c.height / 2)).toFixed(1); }).join('\n')));
} finally { await b.close(); }
