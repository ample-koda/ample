import { chromium } from 'playwright-core'; import path from 'node:path'; import fs from 'node:fs'; import { pathToFileURL } from 'node:url';
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe');
const b = await chromium.launch({ executablePath: exe, args: ['--autoplay-policy=no-user-gesture-required'] }), pg = await b.newPage({ viewport: { width: 1300, height: 900 } }), u = pathToFileURL(path.resolve('..', 'site', 'index.html')).href, errs = [];
pg.on('pageerror', (e) => errs.push(e.message));
try {
  await pg.goto(u + '#/learn/advanced/modulation'); await pg.waitForTimeout(2500);
  await pg.locator('.pv-play').nth(0).click({ modifiers: ['Control'] }); await pg.waitForTimeout(500);
  await pg.evaluate(() => (location.hash = '#/blocks/reverb')); await pg.waitForTimeout(600);
  console.log('still playing after nav:', await pg.evaluate(() => AmpleAudio.set.size), 'label:', await pg.textContent('#t-sound'));
  await pg.click('#t-sound'); await pg.waitForTimeout(300);
  console.log('after stop:', await pg.evaluate(() => AmpleAudio.set.size), 'sound mode on:', await pg.evaluate(() => document.body.classList.contains('sound')), 'label:', (await pg.textContent('#t-sound')).trim());
  await pg.locator('.pv-play').nth(0).click(); await pg.evaluate(() => (location.hash = '#/blocks/delay')); await pg.waitForTimeout(500);
  console.log('plain click stops on nav:', await pg.evaluate(() => AmpleAudio.set.size));
} finally { console.log(errs.length ? 'ERRORS ' + errs.join('|') : 'no errors'); await b.close(); }
