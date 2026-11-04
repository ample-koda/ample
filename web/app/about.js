// The "?" menu: Guide, Help and the About dialog (version, build time and who made it).
import { $, el } from './ui.js';
import { api } from './api.js';

const AUTHOR = { name: 'Tinne', site: 'https://www.youtube.com/@EuphoriaRises', siteLabel: '@EuphoriaRises' };
const PROJECT = { site: 'https://github.com/ample-koda/ample', siteLabel: 'github.com/ample-koda/ample' };

const when = (iso) => { const d = new Date(iso); return Number.isNaN(d.getTime()) ? String(iso) : d.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }); };

/** Opens the About dialog (built once, filled with what the server says each time it opens). */
export async function showAbout() {
  let dialog = $('#about-dialog');
  if (!dialog) {
    dialog = el('dialog', { id: 'about-dialog', className: 'about-dialog', 'aria-labelledby': 'about-title' });
    dialog.addEventListener('click', (event) => { if (event.target === dialog) dialog.close(); });   // a click on the backdrop closes it
    document.body.append(dialog);
  }
  dialog.replaceChildren(el('p', { textContent: 'Loading...' }));
  if (!dialog.open) dialog.showModal();
  let info = null;
  try { info = await api.get('/api/about'); } catch { /* shown below */ }
  const rows = info ? [
    ['Version', info.version],
    ['Built', when(info.builtAt)],
    ['Server started', when(info.startedAt)],
    ['Runtime', info.runtime],
    ['System', info.os],
    ['Language spec', info.spec],
    ['This page', location.host],
  ] : [['Version', 'unknown (the server did not answer)']];
  const text = `AMPLE ${rows.map(([k, v]) => `${k}: ${v}`).join(' | ')}`;
  const copy = el('button', { type: 'button', className: 'about-copy', textContent: 'Copy details', title: 'Copies the version details, handy in a bug report' });
  copy.onclick = async () => { try { await navigator.clipboard.writeText(text); copy.textContent = 'Copied'; } catch { copy.textContent = 'Could not copy'; } setTimeout(() => { copy.textContent = 'Copy details'; }, 1600); };
  const close = el('button', { type: 'button', className: 'primary', textContent: 'Close' });
  close.onclick = () => dialog.close();
  dialog.replaceChildren(
    el('img', { className: 'about-logo', src: 'assets/logo/ample-logo.svg', alt: 'AMPLE', width: 220 }),
    el('p', { id: 'about-title', className: 'about-tag', textContent: 'Assisted Music Programming Language Experience' }),
    el('dl', { className: 'about-facts' }, rows.flatMap(([k, v]) => [el('dt', { textContent: k }), el('dd', { textContent: String(v) })])),
    el('p', { className: 'about-author' }, [
      document.createTextNode('Made by '), el('strong', { textContent: AUTHOR.name }), document.createTextNode(' ('),
      el('a', { href: AUTHOR.site, target: '_blank', rel: 'noopener', textContent: AUTHOR.siteLabel }),
      document.createTextNode(') and the AMPLE contributors'),
    ]),
    el('p', { className: 'about-author' }, [
      el('a', { href: PROJECT.site, target: '_blank', rel: 'noopener', textContent: PROJECT.siteLabel }),
    ]),
    el('div', { className: 'about-actions' }, [copy, close]),
  );
}

export function initHelpMenu() {
  const button = $('#btn-help'), menu = $('#help-popup');
  if (!button || !menu) return;
  const close = () => { menu.hidden = true; button.setAttribute('aria-expanded', 'false'); };
  button.onclick = () => { menu.hidden = !menu.hidden; button.setAttribute('aria-expanded', String(!menu.hidden)); if (!menu.hidden) menu.querySelector('[role="menuitem"]')?.focus(); };
  document.addEventListener('pointerdown', (event) => { if (!button.parentElement.contains(event.target)) close(); });
  button.parentElement.addEventListener('keydown', (event) => { if (event.key === 'Escape') { close(); button.focus(); } });
  menu.addEventListener('click', (event) => { if (event.target.closest('[role="menuitem"]')) close(); });   // the switch for text selection stays open: it shows its state
  $('#btn-about').onclick = () => { showAbout(); };
}
