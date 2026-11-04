// Koda moves with the music: while anything plays in AMPLE (a preview, a build, a sample) Koda starts to sway on the beat, and settles when it stops. A Koda that does nothing bounces and leans on each beat; a
// busy one (thinking, a bubble open) keeps to its work and only sways a little. The beats come from the level of the output (lib/koda-vibe.mjs). Nothing runs while Koda is not shown. @ample:koda
import { outputBands, outputLevels } from './playback-volume.js';
import { createVibe, easeFast, stepVibe, vibePose } from '../lib/koda-vibe.mjs';

const state = createVibe();
const BARS = 9;
const eq = new Array(BARS).fill(0);
let aura = null, ring = null, bars = [], shownLevel = 0, ringAt = 0;
// the face has its own, slower clock: now and then a moment of another face (wide open, in love), now and then a short burst where it follows the beats, in between a calm smile
const face = { nextAt: 0, until: 0, burstUntil: 0, wide: false };

/** The neon behind Koda: a soft glow in the colours of the Jam window and a row of equalizer bars, both following what plays. */
function ensureAura() {
  if (aura || !ctl?.koda?.host) return;
  aura = document.createElement('div');
  aura.className = 'aura';
  aura.setAttribute('aria-hidden', 'true');
  aura.innerHTML = '<div class="glow"></div><div class="ring"></div><div class="eq">' + '<i></i>'.repeat(BARS) + '</div>';
  ctl.koda.host.prepend(aura);
  bars = [...aura.querySelectorAll('.eq i')];
  ring = aura.querySelector('.ring');
}
let ctl = null, raf = 0, prev = 0, applied = false, vibeEmotion = false;

let controlSource = null;
/** A control that makes Koda sway along while the song plays (a function that gives 0 to 1 now, or null). */
export const setKodaControl = (source) => { controlSource = source; };

/** The pulse of the last beat, 0..1 (for the lines that glow with the music). */
export const vibePulse = () => (state.active ? state.pulse : 0);
export const vibeState = () => ({ active: state.active, amp: +state.amp.toFixed(2) });

const reduced = () => { try { return matchMedia('(prefers-reduced-motion: reduce)').matches; } catch { return false; } };

/** ctl: { koda, shown(): bool, busy(): bool, quiet(): bool, linked(): the id of the block Koda is looking at, or null } (koda is the picture, quiet is true when nothing is open or asked). */
export function initVibe(next) {
  ctl = next;
  wake();
}

let settleDeco = 0, settleFace = 0;

/** The music stopped: the body is still at once, but the decoration (the second note) stays a moment and the smile stays a few seconds, softly, before Koda goes back to rest. */
function clear() {
  if (!ctl?.koda) return;
  const { svg, all } = ctl.koda;
  if (applied) {
    all.style.transform = ''; svg.style.transform = ''; applied = false;
    svg.dataset.vibe = 'out';
    clearTimeout(settleDeco);
    settleDeco = setTimeout(() => { if (svg.dataset.vibe === 'out') delete svg.dataset.vibe; }, 2200);
  }
  if (aura) { aura.style.transition = 'opacity .9s ease-out'; aura.style.opacity = '0'; }
  face.nextAt = 0;
  if (vibeEmotion) {
    vibeEmotion = false;
    if (['happy', 'excited', 'love'].includes(ctl.koda.emotion)) {
      ctl.koda.set('happy');
      clearTimeout(settleFace);
      settleFace = setTimeout(() => { if (ctl.koda.emotion === 'happy' && !ctl.busy() && ctl.quiet()) ctl.koda.set('neutral'); }, 3500);
    }
  }
}

/** A new dance starts while Koda is still settling: the timers of the settling are dropped. */
function startOver() { clearTimeout(settleDeco); clearTimeout(settleFace); if (aura) aura.style.transition = 'none'; }

function faceStep(now) {
  const koda = ctl.koda;
  if (!face.nextAt) { face.nextAt = now + 1200; face.until = 0; face.burstUntil = 0; }
  if (now >= face.nextAt) {
    const pick = Math.random();
    // a short burst: the mouth follows the beats while the eyes stay as they are (eyes that open and close with every beat look like flicker)
    if (pick < 0.4) { koda.set('happy'); face.burstUntil = now + 1800; face.until = 0; }
    // a moment with another face, long enough to see it
    else { koda.set(pick < 0.75 ? 'excited' : 'love'); face.until = now + 1700 + Math.random() * 1300; face.burstUntil = 0; }
    face.nextAt = now + 2200 + Math.random() * 2600;
  }
  if (now < face.burstUntil) { if (state.onset) { face.wide = !face.wide; koda.setMouth(face.wide ? 'excited' : 'happy'); } }
  else if ((face.until && now >= face.until) || (face.burstUntil && now >= face.burstUntil)) { face.until = 0; face.burstUntil = 0; koda.set('happy'); }
}

/** How loud the own sound of a block is now (0..1), from the play lights that already follow it; null when the lights are not showing which blocks sound (then the whole output is used). */
function ownLevel(id) {
  try {
    if (!document.querySelector('.node.fx-on')) return null;
    const node = document.querySelector('.node[data-id="' + CSS.escape(id) + '"]');
    if (!node || !node.classList.contains('fx-on')) return 0;
    const v = parseFloat(node.style.getPropertyValue('--fx-lvl'));
    return Number.isFinite(v) ? Math.min(1, v) : 0.5;
  } catch { return null; }
}

function paintAura(mode, level, own = null) {
  ensureAura();
  if (!aura) return;
  // linked to a block: the equalizer and the glow show only that block (the play lights give its level, not its spectrum, so the bars share the level with a moving shape)
  const bands = own === null ? outputBands(BARS) : Array.from({ length: BARS }, (_, i) => own * (0.45 + 0.55 * Math.abs(Math.sin(state.t / 230 + i * 1.9))));
  if (own !== null) level = own * 0.6;
  for (let i = 0; i < BARS; i++) {
    eq[i] += (bands[i] - eq[i]) * (bands[i] > eq[i] ? 0.6 : 0.18);
    bars[i].style.transform = 'scaleY(' + (0.08 + eq[i] * 0.92).toFixed(3) + ')';
  }
  shownLevel += (level - shownLevel) * 0.3;
  aura.style.opacity = String(Math.min(1, state.amp * (own !== null ? (own > 0.02 ? 1 : 0.3) : mode === 'soft' ? 0.55 : 1)).toFixed(3));
  aura.style.setProperty('--lvl', Math.min(1, shownLevel * 1.6).toFixed(3));
  // the glow is light, not movement, so it follows every beat (the colour sways with the lean, a ring goes out on the beat, at most about twice a second)
  const lit = own === null || own > 0.05;   // a linked block that is quiet gets no beat
  aura.style.setProperty('--pulse', (lit ? state.pulse : 0).toFixed(3));
  aura.style.setProperty('--lean', state.lean.toFixed(3));
  if (lit && state.onset && ring && state.t - ringAt > 420 && !matchMedia('(prefers-reduced-motion: reduce)').matches) { ringAt = state.t; ring.animate([{ transform: 'scale(.62)', opacity: 0.75 }, { transform: 'scale(1.2)', opacity: 0 }], { duration: 520, easing: 'cubic-bezier(.2,.7,.3,1)' }); }
}

function frame(now) {
  raf = 0;
  if (!ctl || document.hidden) { prev = 0; return; }
  const dt = Math.min(64, prev ? now - prev : 16);
  prev = now;
  const shown = ctl.shown() && !reduced();
  let level = 0;
  if (shown) { const l = outputLevels(); level = Math.max(l.left, l.right); if (controlSource) level = Math.max(level, 0.8 * controlSource()); }
  stepVibe(state, level, dt);
  if (!shown) { clear(); }
  else if (state.amp > 0.01) {
    const { svg, all } = ctl.koda;
    if (!applied) startOver();
    // linked to a block, Koda follows the sound of that block (equalizer, glow) and only sways a little with the rest
    const link = ctl.linked?.() ?? null;
    const own = link ? ownLevel(link) : null;
    const mode = ctl.busy() || !ctl.quiet() || link ? 'soft' : 'full';
    const pose = vibePose(state, mode);
    if (mode === 'full') {
      svg.dataset.vibe = 'full';
      all.style.transform = `translateY(${pose.lift.toFixed(2)}px) scale(${(1 + pose.squash).toFixed(3)},${(1 - pose.squash).toFixed(3)}) rotate(${pose.lean.toFixed(2)}deg)`;
      svg.style.transform = '';
      // a party face: smiling eyes, and now and then (every fourth beat) the mouth wide open as if singing along
      if (ctl.koda.emotion === 'neutral') { ctl.koda.set('happy'); vibeEmotion = true; }
      if (vibeEmotion) faceStep(state.t);
    } else {
      svg.dataset.vibe = 'soft';
      all.style.transform = '';
      svg.style.transform = `translateY(${(pose.lift / 6).toFixed(2)}px) rotate(${pose.lean.toFixed(2)}deg)`;
    }
    applied = true;
    paintAura(mode, level, own);
  } else if (applied) clear();
  // wake again at once while something moves or plays; otherwise look about four times a second whether music has started
  if (!shown) { prev = 0; setTimeout(wake, 500); return; }
  if (state.amp > 0.01 || state.active || level > 0.005) raf = requestAnimationFrame(frame);
  else { prev = 0; setTimeout(wake, 250); }
}
function wake() { if (!raf && ctl && !document.hidden) raf = requestAnimationFrame(frame); }
if (typeof document !== 'undefined') document.addEventListener('visibilitychange', () => { prev = 0; wake(); });
