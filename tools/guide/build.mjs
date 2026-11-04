// Builds the AMPLE guide (the learning help) from guide/ into docs/guide/, and checks it.
//   node tools/guide/build.mjs            build
//   node tools/guide/build.mjs --check    build in memory and report problems (broken links, missing screenshots, unknown terms); exit 1 on errors
// Sources: guide/pages/*.md (in file name order), guide/glossary.md (the dictionary), guide/img/*.png (screenshots made by guide/shots), guide/assets/*. @ample:guide

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { escapeHtml, inline, plainText, render, slugify, termMatcher } from './md.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..', '..');
const SRC = path.join(root, 'guide');
const OUT = path.join(root, 'docs', 'guide');

/** Reads guide/glossary.md: "## term", optional "aliases:" and "see:" lines, then the definition in Markdown. */
export function parseGlossary(text) {
  const terms = [];
  let current = null;
  for (const line of String(text).replace(/\r\n/g, '\n').split('\n')) {
    const h = /^##\s+(.+?)\s*$/.exec(line);
    if (h) { current = { term: h[1], id: slugify(h[1]), aliases: [], see: [], body: [] }; terms.push(current); continue; }
    if (!current) continue;
    const alias = /^aliases:\s*(.+)$/i.exec(line), see = /^see:\s*(.+)$/i.exec(line);
    if (alias) current.aliases = alias[1].split(',').map((x) => x.trim()).filter(Boolean);
    else if (see) current.see = see[1].split(',').map((x) => x.trim()).filter(Boolean);
    else current.body.push(line);
  }
  return terms.map((t) => ({ ...t, names: [t.term, ...t.aliases], definition: t.body.join('\n').trim(), short: plainText(t.body.join('\n').trim().split(/\n\s*\n/)[0] ?? '').slice(0, 240) }));
}

/** Reads a page: front matter (title, summary) and the Markdown body. */
export function parsePage(file, text) {
  const m = /^---\n([\s\S]*?)\n---\n?/.exec(String(text).replace(/\r\n/g, '\n'));
  const meta = {};
  if (m) for (const l of m[1].split('\n')) { const kv = /^(\w+):\s*(.*)$/.exec(l); if (kv) meta[kv[1]] = kv[2].trim(); }
  const body = m ? String(text).replace(/\r\n/g, '\n').slice(m[0].length) : String(text);
  const slug = path.basename(file, '.md').replace(/^\d+-/, '');
  return { file, slug, title: meta.title ?? '', summary: meta.summary ?? '', body };
}

export function buildGuide({ pages, terms, images, media = new Set() }) {
  const problems = [];
  const warn = (message, level = 'error') => problems.push({ level, message });
  const matcher = termMatcher(terms);
  const slugs = new Set(pages.map((p) => p.slug));
  const termIds = new Set(terms.map((t) => t.id));
  const usedIn = new Map(terms.map((t) => [t.id, []]));
  const usedImages = new Set(), usedMedia = new Set();
  const seenTerm = new Set();

  for (const t of terms) {
    if (seenTerm.has(t.id)) warn(`The term "${t.term}" is defined twice.`);
    seenTerm.add(t.id);
    if (!t.definition) warn(`The term "${t.term}" has no definition.`);
    for (const s of t.see) if (!terms.some((x) => x.term.toLowerCase() === s.toLowerCase() || x.id === slugify(s))) warn(`The term "${t.term}" says see: ${s}, which is not in the glossary.`);
  }

  const link = (target) => {
    if (/^(https?:|mailto:|#|\/)/.test(target)) return target;
    const [slug, hash] = target.split('#');
    if (slug === 'glossary') return `glossary.html${hash ? '#' + hash : ''}`;
    return `${slug}.html${hash ? '#' + hash : ''}`;
  };

  const rendered = pages.map((page) => {
    if (!page.title) warn(`${page.file}: the page has no title in its front matter.`);
    if (!page.summary) warn(`${page.file}: the page has no summary in its front matter.`, 'warning');
    const seen = new Set();
    const res = render(page.body, {
      matcher, seen, link,
      onTerm: (id) => usedIn.get(id).push(page),
      audio: (name, caption) => {
        usedMedia.add(name);
        if (!media.has(name)) warn(`${page.file}: the sound "${name}" has no guide/media/${name}.mp3.`);
        return `<figure class="sound"><audio controls preload="none" src="media/${name}.mp3"></audio>${caption ? `<figcaption>${inline(caption, { link })}</figcaption>` : ''}</figure>`;
      },
      shot: (name, caption) => {
        usedImages.add(name);
        if (!images.has(name)) warn(`${page.file}: the screenshot "${name}" has no guide/img/${name}.png (run npm run guide:shots).`);
        return `<figure><a href="img/${name}.png"><img src="img/${name}.png" alt="${escapeHtml(caption || name)}" loading="lazy"></a>${caption ? `<figcaption>${inline(caption, { link })}</figcaption>` : ''}</figure>`;
      },
    });
    for (const m of page.body.matchAll(/\]\(([^)\s]+)\)/g)) {
      const t = m[1];
      if (/^(https?:|mailto:|#|\/)/.test(t)) continue;
      const [slug, hash] = t.split('#');
      if (slug === 'glossary') { if (hash && !termIds.has(hash)) warn(`${page.file}: the link ${t} points at a term that is not in the glossary.`); continue; }
      if (!slugs.has(slug)) { warn(`${page.file}: the link (${t}) points at a page that does not exist.`); continue; }
    }
    return { page, ...res };
  });
  for (const r of rendered) {
    for (const m of r.page.body.matchAll(/\]\(([\w-]+)#([\w-]+)\)/g)) {
      const target = rendered.find((x) => x.page.slug === m[1]);
      if (target && !target.headings.some((h) => h.id === m[2])) warn(`${r.page.file}: the link (${m[1]}#${m[2]}) points at a heading that does not exist.`);
    }
  }
  for (const name of media) if (!usedMedia.has(name)) warn(`guide/media/${name}.mp3 is not used by any page.`, 'warning');
  for (const name of images) if (!usedImages.has(name)) warn(`guide/img/${name}.png is not used by any page.`, 'warning');
  for (const t of terms) if (!usedIn.get(t.id).length) warn(`The term "${t.term}" is not used in any page yet (it is still in the dictionary).`, 'warning');

  const nav = (current) => `<nav class="sidebar" id="guide-nav" aria-label="Guide"><a class="brand" href="index.html">AMPLE guide</a>${[
    ...pages.map((p) => `<a href="${p.slug}.html"${current === p.slug ? ' aria-current="page"' : ''}>${escapeHtml(p.title)}</a>`),
    `<a href="glossary.html"${current === 'glossary' ? ' aria-current="page"' : ''}>Dictionary</a>`,
  ].join('')}<a class="elsewhere" href="/help">Reference help</a></nav>`;
  const shell = (slug, title, body, extra = '') => `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${escapeHtml(title)} · AMPLE guide</title>
<link rel="stylesheet" href="guide.css"><script>document.documentElement.classList.add('js')</script></head><body>
<header class="topbar"><button class="menu-toggle" type="button" aria-expanded="false" aria-controls="guide-nav"><span aria-hidden="true">☰</span> Menu</button><a class="topbrand" href="index.html">AMPLE guide</a><span class="topcurrent">${escapeHtml(title)}</span></header>
${nav(slug)}<main><article>${body}</article>${extra}<footer>Built from the guide/ folder of the project. Words with a dotted underline are in the <a href="glossary.html">dictionary</a>.</footer></main>
<script type="application/json" id="terms">${JSON.stringify(Object.fromEntries(terms.map((t) => [t.id, { term: t.term, short: t.short }]))).replace(/</g, '\\u003c')}</script>
<script src="guide.js"></script></body></html>`;

  const files = new Map();
  rendered.forEach(({ page, html, headings }, index) => {
    const prev = pages[index - 1], next = pages[index + 1];
    const toc = headings.filter((h) => h.level === 2).length > 2 ? `<nav class="toc" aria-label="On this page"><b>On this page</b>${headings.filter((h) => h.level === 2).map((h) => `<a href="#${h.id}">${escapeHtml(h.text)}</a>`).join('')}</nav>` : '';
    const pager = `<div class="pager">${prev ? `<a href="${prev.slug}.html">← ${escapeHtml(prev.title)}</a>` : '<span></span>'}${next ? `<a href="${next.slug}.html">${escapeHtml(next.title)} →</a>` : `<a href="glossary.html">Dictionary →</a>`}</div>`;
    files.set(`${page.slug}.html`, shell(page.slug, page.title, `<p class="kicker">Page ${index + 1} of ${pages.length}</p>${html.replace(/^(<h1[\s\S]*?<\/h1>)/, `$1${page.summary ? `<p class="lead">${escapeHtml(page.summary)}</p>` : ''}${toc}`)}`, pager));
  });
  files.set('index.html', shell('index', 'Start here', `<h1>AMPLE guide</h1><p class="lead">A guide that teaches, with pictures. The <a href="/help">reference help</a> says exactly what every part does; this guide explains how to think about it.</p>
<ol class="contents">${pages.map((p) => `<li><a href="${p.slug}.html"><b>${escapeHtml(p.title)}</b><span>${escapeHtml(p.summary)}</span></a></li>`).join('')}</ol>
<p>Stuck on a word? Every dotted word is in the <a href="glossary.html">dictionary</a>, and the dictionary says on which page each word is used.</p>`));

  const letters = [...new Set(terms.map((t) => t.term[0].toUpperCase()))].sort();
  const sorted = [...terms].sort((a, b) => a.term.localeCompare(b.term));
  files.set('glossary.html', shell('glossary', 'Dictionary', `<h1>Dictionary</h1><p class="lead">The words of AMPLE and of music, in plain language.</p>
<nav class="letters" aria-label="Letters">${letters.map((l) => `<a href="#letter-${l}">${l}</a>`).join('')}</nav>
${letters.map((l) => `<h2 id="letter-${l}">${l}</h2>${sorted.filter((t) => t.term[0].toUpperCase() === l).map((t) => {
    const see = t.see.map((s) => terms.find((x) => x.term.toLowerCase() === s.toLowerCase() || x.id === slugify(s))).filter(Boolean);
    const pagesUsing = [...new Set(usedIn.get(t.id))];
    return `<section class="entry" id="${t.id}"><h3>${escapeHtml(t.term)}${t.aliases.length ? ` <small>also: ${t.aliases.map(escapeHtml).join(', ')}</small>` : ''}</h3>${render(t.definition, { link, skipTerm: t.id }).html}${see.length ? `<p class="see">See also: ${see.map((x) => `<a href="#${x.id}">${escapeHtml(x.term)}</a>`).join(', ')}</p>` : ''}${pagesUsing.length ? `<p class="seen">Used in: ${pagesUsing.map((p) => `<a href="${p.slug}.html">${escapeHtml(p.title)}</a>`).join(', ')}</p>` : ''}</section>`;
  }).join('')}`).join('')}`));
  return { files, problems, stats: { pages: pages.length, terms: terms.length, images: images.size } };
}

export function loadSources(srcDir = SRC) {
  const pagesDir = path.join(srcDir, 'pages');
  const pages = fs.existsSync(pagesDir) ? fs.readdirSync(pagesDir).filter((f) => f.endsWith('.md')).sort().map((f) => parsePage(f, fs.readFileSync(path.join(pagesDir, f), 'utf8'))) : [];
  const glossaryFile = path.join(srcDir, 'glossary.md');
  const terms = fs.existsSync(glossaryFile) ? parseGlossary(fs.readFileSync(glossaryFile, 'utf8')) : [];
  const imgDir = path.join(srcDir, 'img');
  const images = new Set(fs.existsSync(imgDir) ? fs.readdirSync(imgDir).filter((f) => f.endsWith('.png')).map((f) => f.replace(/\.png$/, '')) : []);
  const mediaDir = path.join(srcDir, 'media');
  const media = new Set(fs.existsSync(mediaDir) ? fs.readdirSync(mediaDir).filter((f) => f.endsWith('.mp3')).map((f) => f.replace(/[.]mp3$/, '')) : []);
  return { pages, terms, images, media };
}

function main() {
  const check = process.argv.includes('--check');
  const built = buildGuide(loadSources());
  for (const p of built.problems) console.log(`${p.level === 'error' ? 'ERROR' : 'warning'}: ${p.message}`);
  const errors = built.problems.filter((p) => p.level === 'error').length;
  if (check) {
    console.log(`guide check: ${built.stats.pages} pages, ${built.stats.terms} terms, ${built.stats.images} screenshots, ${errors} error(s), ${built.problems.length - errors} warning(s)`);
    process.exit(errors ? 1 : 0);
  }
  if (errors) { console.log('Fix the errors above first.'); process.exit(1); }
  fs.rmSync(OUT, { recursive: true, force: true });
  fs.mkdirSync(path.join(OUT, 'img'), { recursive: true });
  for (const [name, html] of built.files) fs.writeFileSync(path.join(OUT, name), html);
  for (const asset of ['guide.css', 'guide.js']) fs.copyFileSync(path.join(SRC, 'assets', asset), path.join(OUT, asset));
  const mediaOut = path.join(SRC, 'media');
  if (fs.existsSync(mediaOut)) { fs.mkdirSync(path.join(OUT, 'media'), { recursive: true }); for (const f of fs.readdirSync(mediaOut)) fs.copyFileSync(path.join(mediaOut, f), path.join(OUT, 'media', f)); }
  for (const img of built.stats.images ? fs.readdirSync(path.join(SRC, 'img')).filter((f) => f.endsWith('.png')) : []) fs.copyFileSync(path.join(SRC, 'img', img), path.join(OUT, 'img', img));
  console.log(`guide built: ${built.stats.pages} pages, ${built.stats.terms} terms, ${built.stats.images} screenshots -> docs/guide/`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
