// Live Preview: the loop player as an engine that takes its audio context and its output as parameters (so Studio and Remix share it, and node tests it
// with a fake context). It loops the segments of one entry { buffer, segments } one lap at a time with a short look-ahead, so a new entry can come in exactly
// at the loop boundary (or at once) and a cut never clicks (5 ms fades). What belongs to one page (the registration with the header player, the download of a
// take) stays in that page. The arithmetic (segments, preview time and take time) is listen-segments.mjs. @ample:listen-preview
import { resumeUnlessCancelled } from './listen-cancel.mjs';
import { heardLength, takeTimeAt } from './listen-segments.mjs';

const FADE = 0.005, LOOKAHEAD = 0.12, TICK = 30;

/**
 * @param {object} deps
 * @param {() => any} deps.context the audio context (created when it is first needed)
 * @param {(context: any) => any} deps.output the node every sound goes into
 * @param {() => void} [deps.beforeStart] called when the sound starts from silence (Studio stops its other players there)
 * @param {(fn: () => void, ms: number) => any} [deps.setTimer]
 * @param {(id: any) => void} [deps.clearTimer]
 */
export function createLoopPlayer(deps) {
  const setTimer = deps.setTimer ?? ((fn, ms) => setInterval(fn, ms)), clearTimer = deps.clearTimer ?? ((id) => clearInterval(id));
  // `entry`/`lapStart` are the lap that is AUDIBLE now; `next` is the lap that is already scheduled (look-ahead) and becomes audible at `next.start`
  const P = { entry: null, next: null, pending: null, playing: false, lapStart: 0, scheduled: [], timer: 0, loading: false, version: 0, onStop: null };
  const listeners = new Set();

  /** Makes the scheduled lap the audible one when its time has come (the clock decides, not the timer). Returns true when the entry changed. */
  function promote(now) {
    if (!P.next || now < P.next.start) return false;
    const changed = P.next.entry !== P.entry;
    P.entry = P.next.entry; P.lapStart = P.next.start; P.next = null;
    return changed;
  }
  const waiting = () => P.pending ?? (P.next && P.next.entry !== P.entry ? P.next.entry : null);

  function state() {
    if (P.playing) promote(deps.context().currentTime);
    const entry = P.entry;
    if (!P.playing || !entry) return { playing: false, loading: P.loading, takeTime: null, pending: !!waiting(), remaining: 0, entry };
    const context = deps.context(), length = heardLength(entry.segments);
    const into = Math.max(0, context.currentTime - P.lapStart);
    return {
      playing: true, loading: P.loading, takeTime: takeTimeAt(entry.segments, into), previewTime: into % Math.max(length, 1e-6),
      pending: !!waiting(), remaining: Math.max(0, length - into), entry,
    };
  }
  const emit = () => listeners.forEach((f) => f(state()));

  /** One lap of an entry from `at` (context time), starting `offset` seconds into the heard loop. */
  function scheduleLap(entry, at, offset = 0) {
    const context = deps.context(), out = deps.output(context);
    let time = at, skip = offset;
    for (const segment of entry.segments) {
      const length = segment.to - segment.from;
      if (skip >= length) { skip -= length; continue; }
      const source = context.createBufferSource(), gain = context.createGain();
      source.buffer = entry.buffer;
      source.connect(gain).connect(out);
      const play = length - skip;
      gain.gain.setValueAtTime(0, time);
      gain.gain.linearRampToValueAtTime(1, time + Math.min(FADE, play / 4));
      gain.gain.setValueAtTime(1, Math.max(time + Math.min(FADE, play / 4), time + play - Math.min(FADE, play / 4)));
      gain.gain.linearRampToValueAtTime(0, time + play);
      source.start(time, segment.from + skip, play);
      P.scheduled.push({ source, end: time + play });
      time += play; skip = 0;
    }
    return time;
  }

  function tick() {
    if (!P.playing) return;
    const context = deps.context();
    P.scheduled = P.scheduled.filter((s) => s.end > context.currentTime - 0.5);
    if (promote(context.currentTime)) emit();   // the lap that was scheduled is audible now: the entry, the take time and the marks change here, not earlier
    if (P.next) return;
    const lapEnd = P.lapStart + heardLength(P.entry.segments);
    if (lapEnd - context.currentTime < LOOKAHEAD + TICK / 1000) {
      // the next lap: the waiting take (swap at the boundary), else the same one again; it is only scheduled here, the audible lap stays until the boundary
      const entry = P.pending ?? P.entry;
      P.pending = null;
      scheduleLap(entry, lapEnd);
      P.next = { entry, start: lapEnd };
    }
  }

  function silenceAll() {
    const context = deps.context();
    for (const s of P.scheduled) { try { s.source.stop(context.currentTime + FADE); } catch { /* already ended */ } }
    P.scheduled = [];
  }

  /**
   * Plays an entry { buffer, segments }. When something plays: timing 'boundary' waits for the end of the current loop, 'now' swaps at once at the same
   * place in the loop. `signal` is the token of the session that hands the entry in: when Stop (or a new Listen) cancelled it before or while the context
   * resumed, nothing is queued or played and the player is left alone. Resolves false in that case.
   */
  async function playEntry(entry, { timing = 'boundary', onStop = null, signal = null } = {}) {
    const context = deps.context();
    if (!(await resumeUnlessCancelled(context, signal))) return false;
    if (P.playing) promote(context.currentTime);
    if (P.playing && timing === 'boundary') { P.pending = entry; emit(); return true; }
    const into = P.playing ? Math.max(0, context.currentTime - P.lapStart) : 0;
    if (!P.playing) { P.version++; deps.beforeStart?.(); }
    silenceAll();
    P.entry = entry; P.next = null; P.pending = null; P.playing = true; P.onStop = onStop;
    const offset = into % Math.max(heardLength(entry.segments), 1e-6), start = context.currentTime + 0.03;
    P.lapStart = start - offset;
    scheduleLap(entry, start, offset);
    clearTimer(P.timer); P.timer = setTimer(tick, TICK);
    emit();
    return true;
  }

  return {
    state,
    playEntry,
    /** Brings a waiting take in at once (Apply now). */
    applyPendingNow() { const next = waiting(); if (next) { P.pending = null; return playEntry(next, { timing: 'now', onStop: P.onStop }); } return Promise.resolve(); },
    setLoading(on) { P.loading = on; emit(); },
    /** Stops the sound (and tells the owner of the session, unless it asked for the stop itself). */
    stop({ quiet = false } = {}) {
      if (!P.playing && !P.pending) return;
      clearTimer(P.timer); P.timer = 0;
      silenceAll();
      P.playing = false; P.pending = null; P.next = null;
      const onStop = P.onStop; P.onStop = null;
      emit();
      if (!quiet) onStop?.();
    },
    isPlaying: () => P.playing,
    currentTake: () => P.entry?.take ?? null,
    onChange(f) { listeners.add(f); return () => listeners.delete(f); },
  };
}
