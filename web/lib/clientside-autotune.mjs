// The autotune of the engine (src/Ample.Audio/Engine/ExtraAutotune.cs) in the browser, for the window and its live preview: the pitch is followed with YIN every 10 ms,
// every moment gets the note of the key, of the melody or of a hand fix, and a PSOLA with a ratio per step moves the voice there. Same steps and numbers as the engine,
// so what you hear here is what the build makes (the engine has the final word). @ample:autotune

const NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];
const MODES = {
  major: [0, 2, 4, 5, 7, 9, 11], minor: [0, 2, 3, 5, 7, 8, 10], 'harmonic-minor': [0, 2, 3, 5, 7, 8, 11], dorian: [0, 2, 3, 5, 7, 9, 10], mixolydian: [0, 2, 4, 5, 7, 9, 10],
  pentatonic: [0, 2, 4, 7, 9], 'minor-pentatonic': [0, 3, 5, 7, 10], blues: [0, 3, 5, 6, 7, 10], chromatic: [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
};
const MAJOR = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
const MINOR = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];
const pc = (n) => ((Math.round(n) % 12) + 12) % 12;

/** The MIDI number of a note name (E4, F#3, Bb2; C4 is 60), or null. */
export function noteNumber(word) {
  const m = /^([A-Ga-g])(#|b)?(-?[0-9])$/.exec(String(word).trim());
  if (!m) return null;
  const n = (Number(m[3]) + 1) * 12 + { C: 0, D: 2, E: 4, F: 5, G: 7, A: 9, B: 11 }[m[1].toUpperCase()] + (m[2] === '#' ? 1 : m[2] === 'b' ? -1 : 0);
  return n >= 0 && n <= 127 ? n : null;
}

/** The name of a MIDI number (60 is C4). */
export const noteName = (n) => `${NAMES[pc(n)]}${Math.floor(Math.round(n) / 12) - 1}`;

function time(word, rate, samplesPerBar, chunk) {
  const m = /^([0-9]+(?:\.[0-9]+)?)(s|ms|bars?)?$/i.exec(word);
  if (!m) throw new Error(`'${word}' in '${chunk}' is not a time: write seconds (6s), milliseconds (500ms) or bars (4.5).`);
  const n = Number(m[1]), unit = (m[2] || '').toLowerCase();
  return Math.round(unit === 's' ? n * rate : unit === 'ms' ? n * rate / 1000 : n * samplesPerBar);
}

/** Reads the fixes text (4.2s-4.7s E4; 7.9s-8.4s G4 vibrato 0%; 12s-13s off) into { from, to, note, vibrato } in samples; throws with the message of the engine. */
export function parseFixes(text, rate, samplesPerBar) {
  const out = [];
  for (const chunk of String(text || '').split(/[;\n]/).map((c) => c.trim()).filter(Boolean)) {
    const words = chunk.split(/[ \t]+/), span = words[0].split('-');
    if (span.length !== 2 || words.length < 2) throw new Error(`'${chunk}' is not a fix: write a time span and a note, such as 4.2s-4.7s E4 (or off to leave it alone).`);
    const from = time(span[0], rate, samplesPerBar, chunk), to = time(span[1], rate, samplesPerBar, chunk);
    if (to <= from) throw new Error(`The span of '${chunk}' ends before it starts.`);
    let note = null;
    if (words[1].toLowerCase() !== 'off') { note = noteNumber(words[1]); if (note === null) throw new Error(`'${words[1]}' in '${chunk}' is not a note: write a name and an octave, such as E4, F#3 or Bb2, or off.`); }
    let vibrato = null;
    for (let i = 2; i < words.length; i += 2) {
      const v = Number.parseFloat(String(words[i + 1] ?? '').replace(/%$/, ''));
      if (words[i].toLowerCase() !== 'vibrato' || !Number.isFinite(v)) throw new Error(`'${words[i]}' in '${chunk}' is not known: after the note only vibrato 0% to 100% may follow.`);
      vibrato = Math.min(100, Math.max(0, Math.round(v)));
    }
    out.push({ from, to, note, vibrato });
  }
  return out.sort((a, b) => a.from - b.from);
}

/** Writes fixes in seconds ({ from, to, note, vibrato } with from and to in seconds) back to the text of the parameter. */
export function formatFixes(fixes) {
  const r = (n) => Math.round(n * 100) / 100;
  return fixes.slice().sort((a, b) => a.from - b.from).map((f) => `${r(f.from)}s-${r(f.to)}s ${f.note === null ? 'off' : noteName(f.note)}${f.vibrato === null || f.vibrato === undefined ? '' : ` vibrato ${f.vibrato}%`}`).join('; ');
}

// the period at the full rate: only lags close to the coarse one are tried, the lowest difference wins (with a parabola through it)
function refine(x, start, length, coarse, reach) {
  const from = Math.max(2, Math.floor(coarse) - reach - 1), to = Math.ceil(coarse) + reach + 1, difference = new Float64Array(to - from + 1);
  for (let lag = from; lag <= to; lag++) {
    let sum = 0;
    for (let j = 0; j < length; j++) { const i = start + j, k = i + lag; const va = i >= 0 && i < x.length ? x[i] : 0, vb = k >= 0 && k < x.length ? x[k] : 0; sum += (va - vb) * (va - vb); }
    difference[lag - from] = sum;
  }
  let best = 0;
  for (let i = 1; i < difference.length; i++) if (difference[i] < difference[best]) best = i;
  if (best === 0 || best === difference.length - 1) return coarse;
  const l = difference[best - 1], m = difference[best], r = difference[best + 1], bend = l - 2 * m + r;
  return from + best + (bend > 0 ? 0.5 * (l - r) / bend : 0);
}

// a value an octave away from the voice around it (the median of the voiced steps within 100 ms) is an octave error of the tracking: it is moved back
function fixOctaves(midi) {
  const copy = Float64Array.from(midi);
  for (let f = 0; f < midi.length; f++) {
    if (Number.isNaN(copy[f])) continue;
    const around = [];
    for (let k = f - 10; k <= f + 10; k++) if (k !== f && k >= 0 && k < copy.length && !Number.isNaN(copy[k])) around.push(copy[k]);
    if (around.length < 4) continue;
    around.sort((p, q) => p - q);
    const gap = copy[f] - around[around.length >> 1];
    if (Math.abs(gap - 12) < 1) midi[f] -= 12; else if (Math.abs(gap + 12) < 1) midi[f] += 12;
  }
}

/** Follows the pitch of a mono Float32Array (-1..1): { hop (samples), midi (NaN where not voiced) }; the period is refined at the full rate and octave errors are moved back. */
export function detectPitch(x, rate) {
  const d = Math.max(1, Math.floor(rate / 11025)), sr = Math.floor(rate / d);
  const s = new Float64Array(Math.floor(x.length / d) + 1);
  for (let k = 0; k < s.length; k++) {
    let sum = 0, n = 0;
    for (let j = k * d; j < k * d + d && j < x.length; j++, n++) sum += x[j];
    s[k] = n ? sum / n : 0;
  }
  const w = Math.floor(sr * 40 / 1000), minLag = Math.max(2, Math.floor(sr / 1000)), maxLag = Math.floor(sr / 65), hop = Math.max(1, Math.floor(sr / 100));
  const frames = Math.floor(s.length / hop) + 1, midi = new Float64Array(frames), diff = new Float64Array(maxLag + 2), floor = 10 ** (-50 / 20);
  const at = (i) => (i >= 0 && i < s.length ? s[i] : 0);
  for (let f = 0; f < frames; f++) {
    midi[f] = NaN;
    const start = f * hop - Math.floor((w + maxLag) / 2);
    let energy = 0;
    for (let j = 0; j < w; j++) energy += at(start + j) ** 2;
    if (Math.sqrt(energy / w) < floor) continue;
    let running = 0, best = -1;
    for (let tau = 1; tau <= maxLag; tau++) {
      let sum = 0;
      for (let j = 0; j < w; j++) { const delta = at(start + j) - at(start + j + tau); sum += delta * delta; }
      running += sum;
      diff[tau] = running > 0 ? sum * tau / running : 1;
    }
    for (let tau = minLag; tau <= maxLag; tau++) {
      if (diff[tau] < 0.15) { while (tau + 1 <= maxLag && diff[tau + 1] < diff[tau]) tau++; best = tau; break; }
    }
    if (best < 0) {
      let lowest = minLag;
      for (let tau = minLag; tau <= maxLag; tau++) if (diff[tau] < diff[lowest]) lowest = tau;
      best = diff[lowest] < 0.3 ? lowest : -1;
    }
    if (best < 0) continue;
    // a weak fundamental (a formant voice): when the period found is twice the true one, half of it is (nearly) as good a period: take it
    const half = Math.floor((best + 1) / 2);
    if (half >= minLag) {
      let h = half;
      for (let k = Math.max(minLag, half - 1); k <= Math.min(maxLag, half + 1); k++) if (diff[k] < diff[h]) h = k;
      if (diff[h] < 0.3 && diff[h] < 2.5 * diff[best] + 0.08) best = h;
    }
    let refined = best;
    if (best > 1 && best < maxLag) { const a = diff[best - 1], b = diff[best], c = diff[best + 1], bend = a - 2 * b + c; if (bend > 0) refined = best + 0.5 * (a - c) / bend; }
    midi[f] = 69 + 12 * Math.log2(rate / refine(x, f * hop * d - Math.floor(w * d / 2), w * d, refined * d, d) / 440);
  }
  const copy = Float64Array.from(midi);
  for (let f = 1; f + 1 < frames; f++) if (!Number.isNaN(copy[f - 1]) && !Number.isNaN(copy[f]) && !Number.isNaN(copy[f + 1])) midi[f] = [copy[f - 1], copy[f], copy[f + 1]].sort((a, b) => a - b)[1];
  for (let f = 0; f < frames;) {
    if (Number.isNaN(midi[f])) { f++; continue; }
    let end = f;
    while (end < frames && !Number.isNaN(midi[end])) end++;
    if (end - f < 3) for (let i = f; i < end; i++) midi[i] = NaN;
    f = end;
  }
  fixOctaves(midi);
  return { hop: hop * d, midi };
}

const mask = (root, steps) => { const m = new Array(12).fill(false); for (const s of steps) m[(root + s) % 12] = true; return m; };

/** The scale of a key text (auto, chromatic, A minor, F# major, D dorian...): { scale: 12 booleans, name }; throws for a key that cannot be read. */
export function keyScale(key, track) {
  const text = String(key ?? '').trim();
  if (!text || text.toLowerCase() === 'auto') {
    const weight = new Array(12).fill(0);
    let voiced = 0;
    for (const m of track.midi) if (!Number.isNaN(m)) { weight[pc(m)]++; voiced++; }
    if (voiced < 50) return { scale: mask(0, MODES.chromatic), name: 'chromatic' };
    let best = { score: -Infinity, root: 0, minor: false };
    for (let root = 0; root < 12; root++) for (const minor of [false, true]) {
      const profile = minor ? MINOR : MAJOR;
      let score = 0;
      for (let p = 0; p < 12; p++) score += weight[p] * profile[(((p - root) % 12) + 12) % 12];
      if (score > best.score) best = { score, root, minor };
    }
    return { scale: mask(best.root, MODES[best.minor ? 'minor' : 'major']), name: `${NAMES[best.root]} ${best.minor ? 'minor' : 'major'}` };
  }
  if (text.toLowerCase() === 'chromatic') return { scale: mask(0, MODES.chromatic), name: 'chromatic' };
  const [rootWord, ...rest] = text.split(/\s+/);
  const root = noteNumber(`${rootWord}4`), mode = (rest.join('-') || 'major').toLowerCase();
  if (root === null || !MODES[mode]) throw new Error(`'${key}' is not a key: write auto, chromatic, or a root and a scale such as A minor, F# major or D dorian (major, minor, harmonic-minor, dorian, mixolydian, pentatonic, minor-pentatonic, blues).`);
  return { scale: mask(root % 12, MODES[mode]), name: text };
}

function nearest(m, scale, previous) {
  if (!Number.isNaN(previous) && Math.abs(m - previous) < 0.65 && scale[pc(previous)]) return previous;
  const center = Math.round(m);
  for (let distance = 0; distance <= 6; distance++) {
    const below = center - distance, above = center + distance, okBelow = scale[pc(below)], okAbove = scale[pc(above)];
    if (okBelow && okAbove) return Math.abs(m - below) <= Math.abs(m - above) ? below : above;
    if (okBelow) return below;
    if (okAbove) return above;
  }
  return center;
}

/** The note every step should be (NaN: left alone) and the correction in semitones per step. settings: { scale, speedMs, amount, humanize, vibrato } (0..1), melody: [{ from, to, note }], fixes in samples. */
export function corrections(track, settings, melody = [], fixes = []) {
  const { midi, hop } = track, n = midi.length;
  const target = new Float64Array(n).fill(NaN), vibrato = new Float64Array(n).fill(settings.vibrato);
  for (let f = 0; f < n; f++) {
    if (Number.isNaN(midi[f])) continue;
    const at = f * hop;
    const fix = fixes.filter((x) => x.from <= at && at < x.to).at(-1);
    if (fix) { vibrato[f] = fix.vibrato === null || fix.vibrato === undefined ? settings.vibrato : fix.vibrato / 100; if (fix.note !== null) target[f] = fix.note; continue; }
    const sung = melody.filter((x) => x.from <= at && at < x.to).at(-1);
    if (sung) { target[f] = sung.note + 12 * Math.round((midi[f] - sung.note) / 12); continue; }
    target[f] = nearest(midi[f], settings.scale, f > 0 ? target[f - 1] : NaN);
  }
  const mean = new Float64Array(n);
  for (let f = 0; f < n; f++) {
    if (Number.isNaN(midi[f])) continue;
    let sum = 0, count = 0;
    // only steps that go to the same note count, so a short note is not pulled by the notes next to it
    for (let k = f - 7; k <= f + 7; k++) if (k >= 0 && k < n && !Number.isNaN(midi[k]) && (k === f || Object.is(target[k], target[f]))) { sum += midi[k]; count++; }
    mean[f] = sum / count;
  }
  const raw = new Float64Array(n);
  let run = 0;
  for (let f = 0; f < n; f++) {
    if (Number.isNaN(midi[f]) || Number.isNaN(target[f])) { run = 0; continue; }
    const from = vibrato[f] * mean[f] + (1 - vibrato[f]) * midi[f];
    const slope = f > 0 && !Number.isNaN(midi[f - 1]) ? Math.abs(midi[f] - midi[f - 1]) : 0;
    const slide = Math.min(1, Math.max(0, (slope - 0.25) / 0.5)), attack = run < 6 ? 1 - run / 6 : 0;
    raw[f] = Math.min(12, Math.max(-12, (target[f] - from) * (1 - settings.humanize * Math.max(slide, attack)) * settings.amount));
    run++;
  }
  const follow = settings.speedMs <= 0 ? 1 : 1 - Math.exp(-10 / settings.speedMs), release = 1 - Math.exp(-10 / 20);
  const smooth = new Float64Array(n);
  let state = 0, before = false;
  for (let f = 0; f < n; f++) {
    const voiced = !Number.isNaN(midi[f]) && !Number.isNaN(target[f]);
    if (voiced && !before) state = raw[f];
    state += (voiced ? follow : release) * ((voiced ? raw[f] : 0) - state);
    smooth[f] = state;
    before = voiced;
  }
  return { target, correction: smooth };
}

/** The correction at a sample: the values of the track steps joined by straight lines, so the pitch never moves in steps of 10 ms. */
export function correctionAt(values, hop, position) {
  const step = position / Math.max(1, hop);
  const a = Math.min(values.length - 1, Math.max(0, Math.floor(step))), b = Math.min(a + 1, values.length - 1), t = Math.min(1, Math.max(0, step - a));
  return values[a] + (values[b] - values[a]) * t;
}

// 1 where a voice is heard and corrected; breath, consonants and silence keep the original sound
function wetSteps(track, correction) {
  return Float64Array.from(correction, (c, f) => (!Number.isNaN(track.midi[f]) && Math.abs(c) > 0.005 ? 1 : 0));
}

/** Moves the pitch of every channel (Float32Arrays) by the correction of its moment (PSOLA, or the spectral method) and blends it with the original where nothing is corrected. */
export function applyCorrections(channels, rate, track, correction, method = 'spectral') {
  if (correction.every((c) => Math.abs(c) < 1e-4)) return channels;
  const tuned = method === 'spectral' ? spectral(channels, rate, (position) => correctionAt(correction, track.hop, position), true) : psola(channels, rate, track, correction);
  const wet = wetSteps(track, correction);
  return channels.map((ch, c) => {
    const out = new Float32Array(ch.length);
    for (let i = 0; i < ch.length; i++) { const w = correctionAt(wet, track.hop, i); out[i] = w * tuned[c][i] + (1 - w) * ch[i]; }
    return out;
  });
}

function psola(channels, rate, track, correction) {
  const length = channels[0].length, fallback = Math.max(2, Math.floor(rate / 100));
  const step = (p) => Math.min(track.midi.length - 1, Math.max(0, Math.floor(p / Math.max(1, track.hop))));
  // the period with its fraction sets the distance of the windows; a rounded one would put the pitch beside the note
  const exactPeriodAt = (p) => { const m = track.midi[step(p)]; return Number.isNaN(m) ? fallback : Math.max(2, rate / (440 * 2 ** ((m - 69) / 12))); };
  const periodAt = (p) => Math.max(2, Math.round(exactPeriodAt(p)));
  const sum = new Float64Array(length);
  for (const ch of channels) for (let i = 0; i < length; i++) sum[i] += ch[i];
  const marks = [];
  let mark = 0;
  while (mark < length) {
    const p = periodAt(mark);
    let peak = mark;
    if (!Number.isNaN(track.midi[step(mark)])) {
      const lo = Math.max(0, mark - Math.floor(p / 4)), hi = Math.min(length - 1, mark + Math.floor(p / 4));
      for (let i = lo; i <= hi; i++) if (Math.abs(sum[i]) > Math.abs(sum[peak])) peak = i;
    }
    if (!marks.length || peak > marks.at(-1)) marks.push(peak);
    mark = Math.max(mark + 2, peak + periodAt(peak));
  }
  const y = channels.map(() => new Float64Array(length)), weight = new Float64Array(length);
  let at = marks[0], nearestMark = 0;
  while (at < length) {
    while (nearestMark + 1 < marks.length && Math.abs(marks[nearestMark + 1] - at) <= Math.abs(marks[nearestMark] - at)) nearestMark++;
    const center = marks[nearestMark], p = periodAt(center), place = Math.round(at);
    for (let i = -p; i < p; i++) {
      const o = place + i, src = center + i;
      if (o < 0 || o >= length || src < 0 || src >= length) continue;
      const w = (p - Math.abs(i)) / p;
      for (let c = 0; c < channels.length; c++) y[c][o] += channels[c][src] * w;
      weight[o] += w;
    }
    at += Math.max(1, exactPeriodAt(center) / 2 ** (correctionAt(correction, track.hop, place) / 12));
  }
  return y.map((out, c) => { for (let i = 0; i < length; i++) out[i] = weight[i] > 1e-6 ? out[i] / Math.max(weight[i], 0.5) : channels[c][i]; return out; });
}

// an in-place radix-2 FFT on two arrays (the inverse one is scaled by 1/n)
function fft(re, im, inverse) {
  const n = re.length;
  for (let i = 1, j = 0; i < n; i++) {
    let bit = n >> 1;
    for (; j & bit; bit >>= 1) j ^= bit;
    j ^= bit;
    if (i < j) { [re[i], re[j]] = [re[j], re[i]]; [im[i], im[j]] = [im[j], im[i]]; }
  }
  for (let len = 2; len <= n; len <<= 1) {
    const angle = 2 * Math.PI / len * (inverse ? 1 : -1), wr = Math.cos(angle), wi = Math.sin(angle);
    for (let i = 0; i < n; i += len) {
      let cr = 1, ci = 0;
      for (let k = 0; k < len / 2; k++) {
        const a = i + k, b = a + len / 2, vr = re[b] * cr - im[b] * ci, vi = re[b] * ci + im[b] * cr;
        re[b] = re[a] - vr; im[b] = im[a] - vi; re[a] += vr; im[a] += vi;
        const t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
      }
    }
  }
  if (inverse) for (let i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
}

/** Moves the pitch of every channel by semitonesAt(sample) with the spectral method; keepFormants keeps the colour of a voice, without it the colour moves along like a tape. */
export const bendChannels = (channels, rate, semitonesAt, keepFormants = false) => spectral(channels, rate, semitonesAt, keepFormants).map((c) => Float32Array.from(c));

// the spectral method: the fine structure of every frame is moved by the ratio, the spectral envelope (from the cepstrum) stays, the phases follow the true frequencies
function spectral(channels, rate, semitonesAt, keepFormants) {
  let n = 1;
  while (n < Math.max(512, Math.floor(rate / 22))) n <<= 1;
  const hop = n / 4, half = n / 2, lifter = Math.max(8, Math.floor(rate * 12 / 10000));
  const window = Float64Array.from({ length: n }, (_, j) => 0.5 - 0.5 * Math.cos(2 * Math.PI * j / n));
  const length = channels[0].length;
  return channels.map((x) => {
    const y = new Float64Array(length), norm = new Float64Array(length);
    const lastPhase = new Float64Array(half + 1), sumPhase = new Float64Array(half + 1), magnitude = new Float64Array(half + 1), trueBin = new Float64Array(half + 1), envelope = new Float64Array(half + 1);
    const outMagnitude = new Float64Array(half + 1), outBin = new Float64Array(half + 1);
    const re = new Float64Array(n), im = new Float64Array(n), cr = new Float64Array(n), ci = new Float64Array(n);
    for (let start = -n + hop; start < length; start += hop) {
      for (let j = 0; j < n; j++) { const i = start + j; re[j] = i >= 0 && i < length ? x[i] * window[j] : 0; im[j] = 0; }
      fft(re, im, false);
      cr.fill(0); ci.fill(0);
      for (let k = 0; k <= half; k++) {
        magnitude[k] = Math.hypot(re[k], im[k]);
        const phase = Math.atan2(im[k], re[k]);
        let delta = phase - lastPhase[k] - 2 * Math.PI * hop * k / n;
        delta -= 2 * Math.PI * Math.round(delta / (2 * Math.PI));
        trueBin[k] = k + delta * n / (2 * Math.PI * hop);
        lastPhase[k] = phase;
        const log = Math.log(magnitude[k] + 1e-3 / 32768);
        cr[k] = log;
        if (k > 0 && k < half) cr[n - k] = log;
      }
      if (keepFormants) {
        fft(cr, ci, true);
        for (let q = lifter + 1; q < n - lifter; q++) { cr[q] = 0; ci[q] = 0; }
        fft(cr, ci, false);
      }
      for (let k = 0; k <= half; k++) envelope[k] = keepFormants ? Math.exp(cr[k]) : 1;
      const ratio = 2 ** (semitonesAt(start + half) / 12);
      outMagnitude.fill(0); outBin.fill(0);
      for (let k = 0; k <= half; k++) {
        const to = Math.round(k * ratio);
        if (to > half) break;
        outMagnitude[to] += magnitude[k] / envelope[k];
        outBin[to] = trueBin[k] * ratio;
      }
      re.fill(0); im.fill(0);
      for (let k = 0; k <= half; k++) {
        sumPhase[k] += 2 * Math.PI * hop * outBin[k] / n;
        const m = outMagnitude[k] * envelope[k];
        re[k] = m * Math.cos(sumPhase[k]); im[k] = m * Math.sin(sumPhase[k]);
        if (k > 0 && k < half) { re[n - k] = re[k]; im[n - k] = -im[k]; }
      }
      fft(re, im, true);
      for (let j = 0; j < n; j++) { const i = start + j; if (i >= 0 && i < length) { y[i] += re[j] * window[j]; norm[i] += window[j] * window[j]; } }
    }
    for (let i = 0; i < length; i++) y[i] = norm[i] > 1e-6 ? y[i] / norm[i] : x[i];
    return y;
  });
}

/** The settings of a block from its parameters (the text values of the song). */
export function autotuneSettings(params, track) {
  const num = (v, fallback) => { const n = Number.parseFloat(String(v ?? '').replace(/^"|"$/g, '')); return Number.isFinite(n) ? n : fallback; };
  const clean = (v) => String(v ?? '').replace(/^"|"$/g, '').trim();
  const vib = clean(params.vibrato);
  const { scale, name } = keyScale(clean(params.key) || 'auto', track);
  return {
    key: name,
    scale,
    speedMs: Math.min(1000, Math.max(0, num(params.speed, 20))),
    amount: Math.min(100, Math.max(0, num(params.amount, 100))) / 100,
    humanize: Math.min(100, Math.max(0, num(params.humanize, 30))) / 100,
    vibrato: vib === 'flatten' ? 0 : vib === 'keep' || vib === '' ? 1 : Math.min(100, Math.max(0, num(vib, 100))) / 100,
  };
}

/** The notes the voice is pulled to, grouped: runs of steps with the same target note, as { from, to (seconds), note, sung (mean sung pitch) }. */
export function noteRuns(track, target, rate) {
  const runs = [];
  for (let f = 0; f < target.length;) {
    if (Number.isNaN(target[f])) { f++; continue; }
    let end = f, sum = 0;
    while (end < target.length && target[end] === target[f]) { sum += track.midi[end]; end++; }
    runs.push({ from: f * track.hop / rate, to: end * track.hop / rate, note: target[f], sung: sum / (end - f) });
    f = end;
  }
  return runs;
}

/** Joins the pieces of the note runs: a piece shorter than `shortest` seconds right next to another (a gap of at most `gap`) joins it, with the note of the longer one;
 * what is left shorter than 50 ms is not shown. The window draws these as the notes. */
export function mergeRuns(runs, shortest = 0.08, gap = 0.03) {
  const out = [];
  for (const r of runs) {
    const previous = out.at(-1);
    const short = (q) => q.to - q.from < shortest;
    // a short piece joins its neighbour, and two pieces of the same note that touch (around a blip that joined one of them) become one note
    if (previous && r.from - previous.to <= gap && (((short(r) || short(previous)) && Math.abs(r.note - previous.note) <= 2) || r.note === previous.note)) {
      const longer = r.to - r.from > previous.to - previous.from ? r : previous;
      const sung = (previous.sung * (previous.to - previous.from) + r.sung * (r.to - r.from)) / Math.max(1e-9, (previous.to - previous.from) + (r.to - r.from));
      out[out.length - 1] = { from: previous.from, to: r.to, note: longer.note, sung };
    } else out.push({ ...r });
  }
  return out.filter((r) => r.to - r.from >= 0.05);
}
