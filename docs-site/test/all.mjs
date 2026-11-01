import { chromium } from 'playwright-core'; import path from 'node:path'; import fs from 'node:fs'; import { pathToFileURL } from 'node:url';
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe');
const b = await chromium.launch({ executablePath: exe, args: ['--autoplay-policy=no-user-gesture-required'] }), pg = await b.newPage({ viewport: { width: 1300, height: 900 } }), errs = [];
pg.on('pageerror', (e) => errs.push(e.message)); pg.on('console', (m) => m.type() === 'error' && errs.push(m.text()));
const u = pathToFileURL(path.resolve('..', 'site', 'index.html')).href;
try {
  await pg.goto(u + '#/learn'); await pg.waitForTimeout(500);
  const routes = await pg.evaluate(() => MUSIC.flatMap((l) => l.pages.map((p) => '/learn/music/' + l.id + '/' + p.slug)));
  let pv = 0, kb = 0, bad = [];
  for (const r of routes) {
    await pg.evaluate((r) => (location.hash = '#' + r), r); await pg.waitForTimeout(250);
    const n = await pg.locator('.pv').count(), k = await pg.locator('.kb').count(); pv += n; kb += k;
    const raw = await pg.evaluate(() => /\{\{|@@\d+@@/.test(document.getElementById('main').innerHTML));
    if (raw || !(await pg.textContent('h1'))) bad.push(r);
  }
  console.log('pages', routes.length, 'previews', pv, 'keyboards', kb, 'bad', bad.join(',') || 'none');
  // every preview must render (OfflineAudioContext) without throwing
  await pg.evaluate(() => (location.hash = '#/learn/music/basic/scales')); await pg.waitForTimeout(400);
  console.log('keyboard click ->', await pg.evaluate(() => { document.querySelector('.kb .wk').click(); return !!AmpleAudio.ctx; }));
  await pg.locator('summary').first().click(); console.log('details open:', await pg.locator('details[open]').count());
  await pg.screenshot({ path: 'scales.png', fullPage: false });
  const specs = await pg.evaluate(() => 0);
} finally { console.log(errs.length ? 'ERRORS ' + [...new Set(errs)].slice(0, 5).join('|') : 'no errors'); await b.close(); }
