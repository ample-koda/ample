/* Layer on top of app.js: learning path, generated block pages, real search, navigation and accessibility.
   Uses the globals of app.js (nav, P, render, side, main, toc, pal, q, res). */
(function () {
  const AMPLE_VERSION = 'ample 1.0'; // the spec's own value (spec version 1.0, status provisional; every block since 1.0)
  const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const toast = (t) => { let e = document.getElementById('toast'); if (!e) { e = document.createElement('div'); e.id = 'toast'; e.setAttribute('role', 'status'); document.body.appendChild(e); } e.textContent = t; e.classList.add('on'); clearTimeout(e._t); e._t = setTimeout(() => e.classList.remove('on'), 2600); };

  /* ---------- Learn: two tracks. "music" = music itself (needs no AMPLE), "ample" = making it in AMPLE ---------- */
  const TR = [['music', 'Music', 'Music itself: sound, rhythm, notes, chords, mixing and ear training. You do not need AMPLE for this track, but boxes show how to try each idea in AMPLE.', MUSIC, ['🌱', '🎛', '🧪', '🎓']], ['ample', 'AMPLE', 'Making it in AMPLE: bars, blocks, effects and songs.', LEARN, ['🎵', '🎚', '🧩', '🚀']]];
  const learnNav = [['', 'Overview']];
  TR.forEach(([id, name, , data]) => data.forEach((l) => { learnNav.push(['--', name + ' · ' + l.title]); l.pages.forEach((p) => learnNav.push([id + '/' + l.id + '/' + p.slug, p.title])); }));
  nav[nav.findIndex((n) => n[1] === 'guide')] = ['Learn', 'learn', learnNav];
  const topLearn = document.querySelector('nav.top a[data-s=guide]'); topLearn.href = '#/learn'; topLearn.dataset.s = 'learn';
  document.querySelectorAll('a[href="#/guide/first-song"]').forEach((a) => (a.href = '#/learn/music/basic/how-to-use'));
  themes.learn = themes.guide; hints.learn = 'Music first. AMPLE is one way to try it.';
  P['/learn'] = () => '<div class="crumbs">Learn</div><h1>Learn</h1><p class="lead">Learn music from zero, and learn to make it in AMPLE. Pages with a play button let you hear what is explained.</p>' + TR.map(([id, name, desc, data, ico]) => '<h2 id="t-' + id + '">' + name + '</h2><p>' + desc + '</p><div class="cards">' + data.map((l, i) => '<a class="card" href="#/learn/' + id + '/' + l.id + '/' + l.pages[0].slug + '"><i>' + ico[i % 4] + '</i><h3>' + l.title + '</h3><p>' + l.blurb + '</p><p style="margin-top:8px">' + l.pages.length + ' pages</p></a>').join('') + '</div>').join('');
  TR.forEach(([id, name, , data]) => data.forEach((l) => l.pages.forEach((p) => (P['/learn/' + id + '/' + l.id + '/' + p.slug] = () => '<div class="crumbs">Learn › ' + name + ' › ' + l.title + '</div><h1>' + p.title + '</h1><p class="lead">' + p.lead + '</p>' + p.body))));
  /* ---------- generated block pages (blocks-data.js comes from spec/templates) ---------- */
  const PV = { reverb: 'pluck', delay: 'pluck', chorus: 'pad', flanger: 'pad', phaser: 'pad', tremolo: 'pad', distortion: 'bass', bitcrusher: 'arp', compressor: 'drums', gate: 'pad', duck: 'pad', width: 'pad', filter: 'pad', eq: 'pluck', pan: 'pluck', volume: 'pad', pitch: 'pluck', reverse: 'pluck', vocoder: 'pad' };
  const ports = (a, title) => `<div class="portbox"><h3>${title}</h3>${a.length ? '<ul class="ports">' + a.map((p) => `<li class="t-${p.t.replace(/[^a-z]/g, '')}${p.t === 'render' ? ' tech' : ''}"><i></i><code>${p.n}</code><span>${p.t}</span>${p.r ? '<em>required</em>' : ''}</li>`).join('') + '</ul>' : '<p class="none">none</p>'}</div>`;
  const cats = [...new Set(BLOCKS.map((b) => b.category))].sort();
  const range = (p) => (p.min != null || p.max != null ? `<span class="rg">${p.min ?? ''} … ${p.max ?? ''}${p.u ? ' ' + esc(p.u) : ''}</span>` : p.o ? '<span class="chips">' + p.o.map((o) => '<span>' + esc(o) + '</span>').join('') + '</span>' : p.u ? '<span class="rg">' + esc(p.u) + '</span>' : '');
  const blockPage = (b) => {
    const same = BLOCKS.filter((x) => x.category === b.category && x.id !== b.id).slice(0, 4);
    const ps = b.params.filter((p) => p.n !== 'enabled');
    return `<div class="crumbs">Blocks › ${b.category}</div><h1><code style="font-size:inherit">${b.id}</code></h1>
<div class="sig"><span class="badge ${b.cls === 'stable' ? 'ok' : ''}">${b.cls} ${b.version}</span><span class="badge">since ${AMPLE_VERSION}</span><span class="badge">${b.status}</span></div>
<p class="lead">${esc(b.description)}</p>
${PV[b.id] ? pv(b.title + ' on a sound', PV[b.id], b.id, {}, 'Rough browser preview of ' + b.title + '. The real block sounds richer.') : ''}
${b.ex ? `<h2 id="x">Example song</h2><p>A complete song that uses this block. Copy it into AMPLE as it is: it carries its own seed, so it sounds the same every time.</p><div class="pv-src" data-code="${encodeURIComponent(b.ex)}" data-note="${b.needs.length ? 'Needs: ' + esc(b.needs.join(' ')) : 'No extra files needed'}"></div>` : b.needs.length ? `<div class="note"><b>Needs</b> ${esc(b.needs.join(' '))}</div>` : ''}
<h2 id="i">Inputs and outputs</h2><div class="portgrid">${ports(b.inputs, 'Inputs')}${ports(b.outputs, 'Outputs')}</div>
${ps.length ? `<h2 id="p">Parameters</h2><table class="paramtable"><tr><th>Name</th><th>Default</th><th>Range</th><th>What it does</th></tr>${ps.map((p) => `<tr><td><code>${p.n}</code>${p.a ? ' <span class="tag new">auto</span>' : ''}</td><td>${esc(p.d)}</td><td>${range(p)}</td><td>${esc(p.doc)}</td></tr>`).join('')}</table>` : ''}
${same.length ? `<div class="note"><b>See also</b> ${same.map((x) => `<a href="#/blocks/${x.id}"><code>${x.id}</code></a>`).join(' · ')}</div>` : ''}
<div class="foot"><span>Generated from spec/templates/${b.id}.json</span></div>`;
  };
  const byId = Object.fromEntries(BLOCKS.map((b) => [b.id, b]));
  BLOCKS.forEach((b) => { P['/blocks/' + b.id] = () => blockPage(b); }); /* every block page is generated from the spec (the old hand-written trim and silence pages are no longer used) */
  P['/blocks'] = () => `<div class="crumbs">Blocks</div><h1>All blocks</h1><p class="lead">${BLOCKS.length} blocks, each with ports, parameters with units and (for many) a sound you can play.</p>${cats.map((c) => `<h2 id="c-${c.replace(/\W/g, '')}">${c}</h2><table><tr><th>Block</th><th>What it does</th><th>Since</th></tr>${BLOCKS.filter((b) => b.category === c).map((b) => `<tr><td><a href="#/blocks/${b.id}"><code>${b.id}</code></a></td><td>${esc(b.description).slice(0, 140)}</td><td>${AMPLE_VERSION}</td></tr>`).join('')}</table>`).join('')}`;
  const blocksNav = [['', 'All blocks']]; cats.forEach((c) => { blocksNav.push(['--', c]); BLOCKS.filter((b) => b.category === c).forEach((b) => blocksNav.push([b.id, b.id, ''])); });
  nav[nav.findIndex((n) => n[1] === 'blocks')] = ['Blocks', 'blocks', blocksNav];
  P['/404'] = () => `<h1>Page not found</h1><p class="lead">There is no such page (yet). ${WIP_HIDDEN ? '' : ' Try the search with <code>Ctrl K</code>.'}</p><a class="btn p" href="#/">Home</a>`;

  /* ---------- render: sidebar groups, aria, prev/next, scroll-spy toc, previews ---------- */
  let spy = null;
  render = function () {
    let path = location.hash.replace(/^#/, '') || '/'; if (!P[path]) path = '/404';
    main.innerHTML = P[path]();
    main.querySelectorAll('.run').forEach((b) => (b.onclick = () => toast('Later this opens the example in the playground.')));
    const s = path.split('/')[1] || '';
    document.querySelectorAll('nav.top a').forEach((a) => { const on = a.dataset.s === s; a.classList.toggle('on', on); on ? a.setAttribute('aria-current', 'page') : a.removeAttribute('aria-current'); });
    const cur = nav.find((n) => n[1] === s);
    const link = (n, slug, title, tag) => { const k = '/' + n[1] + (slug ? '/' + slug : ''); return `<a href="#${k}" ${k === path ? 'class="on" aria-current="page"' : ''}><span>${n[1] === 'blocks' && slug ? '<code>' + title + '</code>' : title}</span>${tag ? '<span class="tag new">new</span>' : ''}</a>`; };
    side.innerHTML = cur ? `<h4>${cur[0]}</h4>` + cur[2].map(([slug, title, tag]) => slug === '--' ? `<h4>${title}</h4>` : link(cur, slug, title, tag)).join('') : nav.map((n) => `<h4>${n[0]}</h4>` + n[2].filter((x) => x[0] !== '--').slice(0, 3).map(([slug, title]) => link(n, slug, title)).join('')).join('');
    if (cur && !main.querySelector('.pn') && !['404'].includes(s)) { // generic previous / next within the section
      const items = cur[2].filter((x) => x[0] !== '--'), i = items.findIndex(([slug]) => '/' + s + (slug ? '/' + slug : '') === path), href = (x) => '#/' + s + (x[0] ? '/' + x[0] : '');
      if (i >= 0 && items.length > 1) main.insertAdjacentHTML('beforeend', `<nav class="pn" aria-label="Previous and next page">${items[i - 1] ? `<a href="${href(items[i - 1])}"><small>Previous</small>← ${items[i - 1][1]}</a>` : '<span></span>'}${items[i + 1] ? `<a href="${href(items[i + 1])}"><small>Next</small>${items[i + 1][1]} →</a>` : ''}</nav>`);
    }
    const hs = [...main.querySelectorAll('h2[id]')];
    toc.innerHTML = hs.length ? '<b>On this page</b>' + hs.map((h) => `<a href="#${path}" data-h="${h.id}">${h.textContent}</a>`).join('') : '';
    toc.querySelectorAll('a').forEach((a) => (a.onclick = (e) => { e.preventDefault(); const h = main.querySelector('#' + a.dataset.h); if (!h) return; h.scrollIntoView({ behavior: matchMedia('(prefers-reduced-motion:reduce)').matches ? 'auto' : 'smooth', block: 'start' }); toc.querySelectorAll('a').forEach((x) => x.classList.toggle('on', x === a)); toc._lock = Date.now(); h.classList.remove('flash'); void h.offsetWidth; h.classList.add('flash'); }));
    spy?.disconnect(); spy = new IntersectionObserver((es) => es.forEach((e) => { if (e.isIntersecting && Date.now() - (toc._lock || 0) > 1300) toc.querySelectorAll('a').forEach((a) => a.classList.toggle('on', a.dataset.h === e.target.id)); }), { rootMargin: '-80px 0px -70% 0px' }); hs.forEach((h) => spy.observe(h));
    document.title = (main.querySelector('h1')?.textContent || 'Docs') + ' · AMPLE docs';
    document.body.classList.remove('menu'); window.scrollTo(0, 0); AmplePreviews.hydrate(main); side.insertAdjacentHTML('afterbegin', '<div class="secs" aria-label="Sections"><h4>Sections</h4>' + [...document.querySelectorAll('nav.top a')].map((x) => '<a href="' + x.getAttribute('href') + '"' + (x.dataset.s === s ? ' class="cur"' : '') + '>' + x.textContent.replace(' ↗', '') + '</a>').join('') + '<a href="#/">Home</a></div>'); main.querySelectorAll('.pn a').forEach((l) => { const nx = /next/i.test(l.querySelector('small')?.textContent || ''); l.dataset.dir = nx ? 'next' : 'prev'; l.lastChild.textContent = l.lastChild.textContent.replace(/^s*←s*|s*→s*$/g, ''); }); main.focus({ preventScroll: true });
  };
  main.tabIndex = -1; main.setAttribute('aria-live', 'off');
  render();

  /* ---------- search: titles, headings, text, parameters; arrows + enter ---------- */
  let idx = null;
  const strip = (h) => h.replace(/<script[\s\S]*?<\/script>/g, '').replace(/<[^>]+>/g, ' ').replace(/&\w+;/g, ' ').replace(/\s+/g, ' ').trim();
  const build = () => idx = Object.keys(P).filter((k) => k !== '/' && k !== '/404').map((k) => { const h = P[k](), t = (h.match(/<h1[^>]*>([\s\S]*?)<\/h1>/) || [, k])[1].replace(/<[^>]+>/g, ''); return { k, title: t, sec: k.split('/')[1], heads: [...h.matchAll(/<h2[^>]*>([\s\S]*?)<\/h2>/g)].map((m) => strip(m[1])).join(' '), text: strip(h), lead: strip((h.match(/<p class="lead">([\s\S]*?)<\/p>/) || [, ''])[1]) }; });
  const sub = (s, w) => { let i = 0; for (const c of s) if (c === w[i]) i++; return i === w.length; };
  const hit = (w) => idx.map((e) => { const t = e.title.toLowerCase(), id = e.k.split('/').pop(); let sc = 0;
    if (t === w || id === w) sc = 100; else if (t.startsWith(w) || id.startsWith(w)) sc = 80; else if (t.includes(w) || id.includes(w)) sc = 60; else if (e.heads.toLowerCase().includes(w)) sc = 35; else if (e.lead.toLowerCase().includes(w)) sc = 28; else if (e.text.toLowerCase().includes(w)) sc = 12; else if (w.length > 2 && sub(t, w)) sc = 8;
    return { e, sc }; }).filter((x) => x.sc).sort((a, b) => b.sc - a.sc).slice(0, 10);
  const snippet = (e, w) => { const i = e.text.toLowerCase().indexOf(w); const s = i < 0 ? e.lead.slice(0, 90) : e.text.slice(Math.max(0, i - 30), i + 70); return esc(s).replace(new RegExp(w.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'ig'), (m) => '<mark>' + m + '</mark>'); };
  let sel = 0;
  list = function () {
    if (!idx) build(); const w = q.value.trim().toLowerCase();
    const rs = w ? hit(w) : idx.filter((e) => ['/learn', '/blocks', '/studio', '/koda', '/playground'].includes(e.k)).map((e) => ({ e, sc: 0 }));
    sel = 0; res.setAttribute('role', 'listbox');
    res.innerHTML = rs.length ? rs.map(({ e }, i) => `<a role="option" id="r${i}" aria-selected="${i === 0}" class="${i ? '' : 'f'}" href="#${e.k}"><span><b>${esc(e.title)}</b>${w ? '<br><small>' + snippet(e, w) + '</small>' : ''}</span><small>${e.sec}</small></a>`).join('') : '<a role="option"><span>No results for “' + esc(w) + '”</span></a>';
  };
  const move = (d) => { const a = [...res.querySelectorAll('a[href]')]; if (!a.length) return; a[sel]?.classList.remove('f'); a[sel]?.setAttribute('aria-selected', 'false'); sel = (sel + d + a.length) % a.length; a[sel].classList.add('f'); a[sel].setAttribute('aria-selected', 'true'); a[sel].scrollIntoView({ block: 'nearest' }); q.setAttribute('aria-activedescendant', a[sel].id); };
  q.setAttribute('role', 'combobox'); q.setAttribute('aria-label', 'Search the docs'); q.oninput = list;
  q.onkeydown = (e) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); move(1); } else if (e.key === 'ArrowUp') { e.preventDefault(); move(-1); }
    else if (e.key === 'Tab') e.preventDefault(); // focus stays in the dialog
    else if (e.key === 'Enter') { const a = res.querySelectorAll('a[href]')[sel]; if (a) { location.hash = a.getAttribute('href'); pal.classList.remove('on'); } }
  };
  pal.setAttribute('role', 'dialog'); pal.setAttribute('aria-modal', 'true'); pal.setAttribute('aria-label', 'Search');
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && pal.classList.contains('on')) { pal.classList.remove('on'); document.getElementById('open-search').focus(); } });
  const os = document.getElementById('open-search'); if (WIP_HIDDEN) { const mpg = document.getElementById('mpg'); if (mpg) mpg.style.display = 'none'; os.style.display = 'none'; document.getElementById('pal').style.display = 'none'; } /* search is hidden for now (see WIP_HIDDEN in app.js) */ os.tabIndex = 0; os.setAttribute('role', 'button'); os.onkeydown = (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); os.click(); } };

  /* ---------- toggles as real controls, mobile menu ---------- */
  ['t-alive', 't-sound'].forEach((id) => { const t = document.getElementById(id); t.tabIndex = 0; t.setAttribute('role', 'button'); t.setAttribute('aria-pressed', 'false');
    t.addEventListener('click', () => setTimeout(() => t.setAttribute('aria-pressed', t.classList.contains('on')), 0));
    t.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); t.click(); } }); });
  document.getElementById('menu').onclick = () => { const on = document.body.classList.toggle('menu'); document.getElementById('menu').setAttribute('aria-expanded', on); };
  document.getElementById('theme').setAttribute('aria-label', 'Switch light or dark theme');
})();

/* Vibe is remembered over refreshes (localStorage). Applied by "clicking" the existing toggle once at start. */
(function () {
  const K = 'ample-docs-vibe', t = document.getElementById('t-alive');
  t.addEventListener('click', () => { try { localStorage.setItem(K, document.body.classList.contains('alive') ? '1' : '0'); } catch {} });
  try { if (localStorage.getItem(K) === '1' && !document.body.classList.contains('alive')) t.click(); } catch {}
})();

/* Subtle reactive background: a soft glow that breathes with the sample level, plus slow rings on hits. Only in Vibe mode. */
(function () {
  const cv = document.createElement('canvas'); cv.id = 'bg2'; cv.setAttribute('aria-hidden', 'true'); document.body.prepend(cv);
  const g = cv.getContext('2d'); let sm = 0, prev = 0, rings = [], hue = 0;
  const size = () => { cv.width = innerWidth / 2; cv.height = innerHeight / 2; }; addEventListener('resize', size); size();
  (function loop() {
    requestAnimationFrame(loop);
    const on = document.body.classList.contains('alive'); cv.style.opacity = on ? 1 : 0; if (!on) return;
    const lv = parseFloat(document.documentElement.style.getPropertyValue('--lvl')) || 0;
    sm += (lv - sm) * (lv > sm ? 0.25 : 0.05); // fast attack, slow release
    if (lv - prev > 0.07 && rings.length < 6) rings.push({ r: 20, a: 0.22 }); prev = lv;
    hue = (hue + 0.08 + sm * 0.6) % 360;
    const w = cv.width, h = cv.height, acc = getComputedStyle(document.documentElement).getPropertyValue('--acc').trim() || '#5fb3ff';
    g.clearRect(0, 0, w, h);
    const r = Math.max(w, h) * (0.45 + sm * 0.25), grd = g.createRadialGradient(w / 2, h * 1.05, 0, w / 2, h * 1.05, r);
    grd.addColorStop(0, 'hsla(' + (175 + (Math.sin(hue / 57) + 1) * 72) + ',80%,62%,' + (0.05 + sm * 0.16) + ')'); grd.addColorStop(1, 'transparent');
    g.fillStyle = grd; g.fillRect(0, 0, w, h);
    g.strokeStyle = acc; rings = rings.filter((o) => o.a > 0.01);
    for (const o of rings) { g.globalAlpha = o.a; g.lineWidth = 1.5; g.beginPath(); g.arc(w / 2, h * 1.05, o.r, 0, 6.283); g.stroke(); o.r += 3 + sm * 6; o.a *= 0.965; }
    g.globalAlpha = 1;
  })();
})();

/* the wordmark gradient rolls (SMIL); stopped for people who prefer reduced motion */
if (matchMedia('(prefers-reduced-motion:reduce)').matches) document.querySelectorAll('#wm-neon animateTransform').forEach((a) => a.remove());

/* A started preview keeps playing over page changes (a plain click layers it). While any plays, the Sound button is a Stop button: it stops them and leaves Sound mode as it was. */
(function () {
  const t = document.getElementById('t-sound'), i = t.querySelector('i'), lab = t.querySelector('span'), orig = [i.innerHTML, lab.textContent, t.title];
  const stopIco = '<svg class="ico" viewBox="0 0 24 24" aria-hidden="true" fill="currentColor"><rect x="6" y="6" width="12" height="12" rx="2"/></svg>';
  const upd = () => { const s = AmpleAudio.set.size > 0 && AmpleAudio.sticky; t.classList.toggle('stopmode', s); i.innerHTML = s ? stopIco : orig[0]; lab.textContent = s ? 'Stop' : orig[1]; t.title = s ? 'Stop the playing sounds (sound mode stays as it is)' : orig[2]; };
  document.addEventListener('ample-preview', upd);
  t.addEventListener('click', (e) => { if (AmpleAudio.set.size > 0 && AmpleAudio.sticky) { e.stopImmediatePropagation(); [...AmpleAudio.set.keys()].forEach((s) => s()); upd(); } }, true);
})();


