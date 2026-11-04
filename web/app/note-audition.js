// Single-note audition for the piano rolls (plan MLS03): a key, a drawn note or a moved note sounds at once.
// The configured local instrument is used when its voice is cached; until then the sketch voice sounds (never silent) and the real voice
// is rendered in the background for the next trigger. Decisions live in web/lib/clientside-note-preview.mjs.

import { flat } from './store.js';
import { createRollPreview } from './roll-preview.js';
import { liveAudioContext, playSketchEvent, audioLatencyInfo } from './live-play.js';
import { playbackOutput } from './playback-volume.js';
import { eventsInWindow } from '../lib/live-synth.mjs';
import { PLAYBACK_LEVEL, auditionSeconds, createLatencyProbe, createVoicePool, previewBadge, previewLevel, warmOrder } from '../lib/clientside-note-preview.mjs';

/**
 * One audition per open roll. `bpm()` and `ppq` give the timing; `onBadge({approximate, label})` is told what is heard.
 * trigger(note, { mono, startedAt }) returns a voice id (release it with release(id)); `startedAt` is performance.now() of the gesture, for the latency probe.
 */
export function createNoteAudition({ clipId, ppq, bpm, onBadge = () => {} }) {
  const preview = createRollPreview(clipId, ppq, () => {});
  const pool = createVoicePool(), probe = createLatencyProbe();
  let master = null, preparing = null, readyAt = 0, disposed = false, lastBadge = '';
  const out = (ctx) => {
    if (!master) {
      master = ctx.createDynamicsCompressor();
      const level = ctx.createGain();
      level.gain.value = previewLevel(PLAYBACK_LEVEL);
      master.connect(level).connect(playbackOutput(ctx));
    }
    return master;
  };
  const refresh = () => {
    const now = performance.now();
    if (preparing || now - readyAt < 400) return;
    preparing = preview.prepare().catch(() => {}).finally(() => { preparing = null; readyAt = performance.now(); });
  };
  const badge = (choice, real) => { const b = previewBadge(choice, real); if (b.label !== lastBadge) { lastBadge = b.label; onBadge(b); } };
  function trigger(note, { mono = false, startedAt = performance.now(), heldMs = 0 } = {}) {
    if (disposed || !note) return 0;
    const ctx = liveAudioContext();
    refresh();
    const length = Math.max(1, Math.round(auditionSeconds(heldMs) * bpm() / 60 * ppq));
    const row = { ...note, at: 0, length: Math.max(note.length || 0, length) };
    const events = eventsInWindow(preview.transform([row]), { ppq, bpm: bpm(), from: 0, to: 1e9, program: note.program ?? 0, channel: note.channel ?? 0 });
    if (!events.length) return 0;
    const at = ctx.currentTime + 0.005, voiceOut = ctx.createGain();
    voiceOut.connect(out(ctx));
    let real = true;
    for (const event of events) {
      event.duration = Math.max(event.duration, auditionSeconds(heldMs));
      if (!preview.schedule(ctx, voiceOut, event, at, bpm())) { real = false; playSketchEvent(ctx, voiceOut, event, at); }
    }
    const duration = events[0].duration;
    const choice = preview.choice;
    badge(choice, real);
    probe.record(performance.now() - startedAt, real);
    const id = pool.start({
      stop() {
        const t = ctx.currentTime;
        voiceOut.gain.cancelScheduledValues(t); voiceOut.gain.setValueAtTime(voiceOut.gain.value, t); voiceOut.gain.linearRampToValueAtTime(0, t + 0.05);
        setTimeout(() => voiceOut.disconnect(), 120);
      },
    }, { mono });
    setTimeout(() => { pool.ended(id); voiceOut.disconnect(); }, (duration + 12) * 1000);
    return id;
  }
  /** Render the pitches the clip uses, then a window around the viewport, in idle time (two renders at a time, see roll-preview). */
  function warm(used, viewTop, viewBottom, velocity = 100) {
    const ctx = liveAudioContext(), todo = warmOrder(used, viewTop, viewBottom);
    const step = async () => {
      if (disposed || !todo.length) return;
      await preparing;
      const pitch = todo.shift();
      await preview.warm(ctx, { pitch, velocity, length: ppq }, bpm());
      (window.requestIdleCallback || setTimeout)(step);
    };
    refresh();
    (window.requestIdleCallback || setTimeout)(step);
  }
  /** Before a clip is played: renders the notes it really has (pitch, velocity and length each count), so they sound with the real instrument and not with the sketch voice. */
  async function warmClip(rows) {
    if (disposed) return;
    const ctx = liveAudioContext(), seen = new Set(), jobs = [];
    refresh();
    try { await preparing; } catch { /* the sketch voice plays */ }
    for (const note of preview.transform(rows).slice(0, 4000)) {
      const key = `${note.pitch}:${note.velocity}:${Math.round(note.length)}:${note.program ?? ''}`;
      if (seen.has(key) || seen.size >= 240) continue;
      seen.add(key);
      jobs.push(preview.warm(ctx, note, bpm()));
    }
    await Promise.allSettled(jobs.filter(Boolean));
  }
  return {
    trigger, warm, warmClip, refresh, preview,
    release: (id) => pool.release(id),
    cut: () => pool.cutAll(),
    latency: () => ({ real: probe.summary(true), fallback: probe.summary(false), device: audioLatencyInfo() }),
    get voices() { return pool.size; },
    dispose() { disposed = true; pool.cutAll(); preview.dispose(); },
  };
}
