// Docs generator. Sources: src/ (site code), content/ (pages as Markdown+HTML), spec/templates of the repository (read only).
// Output: site/ (open site/index.html), STATUS.md (what exists / what changed), manifest.json (hashes of the last build).
// Usage: node build.mjs            build everything
//        node build.mjs --status   only print what is new / changed / missing (no writes)
import fs from 'node:fs'; import path from 'node:path'; import crypto from 'node:crypto';
const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
const rd = (p) => fs.readFileSync(path.join(here, p), 'utf8'), W = (p, t) => { fs.mkdirSync(path.dirname(path.join(here, p)), { recursive: true }); fs.writeFileSync(path.join(here, p), t); };
import { exampleOf } from './lib/exassets.mjs';
const hashEx = (t) => crypto.createHash('sha1').update(String(t).split('\n').filter((l) => !/^\s*#/.test(l)).join('\n')).digest('hex').slice(0, 12); /* the id of a rendered example song, same as sha() in tools/render-samples.mjs */
const hash = (t) => crypto.createHash('sha1').update(t).digest('hex').slice(0, 10);
const streamName = (p) => (p.type === 'audio' && (p.name === 'in' || p.name === 'out') ? 'audio' : p.name); // v1 names a port by the stream it carries (lint L010); the spec still has in/out on a few blocks
const statusOnly = process.argv.includes('--status');
const root = path.resolve(here, '..'); // the repository root: this folder is docs-site/ of the repository, spec/ is next to it
const specDir = path.join(root, 'spec/templates');
const old = fs.existsSync(path.join(here, 'manifest.json')) ? JSON.parse(rd('manifest.json')) : { spec: {}, content: {} };

// 1. blocks from the spec
const blocks = [], man = { spec: {}, content: {} };
for (const f of fs.readdirSync(specDir).filter((f) => f.endsWith('.json')).sort()) {
  const raw = fs.readFileSync(path.join(specDir, f), 'utf8'), j = JSON.parse(raw); man.spec[j.id] = hash(raw);
  blocks.push({ id: j.id, title: j.title, category: j.category, cls: j.class, version: j.version, since: j.since, status: j.status, ex: j.example || '', needs: j.needs || [], description: j.description, preview: !!(j.clientPreview || j.livePreview),
    inputs: (j.inputs || []).map((p) => ({ n: streamName(p), t: p.type, r: !!p.required })), outputs: (j.outputs || []).map((p) => ({ n: streamName(p), t: p.type })),
    params: (j.parameters || []).map((p) => ({ n: p.name, l: p.label, d: p.default, u: p.unit, min: p.min, max: p.max, o: p.options, doc: p.doc, a: !!p.automatable })) });
}

// 1b. the recorded example song of a block (tools/render-samples.mjs -> audio/ex-<hash>.mp3, listed in samples.json under "example")
{ const sp = path.join(here, 'samples.json'), byBlock = new Map(fs.existsSync(sp) ? JSON.parse(fs.readFileSync(sp, 'utf8')).filter((x) => x.example && x.stats).map((x) => [x.example, x]) : []);
  for (const b of blocks) { const x = byBlock.get(b.id); if (b.ex) { const X = exampleOf(b.id, b.ex); b.ex = X.code; b.exKey = X.key; b.exa = { files: X.files, note: X.note, noRemix: X.noRemix }; } if (x && b.ex && x.id === 'ex-' + hashEx(b.exKey || b.ex) && fs.existsSync(path.join(here, 'audio', x.id + '.mp3'))) b.exAudio = { id: x.id, bpm: x.bpm, seconds: x.seconds, cut: x.stats.cut ? Math.round(x.seconds) : 0 }; } }

// 2. learn path from content/learn/<level>/NN-slug.md   (front matter: title, lead; body = HTML + {{pv {json}}} previews)
const fm = (t) => { const m = t.match(/^---\n([\s\S]*?)\n---\n?([\s\S]*)$/); const o = {}; (m ? m[1] : '').split('\n').forEach((l) => { const i = l.indexOf(':'); if (i > 0) o[l.slice(0, i).trim()] = l.slice(i + 1).trim(); }); return { ...o, body: (m ? m[2] : t).trim() }; };
// 1b. the editors of the blocks, in action: screenshots of the app (made by the screenshot runs, tools/screenshots), one copy in get-site/assets/shots.
// A block page shows get-site/assets/shots/blocks/<id>.png (the panel behind Advanced, or the editor of the block) and the pictures listed in EXTRA.
const shotsDir = path.join(root, 'get-site/assets/shots');
const EXTRA = { sequencer: [['sequencer-workspace', 'The sequencer workspace: channels, patterns and the playlist.'], ['sequencer-patterns', 'The pattern tabs, the pattern line and the rows of a pattern.'], ['sequencer-playlist', 'The playlist zoomed to fit.']], 'midi-clip': [['jam-roll', 'AMPLE Jam, Roll view: the notes of the clip, with the other voices behind them.'], ['jam-grid', 'AMPLE Jam, Grid view: one row per sound, one cell per step.']] };
const OWN = { drums: 'The step grid of the drums block, in the properties panel.', guitar: 'The strum of the guitar block, in the properties panel.', lyrics: 'The lyrics editor: lines with their bar, the checks and the timing view.' };
for (const b of blocks) {
  const list = [];
  if (fs.existsSync(path.join(shotsDir, 'blocks', b.id + '.png'))) list.push({ f: 'blocks/' + b.id, alt: b.title + ' editor', cap: OWN[b.id] || ('The ' + b.title + ' panel (Advanced on the block), with a sound going through it and a control moved a little.') });
  for (const [f, cap] of EXTRA[b.id] || []) if (fs.existsSync(path.join(shotsDir, f + '.png'))) list.push({ f, alt: cap.split(':')[0], cap });
  if (list.length) b.shots = list;
}
console.log('blocks with a picture of their editor: ' + blocks.filter((b) => b.shots).length);

import { render as mdRender, remixId } from './lib/md.mjs';
const warns = [], knownBlocks = new Set(blocks.map((x) => x.id)), blockData = new Map(blocks.map((x) => [x.id, x]));
const AI_EXCLUDE = new Set(['ace-music', 'ai-voice', 'voice-clone', 'scorer']); // plugin/AI blocks: not on a learn page yet (not yet)
const expand = (b) => mdRender(b, { blocks: knownBlocks, blockData, warn: (m) => warns.push(m) });
const merged = [];
const readTrack = (track) => { const out = [];
for (const lv of fs.readdirSync(path.join(here, 'content/' + track)).sort()) {
  const dir = 'content/' + track + '/' + lv; if (!fs.statSync(path.join(here, dir)).isDirectory()) continue;
  const meta = fm(rd(dir + '/_level.md')), pages = [];
  for (const f of fs.readdirSync(path.join(here, dir)).filter((f) => f.endsWith('.md') && f[0] !== '_').sort()) {
    const raw = rd(dir + '/' + f), p = fm(raw); if (p.merged) { merged.push(lv + '/' + f); continue; } /* merged into a Music page: kept as a file until it is deleted */ man.content[track + '/' + lv + '/' + f] = hash(raw);
    pages.push({ order: +p.order || (pages.length + 1) * 10, slug: f.replace(/^\d+-|\.md$/g, ''), title: p.title, lead: p.lead, body: expand(p.body), file: lv + '/' + f, previews: (p.body.match(/\{\{pv /g) || []).length });
  }
  pages.sort((a, b) => a.order - b.order); if (!pages.length) continue; out.push({ id: lv, order: +meta.order || 99, title: meta.title, blurb: meta.blurb, pages });
}
return out.sort((a, b) => a.order - b.order); };
const learn = readTrack('learn'), music = fs.existsSync(path.join(here, 'content/music')) ? readTrack('music') : [];

// 3. what changed / is missing
const changed = blocks.filter((b) => old.spec[b.id] && old.spec[b.id] !== man.spec[b.id]).map((b) => b.id);
const added = blocks.filter((b) => !old.spec[b.id]).map((b) => b.id), gone = Object.keys(old.spec).filter((id) => !man.spec[id]);
const cchanged = Object.keys(man.content).filter((k) => old.content[k] !== man.content[k]);
const noPv = learn.flatMap((l) => l.pages).filter((p) => !p.previews).map((p) => p.file);
const stubs = learn.flatMap((l) => l.pages).filter((p) => p.body.replace(/<[^>]+>/g, '').length < 120).map((p) => p.file);
const summary = `spec blocks: ${blocks.length} (new ${added.length}, changed ${changed.length}, removed ${gone.length}) | music pages: ${music.flatMap((l) => l.pages).length} | learn pages: ${learn.flatMap((l) => l.pages).length} (changed ${cchanged.length}, thin ${stubs.length}, without preview ${noPv.length})`;
console.log(summary); if (added.length) console.log('new blocks:', added.join(' ')); if (changed.length) console.log('changed blocks:', changed.join(' ')); if (gone.length) console.log('removed blocks:', gone.join(' '));
if (stubs.length) console.log('thin pages (write more):', stubs.join(' '));
const routes = new Set(['/blocks', '/music', '/learn', ...blocks.map((x) => '/blocks/' + x.id), ...music.flatMap((l) => l.pages.map((p) => '/learn/music/' + l.id + '/' + p.slug)), ...learn.flatMap((l) => l.pages.map((p) => '/learn/ample/' + l.id + '/' + p.slug))]);
for (const [name, list] of [['music', music], ['learn', learn]]) for (const l of list) for (const p of l.pages) for (const m of p.body.matchAll(/href="#(\/(?:blocks|music|learn)[^"]*)"/g)) if (!routes.has(m[1])) warns.push(name + '/' + p.file + ': broken link ' + m[1]);
const seen = new Set(); for (const l of music) for (const p of l.pages) { const k2 = l.id + '/' + p.slug; if (seen.has(k2)) warns.push('duplicate music slug ' + k2); seen.add(k2); }
const words = new Set(fs.readdirSync(path.join(here, 'content'), { recursive: true }).filter((f) => f.endsWith('.md')).flatMap((f) => rd('content/' + f).split(/[^\w-]+/)));
const notOnPage = blocks.filter((x) => !words.has(x.id) && !AI_EXCLUDE.has(x.id)).map((x) => x.id);
console.log('blocks on a learn page: ' + (blocks.length - notOnPage.length - AI_EXCLUDE.size) + '/' + (blocks.length - AI_EXCLUDE.size) + (notOnPage.length ? ' | NOT on a page: ' + notOnPage.join(' ') : ' (all)') + ' | skipped (AI/plugin): ' + [...AI_EXCLUDE].join(' '));
if (warns.length) console.log('WARNINGS:\n' + [...new Set(warns)].join('\n'));
if (statusOnly) process.exit(0);

// 4. write site/
fs.mkdirSync(path.join(here, 'site'), { recursive: true });
for (const f of fs.readdirSync(path.join(here, 'src'))) fs.copyFileSync(path.join(here, 'src', f), path.join(here, 'site', f));
fs.copyFileSync(path.join(here, 'lib/amplet.js'), path.join(here, 'site/amplet.js'));
{ // share IDs of every sample in AMPLE Remix (https://remix.ample-koda.dev/mix/<ID>), used by previews.js
  const ids = {}; for (const s of JSON.parse(rd('samples.json'))) if (s.code) ids[s.id] = remixId(s.code);
  W('site/remix-ids.js', 'window.REMIX_IDS = ' + JSON.stringify(ids, null, 1) + ';\n');
}
// the screenshots of AMPLE are made by the screenshot runs (tools/screenshots) and copied from there, so this site never keeps its own old copy
const shots = path.join(root, 'get-site/assets/shots');
if (fs.existsSync(shots)) { for (const sub of ['', 'blocks']) { const from = path.join(shots, sub); if (!fs.existsSync(from)) continue; fs.mkdirSync(path.join(here, 'site/shots', sub), { recursive: true }); for (const f of fs.readdirSync(from)) if (f.endsWith('.png')) fs.copyFileSync(path.join(from, f), path.join(here, 'site/shots', sub, f)); } }
else console.log('WARNING: no screenshots found at ' + shots + ' (run: npx playwright test -c tools/screenshots/playwright.config.ts at the repository root)');
const vend = [path.join(root, 'web/vendor/codemirror-ample.js'), path.join(here, 'vendor/codemirror-ample.js')].find((p) => fs.existsSync(p)) ?? ''; // the studio/Lite highlighter, copied read-only and made a classic script (works from file://)
if (fs.existsSync(vend)) {
  const v = fs.readFileSync(vend, 'utf8');
  // the names are minified and may start with $ (the bundle of 2026-11-15 exports "$m as createAmplePreview")
  const m = v.match(/export\{([\w$]+) as createAmpleEditor,([\w$]+) as createAmplePreview\};?\s*$/);
  if (!m) throw new Error('codemirror-ample.js: export pattern changed, adapt build.mjs');
  man.vendor = hash(v);
  if (old.vendor && old.vendor !== man.vendor) console.log('highlighter bundle updated');
  const exported = 'window.AmpleCM={createAmpleEditor:' + m[1] + ',createAmplePreview:' + m[2] + '};';
  W('site/vendor/codemirror-ample.js', '(function(){' + v.replace(m[0], () => exported) + '})();');
} else if (old.vendor) man.vendor = old.vendor;
W('site/blocks-data.js', '// generated by build.mjs from spec/templates (read-only). Do not edit.\nwindow.BLOCKS = ' + JSON.stringify(blocks) + ';\n');
W('site/music-data.js', 'window.MUSIC = ' + JSON.stringify(music) + ';\n');
W('site/learn-data.js', '// generated by build.mjs from content/learn. Do not edit.\nconst pv = (title, src, fx, p, note) => `<div class="pv" data-pv=\'${JSON.stringify({ title, src, fx: fx || "dry", p: p || {}, note }).replace(/\'/g, "&#39;")}\'></div>`;\nwindow.LEARN = ' + JSON.stringify(learn) + ';\n');
W('manifest.json', JSON.stringify(man, null, 1));
const PVB = new Set(['reverb', 'delay', 'chorus', 'flanger', 'phaser', 'tremolo', 'distortion', 'bitcrusher', 'compressor', 'gate', 'duck', 'width', 'filter', 'eq', 'pan', 'volume', 'pitch', 'reverse', 'vocoder']);
W('STATUS.md', `# Docs status (generated by build.mjs)\n\n${summary}\n\n## Learn pages\n| file | previews |\n|---|---|\n${learn.flatMap((l) => l.pages).map((p) => `| content/learn/${p.file} | ${p.previews} |`).join('\n')}\n\n## Blocks (from spec)\n| block | category | class | since | sound preview in docs |\n|---|---|---|---|---|\n${blocks.map((b) => `| ${b.id} | ${b.category} | ${b.cls} ${b.version} | ${b.since} | ${PVB.has(b.id) ? 'yes' : 'no'} |`).join('\n')}\n`);
for (const b of blocks) for (const f of (b.exa && b.exa.files) || []) { const to = path.join(here, 'site/example-assets', f.from); fs.mkdirSync(path.dirname(to), { recursive: true }); fs.copyFileSync(path.join(here, 'example-assets/files', f.from), to); } /* only the files an example names */ // the free files of the example songs (example-assets/ATTRIBUTION.md)
if (fs.existsSync(path.join(here, 'audio'))) fs.cpSync(path.join(here, 'audio'), path.join(here, 'site/audio'), { recursive: true }); // the rendered sounds are sources too (audio/, see tools/render-samples.mjs)
console.log('built site/, STATUS.md, manifest.json');
{ // a page that shows a picture the site does not have would show a broken image: stop (0.1.18 shipped without five of them)
  const used = new Set(); for (const f of fs.readdirSync(path.join(here, 'content'), { recursive: true })) if (String(f).endsWith('.md')) for (const m of rd('content/' + f).matchAll(/\{\{shot ([\w-]+) \|/g)) used.add(m[1]);
  const missing = [...used].filter((s) => !fs.existsSync(path.join(here, 'site/shots', s + '.png')));
  if (missing.length) { console.error('MISSING PICTURES (a page shows them, site/shots does not have them): ' + missing.join(' ')); process.exit(1); }
  console.log('pictures used by pages: ' + used.size + ', all in site/shots');
}
// versioned addresses: every script and stylesheet of index.html gets ?v=<hash of its content>, so a browser never keeps an old file after an upload (the sounds carry the version of their song, see previews.js)
if (!statusOnly) {
  const sum = (p) => crypto.createHash('sha1').update(fs.readFileSync(path.join(here, 'site', p))).digest('hex').slice(0, 10);
  let page = fs.readFileSync(path.join(here, 'site/index.html'), 'utf8');
  page = page.replace(/(<(?:script[^>]*\ssrc|link[^>]*\shref)=")([^"?#:]+\.(?:js|css))(")/g, (m, pre, p, post) => (fs.existsSync(path.join(here, 'site', p)) ? pre + p + '?v=' + sum(p) + post : m));
  const extra = ['vendor/codemirror-ample.js'].filter((p) => fs.existsSync(path.join(here, 'site', p)));
  page = page.replace('</head>', '<script>window.ASSET_V=' + JSON.stringify(Object.fromEntries(extra.map((p) => [p, sum(p)]))) + '</script>\n</head>');
  fs.writeFileSync(path.join(here, 'site/index.html'), page);
}
// after every build: start the built page in a stand-in browser and stop on an error it would throw while booting (a let used before it is declared, an unknown name)
if (!statusOnly) { const { spawnSync } = await import('node:child_process'); const boot = spawnSync(process.execPath, [path.join(here, 'tools/boot-check.mjs')], { encoding: 'utf8' }); process.stdout.write(boot.stdout || ''); if (boot.status) { console.error('build stopped: the page would fail while it starts (see above)'); process.exit(1); } }
