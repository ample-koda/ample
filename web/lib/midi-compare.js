const microbeats = (value) => {
  const match = /^(\d+)(?:\.(\d{1,6}))?$/.exec(String(value));
  if (!match) throw new Error('MIDI beat values must have at most six decimal places.');
  return BigInt(match[1]) * 1_000_000n + BigInt((match[2] || '').padEnd(6, '0') || '0');
};

const ticks = (notes, ppq) => notes.map((note) => {
  const bar = BigInt(note.bar);
  if (note.tick !== undefined && note.ticks !== undefined && note.ppq !== undefined) {
    const notePpq = BigInt(note.ppq);
    if (notePpq !== BigInt(ppq)) throw new Error('MIDI note PPQ does not match the comparison PPQ.');
    return { pitch: Number(note.pitch), velocity: Number(note.velocity ?? 96), onset: (bar - 1n) * 4n * notePpq + BigInt(note.tick), length: BigInt(note.ticks) };
  }
  const onset = ((bar - 1n) * 4_000_000n + microbeats(note.beat || 0)) * BigInt(ppq);
  const length = microbeats(note.beats ?? 4) * BigInt(ppq);
  if (onset % 1_000_000n || length % 1_000_000n) throw new Error('MIDI note timing is not exact at this PPQ.');
  return { pitch: Number(note.pitch), velocity: Number(note.velocity ?? 96), onset: onset / 1_000_000n, length: length / 1_000_000n };
});

/** Deterministically pairs same-pitch notes by nearest onset within an integer tick tolerance. */
export function compareMidiNotes(original, converted, ppq, toleranceTicks) {
  if (!Number.isInteger(ppq) || ppq < 1 || !Number.isInteger(toleranceTicks) || toleranceTicks < 0) throw new Error('PPQ and tolerance must be nonnegative integers, with PPQ positive.');
  const a = ticks(original, ppq), b = ticks(converted, ppq), used = new Set(), pairs = [], missing = [];
  for (const source of a) {
    let best = -1, distance = BigInt(toleranceTicks) + 1n;
    for (let i = 0; i < b.length; i++) {
      if (used.has(i) || source.pitch !== b[i].pitch) continue;
      const d = source.onset > b[i].onset ? source.onset - b[i].onset : b[i].onset - source.onset;
      if (d < distance || d === distance && i < best) { best = i; distance = d; }
    }
    if (best < 0 || distance > BigInt(toleranceTicks)) { missing.push(source); continue; }
    used.add(best);
    pairs.push({ onsetDeltaTicks: Number(b[best].onset - source.onset), durationDeltaTicks: Number(b[best].length - source.length), velocityDelta: b[best].velocity - source.velocity });
  }
  const timing = pairs.flatMap(p => [Math.abs(p.onsetDeltaTicks), Math.abs(p.durationDeltaTicks)]);
  const velocity = pairs.map(p => Math.abs(p.velocityDelta));
  return { found: pairs.length, missing: missing.length, extra: b.length - used.size, timing: { maxTicks: timing.reduce((max, x) => Math.max(max, x), 0), meanTicks: timing.length ? Math.floor(timing.reduce((sum, x) => sum + x, 0) / timing.length) : 0 }, velocity: { max: velocity.reduce((max, x) => Math.max(max, x), 0), mean: velocity.length ? Math.floor(velocity.reduce((sum, x) => sum + x, 0) / velocity.length) : 0 }, pairs };
}
