// Playback modes of a prepared performance slot: loop, one-shot or momentary. The voice lifetime lives in this scheduler; launch quantization is independent of the mode.
// @ample:performance-oneshots
export const MODES = ['loop', 'oneshot', 'momentary'];
export const normalizeMode = (mode) => (MODES.includes(mode) ? mode : 'loop');

// HOOK (issue 266 Performance workspace): the workspace creates one scheduler, calls launch/release/disconnect/stopAll from its pads and
// reads remaining()/state() for the display.
//
// Units: every time is in seconds on ONE clock (the AudioContext clock of the launch engine in web/app/performance.js). `now()` reads it,
// `quantize(t)` returns the next grid time at or after t (for the engine: nextBoundary(t, origin, beatSec, beatsPerBar, 'beat' | 'bar')).
//
// `startVoice({ id, mode }, when)` is called at LAUNCH time with the future grid time `when`, so the audio engine can schedule the start sample-accurately
// (the same shape as the engine's play(clip, when, lane)). It must return a handle with
//   stop(at?)  stop at the clock time `at` (immediately without it)
//   cancel()   optional: withdraw a start that has not happened yet (falls back to stop())
//   ended      optional: set to true by the handle when the voice finished by itself
export function createLaunchScheduler({ startVoice, quantize = (t) => t, now = () => 0 }) {
  const slots = new Map();   // id -> { mode, length, voice, next, held }; voice/next: { handle, when, endAt }
  const get = (id) => { if (!slots.has(id)) slots.set(id, { mode: 'loop', length: 0, voice: null, next: null, held: false }); return slots.get(id); };
  const cancelHandle = (h) => { if (h.cancel) h.cancel(); else h.stop?.(); };
  // Stops a voice at `at`, unless it already ends earlier.
  const stopAt = (v, at) => { if (v.endAt !== null && v.endAt <= at) return; v.handle.stop?.(at); v.endAt = at; };
  // Moves a queued voice whose time has come to playing and forgets voices that ended. `strict`: a start exactly at `t` has not happened yet.
  const settle = (s, t, strict = false) => {
    if (s.next && (strict ? s.next.when < t : s.next.when <= t)) { s.voice = s.next; s.next = null; }
    if (s.voice && ((s.voice.endAt !== null && s.voice.endAt <= t) || s.voice.handle.ended === true)) s.voice = null;
  };
  return {
    configure(id, { mode, length = 0 }) { const s = get(id); s.mode = normalizeMode(mode); s.length = Math.max(0, Number(length) || 0); },
    // press: the start is quantized for every mode. A rapid retrigger replaces a queued start instead of stacking; a pad that is still held (key repeat) is ignored.
    launch(id, t = now()) {
      const s = get(id); settle(s, t);
      if (s.mode === 'momentary') { if (s.held) return { action: 'held' }; s.held = true; }
      const q = quantize(t), when = Number.isFinite(q) && q >= t ? q : t;
      if (s.mode === 'loop' && s.voice && !s.next && s.voice.endAt === null) { stopAt(s.voice, when); return { action: 'stop', at: when }; }   // loop pads toggle, at the grid
      if (s.next) { cancelHandle(s.next.handle); s.next = null; }
      const handle = startVoice({ id, mode: s.mode }, when), v = { handle, when, endAt: null };
      if (s.mode === 'oneshot' && s.length > 0) { v.endAt = when + s.length; handle.stop?.(v.endAt); }   // the length counts from the audible start, not from the press
      if (s.voice) stopAt(s.voice, when);   // replaced at the same boundary
      s.next = v; settle(s, t);
      return { action: 'start', at: when };
    },
    // Moves due voices on; the audio is already scheduled, this only keeps state()/remaining() honest. Call from the UI timer.
    tick(t = now()) { for (const s of slots.values()) settle(s, t); },
    // release: only momentary voices stop. A release before the queued start, or exactly on it, cancels the start (it would be zero long).
    release(id, t = now()) {
      const s = slots.get(id); if (!s) return false;
      s.held = false;
      if (s.mode !== 'momentary') return false;
      settle(s, t, true);
      let did = false;
      if (s.next) { cancelHandle(s.next.handle); s.next = null; did = true; }
      if (s.voice) { stopAt(s.voice, t); did = true; }
      return did;
    },
    end(id, t = now()) { const s = slots.get(id); if (s?.voice) stopAt(s.voice, t); },
    // input disconnect: every held momentary pad is released; bed loops and one-shots keep running.
    disconnect(t = now()) { for (const [id, s] of slots) if (s.mode === 'momentary' && s.held) this.release(id, t); },
    // Immediate: cancels every queued start and stops every voice.
    stopAll() {
      for (const s of slots.values()) {
        if (s.next) { cancelHandle(s.next.handle); s.next = null; }
        if (s.voice) { s.voice.handle.stop?.(); s.voice = null; }
        s.held = false;
      }
    },
    // seconds until a one-shot has finished; after a retrigger that is the new run (including the wait for its start). null when not a one-shot with a length, or nothing to finish.
    remaining(id, t = now()) {
      const s = slots.get(id); if (!s || s.mode !== 'oneshot' || !(s.length > 0)) return null;
      settle(s, t);
      const v = s.next || s.voice;
      return v && v.endAt !== null ? Math.max(0, v.endAt - t) : null;
    },
    state(id, t = now()) {
      const s = slots.get(id); if (!s) return { mode: 'loop', playing: false, queued: false, startsAt: null, endsAt: null };
      settle(s, t);
      return { mode: s.mode, playing: Boolean(s.voice), queued: Boolean(s.next), startsAt: s.next ? s.next.when : null, endsAt: s.voice ? s.voice.endAt : null };
    },
  };
}
