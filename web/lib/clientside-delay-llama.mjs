// Delay Llama in the browser: the same FOF voice as the engine (src/Ample.Audio/Engine/DelayLlama.cs), step for step and number for number, for the live preview of the
// block, the singing in its window and Jam. Notes in samples { start, length, pitch, velocity, fine }, settings as the engine reads them. @ample:delay-llama

// a e i o u (MonkSynth lists its points in the order u o a e i; here they follow the vowel axis, as the engine does)
const FORMANTS = [[800, 350, 270, 450, 280], [1150, 2000, 2140, 800, 600], [2900, 2800, 2950, 2830, 2240]];
const BANDWIDTHS = [32.5, 47.5, 62.5];

export const DEFAULTS = Object.freeze({ vowel: 0.5, voice: 0.5, mouth: 0.6, glideMs: 60, vibrato: 0.3, vibratoHz: 5.5, breath: 0.5, unison: 1, detuneCents: 12, spread: 0.6, attackMs: 20, decayMs: 200, sustain: 0.85, releaseMs: 180, gainDb: -6, echo: 0, leveler: 0.5 });

/** The frequency of a formant (0..2) at a vowel (0 a .. 1 u): a Catmull-Rom spline through the five vowel points. */
export function formant(index, vowel) {
  const p = FORMANTS[index], padded = [p[0], p[0], p[1], p[2], p[3], p[4], p[4]];
  const position = Math.min(1, Math.max(0, vowel)) * 4, segment = Math.min(3, Math.floor(position)), t = position - segment, t2 = t * t, t3 = t2 * t;
  const [p0, p1, p2, p3] = padded.slice(segment, segment + 4);
  return 0.5 * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
}

/** The settings of a block from its parameters (the text values of the song); vowel, voice and mouth also give a curve maker when they are auto(...). */
export function llamaSettings(params = {}) {
  const clean = (v) => String(v ?? '').replace(/^"|"$/g, '').trim();
  const num = (name, fallback, min, max) => { const v = Number.parseFloat(clean(params[name])); return Number.isFinite(v) ? Math.min(max, Math.max(min, v)) : fallback; };
  const shape = (name, fallback, vowel = false) => {
    const t = clean(params[name]);
    if (!t || /^\s*(auto|lfo|env)\(/.test(t)) return fallback;
    if (vowel && /^[aeiou]$/i.test(t)) return 'aeiou'.indexOf(t.toLowerCase()) / 4;
    const v = Number.parseFloat(t);
    return Number.isFinite(v) ? Math.min(1, Math.max(0, v / 100)) : fallback;
  };
  const d = DEFAULTS;
  return {
    vowel: shape('vowel', d.vowel, true), voice: shape('voice', d.voice), mouth: shape('mouth', d.mouth),
    glideMs: num('glide', d.glideMs, 0, 2000), vibrato: num('vibrato', d.vibrato * 100, 0, 100) / 100, vibratoHz: num('vibrato_rate', d.vibratoHz, 0.5, 12),
    breath: num('breath', d.breath * 100, 0, 100) / 100, unison: Math.round(num('unison', d.unison, 1, 7)), detuneCents: num('detune', d.detuneCents, 0, 100),
    spread: num('spread', d.spread * 100, 0, 100) / 100, attackMs: num('attack', d.attackMs, 0, 5000), decayMs: num('decay', d.decayMs, 0, 5000),
    sustain: num('sustain', d.sustain * 100, 0, 100) / 100, releaseMs: num('release', d.releaseMs, 0, 8000), gainDb: num('gain', d.gainDb, -48, 12), echo: num('echo', d.echo * 100, 0, 100) / 100, leveler: num('leveler', d.leveler * 100, 0, 100) / 100,
  };
}

const soft = (x) => (Math.abs(x) < 0.8 ? x : Math.sign(x) * (0.8 + 0.2 * Math.tanh((Math.abs(x) - 0.8) / 0.2)));

function echo(left, right, rate, amount) {
  const tapLeft = Math.floor(rate * 0.3096), tapRight = Math.floor(rate * 0.3984), feedback = 0.45;
  const lineLeft = new Float64Array(left.length), lineRight = new Float64Array(right.length);
  for (let i = 0; i < left.length; i++) {
    lineLeft[i] = left[i] * amount + (i >= tapLeft ? lineLeft[i - tapLeft] * feedback : 0);
    lineRight[i] = right[i] * amount + (i >= tapRight ? lineRight[i - tapRight] * feedback : 0);
  }
  for (let i = 0; i < left.length; i++) { left[i] += i >= tapLeft ? lineLeft[i - tapLeft] : 0; right[i] += i >= tapRight ? lineRight[i - tapRight] : 0; }
}

// one voice: the same steps as DelayLlama.Voice of the engine; onTrace (optional) hears the pitch, vowel, mouth and level every 512 samples (for the llama)
function voice(notes, s, rate, length, seed, cents, curves, onTrace) {
  const output = new Float64Array(length);
  const grainLength = Math.max(16, Math.floor(rate * 0.02));
  const window = new Float64Array(grainLength), breath = new Float64Array(grainLength);
  const attack = Math.max(1, Math.floor(rate * 0.0018)), releaseStart = Math.floor(rate * 0.013), releaseLength = Math.max(1, Math.floor(rate * 0.007));
  const period1 = rate * 0.000202, period2 = rate * 0.000263;
  for (let i = 0; i < grainLength; i++) {
    window[i] = i < attack ? 0.5 * (1 - Math.cos(Math.PI * i / attack)) : i >= releaseStart ? 0.5 * (1 - Math.cos(Math.PI * (releaseLength + i - releaseStart) / releaseLength)) : 1;
    breath[i] = Math.sin(2 * Math.PI * i / period1) * Math.exp(-50 * Math.PI * 3 * i / rate) + Math.sin(2 * Math.PI * i / period2) * Math.exp(-50 * Math.PI * 3.5 * i / rate);
  }
  const owner = new Int32Array(length).fill(-1);
  notes.forEach((n, index) => { for (let i = Math.max(0, n.start); i < Math.min(length, n.start + n.length); i++) owner[i] = index; });
  // the LCG of the engine (unsigned 32 bit)
  let rng = (Number(BigInt.asUintN(32, BigInt(seed) ^ (BigInt(seed) >> 32n))) | 1) >>> 0;
  const random = () => { rng = (Math.imul(rng, 1103515245) + 12345) >>> 0; return ((rng >>> 16) & 0x7fff) / 32768; };
  let vibratoPhase = random() * 2 * Math.PI, jitter = 1, glideFrom = NaN, glideTo = NaN, pitch = 60, glideStart = 0;
  const glideLength = Math.max(1, Math.floor(s.glideMs * rate / 1000));
  let jitterCounter = 0;
  const jitterPeriod = Math.max(1, Math.floor(rate * 0.104));
  let envelope = 0, level = 0, stage = 0, noteSince = 0, current = -1, sinceGrain = Number.MAX_VALUE;
  const attackStep = 1 / Math.max(1, s.attackMs * rate / 1000), decayStep = 1 / Math.max(1, s.decayMs * rate / 1000), releaseStep = 1 / Math.max(1, s.releaseMs * rate / 1000);
  for (let i = 0; i < length; i++) {
    const now = owner[i];
    if (now !== current) {
      if (now >= 0) {
        const note = notes[now], target = note.pitch + (note.fine || 0) / 100;
        const legato = current >= 0 && (stage === 1 || stage === 2 || stage === 3);
        if (legato && s.glideMs > 0) { glideFrom = pitch; glideTo = target; glideStart = i; }
        else { pitch = target; glideFrom = glideTo = NaN; stage = 1; noteSince = i; }
        if (!legato) noteSince = i;
        level = 0.35 + 0.65 * note.velocity / 127;
      } else stage = stage === 0 ? 0 : 4;
      current = now;
    }
    if (!Number.isNaN(glideTo)) {
      const t = Math.min(1, (i - glideStart) / glideLength);
      pitch = glideFrom + (glideTo - glideFrom) * (t * t * (3 - 2 * t));
      if (t >= 1) glideFrom = glideTo = NaN;
    }
    if (stage === 1) { envelope += attackStep; if (envelope >= 1) { envelope = 1; stage = 2; } }
    else if (stage === 2) { envelope -= decayStep * (1 - s.sustain); if (envelope <= s.sustain) { envelope = s.sustain; stage = 3; } }
    else if (stage === 4) { envelope -= releaseStep; if (envelope <= 0) { envelope = 0; stage = 0; } }
    if (++jitterCounter >= jitterPeriod) { jitterCounter = 0; jitter = 0.9 + random() * 0.2; }
    vibratoPhase += 2 * Math.PI * s.vibratoHz * jitter / rate;
    const onset = Math.min(1, (i - noteSince) / (rate * 0.3));
    const vibrato = (0.04 + s.vibrato * 0.6) * onset * Math.sin(vibratoPhase);
    const vowel = curves.vowel?.[i] ?? s.vowel, size = curves.voice?.[i] ?? s.voice, open = Math.min(1, Math.max(0, curves.mouth?.[i] ?? s.mouth));
    if (onTrace && (i & 511) === 0) onTrace(i, stage === 0 ? null : pitch + vibrato + (curves.bend ? curves.bend[i] : 0), vowel, open, stage === 0 ? 0 : envelope * level, s.vibrato);
    if (stage === 0) { sinceGrain = Number.MAX_VALUE; continue; }
    const sung = pitch + cents / 100 + vibrato + (curves.bend ? curves.bend[i] : 0);
    const quantised = Math.floor((sung - 12) * 32) / 32 + 12;
    const periodSamples = Math.max(1, rate / (440 * 2 ** ((quantised - 69) / 12)));
    sinceGrain++;
    if (sinceGrain < periodSamples) continue;
    const overshoot = Math.min(1, sinceGrain - periodSamples);
    sinceGrain = overshoot;
    const scale = size * 0.5 + 0.75;
    const f1 = formant(0, vowel) * scale * (0.75 + 0.45 * open), f2 = formant(1, vowel) * scale, f3 = formant(2, vowel) * scale;
    const breathAmount = s.breath * (1.2 - 0.7 * open);
    const compensate = Math.min(3, Math.max(0.1, (sung - 12) * (-1 / 72) + 2));
    const amplitude = 0.5 * compensate * envelope * level * (0.45 + 0.55 * open);
    for (let k = 0; k < grainLength && i + k < length; k++) {
      const t = (k + overshoot) / rate;
      const sample = Math.sin(2 * Math.PI * f1 * t) * Math.exp(-Math.PI * BANDWIDTHS[0] * t)
        + Math.sin(2 * Math.PI * f2 * t) * Math.exp(-Math.PI * BANDWIDTHS[1] * t) * (0.6 + 0.4 * open)
        + Math.sin(2 * Math.PI * f3 * t) * Math.exp(-Math.PI * BANDWIDTHS[2] * t) * 0.8
        + breath[k] * breathAmount;
      output[i + k] += sample * window[k] * amplitude;
    }
  }
  return output;
}

/** Sings the notes: { left, right } (Float32Array, -1..1). curves: { vowel, voice, mouth, bend } per sample (optional); onTrace(sample, pitch, vowel, mouth, level, vibrato) for the llama. */
export function renderLlama(notes, settings, rate, length, seed = 1, curves = {}, onTrace = null) {
  const s = { ...DEFAULTS, ...settings }, voices = Math.min(7, Math.max(1, Math.round(s.unison)));
  const ordered = notes.filter((n) => n.length > 0).sort((a, b) => a.start - b.start);
  const left = new Float64Array(length), right = new Float64Array(length);
  for (let v = 0; v < voices; v++) {
    const spread = voices === 1 ? 0 : (v / (voices - 1)) * 2 - 1, cents = spread * s.detuneCents, pan = spread * Math.min(1, Math.max(0, s.spread));
    const one = voice(ordered, s, rate, length, BigInt(seed) + BigInt(v) * 7919n, cents, curves, v === 0 ? onTrace : null);
    const gl = Math.cos((pan + 1) * Math.PI / 4), gr = Math.sin((pan + 1) * Math.PI / 4), share = 1 / Math.sqrt(voices);
    for (let i = 0; i < length; i++) { left[i] += one[i] * gl * share * Math.SQRT2; right[i] += one[i] * gr * share * Math.SQRT2; }
  }
  if (s.echo > 0) echo(left, right, rate, Math.min(1, s.echo));
  const gain = 10 ** (Math.min(12, Math.max(-48, s.gainDb)) / 20);
  const outLeft = new Float32Array(length), outRight = new Float32Array(length);
  // the leveler of the engine: what gets louder than about -10 dBFS is pulled back slowly (10 ms up, 250 ms down); it only turns down
  let envelope = 0;
  const attackCoefficient = 1 - Math.exp(-1 / (rate * 0.01)), releaseCoefficient = 1 - Math.exp(-1 / (rate * 0.25)), level = Math.min(1, Math.max(0, s.leveler));
  for (let i = 0; i < length; i++) {
    const l = left[i] * gain, r = right[i] * gain, peak = Math.max(Math.abs(l), Math.abs(r));
    envelope += (peak > envelope ? attackCoefficient : releaseCoefficient) * (peak - envelope);
    const g = 1 - level * (1 - (envelope > 0.3 ? 0.3 / envelope : 1));
    outLeft[i] = soft(l * g); outRight[i] = soft(r * g);
  }
  return { left: outLeft, right: outRight };
}

/** The notes of a midi input ({ bar, beat (quarters), beats, pitch, velocity, fine }) as the notes of the voice, in samples from the first bar. */
export function llamaNotes(notes, firstBar, samplesPerBar) {
  return notes.map((n) => ({ start: Math.round((n.bar - firstBar) * samplesPerBar + (Number(n.beat) || 0) * samplesPerBar / 4), length: Math.max(1, Math.round((Number(n.beats) || 1) * samplesPerBar / 4)), pitch: n.pitch, velocity: n.velocity ?? 96, fine: n.fine || 0 }));
}

/** A curve (0..1 per sample) of a vowel, voice or mouth setting written as auto(bar:percent, ...), or null; the points are joined by straight lines like the engine. */
export function llamaCurve(text, firstBar, samplesPerBar, length) {
  const m = /^\s*auto\((.*)\)\s*$/s.exec(String(text ?? '').replace(/^"|"$/g, ''));
  if (!m) return null;
  const points = m[1].split(',').map((p) => p.trim()).filter(Boolean).map((p) => { const [at, value] = p.split(':'); return { at: (Number(at) - 1) * samplesPerBar, value: Math.min(100, Math.max(0, Number.parseInt(value, 10))) }; }).filter((p) => Number.isFinite(p.at) && Number.isFinite(p.value)).sort((a, b) => a.at - b.at);
  if (!points.length) return null;
  const curve = new Float64Array(length);
  let segment = 0;
  for (let i = 0; i < length; i++) {
    const absolute = (firstBar - 1) * samplesPerBar + i;
    while (segment + 1 < points.length && absolute >= points[segment + 1].at) segment++;
    const a = points[segment], b = points[segment + 1];
    const v = absolute < points[0].at || !b ? (absolute < points[0].at ? points[0].value : points.at(-1).value) : a.value + (b.value - a.value) * (absolute - a.at) / Math.max(1, b.at - a.at);
    curve[i] = v / 100;
  }
  return curve;
}
