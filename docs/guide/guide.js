// The tooltips of the guide: a word with a dotted underline shows its short meaning when you hover or focus it; clicking goes to the dictionary.
(() => {
  const data = JSON.parse(document.getElementById('terms')?.textContent || '{}');
  const card = document.createElement('div');
  card.id = 'termcard';
  card.setAttribute('role', 'tooltip');
  document.body.append(card);
  let hideTimer = 0;
  const show = (link) => {
    const t = data[link.dataset.term];
    if (!t) return;
    clearTimeout(hideTimer);
    card.innerHTML = '';
    const title = document.createElement('b');
    title.textContent = t.term;
    const text = document.createElement('div');
    text.textContent = t.short;
    const more = document.createElement('span');
    more.className = 'more';
    more.textContent = 'Click the word to open the dictionary →';
    card.append(title, text, more);
    card.style.display = 'block';
    const r = link.getBoundingClientRect();
    const left = Math.max(8, Math.min(r.left, window.innerWidth - card.offsetWidth - 12));
    const below = r.bottom + card.offsetHeight + 12 < window.innerHeight;
    card.style.left = `${left}px`;
    card.style.top = `${below ? r.bottom + 6 : Math.max(8, r.top - card.offsetHeight - 6)}px`;
  };
  const hide = () => { hideTimer = setTimeout(() => { card.style.display = 'none'; }, 120); };
  document.querySelectorAll('a.term').forEach((a) => {
    a.addEventListener('mouseenter', () => show(a));
    a.addEventListener('focus', () => show(a));
    a.addEventListener('mouseleave', hide);
    a.addEventListener('blur', hide);
  });
  card.addEventListener('mouseenter', () => clearTimeout(hideTimer));
  card.addEventListener('mouseleave', hide);
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') card.style.display = 'none'; });

  // touch screens have no hover: the first tap on a word shows its meaning, a second tap on the same word opens the dictionary
  const coarse = window.matchMedia('(hover: none)').matches;
  let shown = null;
  if (coarse) {
    document.querySelectorAll('a.term').forEach((a) => a.addEventListener('click', (e) => {
      if (shown !== a) { e.preventDefault(); shown = a; show(a); }
    }));
    document.addEventListener('click', (e) => { if (!e.target.closest?.('a.term, #termcard')) { shown = null; card.style.display = 'none'; } });
  }

  // the menu on a narrow screen: a button opens the list of pages, a tap on a page or Escape closes it
  const toggle = document.querySelector('.menu-toggle');
  const setOpen = (open) => { document.body.classList.toggle('nav-open', open); toggle?.setAttribute('aria-expanded', String(open)); };
  toggle?.addEventListener('click', () => setOpen(!document.body.classList.contains('nav-open')));
  document.querySelectorAll('#guide-nav a').forEach((a) => a.addEventListener('click', () => setOpen(false)));
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') setOpen(false); });
  window.matchMedia('(min-width: 821px)').addEventListener('change', (e) => { if (e.matches) setOpen(false); });
  document.querySelector('#guide-nav [aria-current=page]')?.scrollIntoView({ block: 'center' });
})();
