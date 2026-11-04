import test from 'node:test';
import assert from 'node:assert/strict';
import { buildGuide, loadSources, parseGlossary, parsePage } from './build.mjs';
import { inline, render, termMatcher } from './md.mjs';

const terms = parseGlossary(`# Dictionary
intro text that is not a term

## bar
aliases: bars, measure
see: beat
A bar is a chunk of a song.

Second paragraph.

## beat
A beat is one pulse.
`);

test('the glossary is read: terms, aliases, see-also and a short first paragraph for the tooltip', () => {
  assert.deepEqual(terms.map((t) => t.id), ['bar', 'beat']);
  assert.deepEqual(terms[0].names, ['bar', 'bars', 'measure']);
  assert.deepEqual(terms[0].see, ['beat']);
  assert.equal(terms[0].short, 'A bar is a chunk of a song.');
});

test('a term becomes a link with a tooltip the first time it appears, also by alias, and never in code or headings', () => {
  const matcher = termMatcher(terms);
  const seen = new Set();
  const first = inline('A bar has a beat. Count the bars and `bar` in code.', { matcher, seen });
  assert.match(first, /<a class="term" href="glossary.html#bar" data-term="bar">bar<\/a> has a <a class="term" href="glossary.html#beat" data-term="beat">beat<\/a>/);
  assert.match(first, /Count the bars and <code>bar<\/code>/);   // the second use is plain, and so is the code
  assert.equal((first.match(/class="term"/g) || []).length, 2);
  const page = render('# The bar\n\nAnother measure here.', { matcher });
  assert.doesNotMatch(page.html.split('</h1>')[0], /class="term"/);   // not in the heading
  assert.match(page.html, /data-term="bar">measure<\/a>/);
  assert.equal(inline('The word barn is not bar', { matcher: termMatcher(terms), seen: new Set() }).match(/class="term"/g)?.length, 1);   // whole words only
});

test('markdown: lists, tables, callouts, code blocks, figures and links', () => {
  const html = render('## Title\n\n- one\n- **two**\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n> **Tip** be kind\n\n```\nx < y\n```\n\n![[shot:pic|A caption]]\n\nSee [the page](other#part).', { link: (t) => `${t.split('#')[0]}.html#${t.split('#')[1]}` }).html;
  assert.match(html, /<h2 id="title">Title/);
  assert.match(html, /<ul><li>one<\/li><li><strong>two<\/strong><\/li><\/ul>/);
  assert.match(html, /<table><thead><tr><th>a<\/th><th>b<\/th>/);
  assert.match(html, /<aside class="callout tip">/);
  assert.match(html, /x &lt; y/);
  assert.match(html, /<img src="img\/pic.png" alt="A caption">/);
  assert.match(html, /<a href="other.html#part">the page<\/a>/);
});

const page = (file, title, body) => parsePage(file, `---\ntitle: ${title}\nsummary: s\n---\n${body}`);

test('the check finds a missing screenshot, a broken link, an unknown see-also and a term that is defined twice', () => {
  const pages = [page('01-a.md', 'A', '# A\n\n![[shot:nope]]\n\nGo to [b](missing) and [t](glossary#ghost).')];
  const bad = parseGlossary('## x\nsee: y\nbody\n\n## x\nagain');
  const { problems } = buildGuide({ pages, terms: bad, images: new Set(['unused']) });
  const messages = problems.map((p) => p.message).join('\n');
  assert.match(messages, /screenshot "nope"/);
  assert.match(messages, /\(missing\) points at a page that does not exist/);
  assert.match(messages, /glossary#ghost/);
  assert.match(messages, /says see: y/);
  assert.match(messages, /defined twice/);
  assert.match(messages, /unused.png is not used/);
});

test('the dictionary says where a word is used, and the pages link each other', () => {
  const pages = [page('01-a.md', 'A', '# A\n\nA bar is here. Next: [b](b).'), page('02-b.md', 'B', '# B\n\nNothing.')];
  const { files, problems } = buildGuide({ pages, terms, images: new Set() });
  assert.equal(problems.filter((p) => p.level === 'error').length, 0);
  assert.match(files.get('glossary.html'), /Used in: <a href="a.html">A<\/a>/);
  assert.match(files.get('a.html'), /href="b.html">B →/);
  assert.match(files.get('index.html'), /href="a.html"/);
});

test('a sound is a figure with a player, and a missing sound is reported', () => {
  assert.match(render('![[audio:song|A caption]]', {}).html, /<audio controls src="media\/song.mp3">/);
  const pages = [page('01-a.md', 'A', '# A\n\n![[audio:nope|Hear it]]')];
  const { problems, files } = buildGuide({ pages, terms: [], images: new Set(), media: new Set(['unused']) });
  const messages = problems.map((x) => x.message).join('\n');
  assert.match(messages, /the sound "nope" has no guide\/media\/nope.mp3/);
  assert.match(messages, /unused.mp3 is not used/);
  assert.match(files.get('a.html'), /<figure class="sound"><audio controls preload="none" src="media\/nope.mp3"><\/audio><figcaption>Hear it<\/figcaption>/);
});

test('the real guide builds without errors: every screenshot exists, every link and every see-also is right', () => {
  const sources = loadSources();
  assert.ok(sources.pages.length >= 5);
  const { problems } = buildGuide(sources);
  assert.deepEqual(problems.filter((p) => p.level === 'error'), []);
});
