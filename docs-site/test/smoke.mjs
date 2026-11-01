import { chromium } from 'playwright-core';
import path from 'node:path'; import fs from 'node:fs';
const root = path.resolve('..', 'site', 'index.html');
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright');
const dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe');
const b = await chromium.launch({ executablePath: exe, args: ['--autoplay-policy=no-user-gesture-required'] });
const pg = await b.newPage({ viewport: { width: 1300, height: 800 } }); const errs = [];
pg.on('pageerror', (e) => errs.push('pageerror ' + e.message)); pg.on('console', (m) => m.type() === 'error' && errs.push('console ' + m.text()));
const ok = (n, c) => console.log((c ? 'PASS ' : 'FAIL ') + n);
await pg.goto('file:///' + root.replace(/\\/g, '/'));
ok('home', (await pg.textContent('h1')).includes('Write music'));
await pg.goto(pg.url().split('#')[0] + '#/blocks/reverb'); await pg.waitForTimeout(300);
ok('block page', (await pg.textContent('h1')).includes('reverb') && (await pg.locator('table').count()) >= 1 && (await pg.locator('.portbox').count()) === 2);
ok('block preview slot', (await pg.locator('.pv .pv-play').count()) === 1);
await pg.waitForTimeout(1500); await pg.click('.pv-play'); await pg.waitForTimeout(700);
ok('preview plays', await pg.locator('.pv.playing').count() === 1);
await pg.click('.pv-play');
await pg.goto(pg.url().split('#')[0] + '#/learn/music/advanced/space-and-depth'); await pg.waitForTimeout(300);
ok('learn page + 2 previews', (await pg.locator('.pv').count()) >= 2);
const searchHidden = await pg.evaluate(() => WIP_HIDDEN); /* the search is hidden for now (WIP_HIDDEN in app.js): then only check that it is really gone */
if (searchHidden) { ok('search is hidden', !(await pg.locator('#open-search').isVisible())); await pg.keyboard.press('Control+k'); await pg.waitForTimeout(150); ok('Ctrl K opens nothing', !(await pg.locator('#pal.on').count())); } else {
await pg.keyboard.press('Control+k'); await pg.fill('#q', 'vocod'); await pg.waitForTimeout(200);
ok('search finds vocoder', (await pg.locator('#res a').first().textContent()).toLowerCase().includes('vocoder'));
await pg.keyboard.press('ArrowDown'); await pg.keyboard.press('Enter'); await pg.waitForTimeout(300);
ok('enter navigates', pg.url().includes('#/'));
}
await pg.goto(pg.url().split('#')[0] + '#/nope'); await pg.waitForTimeout(200);
ok('404', (await pg.textContent('h1')).includes('not found'));
await pg.setViewportSize({ width: 600, height: 800 }); ok('menu button on mobile', await pg.locator('#menu').isVisible());
await pg.screenshot({ path: 'shot.png' });
await pg.setViewportSize({width:1300,height:800}); await pg.goto(pg.url().split('#')[0]+'#/blocks/reverb'); await pg.click('#t-alive'); await pg.reload(); await pg.waitForTimeout(400);
ok('vibe remembered after refresh', await pg.evaluate(()=>document.body.classList.contains('alive')));
await pg.waitForTimeout(1500); await pg.click('.pv-play'); await pg.waitForTimeout(900);
ok('vibe reacts to sample level', await pg.evaluate(()=>parseFloat(document.documentElement.style.getPropertyValue('--lvl'))>0));
await pg.screenshot({path:'vibe.png'}); await pg.click('.pv-play'); await pg.click('#t-alive'); await pg.reload(); ok('vibe off remembered', !(await pg.evaluate(()=>document.body.classList.contains('alive'))));
console.log(errs.length ? 'ERRORS:\n' + errs.join('\n') : 'no console errors');
await b.close();
