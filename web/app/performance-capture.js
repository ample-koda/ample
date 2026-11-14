// Capture of performed clip launches as editable Playlist parts (issue 279). Pure and self-contained: the Performance
// workspace (issue 266) feeds it
// scheduler-confirmed events and shows the result; nothing here touches the DOM, the store or the audio engine.
// @ample:performance-capture

/**
 * HOOK for the Performance workspace: create one recorder per Record session (a recorder may be reused: start() begins a fresh take).
 * All positions are beats of the shared tempo map; the recorder measures them from the beat given to start().
 *   rec.start(beat, playing?)              Record pressed. `playing`: [{ clip, lane? }] already audible at that beat;
 *                                          they are captured from beat 0 of the take
 *   rec.confirm(kind, clipId, beat, lane?) kind 'start' | 'stop'; call ONLY when the scheduler confirms the (quantized) audible beat.
 *                                          `lane`: a lane plays one clip at a time, so a 'start' in a lane ends the lane's
 *                                          earlier clip at that beat (replacement).
 *                                          Without a lane every clip is its own lane. Returns true when something was recorded.
 *   rec.cancel(clipId)                     a queued action was cancelled: nothing was stored, so no ghost clip appears
 *   rec.stopAll(beat)                      Stop all: closes every playing clip at that beat, recording goes on
 *   rec.finish(beat)                       Record stopped; closes clips still playing at that beat, returns the captured run
 * Events that arrive with an earlier beat than a previous one are treated as happening at the later beat (time never runs backwards).
 */
export function createRecorder({ beatsPerBar = 4 } = {}) {
  let t0 = null, last = 0; const open = new Map(); let runs = [];
  const close = (key, at) => {
    const o = open.get(key);
    if (!o) return;
    open.delete(key);
    if (at > o.from) runs.push({ clip: o.clip, lane: key, from: o.from, to: at });
  };
  const at = (beat) => { const v = Math.max(last, beat - t0, 0); last = v; return v; };
  return {
    get recording() { return t0 !== null; },
    start(beat, playing = []) {
      t0 = Number.isFinite(beat) ? beat : 0; last = 0; open.clear(); runs = [];
      for (const p of playing || []) if (p && p.clip != null) open.set(p.lane ?? p.clip, { clip: p.clip, from: 0 });
    },
    confirm(kind, clipId, beat, lane = clipId) {
      if (t0 === null || !Number.isFinite(beat)) return false;
      const key = lane ?? clipId;
      if (kind === 'start') { const when = at(beat); close(key, when); open.set(key, { clip: clipId, from: when }); return true; }
      if (kind === 'stop') {
        if (open.get(key)?.clip !== clipId) return false;   // not playing (any more): nothing to close
        close(key, at(beat)); return true;
      }
      return false;
    },
    cancel() { return false; /* nothing was recorded for a queued action, so there is nothing to undo */ },
    stopAll(beat) {
      if (t0 === null) return false;
      const end = Number.isFinite(beat) ? at(beat) : last;
      for (const key of [...open.keys()]) close(key, end);
      return true;
    },
    finish(beat) {
      if (t0 === null) return { runs: [], beatsPerBar };
      const end = Number.isFinite(beat) ? at(beat) : last;   // a bad stop beat still closes the open clips, at the last known beat
      for (const key of [...open.keys()]) close(key, end);
      t0 = null; last = 0;
      const out = runs.map((r) => ({ ...r })).sort((a, b) => a.from - b.from || String(a.clip).localeCompare(String(b.clip)));
      runs = [];
      return { runs: out, beatsPerBar };
    },
  };
}

/**
 * Connects a recorder to the launch engine of web/app/performance.js (createPerformance) without changing the engine.
 * The engine reports no confirmation, so a launch (or a batch stop) is confirmed here once its time has passed
 * (sync(), or any later call): launches that were cancelled,
 * taken back (launching what already plays) or replaced while queued never get there.
 * `now` is the engine's clock (seconds). `bpm` must be the engine's tempo (it defaults to engine.bpm); Record
 * starts its own beat 0 at start().
 * Use the returned object instead of the engine for launch / launchBatch / cancelQueued / stopAll / setTempo while recording.
 */
export function recordEngine(engine, rec, { now, bpm = engine.bpm ?? 120 }) {
  let beatSec = 60 / bpm, t0 = null;
  const starts = new Map(), stops = new Map();   // lane -> { clip, when }
  const beat = (sec) => (sec - t0) / beatSec;
  const sync = (t = now()) => {
    engine.state();   // the engine moves due queued launches to playing when it is asked
    const due = [];
    for (const [lane, p] of starts) if (p.when <= t) { due.push({ kind: 'start', lane, ...p }); starts.delete(lane); }
    for (const [lane, p] of stops) if (p.when <= t) { due.push({ kind: 'stop', lane, ...p }); stops.delete(lane); }
    due.sort((x, y) => x.when - y.when || (x.kind === 'stop' ? -1 : 1));   // in time order; at the same time a stop comes before a start
    for (const e of due) rec.confirm(e.kind, e.clip, beat(e.when), e.lane);
  };
  const statusOf = (state, lane, clip) => state[lane]?.find((c) => c.id === clip)?.status;
  return {
    start(playing) { t0 = now(); starts.clear(); stops.clear(); rec.start(0, playing); },
    launch(lane, clip) {
      sync();
      const when = engine.launch(lane, clip);
      // null: it plays already, a queued replacement was taken back
      if (rec.recording) { if (when === null) starts.delete(lane); else starts.set(lane, { clip, when }); }
      sync();
      return when;
    },
    launchBatch(at, batch) {
      sync();
      const before = engine.state();
      const res = engine.launchBatch(at, batch);
      const after = engine.state();   // a batch time that has just passed starts at once: those clips are playing now
      if (rec.recording) for (const b of batch) {
        if (b.action === 'start') {
          const now_ = statusOf(after, b.lane, b.clip);
          const queuedNow = now_ === 'queued' || now_ === 'playing';
          if (statusOf(before, b.lane, b.clip) !== 'playing' && queuedNow) starts.set(b.lane, { clip: b.clip, when: at });
          else starts.delete(b.lane);
        } else if (b.action === 'stop') {
          starts.delete(b.lane);
          const cur = before[b.lane]?.find((c) => c.status === 'playing');
          if (cur && !stops.has(b.lane)) stops.set(b.lane, { clip: cur.id, when: at });
        }
      }
      sync();
      return res;
    },
    cancelQueued(lane) {
      sync();
      const ok = engine.cancelQueued(lane);
      if (ok && starts.delete(lane)) rec.cancel(lane);
      return ok;
    },
    stopAll() { sync(); rec.stopAll(beat(now())); starts.clear(); stops.clear(); engine.stopAll(); },
    // The engine keeps the beat position when the tempo changes; the recorder's seconds-to-beats map is moved the same way.
    setTempo(newBpm) { sync(); const beats = beat(now()); engine.setTempo(newBpm); beatSec = 60 / newBpm; t0 = now() - beats * beatSec; },
    sync,
    finish() { sync(); starts.clear(); stops.clear(); return rec.finish(beat(now())); },
  };
}

/**
 * Turn a captured run into Playlist parts per block: { [clipId]: [{ at (1-based bar), length (bars), name: '' }] }.
 * Start and end snap to the NEAREST bar (a launch that is 3.9999 beats in is bar 2, not bar 1), a part is at
 * least one bar, and overlapping parts of one block are merged
 * (a block may not overlap itself in the Playlist).
 */
export function capturedParts(capture) {
  const bpb = Number(capture?.beatsPerBar) > 0 ? Number(capture.beatsPerBar) : 4; const byClip = new Map();
  for (const r of capture?.runs || []) {
    if (!Number.isFinite(r.from) || !Number.isFinite(r.to)) continue;
    const start = Math.max(0, Math.round(r.from / bpb)); const end = Math.max(start + 1, Math.round(r.to / bpb));
    if (!byClip.has(r.clip)) byClip.set(r.clip, []);
    byClip.get(r.clip).push({ start, end });
  }
  const out = {};
  for (const [clip, spans] of byClip) {
    spans.sort((a, b) => a.start - b.start || a.end - b.end);
    const merged = [];
    for (const s of spans) {
      const m = merged.at(-1);
      if (m && s.start < m.end) m.end = Math.max(m.end, s.end); else merged.push({ ...s });
    }
    out[clip] = merged.map((m) => ({ at: m.start + 1, length: m.end - m.start, name: '' }));
  }
  return out;
}

/**
 * The whole capture as ONE list for editAll (one undo step): a setParts per block. NOTE: setParts replaces the
 * block's parts; saving to a new arrangement or take is for the save step.
 */
export function capturedOps(capture) {
  return Object.entries(capturedParts(capture)).map(([block, parts]) => ({ type: 'setParts', block, parts }));
}
