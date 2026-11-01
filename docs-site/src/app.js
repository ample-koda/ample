/* ===== WIP_HIDDEN (temporary, 2026-11-01): the Playground, the Studio, Koda and the search are not public yet. Set to false to bring them all back (menus, pages, cards, Ctrl K, the Open in playground buttons). ===== */
const WIP_HIDDEN = true;
const run = (t) => WIP_HIDDEN ? '' : `<button class="run" title="Opens this example in the playground">▶ Open in playground</button>`;
const P = {};
const nav = [
  ['Learn', 'guide', [['first-song','Your first song'],]],
  ['Blocks', 'blocks', [['','All blocks'],['trim','trim','new'],['silence','silence','new'],['volume','volume'],['reverb','reverb'],['follow','follow']]],
];
nav.unshift(['Philosophy', 'philosophy', [['','Why AMPLE exists'],['reproducible','Reproducible by default'],['plan-first','The plan comes first'],['text-is-source','Text is the source'],['honest','Honest about the limits'],['versions','Songs outlive versions'],['assistant','An assistant, not the author'],['small-core','A small core']]]);
document.querySelector('nav.top').insertAdjacentHTML('afterbegin', '<a href="#/philosophy" data-s="philosophy">Philosophy</a>');
const quote = (t) => `<blockquote style="margin:18px 0;padding:6px 0 6px 18px;border-left:3px solid var(--acc);font-size:19px;color:var(--hi);max-width:720px">${t}</blockquote>`;
const pn = (a, b) => `<div class="pn">${a ? `<a href="#/philosophy${a[0]}"><small>Previous</small>← ${a[1]}</a>` : '<span></span>'}${b ? `<a href="#/philosophy${b[0]}"><small>Next</small>${b[1]} →</a>` : ''}</div><div class="foot"></div>`;
P['/philosophy'] = () => `<div class="crumbs">Philosophy</div><h1>Why AMPLE exists</h1>
<p class="lead">Music you made should never be music you lost. AMPLE is a language for songs, so that a song is something you can read, keep, change and build again.</p>
${quote('A song you like can always be returned to, even if you never pressed save.')}
<p>AMPLE started with two songs that could not be made again, because the number behind each one (its seed) had never been written down. Everything in AMPLE follows from that loss: what a song is made of is recorded before the sound is made, and the same text builds the same sound.</p>
<p>The name says the rest: <b>A</b>ssisted <b>M</b>usic <b>P</b>rogramming <b>L</b>anguage <b>E</b>xperience. A programming language, because text can be read, compared and kept. Assisted, because writing a language is not for everybody and a helper can make a lot of it easier. An experience, because it should feel like making music, not like filling in forms.</p>
<h2 id="p">The principles</h2>
<div class="cards">
<a class="card" href="#/philosophy/reproducible"><h3>Reproducible by default</h3><p>The same song builds the same sound. Anything else is a choice you make, with a reason.</p></a>
<a class="card" href="#/philosophy/plan-first"><h3>The plan comes first</h3><p>A take is never made before its plan is written down.</p></a>
<a class="card" href="#/philosophy/text-is-source"><h3>Text is the source</h3><p>Every click becomes a change of the text.</p></a>
<a class="card" href="#/philosophy/honest"><h3>Honest about the limits</h3><p>What cannot be made again is marked, not hidden.</p></a>
<a class="card" href="#/philosophy/versions"><h3>Songs outlive versions</h3><p>A new AMPLE never rewrites your song without you asking.</p></a>
<a class="card" href="#/philosophy/assistant"><h3>An assistant, not the author</h3><p>The assistant writes the same text you could write yourself.</p></a>
</div>
${pn(null, ['/reproducible','Reproducible by default'])}`;
P['/philosophy/reproducible'] = () => `<div class="crumbs">Philosophy</div><h1>Reproducible by default</h1>
<p class="lead">Build a song twice and you get the same sound. That is the starting point, not a feature you switch on.</p>
<h2 id="w">What it takes</h2>
<table><tr><th>Part</th><th>How AMPLE does it</th></tr>
<tr><td>Chance</td><td>AMPLE has its own random numbers, the same on every machine, with a master seed in the song. Every block gets its own seed, derived from its name.</td></tr>
<tr><td>Tools</td><td>Every tool that makes sound is recorded with its version, so a take says what made it.</td></tr>
<tr><td>Files</td><td>The recordings a song uses are recorded by what is in them, not by their name.</td></tr>
<tr><td>Changes</td><td>When a different result is wanted, you say so: <code>allow nondeterministic</code> or <code>refresh</code>, and the report shows it.</td></tr></table>
<div class="note"><b>Why not just save the audio?</b> Audio is big, and it is the result. The text and its seeds are the recipe. With the recipe you can change one thing and keep the rest.</div>
${pn(['','Why AMPLE exists'], ['/plan-first','The plan comes first'])}`;
P['/philosophy/plan-first'] = () => `<div class="crumbs">Philosophy</div><h1>The plan comes first</h1>
<p class="lead">A take is never created before its plan is written down.</p>
<p>Before the engine starts, the plan goes into the take bank: both seeds, the settings, the words and the tools. The take is stored next to it. So a song you like can always be built again, even if you never pressed Freeze, and a random seed is recorded too.</p>
<div class="note warn"><b>The cost.</b> This is a rule that makes every build do a little more work and keep a little more. AMPLE accepts that, because the other way round loses songs.</div>
${pn(['/reproducible','Reproducible by default'], ['/text-is-source','Text is the source'])}`;
P['/philosophy/text-is-source'] = () => `<div class="crumbs">Philosophy</div><h1>Text is the source</h1>
<p class="lead">The graph and the piano roll are views of one text file.</p>
<p>When you drag a block, move a note or turn a knob, AMPLE changes the text. When you change the text, the views follow. Nothing lives only in the interface, so a song is a file you can keep, compare and share, and the assistant and you work on the same thing.</p>
<pre><code><span class="k">composition</span> <span class="s">"porch song"</span> { <span class="n">ample</span> <span class="v">1</span>  <span class="n">tempo</span> <span class="v">96</span> }</code></pre>
${pn(['/plan-first','The plan comes first'], ['/honest','Honest about the limits'])}`;
P['/philosophy/honest'] = () => `<div class="crumbs">Philosophy</div><h1>Honest about the limits</h1>
<p class="lead">Not everything can be made again. AMPLE says which part cannot, instead of pretending.</p>
<p>Audio that comes from outside (a recording you import) has no plan: it is marked that way. A plan rebuilt afterwards from finished audio would be lossy, so AMPLE only captures plans at the time, and does not invent them later.</p>
<p>A release says whether it is <b>recipe-exact</b> (built again from the text) or only <b>data-exact</b> (kept as audio).</p>
${pn(['/text-is-source','Text is the source'], ['/versions','Songs outlive versions'])}`;
P['/philosophy/versions'] = () => `<div class="crumbs">Philosophy</div><h1>Songs outlive versions</h1>
<p class="lead">A newer AMPLE never rewrites your song behind your back.</p>
<p>Every song says which AMPLE it was written for (<code>ample 1</code>). A newer AMPLE builds an older song as it was, and only changes the text when you ask it to, with a preview of what changes. A block that changed keeps its old behaviour for old songs.</p>
${pn(['/honest','Honest about the limits'], ['/assistant','An assistant, not the author'])}`;
P['/philosophy/assistant'] = () => `<div class="crumbs">Philosophy</div><h1>An assistant, not the author</h1>
<p class="lead">The assistant writes the same text you could write, and says when the language cannot do what you ask.</p>
<p>The AI assistant proposes changes to the song text. You see them, you accept or refuse them. If a request needs something the language has no word for, the assistant says so and proposes what is possible, and does not fake it with something that only looks alike.</p>
${pn(['/versions','Songs outlive versions'], ['/small-core','A small core'])}`;
P['/philosophy/small-core'] = () => `<div class="crumbs">Philosophy</div><h1>A small core</h1>
<p class="lead">What not everybody needs is a module, off until you switch it on.</p>
<p>Quality scoring, credits, signing, a git mirror and lyrics timing are modules per song. The core stays small, so it stays understandable, and what is not switched on cannot surprise you.</p>
${pn(['/assistant','An assistant, not the author'], null)}`;
if (!WIP_HIDDEN) nav.splice(2, 0, ['Studio', 'studio', [['','The studio at a glance'],['block-mode','Block mode','new']]]);
if (!WIP_HIDDEN) nav.splice(3, 0, ['Koda', 'koda', [['','Meet Koda']]]);
if (!WIP_HIDDEN) document.querySelector('nav.top a[data-s=guide]').insertAdjacentHTML('afterend', '<a href="#/studio" data-s="studio">Studio</a><a href="#/koda" data-s="koda">Koda</a><a href="#/playground" data-s="playground">Playground ↗</a>');
const shot = (title, hint, h = 300) => `<figure style="margin:18px 0;max-width:860px"><div style="height:${h}px;border:1px solid var(--line);border-radius:12px;background:repeating-linear-gradient(135deg,var(--panel) 0 14px,var(--panel2) 14px 28px);display:grid;place-items:center;text-align:center;color:var(--dim)"><div><div style="font-size:30px">🖼</div><b style="color:var(--hi)">${title}</b><div style="font-size:13px;max-width:420px;margin:4px auto 0">${hint}</div></div></div><figcaption style="font-size:13px;color:var(--dim);margin-top:6px">${title}. Screenshot, made by the test suite so that it stays in step with the studio.</figcaption></figure>`;
const liveFrame = (code) => `<div style="margin:18px 0;max-width:860px;border:1px solid var(--line);border-radius:12px;overflow:hidden;background:var(--panel)"><div style="display:flex;align-items:center;gap:10px;padding:8px 12px;border-bottom:1px solid var(--line);font-size:13px;color:var(--dim)"><span style="color:var(--ok)">● live</span> Try it: change the text and press build<span style="flex:1"></span><a href="#/playground">Open in the playground ↗</a></div><div style="display:grid;grid-template-columns:1fr 1fr;min-height:190px"><pre style="margin:0;border:0;border-radius:0;border-right:1px solid var(--line);max-width:none"><code>${code}</code></pre><div style="padding:14px;color:var(--dim);font-size:13px"><div style="display:flex;gap:8px;margin-bottom:10px"><span class="btn p" style="padding:4px 12px">▶ Build</span><span class="btn" style="padding:4px 12px">Stop</span></div><div style="height:54px;border-radius:8px;background:linear-gradient(90deg,#19e3c833,#19e3c888,#19e3c833);display:grid;place-items:center">waveform of the build</div><div style="margin-top:10px">Embedded from <b>playground.ample-koda.dev</b> in a frame: no account, nothing leaves your browser.</div></div></div></div>`;
const live = WIP_HIDDEN ? (code) => '<pre><code>' + code + '</code></pre>' : liveFrame;
P['/studio'] = () => `<div class="crumbs">Studio</div><h1>The studio at a glance</h1><p class="lead">The studio shows one song in several ways at once: as text, as blocks, on a timeline and on a piano roll. They are always the same song.</p>
${shot('The studio: text and blocks side by side','Editor on the left, block graph on the right, play bar below.',340)}
<div class="cards"><a class="card" href="#/studio/block-mode"><h3>Block mode</h3><p>Connect blocks like cables. Every drag writes a line of text.</p></a></div>
<div class="foot"></div>`;
P['/studio/block-mode'] = () => `<div class="crumbs">Studio</div><h1>Block mode</h1><p class="lead">Every block is a box with ports. A line from an output to an input is a <code>from … -&gt; …</code> line in the text.</p>
${shot('Block mode: a song as blocks','A rain source, a road recording and a synth feeding the component scene, with the cars output in the region colour.',360)}
<h2 id="p">Port colours</h2><table><tr><th>Colour</th><th>Carries</th></tr><tr><td><span style="color:#19e3c8">● blue</span></td><td>Audio</td></tr><tr><td><span style="color:var(--warn)">● yellow</span></td><td>A region: a part of a sound you can tweak</td></tr><tr><td><span style="color:var(--control)">● teal</span></td><td>A control: a number over time</td></tr></table>
${shot('Dragging a line to a parameter','The parameter button shows control inputs when you drag a control line.',240)}
<div class="foot"></div>`;
P['/koda'] = () => `<div class="crumbs">Koda</div><h1>Meet Koda</h1><p class="lead">Koda is the helper of AMPLE: a small eighth note that lives in the studio, listens to what you do and answers in the same text you write.</p>
${shot('Koda in the corner of the studio','The note pet with a speech bubble: "Want a fade on the cars?"',260)}
<h2 id="a">What Koda does</h2><p>Ask in plain words (or in Dutch, or in English) and Koda proposes a change to the song text. You see it, you accept it or you refuse it.</p>
${live('<span class="c"># Ask Koda: "make the cars fade out at the end"</span>\n<span class="k">volume</span> carsFade {\n  <span class="n">gain</span> <span class="n">fadeout</span>(<span class="v">64</span>, <span class="v">8</span>)\n  <span class="n">from</span> scene.cars -&gt; audio\n}')}
<div class="note warn"><b>Koda says when it cannot.</b> If the language has no word for what you ask, Koda says so and proposes what is possible.</div>
<div class="foot"></div>`;
P['/playground'] = () => `<div class="crumbs">Playground</div><h1>Playground</h1><p class="lead">Write AMPLE, build it, hear it. No account, nothing to install.</p>
<div style="margin:18px 0;border:1px solid var(--line);border-radius:12px;overflow:hidden;max-width:980px"><div style="display:flex;gap:10px;align-items:center;padding:8px 12px;background:var(--panel);border-bottom:1px solid var(--line);font-size:13px;color:var(--dim)">🔒 playground.ample-koda.dev<span style="flex:1"></span><a href="#/playground">Open full screen ↗</a></div><div style="height:380px;display:grid;place-items:center;background:repeating-linear-gradient(135deg,var(--panel) 0 14px,var(--panel2) 14px 28px);color:var(--dim);text-align:center"><div><div style="font-size:34px">♪</div><b style="color:var(--hi)">The playground, in a frame</b><div style="font-size:13px;margin-top:4px">Editor, blocks, demos and a build button.</div></div></div></div>
<h2 id="d">Demos</h2><div class="cards"><a class="card" href="#/playground"><h3>Cars and music</h3><p>Four cars pass a street. The beat follows the level.</p></a><a class="card" href="#/playground"><h3>Control room</h3><p>Curves and fires steer three llamas.</p></a></div>
<div class="note"><b>Same text everywhere.</b> Every example in these docs has an "Open in playground" button that opens exactly that text.</div>
<div class="foot"></div>`;
const sec = (s) => s === 'guide' ? 'Learn' : s;

P['/'] = () => `
<div class="hero"><h1>Write music <em>as code.</em><br>Build it, hear it, change it, keep it.</h1>
<p class="lead">AMPLE is a language for songs that you can read, version and reproduce. These docs have a guide for learning, a reference for every word of the language and a page for every block.</p>
<div class="cta"><a class="btn p" href="#/guide/first-song">Start with your first song</a><a class="btn" href="#/blocks">Browse the blocks</a></div></div>
<div class="cards">
<a class="card" href="#/guide/first-song"><i>🎵</i><h3>Learn</h3><p>Learn music from zero, and learn to make it in AMPLE, with sounds you can hear.</p></a>
<a class="card" href="#/learn/ample/language/composition-line"><i>{ }</i><h3>Language</h3><p>The composition line, versions, expressions, controls and branches.</p></a>
<a class="card" href="#/blocks"><i>▦</i><h3>Blocks</h3><p>Every block: its ports, parameters, units and an example you can open.</p></a>
<a class="card" href="#/learn/ample/components/what-is-a-component"><i>⧉</i><h3>Components</h3><p>Build your own blocks from other blocks, with properties and region outputs.</p></a>
</div>
<h2>New in AMPLE</h2>
<table><tr><th>Word</th><th>What it does</th></tr>
<tr><td><a href="#/blocks/trim"><code>trim</code></a></td><td>Cuts the silence around the sound of a recording; fades by a share of the sound.</td></tr>
<tr><td><a href="#/learn/ample/components/if-else"><code>if (…) { } else { }</code></a></td><td>Picks a branch of a component from its properties.</td></tr>
<tr><td><a href="#/blocks/silence"><code>silence()</code></a></td><td>Blank audio for a branch that has no sound.</td></tr></table>
<div class="foot"><span>AMPLE docs</span><span>Language version: 1.0 (provisional)</span></div>`;

P['/guide/first-song'] = () => `
<div class="crumbs">Learn › Getting started</div><h1>Your first song</h1>
<p class="lead">A song is a text file. Blocks make or change sound, and lines say what feeds what.</p>
<h2 id="a">The composition line</h2>
<p>Every song starts with a <code>composition</code> line. It names the song and sets its tempo and its master seed, so the same text always builds the same sound.</p>
<pre><code><span class="k">composition</span> <span class="s">"porch song"</span> { <span class="n">ample</span> <span class="v">1</span>  <span class="n">tempo</span> <span class="v">96</span>  <span class="n">master-seed</span> <span class="v">60</span> }</code></pre>
<h2 id="b">A first block</h2>
<p>A block has a type, a name and a place in time: <code>at 1 for 8</code> is "from bar 1, for 8 bars".</p>
<pre>${run()}<code><span class="k">synth</span> pad <span class="n">at</span> <span class="v">1</span> <span class="n">for</span> <span class="v">8</span> {
  <span class="n">wave</span> triangle
  <span class="n">gain</span> <span class="v">-18 dB</span>
}</code></pre>
${WIP_HIDDEN ? '' : '<div class="note"><b>Tip.</b> Not sure what a word does? Press <code>Ctrl K</code> and type it: every block has its own page.</div>'}
<h2 id="c">Next</h2><p>Learn how the beat, the tempo and the bars work, then hear what an effect does.</p>
<div class="pn"><a href="#/learn/music/basic/how-to-use"><small>Previous</small>How to use this course</a><a href="#/learn/music/basic/pulse-and-tempo"><small>Next</small>Pulse and tempo →</a></div>
<div class="foot"></div>`;


const stub = (name, crumbs, text) => () => `<div class="crumbs">${crumbs}</div><h1>${name}</h1><p class="lead">${text}</p><div class="note">This page is still being written.</div>`;
if (WIP_HIDDEN) for (const k of Object.keys(P)) if (['studio', 'koda', 'playground'].includes(k.split('/')[1])) delete P[k];
for (const [t,s,items] of nav) for (const [slug,title] of items) { const k = '/'+s+(slug?'/'+slug:''); if (!P[k]) P[k] = stub(title,t,'This page is still being written.'); }

const side = document.getElementById('side'), main = document.getElementById('main'), toc = document.getElementById('toc');
function render() {
  let path = location.hash.replace(/^#/, '') || '/';
  if (!P[path]) path = '/';
  main.innerHTML = P[path]();
  main.querySelectorAll('.run').forEach(b => b.onclick = () => alert('In the real site this opens the example in the playground.'));
  const s = path.split('/')[1] || '';
  document.querySelectorAll('nav.top a').forEach(a => a.classList.toggle('on', a.dataset.s === s));
  const cur = nav.find(n => n[1] === s);
  side.innerHTML = cur ? `<h4>${cur[0]}</h4>` + cur[2].map(([slug,title,tag]) => { const k='/'+s+(slug?'/'+slug:''); return `<a href="#${k}" class="${k===path?'on':''}"><span>${/blocks/.test(s)&&slug?'<code>'+title+'</code>':title}</span>${tag?'<span class="tag new">new</span>':''}</a>`; }).join('') : nav.map(n => `<h4>${n[0]}</h4>` + n[2].slice(0,3).map(([slug,title]) => `<a href="#/${n[1]}${slug?'/'+slug:''}">${title}</a>`).join('')).join('');
  const hs = [...main.querySelectorAll('h2')];
  toc.innerHTML = hs.length ? '<b>On this page</b>' + hs.map(h => `<a href="#${path}" onclick="document.getElementById('${h.id}')?.scrollIntoView({behavior:'smooth'});return false">${h.textContent}</a>`).join('') : '';
  window.scrollTo(0, 0);
}
window.addEventListener('hashchange', () => { render(); onPage(); });
render();

/* ---------- alive mode ---------- */
const $ = (id) => document.getElementById(id);
const bg = $('bg'), g = bg.getContext('2d');
var notes = [], level = 0;
function sizeBg() { bg.width = innerWidth; bg.height = innerHeight; notes = Array.from({length: 26}, () => ({x: Math.random() * bg.width, y: Math.random() * bg.height, s: 10 + Math.random() * 22, v: .15 + Math.random() * .5, p: Math.random() * 6.28, c: ['#19e3c8','#ff4fd8','#2de2ff','#ffd166'][Math.floor(Math.random() * 4)]})); }
addEventListener('resize', sizeBg); sizeBg();
var frame = 0;
/* ONE beat clock for the whole page. Without music it runs at an idle tempo; with music it is locked to the audio clock of the music. */
var idleBpm = 96, idleT0 = performance.now() / 1000;
const clock = () => soundOn && ctx && musicT0 !== null ? {t: ctx.currentTime - musicT0, bpm: musicBpm} : {t: performance.now() / 1000 - idleT0, bpm: idleBpm};
const beatInfo = () => { const c = clock(), len = 60 / c.bpm, n = c.t / len; return {n: Math.floor(n), phase: n - Math.floor(n), len, t: c.t}; };
function drive() {
  requestAnimationFrame(drive);
  const on = document.body.classList.contains('alive');
  const b = beatInfo(), down = b.n % 4 === 0;
  const pulse = on ? Math.pow(1 - b.phase, 3) * (down ? 1 : .7) : 0;
  const root = document.documentElement.style;
  root.setProperty('--beat', pulse.toFixed(3));
  root.setProperty('--amt', soundOn ? '1' : '.35');
  if (on) $('pulse').style.transform = 'scaleX(' + (((b.n % 4) + b.phase) / 4).toFixed(3) + ')';
}
drive();
const nextBeatDelay = () => { const b = beatInfo(); return Math.max(0, (1 - b.phase) * b.len * 1000); };
// interactions land on the beat: a ring at the pointer on the next beat, and a short note when the sound is on
function onBeat(fn) { setTimeout(fn, nextBeatDelay()); }
function ring(x, y) { const r = document.createElement('div'); r.className = 'ring'; r.style.left = x + 'px'; r.style.top = y + 'px'; document.body.appendChild(r); setTimeout(() => r.remove(), 800); }
function blip(k) { if (!soundOn || !ctx) return; const t = themes[pageKey()], f = midi(t.root + 12 + t.chords[0][k % 3] + (k > 2 ? 12 : 0)); const o = ctx.createOscillator(), a = ctx.createGain(); o.type = 'sine'; o.frequency.value = f; a.gain.setValueAtTime(.0001, ctx.currentTime); a.gain.linearRampToValueAtTime(.08, ctx.currentTime + .01); a.gain.exponentialRampToValueAtTime(.0001, ctx.currentTime + .35); o.connect(a); a.connect(master); o.start(); o.stop(ctx.currentTime + .4); }
document.addEventListener('pointerdown', (e) => { if (!document.body.classList.contains('alive')) return; const x = e.clientX, y = e.clientY; ring(x, y); onBeat(() => { ring(x, y); blip(Math.floor(x / 90)); }); });
var lastHover = null;
document.addEventListener('pointerover', (e) => { if (!document.body.classList.contains('alive')) return; const el = e.target.closest('a,.btn,.tg,.card,button'); if (!el || el === lastHover) return; lastHover = el; const r = el.getBoundingClientRect(); onBeat(() => { if (el.matches(':hover')) { el.classList.remove('hit'); void el.offsetWidth; el.classList.add('hit'); blip(Math.floor(r.left / 70)); } }); });
document.addEventListener('keydown', (e) => { if (document.body.classList.contains('alive') && e.key.length === 1) onBeat(() => blip(e.key.charCodeAt(0))); });
function drawBg() {
  requestAnimationFrame(drawBg);
  if (!document.body.classList.contains('alive')) return;
  frame++; g.clearRect(0, 0, bg.width, bg.height);
  for (const n of notes) {
    n.y -= n.v * (1 + level * 5); n.x += Math.sin(frame / 90 + n.p) * .4;
    if (n.y < -40) { n.y = bg.height + 30; n.x = Math.random() * bg.width; }
    g.globalAlpha = .13 + level * .3; g.fillStyle = n.c; g.font = n.s * (1 + level * .6) + 'px system-ui'; g.fillText('♪', n.x, n.y);
  }
  g.globalAlpha = .1 + level * .25; g.strokeStyle = '#19e3c8'; g.lineWidth = 2; g.beginPath();
  for (let x = 0; x <= bg.width; x += 8) { const y = bg.height - 40 + Math.sin(x / 70 + frame / 30) * (6 + level * 40); x ? g.lineTo(x, y) : g.moveTo(x, y); }
  g.stroke();
}
drawBg();
var hints = {philosophy: 'A song you like can always be returned to.', guide: WIP_HIDDEN ? 'Every block has its own page.' : 'Press Ctrl K to jump to any block.', studio: 'Every drag writes a line of text.', koda: 'Hi! I am the one on the screenshots.', language: 'Try the example: change a number and build.', blocks: 'Every block has an example you can open.', components: 'A component is a block you built yourself.', playground: 'No account. Nothing leaves your browser.', '': 'Write music as code. Turn on Vibe, then Sound: the page moves to the beat.'};
function onPage() {
  const s = (location.hash.replace(/^#/, '').split('/')[1]) || '';
  const b = $('kb'); b.textContent = hints[s] || 'Hello!'; b.style.animation = 'none'; void b.offsetWidth; b.style.animation = '';
  if (soundOn) startMusic();
}
$('t-alive').onclick = () => { const on = document.body.classList.toggle('alive'); $('t-alive').classList.toggle('on', on); onPage(); render(); };

/* ---------- music: every page has its own source ---------- */
var themes = {
  '': {name: 'Welcome', bpm: 92, wave: 'triangle', root: 57, chords: [[0,4,7],[5,9,12],[7,11,14],[0,4,7]], arp: [0,1,2,1]},
  philosophy: {name: 'Always', bpm: 66, wave: 'sine', root: 50, chords: [[0,3,7],[8,12,15],[5,8,12],[7,10,14]], arp: [0,2,1,2]},
  guide: {name: 'First steps', bpm: 100, wave: 'triangle', root: 60, chords: [[0,4,7],[7,11,14],[9,12,16],[5,9,12]], arp: [0,1,2,1]},
  studio: {name: 'Blocks and cables', bpm: 112, wave: 'square', root: 55, chords: [[0,3,7],[3,7,10],[5,8,12],[7,10,14]], arp: [0,1,2,0]},
  koda: {name: 'Little note', bpm: 120, wave: 'sine', root: 64, chords: [[0,4,7],[2,5,9],[4,7,11],[0,4,7]], arp: [2,1,0,1]},
  language: {name: 'Syntax in D', bpm: 84, wave: 'triangle', root: 62, chords: [[0,4,7],[0,3,7],[5,9,12],[7,11,14]], arp: [0,2,1,2]},
  blocks: {name: 'Signal path', bpm: 104, wave: 'sawtooth', root: 52, chords: [[0,7,12],[5,12,17],[3,10,15],[7,14,19]], arp: [0,1,2,1]},
  components: {name: 'Built from parts', bpm: 96, wave: 'triangle', root: 59, chords: [[0,4,7],[4,7,11],[2,5,9],[7,11,14]], arp: [0,1,2,1]},
  playground: {name: 'Play', bpm: 124, wave: 'square', root: 57, chords: [[0,4,7],[5,9,12],[7,11,14],[9,12,16]], arp: [0,2,1,2]},
};
const pageKey = () => { const s = location.hash.replace(/^#/, '').split('/')[1] || ''; return themes[s] ? s : ''; };
const midi = (n) => 440 * Math.pow(2, (n - 69) / 12);
const noteName = (n) => ['C','C#','D','D#','E','F','F#','G','G#','A','A#','B'][n % 12];
function sourceOf(t) {
  const NL = String.fromCharCode(10);
  const steps = t.chords.map((c, i) => '  ' + (1 + i * 2) + ':0:2:' + (t.root + c[0]) + ':50 ' + (1 + i * 2) + ':0:2:' + (t.root + c[1]) + ':44 ' + (1 + i * 2) + ':0:2:' + (t.root + c[2]) + ':44').join(NL);
  return ['composition "' + t.name + '" { ample 1  tempo ' + t.bpm + '  master-seed 7 }', '', 'midi-clip chords at 1 for 8 {', '  bars 8', '  notes', steps, '}', '', 'synth voice at 1 for 8 {', '  wave ' + t.wave, '  attack 120 ms', '  release 900 ms', '  gain -16 dB', '  from chords.notes -> notes', '}', '', 'master master {', '  from voice.audio -> audio', '}'].join(NL);
}
var ctx, master, analyser, timer = null, soundOn = false, step = 0, musicT0 = null, musicBpm = 96;
var layer = null, layers = [], pending = null, pendingTimer = null;
/* A page of the same part of the site keeps its music. Another part hooks in: its music starts on the next bar line of the music that plays,
   the old music fades out over that bar and the new music fades in, and the beat clock of the page changes tempo at that very moment. */
function startMusic() {
  const key = pageKey(), t = themes[key];
  if (layer && layer.key === key) return;
  const now = ctx.currentTime;
  if (pending) { clearInterval(pending.timer); pending.gain.disconnect(); layers = layers.filter((x) => x !== pending); clearTimeout(pendingTimer); pending = null; if (layer) { layer.endAt = Infinity; layer.killAt = Infinity; layer.gain.gain.cancelScheduledValues(now); layer.gain.gain.setValueAtTime(1, now); } }
  let startAt = now + .05, fade = .6;
  if (layer) {
    const barOld = 4 * 60 / layer.bpm, el = now - layer.T0;
    startAt = layer.T0 + Math.ceil((el + .08) / barOld) * barOld;
    fade = 4 * 60 / t.bpm;
    layer.endAt = startAt;
    layer.gain.gain.setValueAtTime(1, startAt);
    layer.gain.gain.linearRampToValueAtTime(0, startAt + fade);
    layer.killAt = startAt + fade + .1;
  }
  const L = {key, bpm: t.bpm, T0: startAt, endAt: Infinity, killAt: Infinity, step: 0, next: 0, gain: ctx.createGain()};
  L.gain.connect(master);
  L.gain.gain.setValueAtTime(layer ? 0 : 1, now);
  if (layer) { L.gain.gain.setValueAtTime(0, startAt); L.gain.gain.linearRampToValueAtTime(1, startAt + fade); }
  layers.push(L);
  const beat = 60 / t.bpm / 2;
  const play = (when) => {
    const chord = t.chords[Math.floor(L.step / 8) % t.chords.length], i = L.step % 8;
    const tone = (n, dur, vol, type) => { const o = ctx.createOscillator(), g = ctx.createGain(); o.type = type; o.frequency.value = midi(n); g.gain.setValueAtTime(0, when); g.gain.linearRampToValueAtTime(vol, when + .02); g.gain.exponentialRampToValueAtTime(.0001, when + dur); o.connect(g); g.connect(L.gain); o.start(when); o.stop(when + dur + .05); };
    if (i === 0) chord.forEach((c) => tone(t.root - 12 + c, beat * 8, .05, 'sine'));
    tone(t.root + 12 + chord[t.arp[i % 4]], beat * 1.8, .06, t.wave);
    if (i % 4 === 0) tone(t.root - 24 + chord[0], beat * 1.5, .09, 'sine');
    L.step++;
  };
  L.timer = setInterval(() => {
    while (ctx.currentTime + .12 >= L.T0 + L.next && L.T0 + L.next < L.endAt) { play(L.T0 + L.next); L.next += beat; }
    if (ctx.currentTime > L.killAt) { clearInterval(L.timer); L.gain.disconnect(); layers = layers.filter((x) => x !== L); }
  }, 25);
  // the page follows the new music at the moment it takes over: beat clock, title and source
  const take = () => { pending = null; layer = L; musicBpm = t.bpm; musicT0 = L.T0; $('mt').textContent = t.name; $('mm').textContent = '· ' + t.bpm + ' bpm · ' + t.wave + ' · ' + noteName(t.root); $('mcode').textContent = sourceOf(t); };
  if (!layer) take(); else { $('mm').textContent = '· next: ' + t.name + ' on the next bar'; pending = L; pendingTimer = setTimeout(take, Math.max(0, (startAt - now) * 1000)); }
}
function stopMusic() { for (const L of layers) { clearInterval(L.timer); try { L.gain.gain.cancelScheduledValues(0); L.gain.gain.setValueAtTime(L.gain.gain.value, ctx.currentTime); L.gain.gain.linearRampToValueAtTime(0, ctx.currentTime + .25); setTimeout(() => L.gain.disconnect(), 400); } catch {} } layers = []; layer = null; pending = null; clearTimeout(pendingTimer); musicT0 = null; }
$('t-sound').onclick = () => {
  soundOn = !soundOn; document.body.classList.toggle('sound', soundOn); $('t-sound').classList.toggle('on', soundOn);
  if (soundOn) { ctx ||= new (window.AudioContext || window.webkitAudioContext)(); ctx.resume(); if (!master) { master = ctx.createGain(); master.gain.value = .8; analyser = ctx.createAnalyser(); analyser.fftSize = 128; master.connect(analyser); analyser.connect(ctx.destination); } startMusic(); }
  else { stopMusic(); level = 0; }
};
$('msrc').onclick = (e) => { e.preventDefault(); $('mus').classList.toggle('open'); };
$('mpg').onclick = (e) => { e.preventDefault(); alert('In the real site this opens the source of the music of this page in playground.ample-koda.dev.'); };
const vu = $('vu').getContext('2d');
(function meter() {
  requestAnimationFrame(meter);
  if (!soundOn || !analyser) return;
  const d = new Uint8Array(analyser.frequencyBinCount); analyser.getByteFrequencyData(d);
  level = d.reduce((a, b) => a + b, 0) / d.length / 255 * 2.2;
  vu.clearRect(0, 0, 180, 52); vu.fillStyle = '#19e3c8';
  for (let i = 0; i < 24; i++) { const hh = d[i] / 255 * 50; vu.fillRect(i * 7.5, 52 - hh, 5, hh); }
})();
onPage();
document.getElementById('theme').onclick = () => { const r = document.documentElement; r.dataset.theme = r.dataset.theme === 'dark' ? 'light' : 'dark'; };
const pal = document.getElementById('pal'), q = document.getElementById('q'), res = document.getElementById('res');
const all = Object.keys(P).filter(k => k !== '/');
function list() { const w = q.value.toLowerCase(); res.innerHTML = all.filter(k => !w || k.toLowerCase().includes(w)).slice(0, 9).map((k,i) => `<a class="${i?'':'f'}" href="#${k}"><span>${k.split('/').pop() || k}</span><small>${k.split('/')[1]}</small></a>`).join(''); }
const open = () => { pal.classList.add('on'); q.value = ''; list(); q.focus(); };
document.getElementById('open-search').onclick = open;
document.addEventListener('keydown', e => { if (!WIP_HIDDEN && (e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); open(); } if (e.key === 'Escape') pal.classList.remove('on'); });
pal.onclick = e => { if (e.target === pal || e.target.closest('a')) pal.classList.remove('on'); };
q.oninput = list;
q.onkeydown = e => { if (e.key === 'Enter') { const a = res.querySelector('a'); if (a) { location.hash = a.getAttribute('href'); pal.classList.remove('on'); } } };
