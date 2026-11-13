// Two-deck cueing and crossfader for the Performance workspace (issue 278, part of 266). Self-contained: the workspace only calls createDeckRig() (see HOOK below).
// Deck A and B are independent loop players on one shared tempo clock; their buses feed an equal-power crossfader into the room output. A separate pre-fader cue
// tap goes to a second AudioContext on a headphone device (setSinkId, fed by a MediaStream), only when the browser can do that; otherwise cue stays silent and state().routing says why.
// Material must already be at the set tempo and start on a declared downbeat; there is no beat detection, warping or scratching. @ample:performance-decks
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

/**
 * HOOK for the Performance workspace (issue 266):
 *   const rig = createDeckRig({ bpm, beatsPerBar });
 *   rig.load('A', { buffer, loopBeats, downbeatBeats }); rig.start('A', { onBar: true }); rig.setCrossfade(0..1);
 *   rig.setCue('B', true); await rig.setCueDevice(deviceId, roomDeviceId); rig.state() for display; rig.dispose().
 */
export function createDeckRig({ bpm, beatsPerBar = 4, ctxFactory = () => new AudioContext() } = {}) {
  const room = ctxFactory();
  const clockStart = room.currentTime;
  let cueCtx = null, cueDeviceId = null, roomDeviceId = null, pos = 0;
  const out = room.createGain(); out.connect(room.destination);
  const cueDest = room.createMediaStreamDestination(); // pre-fader cue tap; only the cue context listens to it, never the room output
  const decks = {};
  for (const id of ['A', 'B']) {
    const bus = room.createGain(), xf = room.createGain(), cueSend = room.createGain();
    bus.connect(xf); xf.connect(out);
    cueSend.gain.value = 0; // pre-fader tap, silent until cue is on AND a separate device is active
    bus.connect(cueSend); cueSend.connect(cueDest);
    decks[id] = { id, bus, xf, cueSend, cue: false, buffer: null, loopBeats: 4, downbeatBeats: 0, src: null, startedAt: null };
  }
  const applyXf = () => {
    const g = crossfadeGains(pos), t = room.currentTime;
    for (const [id, v] of [['A', g.a], ['B', g.b]]) { const p = decks[id].xf.gain; p.cancelScheduledValues(t); p.setTargetAtTime(v, t, RAMP / 3); }
  };
  applyXf();
  const routing = () => routingPlan({ canSplit: cueRoutingSupported(), cueDeviceId, roomDeviceId });
  const cueLive = () => routing().mode === 'split' && !!cueCtx;
  const syncCue = () => {
    for (const d of Object.values(decks)) d.cueSend.gain.setTargetAtTime(d.cue && cueLive() ? 1 : 0, room.currentTime, RAMP / 3);
  };
  return {
    load(id, { buffer, loopBeats = 4, downbeatBeats = 0 }) { Object.assign(decks[id], { buffer, loopBeats, downbeatBeats }); },
    start(id, { onBar = true } = {}) {
      const d = decks[id]; if (!d.buffer || d.src) return;
      const when = onBar ? nextBarStart(room.currentTime, clockStart, bpm, beatsPerBar) : room.currentTime;
      const s = room.createBufferSource(); s.buffer = d.buffer; s.loop = true; s.connect(d.bus);
      s.start(when, (Math.max(0, d.downbeatBeats) * beatSeconds(bpm)) % d.buffer.duration);
      d.src = s; d.startedAt = when;
    },
    stop(id) { const d = decks[id]; if (d.src) { try { d.src.stop(); } catch { /* already stopped */ } d.src = null; d.startedAt = null; } },
    setCrossfade(p) { pos = Math.min(1, Math.max(0, p)); applyXf(); },
    setCue(id, on) { decks[id].cue = !!on; syncCue(); },
    async setCueDevice(deviceId, roomId = null) {
      cueDeviceId = deviceId || null; roomDeviceId = roomId;
      if (cueCtx) { try { await cueCtx.close(); } catch { /* ignore */ } cueCtx = null; }
      if (routing().mode === 'split') { const c = ctxFactory(); await c.setSinkId(deviceId); c.createMediaStreamSource(cueDest.stream).connect(c.destination); cueCtx = c; }
      syncCue();
      return routing();
    },
    state() {
      const t = room.currentTime, ph = {};
      for (const d of Object.values(decks)) ph[d.id] = d.startedAt != null && t >= d.startedAt ? deckPhaseBeats(t - d.startedAt, bpm, d.loopBeats, 0) : null;
      return { playing: { A: !!decks.A.src, B: !!decks.B.src }, cue: { A: decks.A.cue, B: decks.B.cue }, crossfade: pos, phase: ph,
        drift: ph.A != null && ph.B != null ? phaseDriftBeats(ph.A, ph.B, decks.A.loopBeats) : null, routing: routing() };
    },
    dispose() { this.stop('A'); this.stop('B'); room.close?.(); cueCtx?.close?.(); },
  };
}
