// Docs generator. Sources: src/ (site code), content/ (pages as Markdown+HTML), spec/templates of the repository (read only).
// Output: site/ (open site/index.html), STATUS.md (what exists / what changed), manifest.json (hashes of the last build).
// Usage: node build.mjs            build everything
//        node build.mjs --status   only print what is new / changed / missing (no writes)
import fs from 'node:fs'; import path from 'node:path'; import crypto from 'node:crypto';
const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
const rd = (p) => fs.readFileSync(path.join(here, p), 'utf8'), W = (p, t) => { fs.mkdirSync(path.dirname(path.join(here, p)), { recursive: true }); fs.writeFileSync(path.join(here, p), t); };
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

// 2. learn path from content/learn/<level>/NN-slug.md   (front matter: title, lead; body = HTML + {{pv {json}}} previews)
const fm = (t) => { const m = t.match(/^---\n([\s\S]*?)\n---\n?([\s\S]*)$/); const o = {}; (m ? m[1] : '').split('\n').forEach((l) => { const i = l.indexOf(':'); if (i > 0) o[l.slice(0, i).trim()] = l.slice(i + 1).trim(); }); return { ...o, body: (m ? m[2] : t).trim() }; };
import { render as mdRender } from './lib/md.mjs';
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
const vend = [path.join(root, 'web/vendor/codemirror-ample.js'), path.join(here, 'vendor/codemirror-ample.js')].find((p) => fs.existsSync(p)) ?? ''; // the studio/Lite highlighter, copied read-only and made a classic script (works from file://)
if (fs.existsSync(vend)) { let v = fs.readFileSync(vend, 'utf8'); const m = v.match(/export\{(\w+) as createAmpleEditor,(\w+) as createAmplePreview\};?\s*$/); if (!m) throw new Error('codemirror-ample.js: export pattern changed, adapt build.mjs'); man.vendor = hash(v); if (old.vendor && old.vendor !== man.vendor) console.log('highlighter bundle updated'); W('site/vendor/codemirror-ample.js', '(function(){' + v.replace(m[0], 'window.AmpleCM={createAmpleEditor:' + m[1] + ',createAmplePreview:' + m[2] + '};') + '})();'); } else if (old.vendor) man.vendor = old.vendor;
W('site/blocks-data.js', '// generated by build.mjs from spec/templates (read-only). Do not edit.\nwindow.BLOCKS = ' + JSON.stringify(blocks) + ';\n');
W('site/music-data.js', 'window.MUSIC = ' + JSON.stringify(music) + ';\n');
W('site/learn-data.js', '// generated by build.mjs from content/learn. Do not edit.\nconst pv = (title, src, fx, p, note) => `<div class="pv" data-pv=\'${JSON.stringify({ title, src, fx: fx || "dry", p: p || {}, note }).replace(/\'/g, "&#39;")}\'></div>`;\nwindow.LEARN = ' + JSON.stringify(learn) + ';\n');
W('manifest.json', JSON.stringify(man, null, 1));
const PVB = new Set(['reverb', 'delay', 'chorus', 'flanger', 'phaser', 'tremolo', 'distortion', 'bitcrusher', 'compressor', 'gate', 'duck', 'width', 'filter', 'eq', 'pan', 'volume', 'pitch', 'reverse', 'vocoder']);
W('STATUS.md', `# Docs status (generated by build.mjs)\n\n${summary}\n\n## Learn pages\n| file | previews |\n|---|---|\n${learn.flatMap((l) => l.pages).map((p) => `| content/learn/${p.file} | ${p.previews} |`).join('\n')}\n\n## Blocks (from spec)\n| block | category | class | since | sound preview in docs |\n|---|---|---|---|---|\n${blocks.map((b) => `| ${b.id} | ${b.category} | ${b.cls} ${b.version} | ${b.since} | ${PVB.has(b.id) ? 'yes' : 'no'} |`).join('\n')}\n`);
if (fs.existsSync(path.join(here, 'audio'))) fs.cpSync(path.join(here, 'audio'), path.join(here, 'site/audio'), { recursive: true }); // the rendered sounds are sources too (audio/, see tools/render-samples.mjs)
console.log('built site/, STATUS.md, manifest.json');
