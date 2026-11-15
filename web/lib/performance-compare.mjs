// What the DJ heard against what the saved song plays ("heard = saved"): the level of every bar of both, compared bar by bar, and the bars where the sum of the set went past full scale.
// Pure, so node --test checks it. Audio comes as chunks { l, r, scale } (Int16 or Float32 arrays) so a long set is never copied into one big array.

const dbOf = (power) => (power > 1e-12 ? 10 * Math.log10(power) : -120);
export const FLOOR_DB = -60;

/** The level in dB (mean power of both channels) of every bar, from sample `startSample` of the chunks on, `barSamples` long. A bar with no samples is -120. */
export function barLevels(chunks, startSample, barSamples, bars) {
  const sum = new Float64Array(bars), count = new Float64Array(bars);
  let pos = 0;
  for (const chunk of chunks) {
    const n = Math.min(chunk.l.length, chunk.r.length), scale = chunk.scale ?? 1;
    let i = Math.max(0, Math.ceil(startSample - pos));
    for (; i < n; i++) {
      const bar = Math.floor((pos + i - startSample) / barSamples);
      if (bar >= bars) return finish(sum, count);
      const a = chunk.l[i] * scale, b = chunk.r[i] * scale;
      sum[bar] += (a * a + b * b) / 2; count[bar]++;
    }
    pos += n;
  }
  return finish(sum, count);
}
const finish = (sum, count) => Array.from(sum, (value, k) => dbOf(count[k] ? value / count[k] : 0));

/**
 * The bars side by side: { bars: [{ bar, heard, saved, diff }], compared, worst: { bar, diff } | null, meanAbs }. `diff` is saved minus heard in dB.
 * A bar where both are below the floor is silence in both and is not compared;
 * a bar where only one is below it is compared against the floor, so a missing sound is a large difference.
 */
export function compareBars(heard, saved, floorDb = FLOOR_DB) {
  const bars = [];
  for (let k = 0; k < Math.max(heard.length, saved.length); k++) {
    const h = heard[k] ?? -120, s = saved[k] ?? -120;
    if (h < floorDb && s < floorDb) continue;
    bars.push({ bar: k + 1, heard: h, saved: s, diff: Math.max(s, floorDb) - Math.max(h, floorDb) });
  }
  let worst = null, total = 0;
  for (const b of bars) { total += Math.abs(b.diff); if (!worst || Math.abs(b.diff) > Math.abs(worst.diff)) worst = { bar: b.bar, diff: b.diff }; }
  return { bars, compared: bars.length, worst, meanAbs: bars.length ? total / bars.length : 0 };
}

/** The sentence of the result: how far the saved song is from what was heard. `within` is the largest difference that still counts as the same sound. */
export function compareText(result, within = 1) {
  if (!result.compared) return 'The saved song and what you heard are both silent: nothing to compare.';
  const worst = Math.abs(result.worst.diff);
  const sign = (v) => `${v > 0 ? '+' : ''}${v.toFixed(1)} dB`;
  if (worst <= within) {
    return `The saved song sounds like what you heard: every bar within ${within} dB `
      + `(largest difference ${sign(result.worst.diff)}, bar ${result.worst.bar}).`;
  }
  const off = result.bars.filter((b) => Math.abs(b.diff) > within).map((b) => b.bar);
  return `The saved song is close to what you heard on average (${result.meanAbs.toFixed(1)} dB), `
    + `but ${off.length} bar${off.length === 1 ? ' differs' : 's differ'} more than ${within} dB; `
    + `the largest is bar ${result.worst.bar} (${sign(result.worst.diff)}).`;
}

/**
 * The bars (counted from 1 in the saved song) where the sum of the set went past full scale. `peaks` are { time (seconds on the clock of the
 * session), peak (0..) } per block of audio; `startSec` is the time of the first launch,
 * `barSec` the length of a bar. Returns { bars: [numbers], peakDb } (peakDb of the loudest block, or null when the sum never went past it).
 */
export function clippedBars(peaks, startSec, barSec, limit = 1) {
  const bars = new Set(); let top = 0;
  for (const p of peaks) {
    if (!(p.peak > limit) || p.time < startSec) continue;
    bars.add(1 + Math.floor((p.time - startSec) / barSec));
    top = Math.max(top, p.peak);
  }
  return { bars: [...bars].sort((a, b) => a - b), peakDb: top ? 20 * Math.log10(top) : null };
}

/** "bar 12", "bars 12 and 14", "bars 12 to 15" ... a short list of bar numbers for a message. */
export function barList(bars) {
  if (!bars.length) return '';
  const runs = [];
  for (const b of bars) { const last = runs.at(-1); if (last && b === last[1] + 1) last[1] = b; else runs.push([b, b]); }
  const parts = runs.map(([a, z]) => (a === z ? `${a}` : z === a + 1 ? `${a} and ${z}` : `${a} to ${z}`));
  const text = parts.length > 4 ? `${parts.slice(0, 4).join(', ')} and more` : parts.join(', ');
  return `${bars.length === 1 ? 'bar' : 'bars'} ${text}`;
}
