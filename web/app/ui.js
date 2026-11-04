// Small DOM helpers shared by the panels: element builder, toast, context menu and the confirm dialog.

export const $ = (s) => document.querySelector(s);

const sessionWarning = $('#session-warning');
document.addEventListener('ample:session-lost', () => {
  if (!sessionWarning.open) sessionWarning.showModal();
  // a server started with --login has a sign-in page; offer it (a server without it answers 404 and the link stays hidden)
  fetch('/login', { cache: 'no-store' }).then((r) => { if (r.ok) { $('#session-login').hidden = false; $('#session-plain').hidden = true; } }).catch(() => {});
});
sessionWarning.addEventListener('cancel', (event) => event.preventDefault());

const THEME_KEY = 'ample.theme';
export function setTheme(name) {
  const themes = ['studio-dark', 'light', 'high-contrast', 'auto', 'classic'];
  if (!themes.includes(name)) throw new RangeError('Unknown theme');
  document.documentElement.dataset.theme = name;
  try { localStorage.setItem(THEME_KEY, name); } catch { /* storage may be blocked */ }
}

try { setTheme(localStorage.getItem(THEME_KEY) || 'studio-dark'); } catch { setTheme('studio-dark'); }

export function el(tag, props = {}, kids = []) {
  const e = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    // aria-*, data-* and the read-only "list" of an input are attributes; assigning them as properties does nothing (or throws)
    if (key.startsWith('aria-') || key.startsWith('data-') || key === 'list') { if (value !== '' && value !== undefined && value !== null) e.setAttribute(key, String(value)); } else e[key] = value;
  }
  // an icon-only button (▶ × ✕ …) with a title gets the title as its accessible name
  if (tag === 'button' && typeof props.title === 'string' && props.title && !props['aria-label'] && typeof props.textContent === 'string' && !/[A-Za-z0-9]{2,}/.test(props.textContent)) e.setAttribute('aria-label', props.title);
  kids.forEach((k) => e.append(k));
  return e;
}

/** Makes a clickable non-button element usable from the keyboard: focusable, announced as a button, Enter/Space click it. */
export function activatable(node, label) {
  node.tabIndex = 0;
  node.setAttribute('role', 'button');
  if (label) node.setAttribute('aria-label', label);
  node.addEventListener('keydown', (e) => { if (e.target === node && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); node.click(); } });
  return node;
}

let toastTimer = 0;
export function toast(msg) {
  const t = $('#toast');
  t.setAttribute('role', 'status'); t.setAttribute('aria-atomic', 'true');
  t.setAttribute('aria-live', /error|failed|could not|cannot|can't/i.test(String(msg)) ? 'assertive' : 'polite');
  t.textContent = msg;
  t.classList.add('on');
  try {
    if (t.hidePopover && t.hasAttribute('popover')) { t.hidePopover(); t.showPopover(); }
    else { const open = [...document.querySelectorAll('dialog[open]')].pop(); const home = open || document.body; if (t.parentElement !== home) home.append(t); }   // no popover support: ride inside the open dialog so it is not hidden behind it
  } catch { /* the toast stays a plain layer */ }
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove('on'), Math.min(14000, 2800 + String(msg).length * 45));   // a long message stays long enough to read
}

const menuIconPaths = {
  add: '<path d="M12 5v14M5 12h14"/>', open: '<path d="M3 7h7l2 2h9v10H3zM3 7V5h7l2 2"/>', folder: '<path d="M3 7h7l2 2h9v10H3z"/>',
  delete: '<path d="M4 7h16M9 7V4h6v3m3 0-1 13H7L6 7m4 4v5m4-5v5"/>', edit: '<path d="m4 16-.8 4.8L8 20l11-11-4-4zM13.5 6.5l4 4"/>',
  move: '<path d="M12 3v18m0-18L8 7m4-4 4 4m-4 14-4-4m4 4 4-4M3 12h18m-18 0 4-4m-4 4 4 4m14-4-4-4m4 4-4 4"/>',
  copy: '<path d="M8 8h12v13H8zM4 16V4h12"/>', duplicate: '<path d="M8 8h12v13H8zM4 16V4h12"/><path d="M12 14h4m-2-2v4"/>',
  export: '<path d="M12 3v12m-5-5 5 5 5-5M4 17v4h16v-4"/>', import: '<path d="M12 16V4m-5 5 5-5 5 5M4 17v4h16v-4"/>',
  view: '<path d="M2.5 12s3.3-6 9.5-6 9.5 6 9.5 6-3.3 6-9.5 6-9.5-6-9.5-6z"/><circle cx="12" cy="12" r="2.5"/>',
  play: '<path d="m8 5 11 7-11 7z"/>', stop: '<rect x="6" y="6" width="12" height="12" rx="1"/>',
  build: '<path d="m14 6 4 4m-7 7 8-8a3 3 0 0 0-4-4l-8 8m0 0-3 3 4 4 3-3m-4-1 2 2"/>',
  settings: '<circle cx="12" cy="12" r="3"/><path d="m19.4 15 .1.1 1.4 1.1-1.5 2.6-1.7-.7a8 8 0 0 1-1.7 1l-.3 1.9h-3l-.3-1.9a8 8 0 0 1-1.7-1l-1.7.7-1.5-2.6 1.4-1.1a7 7 0 0 1 0-2l-1.4-1.1L7 9.3l1.7.7a8 8 0 0 1 1.7-1l.3-1.9h3l.3 1.9a8 8 0 0 1 1.7 1l1.7-.7 1.5 2.6-1.4 1.1a7 7 0 0 1-.1 2z"/>',
  undo: '<path d="M9 7 4 12l5 5M4 12h10a6 6 0 0 1 6 6"/>', redo: '<path d="m15 7 5 5-5 5m5-5H10a6 6 0 0 0-6 6"/>',
  accept: '<path d="m4 12 5 5L20 6"/>', cancel: '<path d="m6 6 12 12M18 6 6 18"/>', toggle: '<rect x="3" y="7" width="18" height="10" rx="5"/><circle cx="15" cy="12" r="3"/>',
  reset: '<path d="M4 10a8 8 0 1 1 1 7M4 4v6h6"/>', fit: '<path d="M8 3H3v5m13-5h5v5M3 16v5h5m13-5v5h-5"/>', branch: '<circle cx="6" cy="5" r="2"/><circle cx="18" cy="19" r="2"/><circle cx="18" cy="5" r="2"/><path d="M6 7v10a2 2 0 0 0 2 2h8M8 5h6a4 4 0 0 1 4 4v8"/>',
  columns: '<rect x="3" y="4" width="18" height="16" rx="1"/><path d="M9 4v16m6-16v16"/>', select: '<path d="m5 3 14 10-7 1-3 7z"/>', connect: '<circle cx="6" cy="12" r="3"/><circle cx="18" cy="12" r="3"/><path d="M9 12h6"/>', info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5m0-8h.01"/>',
  more: '<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>',
};

function menuIcon(label) {
  const text = label.toLowerCase();
  const rules = [
    [/\b(delete|remove|discard|reject|deny)\b/, 'delete'], [/\b(cancel|close)\b/, 'cancel'], [/\b(rename|edit|change|modify)\b/, 'edit'],
    [/\b(move|relocate)\b/, 'move'], [/\b(duplicate|clone)\b/, 'duplicate'], [/\b(copy|cut)\b/, 'copy'], [/\b(export|download)\b/, 'export'],
    [/\b(import|upload)\b/, 'import'], [/\b(play|listen|preview)\b/, 'play'], [/\b(stop)\b/, 'stop'], [/\b(build|render|check|compile)\b/, 'build'],
    [/\b(undo)\b/, 'undo'], [/\b(redo)\b/, 'redo'], [/\b(accept|approve|apply|save|yes)\b/, 'accept'], [/\b(enable|disable|toggle|bypass|mute)\b/, 'toggle'],
    [/\b(reset|restore)\b/, 'reset'], [/\b(fit|zoom)\b/, 'fit'], [/\b(branch|line|track|sequence)\b/, 'branch'], [/\b(column|sort)\b/, 'columns'],
    [/\b(select|group)\b/, 'select'], [/\b(connect|link|route)\b/, 'connect'], [/\b(open|reveal|show|view|look|go to|find|search|where used|source)\b/, 'view'],
    [/\b(add|new|create|insert)\b/, 'add'], [/\b(setting|parameter|property|detail|info|help|about)\b/, 'settings'],
  ];
  const kind = rules.find(([pattern]) => pattern.test(text))?.[1] || 'more';
  const icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  icon.setAttribute('class', 'menu-icon'); icon.setAttribute('viewBox', '0 0 24 24'); icon.setAttribute('aria-hidden', 'true'); icon.setAttribute('fill', 'none'); icon.setAttribute('stroke', 'currentColor'); icon.setAttribute('stroke-width', '1.7'); icon.setAttribute('stroke-linecap', 'round'); icon.setAttribute('stroke-linejoin', 'round'); icon.innerHTML = menuIconPaths[kind];
  return icon;
}

let menuOpenedAt = 0;

/** Closes the context menu (and says so: something may have been put aside while it was open, like the keyboard). */
export function closeMenu() {
  const menu = $('#menu');
  if (!menu || menu.style.display === 'none' || menu.style.display === '') return;
  menu.style.display = 'none';
  document.dispatchEvent(new CustomEvent('ample:menu-closed'));
}

/** A small screen (a phone) has no room for a menu beside the finger or a list beside a list: the menu becomes a sheet at the bottom and submenus unfold under their item. */
export const compactMenus = () => innerWidth <= 700 || (matchMedia('(pointer: coarse)').matches && innerWidth <= 900);

export function showMenu(x, y, items) {
  const menu = $('#menu');
  const sheet = compactMenus();
  menu.classList.toggle('sheet', sheet);
  menuOpenedAt = performance.now();
  // an item with `children` opens a list beside it as soon as the pointer is on it (like a real context menu); a click or touch opens it too
  const place = () => {
    if (sheet) { menu.style.maxHeight = ''; menu.style.left = ''; menu.style.top = ''; menu.style.overflowY = 'auto'; return; }   // the css puts the sheet at the bottom
    menu.style.maxHeight = `${Math.max(120, innerHeight - 16)}px`;
    menu.style.overflowY = 'auto';
    menu.style.left = `${Math.max(4, Math.min(x, innerWidth - 230))}px`;
    menu.style.top = `${Math.max(4, Math.min(y, innerHeight - menu.offsetHeight - 8))}px`;
  };
  const fits = (fly, sub) => {
    if (sheet) return;
    // beside the item, on the right unless there is no room, and never below the bottom of the window
    const r = sub.getBoundingClientRect(), w = fly.offsetWidth;
    const left = r.right + w > innerWidth - 4 && r.left - w > 4;
    fly.classList.toggle('left', left);
    fly.style.position = 'fixed';
    fly.style.left = `${left ? r.left - w + 2 : r.right - 2}px`;
    fly.style.right = 'auto';
    fly.style.maxHeight = `${Math.max(120, innerHeight - 16)}px`;
    fly.style.overflowY = 'auto';
    fly.style.top = `${Math.max(8, Math.min(r.top - 5, innerHeight - fly.offsetHeight - 8))}px`;
  };
  const render = (list, into) => list.forEach((it) => {
    if (it === '-') { into.append(el('hr')); return; }
    if (it.head) { into.append(el('div', { textContent: it.head, className: 'hd' })); return; }
    if (it.children) {
      const d = el('div', { className: 'sub menu-item-with-icon' }, [it.icon || menuIcon(it.label), document.createTextNode(`${it.label} ›`)]);
      d.onmousedown = (e) => e.preventDefault();
      const fly = el('div', { className: 'flyout' });
      render(it.children, fly);
      d.append(fly);
      d.onmouseenter = () => { if (sheet) return; [...d.parentElement.children].forEach((o) => { if (o !== d) o.classList?.remove('open'); }); d.classList.add('open'); fits(fly, d); };
      d.onclick = (e) => {
        e.stopPropagation(); it.onOpen?.();
        const was = d.classList.contains('open');
        [...d.parentElement.children].forEach((o) => { if (o !== d) o.classList?.remove('open'); });
        d.classList.toggle('open', !(sheet && was));   // on a sheet a second tap folds the list again
        fits(fly, d);
      };
      into.append(d);
      return;
    }
    const d = el('div', { className: `${it.danger ? 'danger ' : ''}menu-item-with-icon`, title: it.title || '' });
    d.append(it.icon || menuIcon(it.label), document.createTextNode(it.label));
    d.onmousedown = (e) => e.preventDefault();
    d.onclick = (e) => { e.stopPropagation(); it.leave?.(); closeMenu(); it.fn(); };
    if (it.hover) { d.onmouseenter = () => it.hover(); d.onmouseleave = () => it.leave?.(); }   // a menu item can do something while the pointer is on it (hear a sound)
    into.append(d);
  });
  menu.innerHTML = '';
  render(items, menu);
  menu.style.display = 'block';
  // a menu opened inside a modal dialog (the file picker) goes into that dialog: the rest of the page is inert and below it
  const modal = [...document.querySelectorAll('dialog[open]')].find((d) => d.matches(':modal'));   // only a real modal dialog: a floating window (AMPLE Jam) is not the page's top layer, and as a transformed box it would clip and shift the menu
  const home = modal || document.body;
  if (menu.parentElement !== home) home.append(menu);
  place();
}

/** Puts the menu back in the page: call before a dialog that held it is removed. */
export function rescueMenu() {
  const menu = $('#menu');
  if (menu && menu.parentElement !== document.body) { menu.style.display = 'none'; document.body.append(menu); }
}

addEventListener('click', () => closeMenu());
// a menu stays where it was opened, so it closes when the page behind it moves (zooming or scrolling outside the menu, a finger dragging the page) or changes size (the keyboard of a phone opens)
const closeWhenOutside = (event) => { const menu = $('#menu'); if (menu && menu.style.display === 'block' && performance.now() - menuOpenedAt > 400 && !menu.contains(event.target)) closeMenu(); };
for (const type of ['wheel', 'scroll', 'touchmove']) addEventListener(type, closeWhenOutside, { passive: true, capture: true });
addEventListener('resize', () => closeMenu());
window.visualViewport?.addEventListener('resize', () => closeMenu());

/** Shows the dialog; resolves with the value of the clicked button (undefined when closed). */
let askTail = null;
export function ask(...args) {
  // one shared #dlg: an ask opened while another is showing waits until that one closes, so no promise can hang
  const p = askTail ? askTail.then(() => askNow(...args)) : askNow(...args);
  const tail = p.then(() => {}, () => {});
  askTail = tail;
  tail.then(() => { if (askTail === tail) askTail = null; });
  return p;
}

function askNow(title, body, buttons, extraNodes = []) {
  return new Promise((res) => {
    const d = $('#dlg');
    d.classList.add('ask-dialog');
    d.classList.toggle('ask-wide', buttons.length > 2);   // three choices sit in one row
    $('#dlg-t').textContent = title;
    $('#dlg-b').textContent = body;
    const ex = $('#dlg-extra');
    ex.innerHTML = '';
    extraNodes.forEach((n) => ex.append(n));
    const bb = $('#dlg-btns');
    bb.innerHTML = '';
    buttons.forEach((b) => {
      const x = el('button', { textContent: b.label, type: 'button', className: b.primary ? 'primary' : '' });
      x.onclick = () => { d.close(); res(b.value); };
      bb.append(x);
    });
    // A close event from the previous dialog can still arrive after this one was opened: ignore it while open.
    d.onclose = () => { if (d.open) return; d.classList.remove('ask-dialog', 'ask-wide'); res(undefined); };
    // Enter in a text field of the dialog presses its main button
    d.onkeydown = (event) => {
      if (event.key !== 'Enter' || event.isComposing || event.shiftKey || event.target.tagName !== 'INPUT' || ['checkbox', 'radio', 'button'].includes(event.target.type)) return;
      const main = bb.querySelector('button.primary');
      if (main) { event.preventDefault(); main.click(); }
    };
    d.showModal();
    ex.querySelector('input[type=text], input:not([type])')?.focus();
  });
}
