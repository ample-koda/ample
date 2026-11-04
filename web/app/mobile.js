// Phone layout (max-width 700px): the compact header's "more" menu and the bottom navigation with its bottom sheet.
// The desktop elements stay in the DOM (hidden by css); the menu items just click them, so every behaviour stays in one place.
// @ample:mobile-ui
import { $, toast } from './ui.js';
import { LONG_PRESS_MS, isLongPress, redirectToPort } from '../lib/touch-gestures.mjs';
import { dragSheet, isTap, sheetMax } from '../lib/sheet-drag.mjs';
import { backAction, createDialogHistory } from '../lib/dialog-history.mjs';

const QUERY = '(max-width: 700px)';
const TOUCH_QUERY = '(max-width: 700px), (pointer: coarse)';

function menuItem(label, run, extra = {}) {
  const b = document.createElement('button');
  b.type = 'button';
  b.setAttribute('role', extra.check ? 'menuitemcheckbox' : 'menuitem');
  b.textContent = label;
  if (extra.state) b.setAttribute('data-' + extra.state + '-state', '');
  if (extra.wide) b.classList.add('wide');
  b.onclick = () => run();
  return b;
}

/** A small popup in the style of the transport popups: toggle button, Escape closes, outside press closes, arrow keys move. */
function popupMenu(button, menu) {
  const close = (focus) => { menu.hidden = true; button.setAttribute('aria-expanded', 'false'); if (focus) button.focus(); };
  const items = () => [...menu.querySelectorAll('[role^="menuitem"]:not([hidden])')];
  button.onclick = () => {
    menu.hidden = !menu.hidden;
    button.setAttribute('aria-expanded', String(!menu.hidden));
    if (!menu.hidden) {
      // keep the menu inside the screen: the button is not always at the right edge
      menu.style.translate = '';
      const box = menu.getBoundingClientRect(), edge = document.documentElement.clientWidth;
      if (box.left < 8) menu.style.translate = `${8 - box.left}px 0`;
      else if (box.right > edge - 8) menu.style.translate = `${edge - 8 - box.right}px 0`;
      items()[0]?.focus();
    }
  };
  menu.addEventListener('click', (e) => { if (e.target.closest('[role^="menuitem"]')) close(false); });
  document.addEventListener('pointerdown', (e) => { if (!menu.hidden && !button.parentElement.contains(e.target)) close(false); });
  button.parentElement.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && !menu.hidden) { e.stopPropagation(); close(true); return; }
    if (menu.hidden || !['ArrowDown', 'ArrowUp'].includes(e.key)) return;
    e.preventDefault();
    const list = items(), at = list.indexOf(document.activeElement);
    list[(at + (e.key === 'ArrowDown' ? 1 : -1) + list.length) % list.length]?.focus();
  });
  return close;
}

const proxy = (id) => () => $(id)?.click();

/** The volume popup lives in the (hidden) header group; on a phone the menu shows that popup as a floating card under the header. */
function openVolume() {
  const group = $('.transport-volume'), popup = $('#play-volume');
  if (!group || !popup) return;
  group.classList.add('phone-open');
  popup.hidden = false;
  $('#btn-volume')?.setAttribute('aria-expanded', 'true');
  const close = (event) => {
    if (event?.type === 'keydown' && event.key !== 'Escape') return;
    if (event?.type === 'pointerdown' && popup.contains(event.target)) return;
    group.classList.remove('phone-open');
    popup.hidden = true;
    $('#btn-volume')?.setAttribute('aria-expanded', 'false');
    document.removeEventListener('pointerdown', close, true);
    document.removeEventListener('keydown', close, true);
  };
  setTimeout(() => { document.addEventListener('pointerdown', close, true); document.addEventListener('keydown', close, true); }, 0);
  popup.querySelector('input')?.focus();
}

function initMoreMenu() {
  const button = $('#btn-more'), menu = $('#more-popup');
  if (!button || !menu) return;
  const heading = (text) => { const h = document.createElement('div'); h.className = 'mm-h'; h.textContent = text; h.setAttribute('role', 'presentation'); return h; };
  menu.append(
    heading('Song'),
    menuItem('Build', proxy('#btn-build')),
    menuItem('Save', proxy('#btn-save')),
    menuItem('Checkpoint...', proxy('#btn-cp')),
    menuItem('Loop', proxy('#btn-loop'), { check: true }),
    menuItem('Play preview', proxy('#btn-build-play-preview')),
    menuItem('Volume...', openVolume),
    heading('View'),
    menuItem('Block library', proxy('#tg-left')),
    menuItem('Properties', proxy('#tg-right')),
    menuItem('Panel', proxy('#tg-bottom')),
    menuItem('Hide top bar', proxy('#lip-top')),
    menuItem('Koda: show or hide', () => import('./koda.js').then((m) => m.toggleKoda())),
    menuItem('Koda: look at something', () => import('./koda.js').then((m) => m.pickLook())),
    menuItem('Koda: mute or unmute', () => import('./koda.js').then((m) => m.muteKoda())),
    menuItem("Koda's own login…", () => import('./koda-login.js').then((m) => m.openKodaLogin())),
    ...(document.fullscreenEnabled ? [menuItem('Full screen', proxy('#btn-fs'))] : []),
    heading('Help'),
    menuItem('Guide', () => window.open('/guide/', '_blank')),
    menuItem('Help', () => window.open('/help', '_blank')),
    menuItem('Stop accidental text selection', () => import('./selection-guard.js').then((m) => m.setSelectionGuard(!m.selectionGuardOn())), { check: true, state: 'select-guard', wide: true }),
    menuItem('About AMPLE', proxy('#btn-about'), { wide: true }),
  );
  import('./selection-guard.js').then((m) => m.setSelectionGuard(m.selectionGuardOn()));   // the state of the selection switch in this menu
  const loop = menu.querySelector('[role="menuitemcheckbox"]');
  const syncLoop = () => loop.setAttribute('aria-checked', $('#btn-loop')?.getAttribute('aria-checked') || 'false');
  button.addEventListener('click', syncLoop);
  popupMenu(button, menu);
}

function initBottomNav() {
  const bottom = $('#bottom'), tabs = $('#btabs'), body = $('#bbody');
  if (!bottom || !tabs || !body) return;
  // the handle of the sheet: drag up/down to resize, swipe down or tap to close
  const handle = document.createElement('div');
  handle.id = 'sheet-handle';
  handle.setAttribute('role', 'separator');
  handle.setAttribute('aria-orientation', 'horizontal');
  handle.setAttribute('aria-label', 'Resize panel; drag down to close');
  handle.tabIndex = 0;
  handle.innerHTML = '<i></i>';
  body.prepend(handle);
  const closeSheet = () => { if (!bottom.classList.contains('min')) $('#b-min').click(); };
  const setHeight = (h) => body.style.setProperty('--sheet-h', `${h}px`);
  handle.onpointerdown = (e) => {
    handle.setPointerCapture(e.pointerId);
    const startY = e.clientY, startHeight = body.getBoundingClientRect().height, max = sheetMax(innerHeight);
    handle.onpointermove = (ev) => { const r = dragSheet({ startY, y: ev.clientY, startHeight, max }); setHeight(r.height); };
    const end = (ev) => {
      handle.onpointermove = handle.onpointerup = handle.onpointercancel = null;
      const r = dragSheet({ startY, y: ev.clientY, startHeight, max });
      if (isTap(startY, ev.clientY) || r.close) { setHeight(startHeight); closeSheet(); } else setHeight(r.height);
    };
    handle.onpointerup = end;
    handle.onpointercancel = end;
  };
  handle.onkeydown = (e) => {
    const max = sheetMax(innerHeight), now = body.getBoundingClientRect().height;
    if (e.key === 'Escape' || e.key === 'Enter' || e.key === ' ') { e.preventDefault(); closeSheet(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setHeight(Math.min(max, now + 40)); }
    else if (e.key === 'ArrowDown') { e.preventDefault(); setHeight(Math.max(160, now - 40)); }
  };

  // "More": the tabs that do not fit in the bar (Variants, Assistant, Log)
  const more = document.createElement('span');
  more.className = 'nav-more';
  more.innerHTML = '<button id="b-more" type="button" aria-haspopup="menu" aria-expanded="false" aria-controls="more-tabs" aria-label="More panels"><span class="ico" aria-hidden="true">⋯</span><span class="lab">More</span></button><span id="more-tabs" class="transport-popup nav-popup" role="menu" hidden></span>';
  tabs.append(more);
  const list = $('#more-tabs');
  for (const [t, label] of [['var', 'Variants'], ['assistant', 'Assistant'], ['log', 'Log']]) {
    list.append(menuItem(label, () => $(`#btabs button[data-t="${t}"]`)?.click()));
  }
  popupMenu($('#b-more'), list);
  const sync = () => {
    const hidden = ['var', 'assistant', 'log'].some((t) => $(`#btabs button[data-t="${t}"]`)?.classList.contains('on')) && !bottom.classList.contains('min');
    $('#b-more').classList.toggle('on', hidden);
  };
  new MutationObserver(sync).observe(tabs, { subtree: true, attributes: true, attributeFilter: ['class'] });
  new MutationObserver(sync).observe(bottom, { attributes: true, attributeFilter: ['class'] });
  // the tab icons and short labels come from the labels already in the buttons
  tabs.querySelectorAll('button[data-t]').forEach((b) => b.setAttribute('aria-expanded', 'false'));
  const aria = () => tabs.querySelectorAll('button[data-t]').forEach((b) => b.setAttribute('aria-expanded', String(b.classList.contains('on') && !bottom.classList.contains('min'))));
  new MutationObserver(aria).observe(tabs, { subtree: true, attributes: true, attributeFilter: ['class'] });
  new MutationObserver(aria).observe(bottom, { attributes: true, attributeFilter: ['class'] });
}

/** On a phone Play and Stop are one button: Play (or Build and play) when idle, Stop while audio plays, Cancel while a build runs. The desktop Stop button stays in the DOM and does the work. */
function initPlayStop() {
  const phone = window.matchMedia(QUERY);
  const play = $('#btn-preview'), stop = $('#btn-stop');
  if (!play || !stop) return;
  const sync = () => {
    const mode = stop.dataset.mode;
    const active = phone.matches && !stop.disabled && !!mode && mode !== 'none';
    play.classList.toggle('as-stop', active);
    if (active) { play.dataset.stopMode = mode; play.setAttribute('aria-label', mode === 'build' ? 'Cancel build' : 'Stop'); } else delete play.dataset.stopMode;
  };
  new MutationObserver(sync).observe(stop, { attributes: true, attributeFilter: ['disabled', 'data-mode', 'class'] });
  phone.addEventListener?.('change', sync);
  play.addEventListener('click', (e) => {
    if (!play.classList.contains('as-stop')) return;
    e.stopImmediatePropagation();
    e.preventDefault();
    stop.click();
  }, true);
  sync();
}

export function initMobile() {
  initMoreMenu();
  initPlayStop();
  initBottomNav();
  initMobileDialogs();
  // on a phone the sheet starts closed so the canvas is visible; this goes through the normal Panel toggle so the stored layout stays in step
  const phone = window.matchMedia(QUERY);
  const closeIfPhone = () => { if (phone.matches && !$('#bottom').classList.contains('min')) $('#b-min').click(); };
  closeIfPhone();
  phone.addEventListener?.('change', closeIfPhone);
}

/** MUI03: canvas on touch. Call after initCanvas (it owns the zoom/map buttons). Pinch zoom, one-finger pan and the mouse behaviour live in canvas.js. */
export function initMobileCanvas() {
  const hint = $('#hint'), map = $('#z-map'), mini = $('#mini'), cv = $('#cv');
  if (!hint || !cv) return;
  const desktopText = hint.textContent;
  const touchText = 'pinch = zoom · drag = pan · long press = menu · tap a dot, then another dot, to connect';
  const mq = window.matchMedia(TOUCH_QUERY);
  const apply = () => { hint.textContent = mq.matches ? touchText : desktopText; };
  apply();
  mq.addEventListener?.('change', apply);
  // the minimap is on demand on a phone: hidden until the Map button is pressed
  if (mq.matches && map && mini && mini.style.display !== 'none') map.click();

  // long press on a block or on the background opens the existing context menu; on a button or dot it shows what its tooltip says
  let press = null;
  const clear = () => { if (press) clearTimeout(press.timer); press = null; };
  cv.addEventListener('pointerdown', (event) => {
    clear();
    if (event.pointerType !== 'touch' || !event.isPrimary) return;
    const start = { x: event.clientX, y: event.clientY };
    press = { start, now: start, downAt: performance.now(), timer: 0 };
    press.timer = setTimeout(() => {
      if (!press || !isLongPress({ start: press.start, now: press.now, downAt: press.downAt, at: performance.now() })) return;
      const { now } = press;
      press = null;
      const target = document.elementFromPoint(now.x, now.y) || cv;
      const tip = target.closest?.('button[title], .port[title]');
      const swallow = (e) => { e.stopImmediatePropagation(); e.preventDefault(); };
      window.addEventListener('click', swallow, { capture: true, once: true });
      setTimeout(() => window.removeEventListener('click', swallow, true), 800);
      if (tip) { toast(tip.title); return; }
      target.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: now.x, clientY: now.y, view: window }));
    }, LONG_PRESS_MS);
  }, { capture: true });
  cv.addEventListener('pointermove', (event) => { if (press && event.isPrimary) press.now = { x: event.clientX, y: event.clientY }; }, { capture: true });
  for (const type of ['pointerup', 'pointercancel']) cv.addEventListener(type, clear, { capture: true });
  cv.addEventListener('touchstart', (event) => { if (event.touches.length > 1) clear(); }, { capture: true, passive: true });
  // Chromium moves a tap to the nearest clickable element; at a small zoom that can press a block button (</> switches the canvas to the source view
  // and hides #cv) when a connector dot was meant. The real finger position decides: a tap that went down at a dot goes to the dot.
  let down = null;
  cv.addEventListener('pointerdown', (event) => { down = event.pointerType === 'touch' ? { x: event.clientX, y: event.clientY } : null; }, { capture: true });
  cv.addEventListener('click', (event) => {
    if (event.pointerType !== 'touch' || !down) return;
    const button = event.target.closest?.('.node button');
    if (!button) return;
    const ports = [...cv.querySelectorAll('.port')];
    const at = redirectToPort(down, button.getBoundingClientRect(), ports.map((p) => p.getBoundingClientRect()));
    if (at < 0) return;
    event.stopImmediatePropagation();
    event.preventDefault();
    ports[at].dispatchEvent(new PointerEvent('click', { bubbles: true, cancelable: true, pointerType: 'touch', clientX: down.x, clientY: down.y }));
  }, { capture: true });
}

/** MUI05: on a phone every dialog is a full-screen page with a Back bar; the system Back button closes the top one. Esc keeps working. */
const DIALOG_QUERY = '(max-width: 700px), (pointer: coarse) and (max-width: 900px)';
export function initMobileDialogs() {
  const mq = window.matchMedia(DIALOG_QUERY);
  const history_ = createDialogHistory();
  const watchers = new WeakMap();
  const titleOf = (d) => {
    const ref = d.getAttribute('aria-labelledby');
    const node = (ref && document.getElementById(ref)) || d.querySelector('h1, h2, h3, .tx-title, .studio-dialog-header strong');
    return (node?.textContent || d.getAttribute('aria-label') || '').trim().slice(0, 60);
  };
  const ensureBar = (d) => {
    let bar = d.querySelector(':scope > .mob-back');
    if (!bar) {
      bar = document.createElement('div');
      bar.className = 'mob-back';
      const back = document.createElement('button');
      back.type = 'button';
      back.className = 'mob-back-btn';
      back.setAttribute('aria-label', 'Back');
      back.textContent = '‹ Back';
      back.onclick = (event) => { event.preventDefault(); backAction(d); };
      const title = document.createElement('span');
      title.className = 'mob-back-title';
      bar.append(back, title);
    }
    bar.querySelector('.mob-back-title').textContent = titleOf(d);
    if (d.firstElementChild !== bar) d.prepend(bar);
  };
  const decorate = (d) => {
    if (d.id === 'session-warning') return;   // a blocking notice stays a card: a Back bar would suggest the session can be kept
    if (!d.open || d.classList.contains('mls-panel')) return;
    if (!mq.matches && !d.querySelector(':scope > .mob-back')) return;
    ensureBar(d);
    if (!watchers.has(d)) {
      // dialogs that rebuild their content keep the bar: it is put back first when removed
      const keep = new MutationObserver(() => { if (d.open && mq.matches && d.firstElementChild?.className !== 'mob-back') ensureBar(d); });
      keep.observe(d, { childList: true });
      watchers.set(d, keep);
    }
    if (mq.matches && history_.opened(d)) history.pushState({ ampleDialog: true }, '');
  };
  const released = (d) => {
    if (d.open) return;
    if (history_.closed(d)) history.back();
  };
  const scan = () => document.querySelectorAll('dialog').forEach((d) => (d.open ? decorate(d) : released(d)));
  new MutationObserver((records) => {
    for (const r of records) {
      if (r.type === 'attributes') { const d = r.target; if (d.open) decorate(d); else released(d); }
      else r.addedNodes.forEach((n) => { if (n.nodeType === 1) { if (n.matches('dialog')) decorate(n); n.querySelectorAll?.('dialog').forEach(decorate); } });
    }
  }).observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['open'] });
  addEventListener('popstate', () => {
    const d = history_.popstate();
    if (!d) return;
    if (!backAction(d) && d.open) history_.opened(d), history.pushState({ ampleDialog: true }, '');
  });
  mq.addEventListener?.('change', scan);
  scan();
}