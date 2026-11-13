// Capture of performed clip launches as editable Playlist parts (issue 279). Pure and self-contained: the Performance workspace (issue 266) feeds it
// scheduler-confirmed events and shows the result; nothing here touches the DOM, the store or the audio engine.
// @ample:performance-capture

/**
 * HOOK for the Performance workspace: create one recorder per Record session.
 *   rec.start(beat)                        Record pressed (beat = shared-tempo-map position)
 *   rec.confirm(kind, clipId, beat)        kind 'start' | 'stop'; call ONLY when the scheduler confirms the (quantized) audible beat
 *   rec.cancel(clipId)                     a queued action was cancelled: no event is stored, so no ghost clip appears
 *   rec.finish(beat)                       Record stopped; closes clips still playing, returns the captured run
 * A 'start' of a clip that is already playing is a replacement: the earlier run ends at that beat and a new one begins.
 */
export function createRecorder({ beatsPerBar = 4 } = {}) {
  let t0 = null; const open = new Map(); const runs = [];
  const close = (id, beat) => { const o = open.get(id); if (!o) return; open.delete(id); if (beat > o.from) runs.push({ clip: id, from: o.from, to: beat }); };
  return {
    get recording() { return t0 !== null; },
    start(beat) { t0 = beat; open.clear(); runs.length = 0; },
    confirm(kind, clipId, beat) {
      if (t0 === null || !Number.isFinite(beat)) return false;
      const at = Math.max(0, beat - t0);
      if (kind === 'start') { close(clipId, at); open.set(clipId, { from: at }); return true; }
      if (kind === 'stop') { close(clipId, at); return true; }
      return false;
    },
    cancel() { /* nothing was recorded for a queued action, so there is nothing to undo */ },
    finish(beat) {
      if (t0 === null) return { runs: [], beatsPerBar };
      const end = Math.max(0, beat - t0);
      for (const id of [...open.keys()]) close(id, end);
      t0 = null;
      return { runs: runs.map((r) => ({ ...r })).sort((a, b) => a.from - b.from || a.clip.localeCompare(b.clip)), beatsPerBar };
    },
  };
}

/** Turn a captured run into Playlist parts per block: { [clipId]: [{ at (1-based bar), length (bars), name: '' }] }. Positions snap to whole bars (at least one). */
export function capturedParts(capture) {
  const bpb = capture.beatsPerBar || 4; const out = {};
  for (const r of capture.runs) {
    const at = Math.floor(r.from / bpb) + 1;
    const length = Math.max(1, Math.round((r.to - r.from) / bpb));
    (out[r.clip] ||= []).push({ at, length, name: '' });
  }
  return out;
}
