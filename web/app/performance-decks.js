// Two-deck cueing and crossfader for the Performance workspace (issue 278, part of 266). Self-contained: the workspace only calls createDeckRig() (see HOOK below).
// Deck A and B are independent loop players on one shared tempo clock; their buses feed an equal-power crossfader into the room output. A separate pre-fader cue
// tap goes to a second AudioContext on a headphone device (setSinkId, fed by a MediaStream), only when the browser can do that; otherwise cue stays silent and state().routing says why.
// Material must already be at the set tempo and start on a declared downbeat; there is no beat detection, warping or scratching. The tempo is fixed for the life of a rig. @ample:performance-decks
import { beatSeconds, crossfadeGains, deckPhaseBeats, nextBarStart, phaseDriftBeats, routingPlan } from '../lib/deck-math.mjs';

const RAMP = 0.03; // seconds, gain ramps avoid clicks

export function cueRoutingSupported() {
  const C = globalThis.AudioContext;
  return !!(C && C.prototype && 'setSinkId' in C.prototype && globalThis.navigator?.mediaDevices?.enumerateDevices);
}

export async function listOutputs() {
  if (!cueRoutingSupported()) return [];
  try { return (await navigator.mediaDevices.enumerateDevices()).filter(d => d.kind === 'audiooutput'); } catch { return []; }
}

const closeQuiet = async (c) => { try { if (c && c.state !== 'closed') await c.close?.(); } catch { /* already closed */ } };
const quiet = (p) => { try { p?.catch?.(() => {}); } catch { /* ignore */ } };

/**
 * HOOK for the Performance workspace (issue 266):
 *   const rig = createDeckRig({ bpm, beatsPerBar });
 *   rig.load('A', { buffer, loopBeats, downbeatBeats }); rig.start('A', { onBar: true }); rig.setCrossfade(0..1);
 *   rig.setCue('B', true); await rig.setCueDevice(deviceId, roomDeviceId); rig.state() for display; rig.dispose().
 * start/stop are quantised to the next bar by default ({ onBar: false } = now). load() on a playing deck swaps the material at the next bar.
 * Call resume() from a user gesture if state().context is "suspended". setCueDevice(id) keeps the room device given earlier.
 */
export function createDeckRig({ bpm, beatsPerBar = 4, ctxFactory = () => new AudioContext() } = {}) {
  beatSeconds(bpm);
  if (!(beatsPerBar > 0)) throw new RangeError('beatsPerBar must be positive');
  const beat = beatSeconds(bpm);
  const room = ctxFactory();
  const clockStart = room.currentTime;
  let cueCtx = null, cueSrc = null, cueDeviceId = null, roomDeviceId = null, cueError = null, cueGen = 0, pos = 0, disposed = false;
  const out = room.createGain(); out.connect(room.destination);
  // pre-fader cue tap; only the cue context listens to its stream, never the room output. Without a MediaStreamDestination there is no tap at all.
  const cueDest = typeof room.createMediaStreamDestination === 'function' ? room.createMediaStreamDestination() : null;
  const decks = {};
  for (const id of ['A', 'B']) {
    const bus = room.createGain(), xf = room.createGain(), cueSend = room.createGain();
    bus.connect(xf); xf.connect(out);
    cueSend.gain.value = 0; // silent until cue is on AND a separate device is active
    if (cueDest) { bus.connect(cueSend); cueSend.connect(cueDest); }
    decks[id] = { id, bus, xf, cueSend, cue: false, buffer: null, loopBeats: 4, downbeatBeats: 0, src: null, startedAt: null, stopAt: null };
  }
  const gainsNow = () => { const g = crossfadeGains(pos); return [['A', g.a], ['B', g.b]]; };
  for (const [id, v] of gainsNow()) decks[id].xf.gain.value = v;
  const applyXf = () => {
    const t = room.currentTime;
    for (const [id, v] of gainsNow()) { const p = decks[id].xf.gain; p.cancelScheduledValues(t); p.setTargetAtTime(v, t, RAMP / 3); }
  };
  const canSplit = () => !!cueDest && typeof room.setSinkId === 'function';
  const routing = () => routingPlan({ canSplit: canSplit(), cueDeviceId, roomDeviceId, error: cueError });
  const cueLive = () => !disposed && !!cueCtx && routing().mode === 'split';
  const syncCue = () => {
    if (disposed) return;
    for (const d of Object.values(decks)) d.cueSend.gain.setTargetAtTime(d.cue && cueLive() ? 1 : 0, room.currentTime, RAMP / 3);
  };
  const barAt = () => nextBarStart(room.currentTime, clockStart, bpm, beatsPerBar);
  const begin = (d, when) => {
    const s = room.createBufferSource(); s.buffer = d.buffer; s.loop = true;
    const loopSec = d.loopBeats * beat;
    if (loopSec < d.buffer.duration - 1e-6) { s.loopStart = 0; s.loopEnd = loopSec; } // material longer than the declared loop: loop exactly the declared length
    const len = s.loopEnd > 0 ? s.loopEnd : d.buffer.duration;
    s.onended = () => { if (d.src === s) { d.src = null; d.startedAt = null; d.stopAt = null; } try { s.disconnect(); } catch { /* ignore */ } };
    s.connect(d.bus);
    s.start(when, (Math.max(0, d.downbeatBeats) * beat) % len);
    d.src = s; d.startedAt = when; d.stopAt = null;
  };
  const playing = (d) => !!d.src && !(d.stopAt != null && room.currentTime >= d.stopAt);
  const rig = {
    load(id, { buffer, loopBeats = 4, downbeatBeats = 0 } = {}) {
      const d = decks[id];
      if (!d) throw new RangeError('unknown deck ' + id);
      if (!buffer || !(buffer.duration > 0)) throw new TypeError('buffer with a duration is required');
      if (!(loopBeats > 0)) throw new RangeError('loopBeats must be positive');
      Object.assign(d, { buffer, loopBeats, downbeatBeats: Number.isFinite(downbeatBeats) ? downbeatBeats : 0 });
      if (disposed || !d.src || d.stopAt != null) return;
      // loaded while playing: the old material runs out at the next bar, the new one starts there, so phase and the loop length stay consistent
      const when = barAt(), old = d.src;
      try { old.stop(when); } catch { /* already stopped */ }
      begin(d, when);
    },
    start(id, { onBar = true } = {}) {
      const d = decks[id]; if (disposed || !d || !d.buffer || d.src) return false;
      quiet(room.resume?.()); // a context made before a user gesture is suspended and its clock does not run
      begin(d, onBar ? barAt() : room.currentTime);
      return true;
    },
    stop(id, { onBar = true } = {}) {
      const d = decks[id]; if (!d || !d.src) return;
      const now = room.currentTime, when = onBar ? barAt() : now;
      try { d.src.stop(when); } catch { /* already stopped */ }
      if (when <= now) { d.src = null; d.startedAt = null; d.stopAt = null; } else d.stopAt = when;
    },
    setCrossfade(p) { pos = typeof p === 'number' && !Number.isNaN(p) ? Math.min(1, Math.max(0, p)) : pos; if (!disposed) applyXf(); },
    setCue(id, on) { decks[id].cue = !!on; syncCue(); },
    async resume() { try { await room.resume?.(); await cueCtx?.resume?.(); } catch { /* no gesture yet */ } },
    async setCueDevice(deviceId, roomId) {
      if (disposed) return routing();
      const gen = ++cueGen;
      cueDeviceId = deviceId || null; if (roomId !== undefined) roomDeviceId = roomId || null; cueError = null;
      const old = cueCtx, oldSrc = cueSrc; cueCtx = null; cueSrc = null;
      syncCue(); // silent first, then swap
      try { oldSrc?.disconnect(); } catch { /* ignore */ }
      await closeQuiet(old);
      if (gen === cueGen && !disposed && routing().mode === 'split') {
        let c = null;
        try {
          c = ctxFactory();
          await c.setSinkId(cueDeviceId);
          if (gen !== cueGen || disposed) { await closeQuiet(c); return routing(); }
          const node = c.createMediaStreamSource(cueDest.stream); node.connect(c.destination);
          await c.resume?.();
          cueCtx = c; cueSrc = node;
        } catch (e) {
          cueError = String(e?.message || e?.name || e || 'unknown error');
          await closeQuiet(c);
        }
      }
      if (gen === cueGen) syncCue();
      return routing();
    },
    state() {
      const t = room.currentTime, ph = {};
      for (const d of Object.values(decks)) ph[d.id] = d.startedAt != null && t >= d.startedAt ? deckPhaseBeats(t - d.startedAt, bpm, d.loopBeats, 0) : null;
      const wrap = Math.min(decks.A.loopBeats, decks.B.loopBeats); // compare on the shorter loop so a 4 beat deck against an 8 beat deck still reads sensibly
      return { bpm, playing: { A: playing(decks.A), B: playing(decks.B) }, cue: { A: decks.A.cue, B: decks.B.cue }, crossfade: pos, phase: ph,
        drift: ph.A != null && ph.B != null ? phaseDriftBeats(ph.A % wrap, ph.B % wrap, wrap) : null,
        context: room.state ?? null, cueContext: cueCtx?.state ?? null, routing: routing() };
    },
    dispose() {
      if (disposed) return;
      this.stop('A', { onBar: false }); this.stop('B', { onBar: false });
      disposed = true; cueGen++;
      for (const d of Object.values(decks)) { for (const n of [d.bus, d.xf, d.cueSend]) { try { n.disconnect(); } catch { /* ignore */ } } }
      try { out.disconnect(); } catch { /* ignore */ }
      try { cueDest?.disconnect?.(); } catch { /* ignore */ }
      try { cueSrc?.disconnect(); } catch { /* ignore */ }
      try { for (const tr of cueDest?.stream?.getTracks?.() || []) tr.stop(); } catch { /* ignore */ }
      const c = cueCtx; cueCtx = null; cueSrc = null;
      quiet(closeQuiet(room)); quiet(closeQuiet(c));
    },
  };
  return rig;
}
