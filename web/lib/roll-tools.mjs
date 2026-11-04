// Extra piano-roll tools (PR15-PR17, PR19): pure functions over plain note objects ({ at, length, pitch, velocity, ... }, ticks),
// so they are testable without the DOM and reusable from any selection the roll builds. The caller writes the result back by index
// (these functions preserve array order and any extra fields via spread, so `index` from notes-model's absolute() survives).

function seededRandom(seed) { const x = Math.sin(seed * 999.123) * 10000; return x - Math.floor(x); }

/** PR15: turns a chord (notes that mostly start together) into an arpeggio across the chord's own time span. `mode` is
 * up | down | updown | random (seeded, so the same seed always arpeggiates the same way). */
export function arpeggiate(chordNotes, { mode = 'up', rateTicks = 240, gatePercent = 80, octaveRange = 1, seed = 1 } = {}) {
  if (!chordNotes.length) return [];
  const at0 = Math.min(...chordNotes.map((n) => n.at));
  const totalLength = Math.max(...chordNotes.map((n) => n.at + n.length)) - at0;
  const steps = Math.max(1, Math.floor(totalLength / Math.max(1, rateTicks)));
  const pitches = [...new Set(chordNotes.map((n) => n.pitch))].sort((a, b) => a - b);
  const velocity = Math.round(chordNotes.reduce((s, n) => s + n.velocity, 0) / chordNotes.length);
  const n = pitches.length;
  const pick = (i) => {
    if (mode === 'down') return pitches[n - 1 - (i % n)];
    if (mode === 'updown' && n > 1) { const cyc = 2 * n - 2, k = i % cyc; return pitches[k < n ? k : cyc - k]; }
    if (mode === 'random') return pitches[Math.floor(seededRandom(seed + i) * n)];
    return pitches[i % n];
  };
  const out = [];
  for (let i = 0; i < steps; i++) {
    const octave = octaveRange > 1 ? Math.floor(i / n) % octaveRange : 0;
    const pitch = Math.max(0, Math.min(127, pick(i) + octave * 12));
    const length = Math.max(1, Math.round(rateTicks * (Math.max(1, Math.min(100, gatePercent)) / 100)));
    out.push({ at: at0 + i * rateTicks, length, pitch, velocity });
  }
  return out;
}

/** PR16: extends each note (sorted by start) to the start of the next one (a monophonic line, legato). The last note is unchanged. */
export function legato(notes) {
  const sorted = [...notes].sort((a, b) => a.at - b.at);
  return sorted.map((n, i) => { const next = sorted[i + 1]; return next && next.at > n.at ? { ...n, length: Math.max(1, next.at - n.at) } : n; });
}

/** PR16: shortens every note to `percent` of its length (at least one tick), start unchanged. */
export function staccato(notes, percent) {
  const p = Math.max(1, Math.min(100, percent)) / 100;
  return notes.map((n) => ({ ...n, length: Math.max(1, Math.round(n.length * p)) }));
}

/** PR16: trims a note that runs into the next one (sorted by start) so no two overlap; non-overlapping notes are unchanged. */
export function removeOverlaps(notes) {
  const sorted = [...notes].sort((a, b) => a.at - b.at);
  return sorted.map((n, i) => { const next = sorted[i + 1]; return next && n.at + n.length > next.at ? { ...n, length: Math.max(1, next.at - n.at) } : n; });
}

/** PR17: flips pitch around `axisPitch` (the selection's own middle pitch, by default). */
export function flipPitch(notes, axisPitch) {
  const axis = axisPitch ?? (Math.min(...notes.map((n) => n.pitch)) + Math.max(...notes.map((n) => n.pitch))) / 2;
  return notes.map((n) => ({ ...n, pitch: Math.max(0, Math.min(127, Math.round(2 * axis - n.pitch))) }));
}

/** PR17: reverses the selection in time (the last note becomes the first), pitches unchanged. */
export function reverseTime(notes) {
  const minAt = Math.min(...notes.map((n) => n.at)), maxEnd = Math.max(...notes.map((n) => n.at + n.length));
  return notes.map((n) => ({ ...n, at: minAt + (maxEnd - (n.at + n.length)) }));
}

/** PR17: scales the selection in time by `factor` (2 doubles, 0.5 halves), anchored on the selection's own earliest start. */
export function scaleTime(notes, factor) {
  const minAt = Math.min(...notes.map((n) => n.at));
  return notes.map((n) => ({ ...n, at: minAt + Math.round((n.at - minAt) * factor), length: Math.max(1, Math.round(n.length * factor)) }));
}
