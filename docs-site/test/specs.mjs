import { chromium } from 'playwright-core'; import path from 'node:path'; import fs from 'node:fs'; import { pathToFileURL } from 'node:url';
const cache = path.join(process.env.LOCALAPPDATA, 'ms-playwright'), dir = fs.readdirSync(cache).filter((d) => d.startsWith('chromium_headless_shell')).sort().pop();
const exe = path.join(cache, dir, fs.readdirSync(path.join(cache, dir))[0], 'chrome-headless-shell.exe');
const b = await chromium.launch({ executablePath: exe }), pg = await b.newPage();
try { await pg.goto(pathToFileURL(path.resolve('..', 'site', 'index.html')).href); await pg.waitForTimeout(400);
const r = await pg.evaluate(async () => { const specs = [...new Set(MUSIC.flatMap((l) => l.pages.map((p) => p.body)).join('').match(/data-pv='([^']*)'/g).map((s) => s.slice(9, -1).replace(/&#39;/g, "'")))].map((s) => JSON.parse(s)); const bad = []; let ok = 0;
  for (const s of specs) for (const wet of [true, false]) { try { const buf = await AmplePreviews.render(s, wet); let pk = 0; for (const v of buf.getChannelData(0)) pk = Math.max(pk, Math.abs(v)); if (!(pk > 0.01)) bad.push('silent: ' + s.title); else ok++; } catch (e) { bad.push(s.title + ': ' + e.message); } } return { specs: specs.length, ok, bad: [...new Set(bad)].slice(0, 10) }; });
console.log(JSON.stringify(r)); } finally { await b.close(); }
