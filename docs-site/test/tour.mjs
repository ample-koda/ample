import { chromium } from 'playwright-core'; import path from 'node:path'; import fs from 'node:fs'; import { pathToFileURL } from 'node:url';
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe');
const b = await chromium.launch({ executablePath: exe }), pg = await b.newPage({ viewport: { width: 1300, height: 900 } }), errs = [];
pg.on('pageerror', (e) => errs.push(e.message));
try { const u = pathToFileURL(path.resolve('..', 'site', 'index.html')).href;
  const routes = await pg.goto(u + '#/learn').then(() => pg.evaluate(() => LEARN.find((l) => l.id === 'tour').pages.map((p) => '/learn/ample/tour/' + p.slug)));
  for (const r of routes) { await pg.evaluate((r) => (location.hash = '#' + r), r); await pg.waitForTimeout(250); const rows = await pg.locator('table.blocktable tr').count(); const raw = await pg.evaluate(() => /\{\{|@@\d/.test(document.getElementById('main').innerHTML)); console.log(r, 'rows', rows, raw ? 'RAW!' : 'ok'); }
  await pg.evaluate(() => (location.hash = '#/learn/ample/tour/sources-and-instruments')); await pg.waitForTimeout(400); await pg.screenshot({ path: 'tour.png' });
} finally { console.log(errs.length ? 'ERRORS ' + errs.join('|') : 'no errors'); await b.close(); }
