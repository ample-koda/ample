// The one AudioContext of the page (local-preview chunk 1.2).
// Everything that plays or measures live (Jam, keys, note audition, the live chain, the take player's meters)
// shares this context, so there is one clock, one output and one latency to report.
// Cases that must own a context (recording at the microphone's rate, a context that is closed after a one-off decode) still create their own.

const FALLBACK_RATE = 48000;
let context = null, master = null, armed = false;

const Context = () => (typeof window === 'undefined' ? null : window.AudioContext || window.webkitAudioContext || null);

/** True when this browser can make an AudioContext at all. */
export const hasAudio = () => !!Context();

/** The shared context, created lazily with the low-latency hint. Call resumeOnGesture() from a click or key handler to make it run. */
export function getContext() {
  if (!context || context.state === 'closed') {
    const Ctor = Context();
    if (!Ctor) throw new Error('Web Audio is unavailable in this browser.');
    context = new Ctor({ latencyHint: 'interactive' });
    master = null;
  }
  return context;
}

/** Create-or-get the shared context and resume it; call synchronously inside a user gesture. */
export function resumeOnGesture() { const ctx = getContext(); if (ctx.state !== 'running') ctx.resume(); return ctx; }

/** The sample rate of the shared context (the rate of every parity comparison with OfflineAudioContext). */
export const sampleRate = () => (context ? context.sampleRate : FALLBACK_RATE);

/** The shared master gain node that connects to the destination; sketch synths and takes may connect here. */
export function masterBus() {
  const ctx = getContext();
  if (!master) { master = ctx.createGain(); master.connect(ctx.destination); }
  return master;
}

/** Reported latency of the shared context in seconds: base (processing) and output (device, 0 where the browser does not say, e.g. Safari). */
export function outputLatency() {
  const ctx = getContext();
  return { base: ctx.baseLatency || 0, output: ctx.outputLatency || 0, total: (ctx.baseLatency || 0) + (ctx.outputLatency || 0) };
}

/** Resume the shared context on the first pointer or key of the page, so the first key press already sounds. */
export function armGestureResume() {
  if (armed || typeof window === 'undefined') return;
  armed = true;
  const unlock = () => { try { resumeOnGesture(); } catch { /* no audio available */ } };
  for (const type of ['pointerdown', 'keydown']) window.addEventListener(type, unlock, { once: true, capture: true });
}
