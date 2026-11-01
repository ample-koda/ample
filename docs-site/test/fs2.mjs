import { chromium } from 'playwright-core'; import path from 'node:path'; import fs from 'node:fs'; import { pathToFileURL } from 'node:url';
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe'), site = path.resolve('..', 'site');
const b = await chromium.launch({ executablePath: exe });
try { const pg = await b.newPage({ viewport: { width: 390, height: 800 } }); await pg.goto('https://ample-koda.dev/index.html' + '#/guide/first-song'); await pg.waitForTimeout(900);
  console.log(JSON.stringify(await pg.evaluate(() => ({ h1: document.querySelector('h1')?.textContent, pn: [...document.querySelectorAll('.pn a')].map((a) => a.textContent.replace(/\s+/g, ' ') + ' -> ' + a.getAttribute('href')) }))));
  await pg.locator('.pn a').last().click(); await pg.waitForTimeout(500); console.log('next goes to:', await pg.evaluate(() => document.querySelector('h1')?.textContent));
  const dead = []; const routes = await pg.evaluate(() => Object.keys(P).filter((k) => k.startsWith('/learn/') && k.split('/').length === 5));
  for (const r of routes) { await pg.evaluate((r) => { location.hash = '#' + r; }, r); await pg.waitForTimeout(25); const n = await pg.evaluate(() => [...document.querySelectorAll('.pn a')].length); if (n < 2) dead.push(r + ' (' + n + ')'); }
  console.log('learn pages with fewer than 2 prev/next cards:', dead.length, dead.slice(0, 12).join(' '));
} finally { await b.close(); }
