// Koda, the small helper of AMPLE (plan/pet): one floating layer above the page with the note, its bubbles and its presence (visible, hiding, hidden, peeking, appearing, muted). Koda speaks only when asked: a block,
// a piece of code or a field is the subject; what AMPLE can tell by itself is told at once, the rest goes to the backend (koda-backend.js). Nothing here changes the project except an accepted draft. @ample:koda
import { S, applyText, flat, subscribe, undo, redo } from './store.js';
import { $, el, showMenu, toast } from './ui.js';
import { currentPower, loadModels, menuItems, onModelChange, summary } from './koda-model.js';
import { Koda } from './koda-art.js';
import { answerBubble, choiceBubble, draftBubble, menuBubble, noteBubble, thoughtBubble, variantsBubble } from './koda-bubbles.js';
import { KodaBusy, KodaUnavailable, backend, setBackend } from './koda-backend.js';
import { activeText, serverBackend, setBare, setLiveHooks, tellUserEdit } from './koda-server.js';
import { clearGhosts, showGhosts } from './koda-ghosts.js';
import { editorOpen, hideInline, inlineShown, showInline } from './koda-inline.js';
import { editorShowsSong } from './source.js';
import { loadKodaLanguage, onKodaLanguage, tx } from './koda-lang.js';
import { blockSubject, codeSubject, docsHref, explainLocally, fieldSubject, subjectName } from '../lib/koda-context.mjs';
import { stagedSequence } from '../lib/koda-sequence.mjs';
import { clampSpot, placeBubble } from '../lib/koda-placement.mjs';
import { groupChanges, structuredHunks, textWith } from '../lib/koda-diff.mjs';
import * as Presence from '../lib/koda-presence.mjs';
import { aimEnd, aimMove, clearGaze, clearLook, glance, initGaze, isLinked, letGoNewest, lookAt, looksCount, lookSubject, targetAt, targetKey, unlook } from './koda-gaze.js';
import { makeShake } from '../lib/koda-shake.mjs';
import { initVibe, vibePulse } from './koda-vibe.js';
import { namedBlocks } from '../lib/koda-glance.mjs';

const KEY = 'ample.koda';
import { kodaLanguage as kodaLanguageNow } from './koda-lang.js';
// The size of Koda: 1 by default and 0.8 on a phone; a choice (0.5 to 1.25 in steps of 0.05, mostly meant to make Koda smaller) is kept in this browser only
const SCALE_KEY = 'ample.koda.scale', SCALE_MIN = 0.5, SCALE_MAX = 1.25;
const snapScale = (v) => Math.min(SCALE_MAX, Math.max(SCALE_MIN, Math.round(v * 20) / 20));
const defaultScale = () => (innerWidth <= 700 ? 0.8 : 1);
const scale = () => { try { const s = parseFloat(localStorage.getItem(SCALE_KEY)); if (Number.isFinite(s)) return snapScale(s); } catch { /* storage may be blocked */ } return defaultScale(); };
const SIZE = () => { const k = scale(); return { width: Math.round((innerWidth <= 700 ? 110 : 150) * k), height: Math.round((innerWidth <= 700 ? 100 : 140) * k) }; };
const load = () => { try { return JSON.parse(localStorage.getItem(KEY) || '{}'); } catch { return {}; } };
const save = () => { try { localStorage.setItem(KEY, JSON.stringify({ enabled: me.enabled, ...Presence.toSaved(me.p) })); } catch { /* storage may be blocked */ } };

const me = { enabled: false, p: Presence.initial(), koda: null, layer: null, float: null, host: null, tab: null, bubble: null, subject: null, busy: null, pending: null, edge: 'right', raf: 0, ready: false, risk: 'safe', locked: false, pill: null, dot: null };

/* ---------- the layer ---------- */
export function initKoda() {
  if (me.ready) return;
  me.ready = true;
  setBackend(serverBackend);
  loadKodaLanguage();
  onKodaLanguage(() => { if (me.bubble?.kind === 'menu') ask(me.subject); });
  const saved = load();
  me.enabled = !!saved.enabled;
  me.p = Presence.initial(saved);
  const petHost = el('div', { className: 'pethost' });
  const badge = el('div', { className: 'badge', 'aria-hidden': 'true' });
  const muteMark = el('div', { className: 'mutebadge', title: 'Koda is muted', hidden: true });
  muteMark.innerHTML = '<svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true"><path d="M9 17.5a2.5 2 0 1 1-1-1.6V6l9-2v10.5" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"/><path d="M4 4l16 16" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>';
  // draft / live: a pill in the bubble that can be switched until the first reply of the conversation, then a small dot under Koda says which it was
  me.pill = el('button', { type: 'button', className: 'riskpill', role: 'switch', 'aria-checked': 'false', hidden: true, onclick: () => { me.risk = me.risk === 'safe' ? 'unsafe' : me.risk === 'unsafe' ? 'bare' : 'safe'; rememberRisk(); paintRisk(); } });   // draft, live, bare metal
  me.dot = el('span', { className: 'riskdot', 'aria-hidden': 'true', hidden: true });
  me.risk = recalledRisk();   // live mode stays on until it is switched off (remembered in this browser)
  // a choice of model or thinking level that is not the default is always visible: a small chip under Koda (a click opens the menu)
  me.modelChip = el('button', { type: 'button', className: 'kmodelchip', hidden: true, onclick: (e) => { e.stopPropagation(); const r = e.currentTarget.getBoundingClientRect(); openMenu(r.left, r.top); } });
  me.float = el('div', { className: 'float', id: 'koda-float' }, [badge, muteMark, petHost, me.dot, me.modelChip]);
  me.host = el('div', { className: 'bubhost' });
  me.tab = el('button', { type: 'button', className: 'koda-tab', title: 'Show Koda', 'aria-label': 'Show Koda', hidden: true, textContent: '♪', onclick: () => { if (me.tabDragged) { me.tabDragged = false; return; } showKoda(); } });
  me.layer = el('div', { className: 'koda koda-layer', id: 'koda-layer', hidden: !me.enabled }, [me.host, me.float, me.tab]);
  document.body.append(me.layer);
  me.muteMark = muteMark;
  me.koda = new Koda(petHost);
  onModelChange(applyModelLook);
  if (me.enabled) loadModels();
  applyModelLook();
  me.koda.svg.setAttribute('tabindex', '0');
  me.koda.svg.setAttribute('role', 'button');
  me.koda.svg.setAttribute('aria-label', 'Koda, the helper: press Enter to ask, drag to move');
  const size = SIZE();
  const spot = me.p.spot ?? { left: innerWidth - size.width - 30, top: innerHeight - size.height - (innerWidth <= 700 ? 168 : 36) };
  setSpot(spot);
  wireDrag();
  wireTabDrag();
  const shown = () => me.enabled && me.p.presence === 'visible' && !me.layer.hidden;
  initGaze({ head: () => { if (!shown()) return null; const r = me.koda.svg.getBoundingClientRect(); return { x: r.left + r.width * 0.36, y: r.top + r.height * 0.68 }; }, beat: vibePulse });
  initVibe({ koda: me.koda, shown: () => shown() && !me.p.muted, busy: () => !!me.busy, quiet: () => !me.bubble, linked: () => { const s = lookSubject(); return s && (s.kind === 'block' || s.kind === 'field') ? s.block : null; } });
  me.float.addEventListener('contextmenu', (e) => { e.preventDefault(); openMenu(e.clientX, e.clientY); });
  me.koda.svg.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggleMenuBubble(); } });
  addEventListener('keydown', onKey, true);
  addEventListener('pointerdown', () => { delete document.body.dataset.kb; }, true);
  addEventListener('resize', () => setSpot(me.p.spot ?? spot));
  paintPresence(false);
}

/** The draft / live state of the conversation: the pill shows while Koda is being asked (not after the first reply), the headband as long as unsafe is on. */
/** Koda looks like the model and the thinking level it works with (a small, quick setting soft, a large model that thinks hard radiant), and says so when it is not the default. */
function applyModelLook() {
  if (!me.koda) return;
  me.koda.setPower(currentPower());
  if (!me.modelChip) return;
  const s = summary();
  me.modelChip.hidden = !s.override;
  me.modelChip.textContent = `${s.model} · ${s.effort}`;
  me.modelChip.title = tx('model.chip.title');
}
function paintRisk() {
  if (!me.ready || !me.pill) return;
  const unsafe = me.risk !== 'safe', bare = me.risk === 'bare';   // live and bare metal both change things at once
  const asking = !me.locked && ['menu', 'choice', 'thought'].includes(me.bubble?.kind);
  me.pill.hidden = !asking;
  me.pill.setAttribute('aria-checked', String(unsafe));
  me.pill.dataset.mode = me.risk;
  me.pill.title = tx('risk.title');
  me.pill.replaceChildren(el('i'), document.createTextNode(tx(bare ? 'risk.bare' : unsafe ? 'risk.unsafe' : 'risk.safe')));
  me.dot.hidden = !(me.locked && me.bubble);
  me.dot.classList.toggle('unsafe', unsafe);
  me.dot.classList.toggle('bare', bare);
  if (me.layer) me.layer.dataset.mode = me.risk;   // the whole of Koda takes the tint of the mode
  setBare(bare);   // bare metal also tells the model to be quick and rough
  me.koda?.setRisk(unsafe, bare);
}
/** The first real reply (an answer, a draft or variants) locks the choice for the rest of the conversation; a closed conversation starts again on safe. */
/** Live mode is remembered in this browser only (never on the server) until it is switched off again. */
const RISK_KEY = 'ample.koda.live';
const recalledRisk = () => { try { const v = localStorage.getItem(RISK_KEY); return v === '2' ? 'bare' : v === '1' ? 'unsafe' : 'safe'; } catch { return 'safe'; } };
const rememberRisk = () => { try { if (me.risk !== 'safe') localStorage.setItem(RISK_KEY, me.risk === 'bare' ? '2' : '1'); else localStorage.removeItem(RISK_KEY); } catch { /* storage may be blocked */ } };
const lockRisk = () => { me.locked = true; paintRisk(); };

// Bare metal, the live view: the song follows what the model makes, edit by edit; the steps in between are folded into one undo step when it is done,
// and what the person changes meanwhile is handed to the model (koda-server.js tellUserEdit).
const live = { running: false, applying: false, known: '', start: null, did: false };
setLiveHooks({
  start() { live.running = true; live.known = activeText(); live.start = null; live.did = false; },
  async show(text) {
    if (live.start === null) live.start = S.undo.length;
    live.applying = true;
    try { await applyText(text); live.did = true; } finally { live.applying = false; }
    live.known = activeText();
  },
  paused() { toast(tx('bare.paused')); },
  end() {
    live.running = false;
    if (live.start !== null && S.undo.length > live.start + 1) S.undo.splice(live.start + 1);   // one undo step for everything Koda did while it worked
    live.start = null;
  },
});
subscribe(() => {
  if (!live.running || live.applying) return;
  const now = activeText();
  if (now !== live.known) { const was = live.known; live.known = now; tellUserEdit(was, now); }
});

/** The note at the edge (Koda is away): drag it along the edge to move it, and pull it far enough from the edge to bring Koda out where you let go. */
const PULL = 70;
function wireTabDrag() {
  const tab = me.tab;
  let from = null;
  const where = (e) => {
    const horizontal = me.edge === 'left' || me.edge === 'right';
    const along = horizontal ? e.clientY : e.clientX;
    const away = me.edge === 'left' ? e.clientX : me.edge === 'right' ? innerWidth - e.clientX : me.edge === 'top' ? e.clientY : innerHeight - e.clientY;
    return { horizontal, along, away: Math.max(0, away) };
  };
  tab.addEventListener('pointerdown', (e) => {
    if (e.button !== 0) return;
    from = { x: e.clientX, y: e.clientY, moved: false };
    tab.setPointerCapture(e.pointerId);
  });
  tab.addEventListener('pointermove', (e) => {
    if (!from) return;
    if (!from.moved && Math.hypot(e.clientX - from.x, e.clientY - from.y) < 6) return;
    from.moved = true;
    const w = where(e);
    // along the edge the note follows the finger; away from it it is pulled out a little (more and more reluctantly), and glows once Koda would come out
    if (w.horizontal) tab.style.top = `${Math.max(10, Math.min(innerHeight - 44, w.along - 17))}px`;
    else tab.style.left = `${Math.max(10, Math.min(innerWidth - 44, w.along - 17))}px`;
    const pull = Math.min(w.away, PULL) * 0.6;
    const shift = { left: [pull, 0], right: [-pull, 0], top: [0, pull], bottom: [0, -pull] }[me.edge];
    tab.style.transform = `translate(${shift[0]}px, ${shift[1]}px)`;
    tab.classList.toggle('pulled', w.away >= PULL);
  });
  const end = (e, cancelled) => {
    if (!from) return;
    const moved = from.moved;
    from = null;
    tab.classList.remove('pulled');
    tab.style.transform = '';
    if (!moved) return;
    me.tabDragged = true;   // the click that follows a drag is not a tap on the note
    setTimeout(() => { me.tabDragged = false; }, 0);
    if (cancelled) { paintPresence(false); return; }
    const w = where(e), size = SIZE();
    if (w.away >= PULL) {
      // Koda comes out where the finger let go
      setSpot({ left: e.clientX - size.width / 2, top: e.clientY - size.height / 2 });
      showKoda();
      return;
    }
    // the note stays where it was put along the edge (Koda comes back at that height)
    if (w.horizontal) setSpot({ ...me.p.spot, top: w.along - 17 - 40 });
    else setSpot({ ...me.p.spot, left: w.along - 17 - 60 });
    paintPresence(false);
    save();
  };
  tab.addEventListener('pointerup', (e) => end(e, false));
  tab.addEventListener('pointercancel', (e) => end(e, true));
}

function setSpot(spot) {
  const c = clampSpot(spot, SIZE(), { width: innerWidth, height: innerHeight });
  me.p = { ...me.p, spot: c };
  me.float.style.left = `${c.left}px`;
  me.float.style.top = `${c.top}px`;
  me.float.style.width = `${SIZE().width}px`;
}

/* ---------- presence: hide, peek, appear, mute ---------- */
const nearestEdge = () => {
  const r = me.float.getBoundingClientRect();
  const d = { left: r.left, right: innerWidth - r.right, bottom: innerHeight - r.bottom, top: r.top };
  return Object.entries(d).sort((a, b) => a[1] - b[1])[0][0];
};
function edgeOffsets(edge, peek) {
  const r = { left: me.p.spot.left, top: me.p.spot.top, ...SIZE() };
  const gone = { left: -(r.left + r.width + 24), right: innerWidth - r.left + 24, top: -(r.top + r.height + 24), bottom: innerHeight - r.top + 24 };
  const keep = peek ? (edge === 'bottom' || edge === 'top' ? 64 : 46) : 0;
  const away = edge === 'left' || edge === 'right' ? { x: gone[edge], y: 0 } : { x: 0, y: gone[edge] };
  const back = { left: keep, right: -keep, top: keep, bottom: -keep }[edge];
  return edge === 'left' || edge === 'right' ? { x: away.x + back, y: 0 } : { x: 0, y: away.y + back };
}
function paintPresence(animate) {
  const s = me.p.presence;
  const f = me.float;
  f.classList.remove('is-hiding', 'is-hidden', 'is-peeking', 'is-appearing');
  const out = edgeOffsets(me.edge, false), peek = edgeOffsets(me.edge, true);
  f.style.setProperty('--hx', `${out.x}px`); f.style.setProperty('--hy', `${out.y}px`);
  f.style.setProperty('--px', `${peek.x}px`); f.style.setProperty('--py', `${peek.y}px`);
  if (s === 'hiding') f.classList.add('is-hiding');
  else if (s === 'hidden') f.classList.add('is-hidden');
  else if (s === 'peeking') f.classList.add('is-peeking');
  else if (s === 'appearing') f.classList.add('is-appearing');
  f.classList.toggle('pending', me.p.pending);
  me.muteMark.hidden = !me.p.muted;
  me.koda.svg.dataset.muted = me.p.muted ? '1' : '0';   // the tape over the mouth
  me.tab.hidden = s !== 'hidden' || !me.enabled;
  if (!me.tab.hidden) {
    const mid = { left: ['left', '0px', `${me.p.spot.top + 40}px`], right: ['right', '0px', `${me.p.spot.top + 40}px`] }[me.edge];
    me.tab.style.left = me.edge === 'left' ? '0px' : me.edge === 'right' ? 'auto' : `${me.p.spot.left + 60}px`;
    me.tab.style.right = me.edge === 'right' ? '0px' : 'auto';
    me.tab.style.top = me.edge === 'bottom' ? 'auto' : me.edge === 'top' ? '0px' : `${Math.max(10, me.p.spot.top + 40)}px`;
    me.tab.style.bottom = me.edge === 'bottom' ? '0px' : 'auto';
    void mid;
  }
  me.koda.pause(!Presence.animating(me.p) || !me.enabled);
  const btn = $('#btn-koda');
  if (btn) { btn.setAttribute('aria-pressed', String(me.enabled && s !== 'hidden' && s !== 'hiding')); btn.classList.toggle('on', me.enabled && s !== 'hidden' && s !== 'hiding'); }
  void animate;
}
export function hideKoda(force = false) {
  if (!me.ready || me.p.presence === 'hidden') return;
  // busy: ask whether it may go on out of sight (yes: it hides and works on; no: the work stops and it hides)
  if (me.busy && !force && me.p.presence === 'visible') {
    setBubble('toast', noteBubble({ kind: 'toast', label: tx('hide.busy.label'), text: tx('hide.busy.text'), buttons: [{ label: tx('hide.busy.go'), primary: true, value: 'go' }, { label: tx('hide.busy.stop'), quiet: true, value: 'stop' }] }, (v) => { if (v === 'stop') me.busy?.abort(); hideKoda(true); }, () => { closeBubble(); if (me.busy) me.showThought?.(); }), 'think');
    return;
  }
  closeBubble();
  me.edge = nearestEdge();
  me.p = Presence.hide(me.p);
  paintPresence(true);
  me.koda.set('happy');
  setTimeout(() => { me.p = Presence.hidden(me.p); me.koda.set('neutral'); paintPresence(false); save(); }, 760);
}
export function showKoda() {
  if (!me.ready) return;
  if (!me.enabled) { me.enabled = true; me.layer.hidden = false; }
  if (me.p.presence === 'hidden' || me.p.presence === 'peeking' || me.p.presence === 'hiding') {
    me.p = { ...Presence.show(me.p), pending: me.p.pending };
    paintPresence(true);
    me.koda.set('jump');
    setTimeout(() => { me.p = Presence.appeared(me.p); me.koda.set('neutral'); paintPresence(false); save(); if (me.pending) openPending(); else if (me.busy && !me.bubble) me.showThought?.(); }, 900);
  } else paintPresence(false);
  save();
}
/** The header button and the menus: turns Koda on (first time with a hello), shows or hides. */
export function toggleKoda() {
  if (!me.ready) return;
  if (!me.enabled || ['hidden', 'hiding', 'peeking'].includes(me.p.presence)) {
    const first = !me.enabled;
    showKoda();
    if (first) setTimeout(() => { if (!me.bubble && Presence.mayOpenBubble(me.p)) openNote({ label: tx('hello.label'), text: tx('hello.text'), buttons: [{ label: tx('hello.thanks'), primary: true }] }, () => closeBubble()); }, 1000);
  } else hideKoda();
}
/** A press on a muted Koda: the first only shows the question (wide eyes, a corner of the tape that lifts and settles), the second one inside a few seconds takes the tape off and unmutes. */
function tapeTap() {
  const svg = me.koda.svg;
  if (me.tapeAsk) {
    clearTimeout(me.tapeAsk);
    me.tapeAsk = 0;
    svg.classList.remove('tape-ask');
    svg.classList.add('tape-peel');
    me.koda.set('surprised');
    setTimeout(() => { svg.classList.remove('tape-peel'); muteKoda(false); }, 700);
    return;
  }
  me.koda.set('asking');
  svg.classList.add('tape-ask');
  me.tapeAsk = setTimeout(() => { me.tapeAsk = 0; svg.classList.remove('tape-ask'); if (me.koda.emotion === 'asking' && !me.bubble) me.koda.set('neutral'); }, 4200);
}
export function muteKoda(flag = !me.p.muted) {
  if (!me.ready) return;
  me.p = flag ? Presence.mute(me.p) : Presence.unmute(me.p);
  if (flag) closeBubble();
  paintPresence(false);
  save();
  if (!flag && me.p.presence === 'visible') { me.koda.set('happy'); openNote({ label: tx('unmuted.label'), text: tx('unmuted.text') }, () => closeBubble()); setTimeout(() => me.koda.set('neutral'), 1600); }
}
export const kodaState = () => ({ enabled: me.enabled, presence: me.p.presence, muted: me.p.muted, pending: me.p.pending, emotion: me.koda?.emotion, bubble: me.bubble?.kind ?? null, busy: !!me.busy, draft: !!me.draftCtl, inline: inlineShown() });

/** Koda looks at what is under the pointer (a block or a line of code): it becomes the subject, and a neon line stays to it. Nothing there: Koda looks puzzled. */
function lookFrom(x, y, add = false) {
  const target = targetAt(x, y);
  if (target && isLinked(target)) { unlook(target); clearTimeout(me.hahTimer); me.koda.set('neutral'); return; }   // let go on a thing that is linked: that one is let go of
  const subject = target ? lookAt(target, add && looksCount() > 0) : null;
  if (!subject) me.koda.set('confused');
  else if (me.aimKey && me.aimKey === targetKey(target)) me.koda.set('asking');   // it already reacted while the pointer was dragged here
  else hah();
  setTimeout(() => { if (!me.bubble && !me.busy && ['asking', 'confused'].includes(me.koda.emotion)) me.koda.set('neutral'); }, 1400);
}

/** While the line is dragged (Ctrl or a quick second touch): Koda reacts at once to what the pointer is over. Something to talk about: the "hah!", again for each other thing; nothing: it looks puzzled. */
function aimReact(x, y) {
  const now = performance.now();
  if (now - (me.aimAt ?? 0) < 50) return;
  me.aimAt = now;
  const target = targetAt(x, y);
  const key = targetKey(target);
  if (key === me.aimKey) return;
  me.aimKey = key;
  if (target && isLinked(target)) { clearTimeout(me.hahTimer); me.koda.set('sad'); }   // letting go here lets go of it
  else if (target) hah('excited'); else { clearTimeout(me.hahTimer); me.koda.set('confused'); }
}

/** Shaken: Koda throws off the line it looked at last (shake on and the next one goes, newest first); when nothing is left, or the shaking stops, it is puzzled a moment and then smiles. */
const THROW_GAP = 690;   // at most one line per 0.69 s, however hard it is shaken
function throwOff() {
  const now = performance.now();
  if (now - (me.threwAt ?? -1e9) < THROW_GAP) return;
  me.threwAt = now;
  me.aimKey = '';
  clearTimeout(me.hahTimer);
  me.koda.set('confused');
  letGoNewest();
  clearTimeout(me.calmTimer);
  me.calmTimer = setTimeout(() => {
    const quiet = !me.bubble || me.bubble.kind === 'toast';
    if (!quiet || me.busy || me.koda.emotion !== 'confused') return;
    me.koda.set('happy');
    setTimeout(() => { if (!me.busy && me.koda.emotion === 'happy' && (!me.bubble || me.bubble.kind === 'toast')) me.koda.set('neutral'); }, 1500);
  }, 700);
}

/** The "hah!" of a link: Koda startles (wide eyes, the gold ! pops in) and settles into asking while the line holds. Linked again to something else: it pops again. */
function hah(settle = 'asking') {
  clearTimeout(me.hahTimer);
  me.koda.set('neutral');   // a fresh pop each time, also when it was already startled
  requestAnimationFrame(() => {
    me.koda.set('surprised');
    me.hahTimer = setTimeout(() => { if (me.koda.emotion === 'surprised') me.koda.set(settle); }, 700);
  });
}

/** Look at a block (from a menu): Koda is shown first when it is off. */
export function lookAtBlock(id) {
  const go = () => { if (lookAt({ kind: 'block', id })) hah(); else me.koda.set('confused'); setTimeout(() => { if (!me.bubble && !me.busy && me.koda.emotion === 'asking') me.koda.set('neutral'); }, 1400); };
  if (me.ready && me.enabled && me.p.presence === 'visible') go(); else { toggleKoda(); setTimeout(go, 1000); }
}

/** Tap what Koda should look at: the next press on anything (a block, a field, the library, a template, a file, a line of code) is not an ordinary click but points Koda there. Esc cancels. */
export function pickLook() {
  const start = () => {
    document.body.classList.add('koda-picking');
    toast(tx('look.pick'));
    const stop = () => { document.body.classList.remove('koda-picking'); removeEventListener('pointerdown', down, true); removeEventListener('keydown', key, true); };
    const down = (e) => { e.preventDefault(); e.stopPropagation(); stop(); addEventListener('click', (c) => { c.preventDefault(); c.stopPropagation(); }, { capture: true, once: true }); lookFrom(e.clientX, e.clientY); };
    const key = (e) => { if (e.key === 'Escape') { e.preventDefault(); stop(); } };
    addEventListener('pointerdown', down, true);
    addEventListener('keydown', key, true);
  };
  if (me.ready && me.enabled && me.p.presence === 'visible') start(); else { toggleKoda(); setTimeout(start, 1000); }
}

/* ---------- dragging and clicking ---------- */
function wireDrag() {
  let down = null;
  // a quick second touch (or click) that is dragged is the same as Ctrl and drag: for a touch screen or a remote desktop where there is no Ctrl
  const lastTap = { t: -1e9, x: 0, y: 0 };
  const svg = me.koda.svg;
  svg.style.touchAction = 'none';
  svg.addEventListener('pointerdown', (e) => {
    svg.setPointerCapture(e.pointerId);
    const r = me.float.getBoundingClientRect();
    // Ctrl held: not a move, but a look (a neon line from Koda to what the pointer is let go on)
    // taps in a row: the second is a look (drag), the third adds to what Koda looks at (like Ctrl+Shift and drag)
    const quick = performance.now() - lastTap.t < 450 && Math.hypot(e.clientX - lastTap.x, e.clientY - lastTap.y) < 48;
    const chain = quick ? (lastTap.chain ?? 1) + 1 : 1;
    down = { chain, add: ((e.ctrlKey || e.metaKey) && e.shiftKey) || chain >= 3, shake: makeShake(3), shaken: false, dx: e.clientX - r.left, dy: e.clientY - r.top, x0: e.clientX, y0: e.clientY, moved: false, aim: e.ctrlKey || e.metaKey || chain >= 2 };
    if (down.aim) { closeBubble(); const band = document.getElementById('band'); if (band) band.style.display = 'none'; }   // no selection rectangle left from a press beside Koda
  });
  svg.addEventListener('pointermove', (e) => {
    if (!down) return;
    if (!down.moved && Math.abs(e.clientX - down.x0) + Math.abs(e.clientY - down.y0) > 6) { down.moved = true; if (!down.aim) me.float.classList.add('drag'); }
    if (down.moved && down.aim) {
      if (down.shake.feed(e.clientX, e.clientY, performance.now())) { down.shake.reset(); down.threw = true; throwOff(); }
      aimMove(e.clientX, e.clientY); aimReact(e.clientX, e.clientY); return;
    }
    if (down.moved) {
      setSpot({ left: e.clientX - down.dx, top: e.clientY - down.dy });
      // Koda itself is shaken to and fro while it looks at things: it throws the lines off
      if (looksCount() > 0 && down.shake.feed(e.clientX, e.clientY, performance.now())) { down.shake.reset(); down.threw = true; throwOff(); }
    }
  });
  const end = (e) => {
    if (down?.aim && down.moved) { if (!down.threw) { aimEnd(); lookFrom(e.clientX, e.clientY, down.add); } else aimEnd(); me.aimKey = ''; down = null; return; }
    if (down && !down.moved) { lastTap.t = performance.now(); lastTap.x = e.clientX; lastTap.y = e.clientY; lastTap.chain = down.chain; if (me.p.muted && me.p.presence === 'visible') tapeTap(); else toggleMenuBubble(); }
    if (down?.moved) save();
    down = null; me.float.classList.remove('drag');
  };
  svg.addEventListener('pointerup', end);
  svg.addEventListener('pointercancel', () => { if (down?.aim) aimEnd(); down = null; me.float.classList.remove('drag'); });
}
async function openMenu(x, y) {
  // the model and thinking level: what this Koda session uses, a choice for this session only (or as the default of Koda), and when it counts if Koda is working now
  const modelItems = await menuItems({ busy: !!me.busy, apply: (model, effort) => backend().applyModel?.(model, effort) }).catch(() => []);
  showMenu(x, y, [
    { head: 'Koda' },
    { label: tx('ctx.ask'), fn: () => ask(currentSubject()) },
    '-',
    ...modelItems,
    '-',
    { label: tx('ctx.resize'), fn: () => startResize() },
    { label: me.p.muted ? tx('menu.unmute') : tx('menu.mute'), fn: () => muteKoda() },
    { label: tx('ctx.hide'), fn: () => hideKoda() },
    { label: tx('ctx.login'), fn: () => import('./koda-login.js').then((m) => m.openKodaLogin()) },
  ]);
}

/* ---------- resize: four corners scale the whole of Koda in steps of 0.05 ---------- */
function startResize() {
  if (me.resizing) return;
  const box = el('div', { className: 'k-resize', role: 'group', 'aria-label': 'Resize Koda' });
  const label = el('span', { className: 'k-resize-label' });
  let loopId = 0;   // the frame follows Koda while it is dragged, until Done
  const frame = () => { const r = me.float.getBoundingClientRect(); Object.assign(box.style, { left: `${r.left}px`, top: `${r.top}px`, width: `${r.width}px`, height: `${r.height}px` }); label.textContent = `${scale().toFixed(2)}×`; };
  const apply = (next, anchor, corner) => {
    const before = me.float.getBoundingClientRect(), ratio = next / scale();
    try { localStorage.setItem(SCALE_KEY, String(next)); } catch { /* storage may be blocked */ }
    const w = before.width * ratio, h = before.height * ratio;
    const spot = anchor ? { left: corner.includes('l') ? anchor.x - w : anchor.x, top: corner.includes('t') ? anchor.y - h : anchor.y } : me.p.spot;
    setSpot(spot); save(); frame();
  };
  const finish = () => { cancelAnimationFrame(loopId); box.remove(); me.resizing = null; document.removeEventListener('keydown', onKey, true); window.removeEventListener('resize', frame); save(); };
  const onKey = (e) => { if (e.key === 'Escape' || e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); finish(); } };
  const begin = (e, corner) => {
    e.preventDefault(); e.stopPropagation();
    const r = me.float.getBoundingClientRect(), from = scale();
    const anchor = { x: corner.includes('l') ? r.right : r.left, y: corner.includes('t') ? r.bottom : r.top };   // the opposite corner stays where it is
    const d0 = Math.max(8, Math.hypot(e.clientX - anchor.x, e.clientY - anchor.y));
    const handle = e.currentTarget;
    handle.setPointerCapture?.(e.pointerId);
    const move = (ev) => { const next = snapScale(from * Math.hypot(ev.clientX - anchor.x, ev.clientY - anchor.y) / d0); if (next !== scale()) apply(next, anchor, corner); };
    const up = () => { handle.removeEventListener('pointermove', move); handle.removeEventListener('pointerup', up); handle.removeEventListener('pointercancel', up); };
    handle.addEventListener('pointermove', move); handle.addEventListener('pointerup', up); handle.addEventListener('pointercancel', up);
  };
  for (const corner of ['tl', 'tr', 'bl', 'br']) { const h = el('div', { className: `k-resize-handle ${corner}`, 'data-corner': corner }); h.addEventListener('pointerdown', (e) => begin(e, corner)); box.append(h); }
  box.append(label, el('button', { type: 'button', className: 'k-resize-reset', textContent: 'Reset', title: 'The default size', onclick: () => { try { localStorage.removeItem(SCALE_KEY); } catch { /* storage may be blocked */ } setSpot(me.p.spot); save(); frame(); } }), el('button', { type: 'button', className: 'k-resize-done', textContent: 'Done', onclick: finish }));
  me.layer.append(box); me.resizing = box;
  document.addEventListener('keydown', onKey, true); window.addEventListener('resize', frame);
  const follow = () => { frame(); loopId = requestAnimationFrame(follow); };
  follow();
}

/* ---------- bubbles ---------- */
function place() {
  me.raf = 0;
  if (!me.bubble) return;
  // a draft that waits shows itself in the source editor as soon as that is open
  if (me.draftCtl && !inlineShown() && editorOpen() && editorShowsSong() && editorInSync()) me.draftCtl.show();
  const f = me.float.getBoundingClientRect();
  const b = me.bubble.node;
  const w = b.offsetWidth, h = b.offsetHeight;
  const spot = placeBubble({ pet: { left: f.left, top: f.top, width: f.width, height: f.height }, container: { width: innerWidth, height: innerHeight }, bubble: { width: w, height: h } });
  b.classList.toggle('below', spot.below);
  // the resize grip is at the bottom right, unless there is little room to the right of the bubble (it grows to the left then); it keeps its side while it is being dragged
  if (!b.dataset.gripLock) b.classList.toggle('grip-left', innerWidth - (spot.left + w) < 140);
  b.classList.toggle('long', h >= innerHeight - 17);   // only a bubble that fills the screen scrolls (the tail of the bubble is no reason for a scrollbar)
  b.style.setProperty('--tx', `${spot.tail}px`);
  b.style.transform = `translate(${spot.left}px,${spot.top}px)`;
  me.raf = requestAnimationFrame(place);
}
function setBubble(kind, node, emotion) {
  if (kind !== 'draft') { clearGhosts(); hideInline(); }   // the dashed draft on the board goes when the draft is decided
  closeBubble(true);
  me.bubble = { kind, node };
  me.host.append(node);
  if (['menu', 'choice', 'thought'].includes(kind)) node.append(me.pill);
  if (emotion) me.koda.set(emotion);
  me.p = { ...me.p, pending: false };
  me.float.classList.remove('pending');
  if (!me.raf) me.raf = requestAnimationFrame(place);
  if (kind === 'answer' || kind === 'draft' || kind === 'thought') me.locked = true;   // the choice counts from the moment Koda starts to think
  paintRisk();
}
export function closeBubble(keepEmotion = false) {
  if (!me.ready) return;
  if (!keepEmotion && me.bubble?.kind === 'draft') { clearGhosts(); hideInline(); me.draftCtl = null; me.patchClear?.(); me.patchClear = null; }
  me.bubble?.node.remove();
  me.bubble = null;
  if (!keepEmotion && !me.busy) { me.koda.set('neutral'); me.risk = recalledRisk(); me.locked = false; }
  paintRisk();
}
/** The answer to a question asked by hand: shown at once while Koda is on screen (muted or not: the question was asked); a hidden Koda keeps it and peeks out of the edge, and it shows when Koda comes back. */
function offer(open, hold) {
  if (me.p.presence === 'visible') { open(); return; }
  me.pending = hold;
  me.p = Presence.ready(me.p);
  paintPresence(false);
  if (!me.p.muted && me.enabled) showKoda();   // not muted: it comes back from its edge by itself, and the bubble opens when it is there
}
function openPending() {
  const p = me.pending;
  me.pending = null;
  me.p = { ...me.p, pending: false };
  paintPresence(false);
  p?.();
}
const openNote = (o, onPick) => setBubble(o.kind ?? 'toast', noteBubble(o, onPick, () => closeBubble()), o.emotion);

function toggleMenuBubble() {
  if (me.p.presence === 'peeking' || me.p.presence === 'hidden') { showKoda(); return; }
  if (me.bubble) { closeBubble(); return; }
  // Koda is working and the bubble was clicked away: clicking Koda shows what it is doing now (with the way to steer or stop), not the menu
  if (me.busy && me.showThought) { me.showThought(); return; }
  if (me.pending) { openPending(); return; }
  ask(currentSubject());
}

/* ---------- the subject: what Koda looks at ---------- */
export function currentSubject() {
  try {
    const looked = lookSubject();   // what Koda was pointed at (a field, a library block, a template, a file...) wins until the line fades
    if (looked) return looked;
    const view = document.querySelector('#vs-source.on') ? 'source' : 'blocks';
    if (view === 'source') {
      const ta = document.querySelector('#se-ta');
      if (ta && ta.selectionStart !== ta.selectionEnd) return codeSubject(ta.value, ta.selectionStart, ta.selectionEnd);
    }
    const id = S.sel;
    const node = id ? flat().nodes.find((n) => n.id === id) : null;
    if (node && !node.proxy && !node.isGroup) return blockSubject(node, flat(), S.tpl);
  } catch { /* no subject */ }
  return null;
}
export /** The editor shows what the song holds (it has finished loading and nothing was typed that is not applied). */
const editorInSync = () => document.querySelector('#se-ta')?.value === S.doc?.text;
export const subjectOfBlock = (id) => { const node = flat().nodes.find((n) => n.id === id); return node ? blockSubject(node, flat(), S.tpl) : null; };
export const subjectOfField = (id, name) => { const node = flat().nodes.find((n) => n.id === id); return node ? fieldSubject(node, name, S.tpl) : null; };
export { codeSubject };

/* ---------- constructing: Koda builds along while a build runs (real: hammer and bricks, preview or a proposal: blueprint and pencil) ---------- */
let constructing = '', variation = 0, lastProgress = -1, rotateTimer = 0;
const progressOf = () => (S.build?.completed?.size || 0) + (S.build?.live ? Object.keys(S.build.live).length : 0);
function nextVariation() { variation = (variation + 1) % 3; me.koda?.setVariant(variation); }
function paintBuild() {
  if (!me.ready || !me.koda || me.p.presence !== 'visible') return;
  const busy = !!(S.build?.running || document.body.classList.contains('ai-busy'));
  const want = !busy ? '' : S.build?.running && S.build.intent !== 'preview' ? 'build' : 'sketch';
  if (want && want === constructing) {
    // every step forward of the build is a new variation (smooth: the pictures fade into each other)
    const now = progressOf();
    if (now !== lastProgress) { lastProgress = now; nextVariation(); clearInterval(rotateTimer); rotateTimer = setInterval(nextVariation, 7000); }
    return;
  }
  if (want === constructing) return;
  // not while Koda is answering, has a bubble open or shows something else of its own
  if (want && (me.busy || me.bubble)) return;
  clearInterval(rotateTimer);
  if (!want) { if (['build', 'sketch'].includes(me.koda.emotion)) me.koda.set('neutral'); constructing = ''; return; }
  constructing = want;
  lastProgress = progressOf();
  variation = Math.floor(Math.random() * 3);   // a different one each build to start with
  me.koda.setVariant(variation);
  me.koda.set(want);
  rotateTimer = setInterval(nextVariation, 7000);   // when nothing moves for a while it still changes now and then
}
subscribe(paintBuild);
// another song: what Koda was working on belongs to the old one, so it is stopped without asking (and an answer that was waiting for the old song is dropped)
let songPath = S.path;
subscribe(() => {
  if (S.path === songPath) return;
  songPath = S.path;
  if (!me.ready) return;
  me.busy?.abort();
  if (me.pending) { me.pending = null; me.p = { ...me.p, pending: false }; paintPresence(false); }
  if (me.bubble && ['thought', 'toast'].includes(me.bubble.kind)) closeBubble();
});

/* ---------- asking ---------- */
/** Ask about a subject (a block, a field or code): the menu, or with mode 'explain' straight the explanation. */
export function ask(subject, mode = 'menu') {
  if (!me.ready) return;
  me.subject = subject;
  if (!me.enabled || me.p.presence !== 'visible') {
    showKoda();
    setTimeout(() => ask(subject, mode), 950);
    return;
  }
  if (mode === 'explain' && subject) { explain(subject); return; }
  setBubble('menu', menuBubble({
    subject: subject ? subjectName(subject) : '', muted: me.p.muted,
    onExplain: () => (subject ? explain(subject) : toast(tx('select.first'))), onDo: () => (subject ? doThis(subject) : toast(tx('select.first'))),
    onHide: () => hideKoda(), onMute: () => muteKoda(),
  }, () => closeBubble()), 'happy');
}
function explain(subject) {
  const local = explainLocally(subject, S.tpl);
  if (!local) { askModel('explain', subject, ''); return; }
  relatedLines(subject, local.text, local.more);
  setBubble('answer', answerBubble({ title: local.title, text: local.text, more: local.more, source: { text: tx('explain.docs'), link: tx('explain.link'), href: docsHref(subject) }, onAsk: followUp(subject, local.text, local.more) }, (v) => { if (v === 'more') askModel('explain', subject, 'Tell me more.'); else closeBubble(); }, () => closeBubble()), 'talk');
}
/** A question about an answer that was just given: the same subject, with what Koda said along, so the model knows what the question is about. */
function followUp(subject, ...said) {
  const context = said.filter(Boolean).join('\n').slice(0, 1500);
  return (question) => askModel('explain', { ...subject, said: context }, question);
}
function doThis(subject) {
  setBubble('choice', choiceBubble({ title: tx('do.title'), text: tx('do.text', { x: subjectName(subject) }), options: [], placeholder: tx('do.placeholder'), submit: tx('do.submit') }, (text) => askModel('run', subject, text), () => closeBubble()), 'asking');
}
/** The buttons after something was done: thanks, and (when the backend can) "hang around", so the next question goes on in the same conversation. */
/** After a draft was kept: thanks, or one more question that goes with what was just done. */
function keptBubble(subject, taskId) {
  const text = tx('kept.text');
  setBubble('answer', answerBubble({ title: '', label: tx('kept.label'), text, buttons: [{ label: tx('answer.thanks'), primary: true, value: 'ok' }, { label: tx('answer.ask'), quiet: true, value: 'ask' }, ...assistantButton(taskId)], onAsk: followUp(subject, text) }, (v) => { if (v === 'open') openInAssistant(taskId); else closeBubble(); }, () => closeBubble()), 'happy');
}
const thanksButtons = () => [{ label: tx('answer.thanks'), primary: true }];
const afterThanks = () => closeBubble();
async function askModel(kind, subject, text, resume) {
  // Koda was asked to hang around: it says so and asks whether this goes on with what was just done
  if (resume === undefined && backend().hangQuick?.()) {
    // asked again right away: no need to confirm, it simply goes on
    return askModel(kind, subject, text, true);
  }
  if (resume === undefined && backend().hanging?.()) {
    openNote({ label: tx('hang.label'), text: tx('hang.text'), emotion: 'happy', buttons: [{ label: tx('hang.go'), primary: true, value: 'go' }, { label: tx('hang.fresh'), quiet: true, value: 'fresh' }] }, (v) => {
      backend().letGo?.();
      closeBubble();
      askModel(kind, subject, text, v === 'go');
    });
    return;
  }
  if (me.busy) {
    setBubble('toast', noteBubble({ kind: 'toast', label: tx('busy.label'), text: tx('busy.text'), buttons: [{ label: tx('busy.stop'), value: 'stop', primary: true }, { label: tx('busy.wait'), value: 'wait', quiet: true }] }, async (v) => { if (v === 'stop') { const running = me.busy; await backend().interrupt?.(); running?.abort(); } closeBubble(); }, () => closeBubble()), 'sleepy');
    return;
  }
  const controller = new AbortController();
  me.busy = controller;
  const clip = (s, n) => (String(s).length > n ? `${String(s).slice(0, n - 1)}…` : String(s));
  const headline = () => tx('thought.looking', { x: subjectName(subject) });
  // the bubble shows what Koda is thinking (the model's own line, as soon as there is one); what it was asked is small below it, and opens to the whole text when that is longer
  const asked = String(text || '').trim();
  const work = asked ? { short: clip(asked, 60), full: asked } : null;
  // while Koda works: extra input steers it, "stop and listen" and a hard stop both end the turn (the person can still say what to do next); the cross only hides the bubble
  const controls = {
    steer: backend().steer ? async (typed) => { try { await backend().steer(typed); toast(tx('thought.steered')); } catch (error) { toast(error.message || String(error)); } } : null,
    listen: async () => { controller.mode = 'listen'; await backend().interrupt?.(); controller.abort(); },
    stop: async () => { controller.mode = 'stop'; await backend().interrupt?.(); controller.abort(); },
  };
  const showThought = () => setBubble('thought', thoughtBubble(headline(), () => closeBubble(), work, controls), 'think');
  showThought();
  me.showThought = showThought;
  // a permission the model asks for (reading a source asset, ...): live mode allows reading an asset by itself, otherwise the bubble asks and the thinking goes on afterwards
  backend().setApprover?.(async (a) => {
    if (me.risk !== 'safe' && a.kind === 'asset') return 'acceptForSession';
    const decision = await new Promise((resolve) => openNote({ label: tx('perm.label'), text: tx('perm.text', { x: a.summary || a.kind }), emotion: 'confused', buttons: [{ label: tx('perm.allow'), primary: true, value: 'acceptForSession' }, { label: tx('perm.deny'), quiet: true, value: 'decline' }] }, (v) => resolve(v || 'decline')));
    if (me.busy === controller) showThought();
    return decision;
  });
  // two ways of thinking: the notes walk along the beam while Koda works on an answer, and with a magnifying glass while it reads the song (the lines glance at the blocks it reads)
  const setThought = (text) => { const dots = me.bubble?.kind === 'thought' ? me.bubble.node.querySelector('.dots') : null; const line = dots?.querySelector('.tline'); if (line) line.textContent = text; };
  let back = 0;
  // what the model is working on, in its own words (the latest line of its thinking), small under the headline
  const onThought = (line) => { const s = String(line).replace(/[*_`#]/g, '').trim(); if (s) setThought(s.length > 150 ? `${s.slice(0, 149)}…` : s); };
  const onGlance = (ids) => {
    glance(ids.filter((id) => id !== (subject.block?.name ?? subject.block)));
    if (me.busy !== controller) return;
    me.koda.set('scan');
    clearTimeout(back);
    back = setTimeout(() => { if (me.busy === controller) me.koda.set('think'); }, 3200);
  };
  try {
    const result = await (kind === 'run' ? backend().run(subject, text, { signal: controller.signal, onGlance, onThought, resume }) : backend().explain(subject, text, { signal: controller.signal, onGlance, onThought, resume }));
    me.busy = null; me.showThought = null;
    if (controller.signal.aborted) { stopped(controller.mode, kind, subject, text); return; }
    offer(() => showResult(kind, subject, result), () => showResult(kind, subject, result));
  } catch (error) {
    me.busy = null; me.showThought = null;
    if (controller.signal.aborted) { stopped(controller.mode, kind, subject, text); return; }
    offer(() => showError(kind, subject, text, error), () => showError(kind, subject, text, error));
  }
}
/** After the person stopped Koda: thanks, continue, or one more thing (with "stop and listen" the field is open at once). The next input goes to a new turn that knows what was being done. */
function stopped(mode, kind, subject, original) {
  if (!mode) { closeBubble(); return; }
  const was = String(original || '').replace(/\s+/g, ' ').slice(0, 400);
  const again = (now) => askModel(kind, subject, `You were working on this request and the user stopped you: "${was}". The user now says: "${now}". Go on from where you were (look at the song as it is now) and take this into account.`);
  const listen = mode === 'listen';
  setBubble('answer', answerBubble({ title: '', label: tx('stopped.label'), text: tx(listen ? 'stopped.listen.text' : 'stopped.text'), askOpen: listen, onAsk: again, buttons: [{ label: tx('stopped.continue'), primary: true, value: 'continue' }, { label: tx('answer.ask'), quiet: true, value: 'ask' }, { label: tx('answer.thanks'), quiet: true, value: 'ok' }] }, (v) => { if (v === 'continue') again(tx('stopped.continue.say')); else closeBubble(); }, () => closeBubble()), 'neutral');
}

/** Koda did something the person should look at in the assistant: the Assistant tab and the conversation open from here. */
const openInAssistant = (taskId) => { closeBubble(); if (taskId) import('./ai.js').then((m) => m.showInAssistant(taskId)); };
const assistantButton = (taskId) => (taskId ? [{ label: tx('open.assistant'), quiet: true, value: 'open' }] : []);

function showVariants(kind, subject, result, said = '') {
  const { drafts, block } = result;
  // the variants of one proposal share a task: it is dropped as a whole, and only when none is chosen (the chosen one is applied and drops it itself)
  const discard = (except = -1) => { if (result.shared) { if (except < 0) result.discard?.(); } else drafts.forEach((d, i) => { if (i !== except) d.discard?.(); }); };
  const hear = async (which) => {
    const ai = await import('./ai.js');
    if (which === null) { ai.stopPreview(); return; }
    if (which === 'original') { if (!(await ai.hearOriginal(block))) toast(tx('variants.nobuild')); return; }
    ai.buildAndListen(drafts[which].taskId, drafts[which].listen || block);
  };
  const items = drafts.map((d) => ({ name: d.name, note: d.note, lines: d.summary.map((c) => c.text) }));
  setBubble('draft', variantsBubble({ items, canOriginal: !!(block && S.build.takes[block]), onHear: hear, said, onAsk: async (question, at, progress) => {
    me.koda?.set('think');
    const opts = { onThought: (line) => progress?.(String(line).replace(/[*_`#]/g, '').trim().slice(0, 150)) };
    try {
      if (backend().continue && drafts[0]?.taskId) {
        // a request on the proposal itself (same working copy): a question is answered, a change gives new variants or a new draft
        const r = await backend().continue(drafts[0].taskId, subject, question, opts);
        if (r.kind === 'variants') { showVariants(kind, subject, r, r.say); return ''; }
        if (r.kind === 'draft') { showResult(kind, subject, r); return ''; }
        return [r.text, r.more].filter(Boolean).join('\n\n');
      }
      const said = drafts.map((d, i) => String.fromCharCode(65 + i) + ': ' + [d.note, ...d.summary.map((c) => c.text)].filter(Boolean).join(' ')).join('\n').slice(0, 1500);
      const r = await backend().explain({ ...subject, said: 'You made these variants (the user is looking at them and is on ' + String.fromCharCode(65 + at) + '):\n' + said }, question, opts);
      return r.text;
    } finally { me.koda?.set('excited'); }
  } }, (v, i) => {
    if (v === 'use') { discard(i); showResult(kind, subject, drafts[i]); } else if (v === 'again') { discard(); askModel('run', subject, result.text || ''); } else { discard(); closeBubble(); }
  }, () => { discard(); closeBubble(); }), 'excited');
}

/** After an answer: faded lines to the blocks that the answer talks about (not the one Koda is already linked to), which stay a few seconds and fade out slowly. */
function relatedLines(subject, ...texts) {
  try {
    const own = subject?.block?.name ?? subject?.block;
    const ids = flat().nodes.map((n) => n.id);
    const found = namedBlocks(texts.filter(Boolean).join(' '), ids).filter((id) => id !== own);
    if (found.length) glance(found, 7000);
  } catch { /* decoration only */ }
}

/** The sequencer editor is open and the proposal changes the sequence kept in the song: only that sequence changed, it goes into the editor and the person applies it; more changed, so the editor must be closed first. */
async function seqIntercept(result) {
  const { leaveSequencerDiscarding, lossOfStaging, sequencerInfo, stageSequence } = await import('./sequencer.js');
  const info = sequencerInfo();
  if (!info?.embedded || result.kind !== 'draft' || !result.proposed) return false;
  const staged = stagedSequence(activeText() ?? '', result.proposed, info.blockId);
  let keepMine = false;
  if (staged) {
    // the new sequence must not quietly take away what the person has in other channels, patterns or rows
    const lost = lossOfStaging(staged.text);
    if (lost) {
      const choice = await new Promise((resolve) => openNote({ kind: 'warn', label: tx('seq.lost.label'), text: tx('seq.lost.text', { x: lost }), emotion: 'confused', buttons: [{ label: tx('seq.keepmine'), primary: true, value: 'mine' }, { label: tx('seq.put'), quiet: true, value: 'go' }, { label: tx('seq.cancel'), quiet: true, value: 'no' }] }, (v) => resolve(v || 'no')));
      if (choice === 'no') { result.discard?.(); closeBubble(); return true; }
      keepMine = choice === 'mine';
    }
    if (!stageSequence(staged.text, keepMine)) return false;
    result.discard?.();
    openNote({ label: tx('seq.staged.label'), text: tx('seq.staged.text'), emotion: 'happy', buttons: [{ label: tx('choice.ok'), primary: true }] }, () => closeBubble());
    return true;
  }
  const go = await new Promise((resolve) => openNote({ kind: 'warn', label: tx('seq.more.label'), text: tx('seq.more.text'), emotion: 'confused', buttons: [{ label: tx('seq.close'), primary: true, value: 'go' }, { label: tx('seq.cancel'), quiet: true, value: 'no' }] }, (v) => resolve(v === 'go')));
  if (!go) { result.discard?.(); closeBubble(); return true; }
  leaveSequencerDiscarding();
  return false;
}

function showResult(kind, subject, result) {
  if (result.kind === 'draft' && result.proposed && document.querySelector('.sqmain')) { seqIntercept(result).then((done) => { if (!done) showResultNow(kind, subject, result); }); return; }
  showResultNow(kind, subject, result);
}

function showResultNow(kind, subject, result) {
  clearGhosts();
  hideInline();
  if (result.kind === 'variants') { showVariants(kind, subject, result); return; }
  if (result.kind === 'draft' && me.risk !== 'safe' && result.apply && !result.fromQuestion) {
    // unsafe: no question, the draft is applied at once, through the same accept as a kept draft (one undo step, in the history); a stale song or a check with an issue falls back to the draft
    if (!result.issue && (result.baseText === undefined || result.baseText === activeText())) { autoApply(result, subject); return; }
    toast(tx('risk.held'));
  }
  if (result.kind === 'draft') {
    try { showGhosts(result.blocks); } catch { /* the board is not shown */ }
    // the decision is the same in the bubble and in the editor: keep it, talk it over, or throw it away
    // a patch of several blocks: the blocks that change are lit on the canvas and the rest steps back; looking at a block of the list points Koda at it; what may be heard plays on hover
    const patch = groupChanges(result.summary).filter((g) => g.block);
    const nodeOf = (name) => [...document.querySelectorAll('.node[data-id]')].find((n) => n.dataset.id === name);
    const clearMarks = () => { document.body.classList.remove('koda-patch'); document.querySelectorAll('.koda-pm').forEach((n) => n.classList.remove('koda-pm', 'koda-pm-gone')); };
    if (patch.length > 1) {
      document.body.classList.add('koda-patch');
      for (const g of patch) { const n = nodeOf(g.block); if (n) n.classList.add('koda-pm', ...(g.kind === 'gone' ? ['koda-pm-gone'] : [])); }
      glance(patch.map((g) => g.block).filter((b) => nodeOf(b)));
    }
    const onFocus = (block) => { if (block && nodeOf(block)) lookAt({ kind: 'block', id: block }); };
    const onHear = (on) => { import('./ai.js').then((m) => m.hearProposal(result.taskId, on)); };
    me.patchClear = () => { clearMarks(); onHear(false); };
    const finish = () => { clearGhosts(); hideInline(); me.draftCtl = null; me.patchClear?.(); me.patchClear = null; };
    // every change has a switch: what is switched off is left out of what is kept
    const off = new Set();
    const base = () => result.baseText ?? activeText() ?? '';
    const chosen = () => textWith(base(), result.proposed, new Set(structuredHunks(base(), result.proposed).map((_, i) => i).filter((i) => !off.has(i))));
    let inlineCtl = null;
    // once "Let's go" is pressed nothing else of the bubble (not Details, not Not quite, not No, not the switches) may be pressed while the change is being put in the song
    let deciding = false;
    const lock = () => { deciding = true; const node = me.bubble?.node; if (node) { node.inert = true; node.classList.add('deciding'); } document.querySelectorAll('.koda-details').forEach((o) => { o.inert = true; }); };
    const keep0 = async () => {
      // the song changed since the draft was made: it would overwrite that change
      if (result.baseText !== undefined && result.baseText !== activeText()) { finish(); result.discard?.(); openNote({ kind: 'warn', label: tx('stale.label'), text: tx('stale.text'), emotion: 'confused', buttons: result.asked ? [{ label: tx('stale.again'), primary: true, value: 'again' }, { label: tx('choice.ok'), quiet: true, value: 'ok' }] : [{ label: tx('choice.ok'), primary: true }] }, (v) => { if (v === 'again' && result.asked) askModel(kind, subject, result.asked); else closeBubble(); }); return; }
      if (result.proposed && off.size) {
        // some changes are switched off: only the others are put in the song (all off: nothing happens)
        const text = chosen();
        finish();
        if (text === base()) { result.discard?.(); openNote({ label: tx('notyet.label'), text: tx('draft.none') }, () => closeBubble()); return; }
        const done = await result.applyPartial?.(text);
        if (done === false) { openNote({ kind: 'error', label: tx('kept.failed.label'), text: tx('kept.failed.text'), emotion: 'sad' }, () => closeBubble()); return; }
        keptBubble(subject, result.taskId);
        return;
      }
      finish();
      const ok = await result.apply?.();
      if (ok === false) { openNote({ kind: 'error', label: tx('kept.failed.label'), text: tx('kept.failed.text'), emotion: 'sad' }, () => closeBubble()); return; }
      keptBubble(subject, result.taskId);
    };
    const keep = async () => { if (deciding) return; lock(); return keep0(); };
    const again = () => { if (deciding) return; finish(); setBubble('choice', choiceBubble({ title: tx('again.title'), text: tx('again.text'), options: [], placeholder: tx('again.placeholder'), submit: tx('do.submit') }, (text) => askModel('run', subject, text), () => closeBubble()), 'asking'); };
    const drop = () => { if (deciding) return; finish(); result.discard?.(); closeBubble(); };
    // in the source editor the suggestion is shown in the text itself (Tab keeps it, Esc throws it away, the little field talks it over)
    const inline = () => { inlineCtl = showInline({ proposed: off.size ? chosen() : result.proposed, onAccept: keep, onReject: drop, onDiscuss: (text) => { hideInline(); result.discard?.(); askModel('run', subject, text); }, onStale: () => {} }); };   // typed in the editor: the suggestion goes and comes back when the editor shows the song again
    me.draftCtl = result.proposed ? { show: inline } : null;
    if (result.proposed && editorOpen() && editorShowsSong() && editorInSync()) inline();
    const onToggle = (hunk, on) => { if (on) off.delete(hunk); else off.add(hunk); if (inlineCtl && inlineShown()) inlineCtl.update(chosen()); };
    let building = null;
    const preview = () => {
      if (deciding || building) return;
      if (off.size) toast(tx('draft.previewAll'));
      // a build indicator with a way out: cancelling stops the wait and nothing is played (the build itself ends in the background)
      const ctl = new AbortController();
      const bar = el('div', { className: 'koda-building', role: 'status' }, [el('span', { className: 'spin', 'aria-hidden': 'true' }), el('span', { textContent: tx('draft.building') }), el('button', { type: 'button', className: 'quiet', textContent: tx('draft.building.cancel'), onclick: () => { ctl.abort(); done(); toast(tx('draft.building.cancelled')); } })]);
      const done = () => { building = null; bar.remove(); };
      building = ctl;
      me.bubble?.node.querySelector('.chgs')?.after(bar);
      import('./ai.js').then((m) => m.buildAndListen(result.taskId, undefined, false, ctl.signal)).finally(() => { if (building === ctl) done(); });
    };
    setBubble('draft', draftBubble({ summary: result.summary, note: result.note, checked: result.checked, canPreview: !!result.taskId, more: result.more, diff: result.diff, applyNow: !!result.fromQuestion, onToggle: result.proposed ? onToggle : null, onFocus, onHear: result.taskId ? onHear : null }, (v) => { if (v === 'keep') keep(); else if (v === 'preview') preview(); else if (v === 'again') again(); else drop(); }, drop), 'excited');
    return;
  }
  relatedLines(subject, result.text, result.more);
  const back = S.undo.length ? [{ label: tx('undo.prev'), quiet: true, value: 'undo' }] : [];
  setBubble('answer', answerBubble({ title: result.title, text: result.text, more: result.more, onAsk: followUp(subject, result.text, result.more), buttons: [{ label: tx('answer.thanks'), value: 'thanks', primary: true }, ...(answerAction(result) ? [{ label: tx('answer.do'), value: 'do', title: answerAction(result) }] : []), { label: tx('answer.more'), value: 'more' }, { label: tx('answer.ask'), value: 'ask' }, ...assistantButton(result.taskId), ...back] }, (v) => { if (v === 'open') openInAssistant(result.taskId); else if (v === 'more') askModel('explain', subject, 'Tell me more.'); else if (v === 'do') askModel('run', { ...subject, said: [result.text, result.more].filter(Boolean).join('\n').slice(0, 1500) }, `${tx('answer.do.ask')} ${result.action}`.trim()); else if (v === 'undo') undoneBubble(subject, false); else closeBubble(); }, () => closeBubble()), 'talk');
}
// an answer that tells the person to press Do that always gets the button, also when the model forgot to give the action sentence
const answerAction = (result) => result.action || (/do that/i.test(String(result.text || '') + ' ' + String(result.more || '')) ? (result.title || 'Apply what the answer suggests') : '');
async function autoApply(result, subject) {
  lockRisk();
  clearGhosts(); hideInline();
  const depth = S.undo.length;
  let ok = false;
  try { ok = (await result.apply()) !== false; } catch { ok = false; }
  if (!ok) { openNote({ kind: 'error', label: tx('kept.failed.label'), text: tx('kept.failed.text'), emotion: 'sad' }, () => closeBubble()); return; }
  const made = S.undo.length > depth || live.did;   // bare metal: Koda's changes are already in the song (live view), as one undo step
  live.did = false;
  const text = [result.note, tx('risk.done.text')].filter(Boolean).join(' ');
  const buttons = [...(made ? [{ label: tx('risk.undo'), primary: true, value: 'undo' }] : []), { label: tx('answer.thanks'), quiet: made, primary: !made, value: 'ok' }, { label: tx('answer.ask'), quiet: true, value: 'ask' }, ...assistantButton(result.taskId)];
  setBubble('answer', answerBubble({ title: '', label: tx('risk.done.label'), text, buttons, onAsk: followUp(subject, text) }, async (v) => { if (v === 'undo') await undoneBubble(subject, made); else if (v === 'open') openInAssistant(result.taskId); else closeBubble(); }, () => closeBubble()), 'happy');
}
/** After an undo: what was undone, and what can be done next: thanks, a question, do it again (when Koda's own change was undone), or one more step back. */
async function undoneBubble(subject, redoable) {
  await undo();
  const text = tx('risk.undone');
  const buttons = [{ label: tx('answer.thanks'), primary: true, value: 'ok' }, { label: tx('answer.ask'), quiet: true, value: 'ask' }, ...(redoable ? [{ label: tx('undo.again'), quiet: true, value: 'redo' }] : []), ...(S.undo.length ? [{ label: tx('undo.prev'), quiet: true, value: 'undo' }] : [])];
  setBubble('answer', answerBubble({ title: '', label: tx('undo.label'), text, buttons, onAsk: followUp(subject, text) }, async (v) => { if (v === 'undo') await undoneBubble(subject, false); else if (v === 'redo') { await redo(); toast(tx('undo.redone')); closeBubble(); } else closeBubble(); }, () => closeBubble()), 'neutral');
}
function showError(kind, subject, text, error) {
  if (error instanceof KodaUnavailable) { openNote({ kind: 'warn', label: tx('notyet.label'), text: error.message, emotion: 'confused', buttons: [{ label: tx('choice.ok'), primary: true }] }, () => closeBubble()); return; }
  if (error instanceof KodaBusy) { openNote({ kind: 'toast', label: tx('busy.label'), text: error.message, emotion: 'sleepy' }, () => closeBubble()); return; }
  openNote({ kind: 'error', label: tx('failed.label'), text: error?.message || tx('failed.text'), emotion: 'sad', buttons: [{ label: tx('failed.again'), primary: true, value: 'again' }, { label: tx('failed.close'), quiet: true, value: 'close' }] }, (v) => { if (v === 'again') askModel(kind, subject, text); else closeBubble(); });
}

/* ---------- keyboard ---------- */
function onKey(e) {
  if (e.key === 'Escape' && me.bubble && !document.querySelector('dialog[open]')) { e.preventDefault(); closeBubble(); return; }
  if (e.key === 'Tab') document.body.dataset.kb = '1';
  if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && e.key.toLowerCase() === 'k') {
    e.preventDefault(); e.stopPropagation();
    if (!me.enabled || me.p.presence !== 'visible') { toggleKoda(); setTimeout(() => ask(currentSubject()), 1000); return; }
    if (me.bubble) { closeBubble(); return; }
    if (me.busy && me.showThought) { me.showThought(); return; }   // working: what Koda is doing now, not the menu
    ask(currentSubject());
  }
}
