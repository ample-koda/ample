(function () {
  'use strict';
  var index = JSON.parse(document.getElementById('search-index').textContent);
  var q = document.getElementById('q'), results = document.getElementById('results');
  var pages = Array.prototype.slice.call(document.querySelectorAll('.page'));
  function esc(s) { return s.replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
  function search(text) {
    var words = text.toLowerCase().split(/\s+/).filter(Boolean);
    if (!words.length) { results.hidden = true; return; }
    var hits = index.map(function (e) {
      var t = e.text.toLowerCase(), ti = e.title.toLowerCase(), score = 0, first = -1;
      for (var i = 0; i < words.length; i++) {
        var w = words[i], at = t.indexOf(w);
        if (at < 0 && ti.indexOf(w) < 0) { return null; }
        if (ti.indexOf(w) >= 0) { score += 20; }
        if (at >= 0) { score += Math.min(10, t.split(w).length - 1); if (first < 0) { first = at; } }
      }
      return { e: e, score: score, first: first };
    }).filter(Boolean).sort(function (a, b) { return b.score - a.score; }).slice(0, 25);
    results.hidden = false;
    results.innerHTML = '<h2>' + hits.length + ' result' + (hits.length === 1 ? '' : 's') + '</h2>' + hits.map(function (h) {
      var snip = h.first >= 0 ? esc(h.e.text.substr(Math.max(0, h.first - 60), 180)) : '';
      words.forEach(function (w) { snip = snip.replace(new RegExp('(' + w.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + ')', 'ig'), '<mark>$1</mark>'); });
      return '<div class="hit"><a href="#' + h.e.id + '">' + esc(h.e.title) + '</a><small>' + esc(h.e.section) + '</small><span>' + snip + '</span></div>';
    }).join('');
  }
  q.addEventListener('input', function () { search(q.value); });
  results.addEventListener('click', function (e) { if (e.target.tagName === 'A') { q.value = ''; search(''); } });
  document.addEventListener('keydown', function (e) {
    if (e.key === '/' && document.activeElement !== q) { e.preventDefault(); q.focus(); }
    if (e.key === 'Escape') { q.value = ''; search(''); q.blur(); }
  });
  document.addEventListener('click', function (e) {
    var b = e.target.closest ? e.target.closest('.copy') : null;
    if (!b) { return; }
    var text = b.parentNode.querySelector('code').textContent;
    var done = function () { b.textContent = 'Copied'; setTimeout(function () { b.textContent = 'Copy'; }, 1200); };
    if (navigator.clipboard && navigator.clipboard.writeText) { navigator.clipboard.writeText(text).then(done, function () {}); }
  });
  document.getElementById('menu').addEventListener('click', function () { document.body.classList.toggle('menu'); });
  var root = document.documentElement;
  try { var saved = localStorage.getItem('ample-help-theme'); if (saved) { root.setAttribute('data-theme', saved); } } catch (e) { /* storage may be blocked */ }
  document.getElementById('theme').addEventListener('click', function () {
    var cur = root.getAttribute('data-theme');
    var dark = cur ? cur === 'dark' : window.matchMedia('(prefers-color-scheme: dark)').matches;
    var next = dark ? 'light' : 'dark';
    root.setAttribute('data-theme', next);
    try { localStorage.setItem('ample-help-theme', next); } catch (e) { /* ignore */ }
  });
  var links = Array.prototype.slice.call(document.querySelectorAll('nav a'));
  if ('IntersectionObserver' in window) {
    var obs = new IntersectionObserver(function (entries) {
      entries.forEach(function (en) {
        if (en.isIntersecting) { links.forEach(function (l) { l.classList.toggle('on', l.getAttribute('href') === '#' + en.target.id); }); }
      });
    }, { rootMargin: '-10% 0px -80% 0px' });
    pages.forEach(function (p) { obs.observe(p); });
  }
})();
