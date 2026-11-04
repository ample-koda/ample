/**
 * The stretches of a region that are not silent: a region carries the joins of a loop and is silent elsewhere, so each stretch is one join.
 * channels: arrays of samples (-1..1); returns [{ from, to }] in samples. Gaps shorter than minGap seconds are bridged, stretches shorter than 10 ms dropped.
 */
export function seamWindows(channels, rate, minGap = 0.2, floor = 1e-4) {
  const n = channels.length ? channels[0].length : 0;
  const out = [];
  let from = -1, last = -1;
  const gap = Math.round(minGap * rate);
  for (let i = 0; i < n; i++) {
    let loud = false;
    for (const c of channels) if (Math.abs(c[i]) > floor) { loud = true; break; }
    if (!loud) continue;
    if (from < 0) from = i;
    else if (i - last > gap) { out.push({ from, to: last + 1 }); from = i; }
    last = i;
  }
  if (from >= 0) out.push({ from, to: last + 1 });
  return out.filter((w) => w.to - w.from >= rate * 0.01);
}

/** Two sets of channels added (the shorter is zero-padded); the result has as many channels as the larger set. */
export function sumChannels(a, b) {
  const channels = Math.max(a.length, b.length);
  const length = Math.max(a[0]?.length ?? 0, b[0]?.length ?? 0);
  return Array.from({ length: channels }, (_, c) => {
    const x = a[Math.min(c, a.length - 1)], y = b[Math.min(c, b.length - 1)];
    const out = new Float32Array(length);
    for (let i = 0; i < length; i++) out[i] = (x?.[i] ?? 0) + (y?.[i] ?? 0);
    return out;
  });
}
