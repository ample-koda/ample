// Playback modes of a prepared performance slot: loop, one-shot or momentary. The voice lifetime lives in this scheduler; launch quantization is independent of the mode.
// @ample:performance-oneshots
export const MODES = ['loop', 'oneshot', 'momentary'];
export const normalizeMode = (mode) => (MODES.includes(mode) ? mode : 'loop');

// HOOK (issue 266 Performance workspace): the workspace creates one scheduler, calls launch/release/stopAll from its pads and
// reads remaining()/state() for the display. `startVoice(slot, when)` must return a handle with stop(); it is injected by the caller.
export function createLaunchScheduler({ startVoice, quantize = (t) => t, now = () => 0 }) {
  const slots = new Map();   // id -> { mode, length, voice, queued, startAt, held }
  const get = (id) => { if (!slots.has(id)) slots.set(id, { mode: 'loop', length: 0, voice: null, queued: null, startAt: 0, held: false }); return slots.get(id); };
  const begin = (id, s, when) => { s.voice?.stop?.(); s.voice = startVoice({ id, mode: s.mode }, when); s.startAt = when; s.queued = null; };
  return {
    configure(id, { mode, length = 0 }) { const s = get(id); s.mode = normalizeMode(mode); s.length = Math.max(0, Number(length) || 0); },
    // press: the start is quantized for every mode. A rapid retrigger replaces a queued start instead of stacking.
    launch(id, t = now()) {
      const s = get(id), when = quantize(t);
      s.held = true;
      if (s.mode === 'loop' && s.voice && !s.queued) { s.voice.stop?.(); s.voice = null; return { action: 'stop' }; }   // loop pads toggle
      s.queued = when;
      if (when <= t) begin(id, s, when);
      return { action: 'start', at: when };
    },
    // Called by the clock; starts every queued voice that is due.
    tick(t = now()) { for (const [id, s] of slots) if (s.queued !== null && s.queued <= t) begin(id, s, s.queued); for (const [id, s] of slots) if (s.voice && s.mode === 'oneshot' && s.length && t >= s.startAt + s.length) this.end(id); },
    // release: only momentary voices stop; a note-off before the queued start cancels the start.
    release(id) { const s = get(id); s.held = false; if (s.mode !== 'momentary') return; s.queued = null; s.voice?.stop?.(); s.voice = null; },
    end(id) { const s = get(id); s.voice?.stop?.(); s.voice = null; },
    // input disconnect: every held momentary voice stops, bed loops and one-shots keep running.
    disconnect() { for (const id of slots.keys()) this.release(id); },
    stopAll() { for (const s of slots.values()) { s.voice?.stop?.(); s.voice = null; s.queued = null; s.held = false; } },
    // seconds until a one-shot finishes (null when not playing or not a one-shot)
    remaining(id, t = now()) { const s = slots.get(id); return s && s.voice && s.mode === 'oneshot' && s.length ? Math.max(0, s.startAt + s.length - t) : null; },
    state(id) { const s = slots.get(id); return s ? { mode: s.mode, playing: Boolean(s.voice), queued: s.queued !== null } : { mode: 'loop', playing: false, queued: false }; },
  };
}
