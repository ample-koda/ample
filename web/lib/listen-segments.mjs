// Live Preview in the code editor (plan/live-preview/PLAN.md, WP2/WP3/WP5): pure arithmetic of what loops.
//  - Bars (decision 13, plan 6.4): cut from a full render of the current code, or built from bar 1 to one bar past the range, so every block has the
//    timeline and the state of the whole song (the end of a take is processed as an end, so the build goes one bar further); a fast draft builds the range
//    alone and is never presented as exact. Only the chosen bars are downloaded: `wavSlice` turns a WAV header into the byte range of a stretch.
//  - Silence: windows under -60 dBFS; edges trimmed, and (optional) internal gaps of at least one bar and at least two seconds skipped, with a margin around
//    sound so attacks and tails stay. The cuts are only in playback: segments in take seconds, and the map between preview time and take time.
// Pure, tested in node (web/lib/test/listen-segments.test.mjs).

/** Seconds of one bar. */
export const barSeconds = (tempo, beatsPerBar = 4) => (beatsPerBar * 60) / (Number(tempo) > 0 ? Number(tempo) : 120);

/** The loop of bars a..b (inclusive, `{ firstBar, bars }`) in a take that starts at bar `takeFirstBar`: { start, end } in seconds. */
export function barsLoop(range, barSec, takeFirstBar = 1) {
  const start = (range.firstBar - takeFirstBar) * barSec;
  return { start: Math.max(0, start), end: Math.max(0, start) + range.bars * barSec };
}

/**
 * How to hear bars a..b when no full render can be cut: the range to build and where the bars are in that take.
 * From the start (default): bars 1 to b + 1, so the engine works through the whole song up to the range (`before` bars are rendered and never played)
 * and the end of the take falls after the range. A fast draft builds a..b + 1 alone.
 * @returns {{ build: { firstBar: number, bars: number }, takeFirstBar: number, before: number, method: 'from-start'|'fast-draft' }}
 */
export function rangeBuild(range, { fastDraft = false } = {}) {
  const last = range.firstBar + range.bars - 1;
  if (fastDraft) return { build: { firstBar: range.firstBar, bars: range.bars + 1 }, takeFirstBar: range.firstBar, before: 0, method: 'fast-draft' };
  return { build: { firstBar: 1, bars: last + 1 }, takeFirstBar: 1, before: range.firstBar - 1, method: 'from-start' };
}

/**
 * The bytes of a stretch of a WAV take, from its header (the first bytes of the file) and its total size: { from, to } (inclusive byte range of the
 * samples), the time where the stretch really starts (on a frame), and `wav(samples)` that puts the header in front of those bytes as a WAV of its own.
 * Null when the header is not a PCM/float WAV whose data chunk starts inside the bytes given (the caller then reads the whole take).
 */
export function wavSlice(header, totalBytes, fromSec, toSec) {
  const bytes = new Uint8Array(header), view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const tag = (at) => String.fromCharCode(bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]);
  if (bytes.length < 12 || tag(0) !== 'RIFF' || tag(8) !== 'WAVE') return null;
  let at = 12, rate = 0, align = 0, dataAt = -1, dataSize = 0;
  while (at + 8 <= bytes.length) {
    const id = tag(at), size = view.getUint32(at + 4, true);
    if (id === 'fmt ') { rate = view.getUint32(at + 12, true); align = view.getUint16(at + 20, true); }
    if (id === 'data') { dataAt = at + 8; dataSize = Math.min(size, totalBytes - dataAt); break; }
    at += 8 + size + (size % 2);
  }
  if (dataAt < 0 || !rate || !align) return null;
  const frames = Math.floor(dataSize / align);
  const first = Math.max(0, Math.min(frames, Math.floor(fromSec * rate))), last = Math.max(first + 1, Math.min(frames, Math.ceil(toSec * rate)));
  const from = dataAt + first * align, to = dataAt + last * align - 1;
  const wav = (samples) => {
    const data = new Uint8Array(samples), out = new Uint8Array(dataAt + data.length), v = new DataView(out.buffer);
    out.set(bytes.subarray(0, dataAt));
    out.set(data, dataAt);
    v.setUint32(4, out.length - 8, true);
    v.setUint32(dataAt - 4, data.length, true);
    return out.buffer;
  };
  return { from, to, offset: first / rate, wav };
}

/** One flag per window: true when the window is silent (RMS under `thresholdDb` dBFS on every channel). */
export function silentWindows(channels, rate, { windowMs = 10, thresholdDb = -60 } = {}) {
  const size = Math.max(1, Math.round(rate * windowMs / 1000)), length = channels[0]?.length || 0;
  const count = Math.ceil(length / size), flags = new Uint8Array(count), limit = 10 ** (thresholdDb / 20);
  for (let w = 0; w < count; w++) {
    let loud = false;
    for (const channel of channels) {
      let sum = 0;
      const from = w * size, to = Math.min(length, from + size);
      for (let i = from; i < to; i++) sum += channel[i] * channel[i];
      if (Math.sqrt(sum / Math.max(1, to - from)) >= limit) { loud = true; break; }
    }
    flags[w] = loud ? 0 : 1;
  }
  return { flags, windowSec: size / rate };
}

/**
 * The segments of a loop that play, in take seconds.
 * mode 'original': the whole loop; 'edges': without the silence before the first and after the last sound; 'gaps': also without internal silences of at
 * least `minGapBars` bars and at least `minGapSec` seconds. A margin of `padSec` stays around sound. A loop with no sound at all keeps its length (silent: true).
 * @returns {{ segments: { from: number, to: number }[], silent: boolean, skipped: number }}
 */
export function playSegments({ flags, windowSec }, loop, { mode = 'original', minGapSec = 2, minGapBars = 1, barSec = 2, padSec = 0.15 } = {}) {
  const whole = { segments: [{ from: loop.start, to: loop.end }], silent: false, skipped: 0 };
  const first = Math.floor(loop.start / windowSec), last = Math.min(flags.length, Math.ceil(loop.end / windowSec));
  const sound = [];   // runs of loud windows: [startSec, endSec]
  for (let w = first; w < last; w++) {
    if (flags[w]) continue;
    const from = Math.max(loop.start, w * windowSec), to = Math.min(loop.end, (w + 1) * windowSec);
    if (sound.length && Math.abs(sound[sound.length - 1][1] - from) < 1e-9) sound[sound.length - 1][1] = to; else sound.push([from, to]);
  }
  if (!sound.length) return { ...whole, silent: true };
  if (mode === 'original') return whole;
  const minGap = Math.max(minGapSec, minGapBars * barSec);
  const kept = [];
  for (const [from, to] of sound) {
    const prev = kept[kept.length - 1];
    if (prev && (mode !== 'gaps' || from - prev.to < minGap)) prev.to = to; else kept.push({ from, to });
  }
  const segments = kept.map((k) => ({ from: Math.max(loop.start, k.from - padSec), to: Math.min(loop.end, k.to + padSec) }));
  const length = segments.reduce((sum, s) => sum + (s.to - s.from), 0);
  return { segments, silent: false, skipped: Math.max(0, (loop.end - loop.start) - length) };
}

/** The length of a loop as it is heard (the segments one after the other). */
export const heardLength = (segments) => segments.reduce((sum, s) => sum + (s.to - s.from), 0);

/** Preview time (seconds into the heard loop, wrapped) to take time. */
export function takeTimeAt(segments, previewTime) {
  const total = heardLength(segments);
  if (!(total > 0)) return segments[0]?.from ?? 0;
  let t = ((previewTime % total) + total) % total;
  for (const s of segments) { const len = s.to - s.from; if (t < len) return s.from + t; t -= len; }
  return segments[segments.length - 1].to;
}

/** Take time to preview time (a time inside a skipped gap maps to the start of the next segment). */
export function previewTimeAt(segments, takeTime) {
  let before = 0;
  for (const s of segments) {
    if (takeTime < s.from) return before;
    if (takeTime <= s.to) return before + (takeTime - s.from);
    before += s.to - s.from;
  }
  return before;
}
