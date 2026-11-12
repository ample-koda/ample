// Tempo-aware mapping between song position (beats) and seconds, and between a playing take's position and the audio-context clock, with tempo changes and loop wrap.
// Pure functions (tested in node). The schedulers (live-clock-gate.mjs, the sequencer lanes) ask this module where a beat is instead of assuming one constant tempo.

/** A tempo map from [{ beat, bpm }] (sorted by beat; the first entry is taken to start at beat 0). Returns { secondsAt(beat), beatAt(seconds), bpmAt(beat) }. */
export function tempoMap(changes, fallbackBpm = 120) {
  const list = (Array.isArray(changes) ? changes : []).filter((c) => c && Number.isFinite(c.beat) && c.bpm > 0).map((c) => ({ beat: c.beat, bpm: c.bpm })).sort((a, b) => a.beat - b.beat);
  if (!list.length || list[0].beat > 0) list.unshift({ beat: 0, bpm: list.length ? list[0].bpm : fallbackBpm });
  else list[0].beat = 0;
  let t = 0;
  const segs = list.map((c, i) => { const s = { beat: c.beat, bpm: c.bpm, start: t }; if (i + 1 < list.length) t += (list[i + 1].beat - c.beat) * 60 / c.bpm; return s; });
  const seg = (beat) => { let k = 0; while (k + 1 < segs.length && segs[k + 1].beat <= beat) k++; return segs[k]; };
  const segAt = (seconds) => { let k = 0; while (k + 1 < segs.length && segs[k + 1].start <= seconds) k++; return segs[k]; };
  return {
    bpmAt: (beat) => seg(beat).bpm,
    secondsAt: (beat) => { const s = seg(Math.max(0, beat)); return s.start + (Math.max(0, beat) - s.beat) * 60 / s.bpm; },
    beatAt: (seconds) => { const s = segAt(Math.max(0, seconds)); return s.beat + (Math.max(0, seconds) - s.start) * s.bpm / 60; },
  };
}

/** Wraps a position into a loop [start, end) (any unit). Without a usable loop the position is returned unchanged. */
export function wrapLoop(position, loop) {
  if (!loop || !(loop.end > loop.start)) return position;
  const span = loop.end - loop.start;
  return loop.start + (((position - loop.start) % span) + span) % span;
}

/**
 * Anchor between the take clock and the audio clock: take position `position` (seconds) was heard at context time `time`.
 * contextTimeOf(anchor, target, loop) gives the context time at which take position `target` is heard next (at or after the anchor), also across loop wraps.
 */
export function makeAnchor(position, time) { return { position, time }; }

export function contextTimeOf(anchor, target, loop) {
  let ahead = target - anchor.position;
  if (loop && loop.end > loop.start) {
    const span = loop.end - loop.start;
    ahead = (((target - anchor.position) % span) + span) % span;
  } else if (ahead < 0) return null;   // already past and no loop
  return anchor.time + ahead;
}

/** Take position that is playing at context time `now`, wrapped by the loop. */
export function positionAt(anchor, now, loop) { return wrapLoop(anchor.position + (now - anchor.time), loop); }

/**
 * Step start times (context time) that fall in [now, now + lookahead): steps are `stepBeats` long on the beat grid of `map`; `anchorBeat` is the song beat that is heard at `anchor`.
 * Returns [{ k, time }] with k the absolute step index. Used to queue gain/note events ahead of the clock across tempo changes.
 */
export function stepsInWindow(map, anchor, anchorBeat, stepBeats, now, lookahead) {
  if (!(stepBeats > 0)) return [];
  const base = map.secondsAt(anchorBeat);
  const from = map.beatAt(base + (now - anchor.time)), to = map.beatAt(base + (now + lookahead - anchor.time));
  const out = [];
  for (let k = Math.max(0, Math.ceil(from / stepBeats - 1e-9)); k * stepBeats < to - 1e-12; k++) out.push({ k, time: anchor.time + (map.secondsAt(k * stepBeats) - base) });
  return out;
}
