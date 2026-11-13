// Panels: show or hide, resize, remember per project. @ample:layout

import { $ } from './ui.js';

const KEY = 'ample.layout';

function load() { try { return JSON.parse(localStorage.getItem(KEY) || '{}'); } catch { return {}; } }
function store(v) { try { localStorage.setItem(KEY, JSON.stringify(v)); } catch { /* storage may be blocked */ } }

export function initLayout() {
  const work = $('#work');
  const bottom = $('#bottom');
  const saved = load();
  const apply = () => {
    work.classList.toggle('noL', !!saved.noL);
    work.classList.toggle('noR', !!saved.noR);
    bottom.classList.toggle('min', !!saved.min);
    if (saved.bh) bottom.style.setProperty('--bh', `${saved.bh}px`);
    // an inline width (from dragging the grip) beats the .noL/.noR class, so a hidden sidebar must set its column to 0 here
    work.style.setProperty('--lw', saved.noL ? '0px' : saved.lw ? `${saved.lw}px` : '');
    work.style.setProperty('--rw', saved.noR ? '0px' : saved.rw ? `${saved.rw}px` : '');
    document.body.classList.toggle('noH', !!saved.noH);
    $('#lip-top').textContent = saved.noH ? '▾' : '▴';
    $('#lip-top').setAttribute('aria-expanded', String(!saved.noH));
    const drawers = matchMedia('(max-width:900px)').matches;   // a narrow screen: the button says whether the drawer is open
    $('#tg-left').classList.toggle('on', drawers ? work.classList.contains('dr-L') : !saved.noL);
    $('#tg-right').classList.toggle('on', drawers ? work.classList.contains('dr-R') : !saved.noR);
    $('#tg-bottom').classList.toggle('on', !saved.min);
    $('#b-min').textContent = saved.min ? '▴' : '▾';
    $('#lip-left').textContent = saved.noL ? '▸' : '◂';
    $('#lip-left').setAttribute('aria-expanded', String(!saved.noL));
    $('#lip-right').textContent = saved.noR ? '◂' : '▸';
    $('#lip-right').setAttribute('aria-expanded', String(!saved.noR));
  };
  const resized = () => window.dispatchEvent(new Event('resize'));
  // the panel may take what is under the header, banner and tab bar, but the canvas keeps 200 px: --bmax follows the real top of the work area
  const fit = () => { const room = Math.max(120, Math.round(innerHeight - $('#work').getBoundingClientRect().top - 200)); bottom.style.setProperty('--bmax', `${room}px`); return room; };
  window.addEventListener('resize', fit);
  const flip = (k) => { saved[k] = !saved[k]; store(saved); apply(); resized(); };
  // on a narrow screen the sidebars are not columns: the same buttons open them as a drawer over the canvas (not remembered)
  const narrow = matchMedia('(max-width:900px)');
  const back = document.createElement('div');
  back.id = 'drawer-back';
  work.append(back);
  const drawer = (name) => {
    const open = work.classList.contains(name);
    work.classList.remove('dr-L', 'dr-R');
    if (!open) work.classList.add(name);
    const l = work.classList.contains('dr-L'), r = work.classList.contains('dr-R');
    $('#tg-left').classList.toggle('on', l);
    $('#tg-right').classList.toggle('on', r);
    back.hidden = !(l || r);
  };
  back.hidden = true;
  back.onclick = () => { work.classList.remove('dr-L', 'dr-R'); back.hidden = true; apply(); };
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !back.hidden) back.click(); });
  narrow.addEventListener('change', () => { if (!narrow.matches) back.click(); });
  const side = (key, name) => () => (narrow.matches ? drawer(name) : flip(key));
  $('#tg-left').onclick = side('noL', 'dr-L');
  $('#tg-right').onclick = side('noR', 'dr-R');
  $('#lip-left').onclick = () => flip('noL');
  $('#lip-right').onclick = () => flip('noR');
  $('#lip-top').onclick = () => flip('noH');
  // the tab sits right under the top bar: follow its height (it changes with the window width)
  const header = document.querySelector('header');
  const follow = () => document.documentElement.style.setProperty('--hh', `${header.offsetHeight}px`);
  new ResizeObserver(follow).observe(header);
  follow();
  $('#tg-bottom').onclick = () => flip('min');
  $('#b-min').onclick = () => flip('min');
  // full screen: only where the browser allows it (not in Safari on an iPhone)
  const fs = $('#btn-fs');
  if (document.fullscreenEnabled) {
    fs.hidden = false;
    const sync = () => { const on = !!document.fullscreenElement; document.body.classList.toggle('fs-on', on); fs.setAttribute('aria-pressed', String(on)); resized(); };
    document.addEventListener('fullscreenchange', sync);
    fs.onclick = () => { (document.fullscreenElement ? document.exitFullscreen() : document.documentElement.requestFullscreen()).catch(() => {}); };
  }
  const grip = $('#bgrip');
  grip.onpointerdown = (e) => {
    grip.setPointerCapture(e.pointerId);
    const y0 = e.clientY, h0 = bottom.getBoundingClientRect().height;
    let frame = 0, nextY = y0;
    const move = (y) => { saved.bh = Math.max(120, Math.min(fit(), Math.round(h0 + (y0 - y)))); saved.min = false; bottom.classList.remove('min'); bottom.style.setProperty('--bh', `${saved.bh}px`); };
    grip.onpointermove = (ev) => {
      nextY = ev.clientY;
      if (!frame) frame = requestAnimationFrame(() => { frame = 0; move(nextY); });
    };
    const end = (ev) => { if (frame) cancelAnimationFrame(frame); frame = 0; move(ev.clientY); grip.onpointermove = grip.onpointerup = grip.onpointercancel = null; store(saved); resized(); };
    grip.onpointerup = end;
    grip.onpointercancel = end;
  };
  const resizeSide = (id, prop, dir) => {
    const handle = $(id);
    handle.onmousedown = (e) => e.preventDefault();   // no native drag of a library item or a text selection may start under the handle (it cancels the pointer)
    handle.onpointerdown = (e) => {
      handle.setPointerCapture(e.pointerId);
      const x0 = e.clientX, w0 = (dir === 'l' ? $('#left') : $('#right')).getBoundingClientRect().width;
      handle.onpointermove = (ev) => {
        saved[prop] = Math.max(160, Math.min(560, Math.round(w0 + (dir === 'l' ? 1 : -1) * (ev.clientX - x0))));
        apply();
      };
      handle.onpointerup = () => { handle.onpointermove = null; store(saved); resized(); };
    };
  };
  resizeSide('#lgrip', 'lw', 'l');
  resizeSide('#rgrip', 'rw', 'r');
  apply();
  fit();
  resized();
}
