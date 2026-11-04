// The rewind block in the browser, the same arithmetic as src/Ample.Audio/Engine/ExtraRewind.cs (S09): the music stops, only a scratch is heard, the music goes on from an earlier
// (or later) place of the same sound. Times are those of the block. Pure, no DOM. @ample:rewind

const clamp = (value, low, high) => Math.min(high, Math.max(low, value));
const read = (text, fallback) => { const n = Number.parseFloat(String(text ?? '').trim()); return Number.isFinite(n) ? n : fallback; };

/** Reads the events text: `at 6s scratch 2s back 5s; at 14s scratch 1s back -3s`. A time is seconds (6s), milliseconds (500ms) or decimal bars from the start of the block (4.5). Throws an Error with a plain sentence. */
export function parseRewindEvents(text, rate, samplesPerBar) {
  const time = (word, chunk) => {
    const m = /^(-?\d+(?:\.\d+)?)(s|ms|bar|bars)?$/i.exec(word);
    if (!m) throw new Error(`'${word}' in '${chunk}' is not a time: write seconds (6s), milliseconds (500ms) or bars (4.5).`);
    const unit = (m[2] || '').toLowerCase(), n = Number(m[1]);
    const samples = unit === 's' ? n * rate : unit === 'ms' ? n * rate / 1000 : n * samplesPerBar;
    if (Math.abs(samples) > 600 * rate) throw new Error(`'${word}' in '${chunk}' is further than 10 minutes.`);
    return Math.round(samples);
  };
  const events = [];
  for (const chunk of String(text ?? '').split(/[;\n]/).map((c) => c.trim()).filter(Boolean)) {
    const words = chunk.split(/\s+/);
    let at = null, scratch = rate, back = 0;
    for (let i = 0; i < words.length; i += 2) {
      if (i + 1 >= words.length) throw new Error(`'${chunk}' ends after '${words[i]}': give a time after it.`);
      const value = time(words[i + 1], chunk), key = words[i].toLowerCase();
      if (key === 'at') at = value; else if (key === 'scratch') scratch = value; else if (key === 'back') back = value;
      else throw new Error(`'${words[i]}' in '${chunk}' is not known: use at, scratch and back.`);
    }
    if (at === null || at < 0) throw new Error(`'${chunk}' has no 'at' (a time from the start of the block).`);
    if (scratch < 0 || scratch > 30 * rate) throw new Error(`The scratch of '${chunk}' must last 0 to 30 seconds.`);
    events.push({ at, scratch, back });
  }
  return events.sort((a, b) => a.at - b.at);
}

// how far the record has turned after u (0..1) of a start-up of n samples; the motor gets going fast and then settles, the straight one rises evenly
const up = (u, n, motor) => n * (motor ? (u + (Math.exp(-5 * u) - 1) / 5) / (1 - Math.exp(-5)) : u * u / 2);
// and for a brake: the motor slows down roughly quadratically
const down = (u, n, motor) => n * (motor ? (1 - (1 - u) ** 3) / 3 : u - u * u / 2);

/** Rewinds one channel. `fx` is the channel of a real scratch sample or null. Returns { out, region } (region: the scratches alone). */
export function rewindChannel(src, fx, events, params, rate, signal) {
  const length = src.length, mix = new Float64Array(length), region = new Float64Array(length);
  const sound = params.sound || 'auto', fadeMs = clamp(read(params.fade, 5), 0, 50), hardness = clamp(read(params.hardness, 100), 0, 100), blend = clamp(read(params.blend, 120), 0, 500);
  const smearLength = Math.trunc(clamp(read(params.smear, 0), 0, 2000) * rate / 1000), brakeLength = Math.trunc(clamp(read(params.brake, 0), 0, 2000) * rate / 1000);
  const motor = params.curve !== 'linear', loop = params.end === 'loop', fade = Math.trunc(fadeMs * rate / 1000);
  const half = Math.trunc(blend * (100 - hardness) / 100 * rate / 1000 / 2), overlap = half * 2, fadeLength = overlap > 0 ? overlap : fade;
  const readAt = (position) => {
    if (position < 0) position = 0;
    if (position >= length - 1) {
      if (!loop || length === 0) return position >= length ? 0 : src[length - 1];
      position %= length;
    }
    const index = Math.trunc(position), fraction = position - index;
    return src[index] * (1 - fraction) + src[Math.min(length - 1, index + 1)] * fraction;
  };
  const advance = (i, upLength) => (i < upLength ? up(i / upLength, upLength, motor) : (upLength > 0 ? up(1, upLength, motor) : 0) + (i - upLength));
  const music = (start, count, from, upLength, brake, fadeIn, fadeOut) => {
    count = Math.min(count, length - start);
    for (let i = 0; i < count; i++) {
      if ((i & 0xFFFF) === 0) signal?.throwIfAborted();
      let moved = advance(i, upLength);
      const brakeStart = count - brake;
      if (brake > 0 && i >= brakeStart) moved = advance(brakeStart, upLength) + down((i - brakeStart) / brake, brake, motor);
      const gain = (fadeIn > 0 && i < fadeIn ? i / fadeIn : 1) * (fadeOut > 0 && count - 1 - i < fadeOut ? (count - 1 - i) / fadeOut : 1);
      mix[start + i] += readAt(from + moved) * gain;
    }
  };

  let t = 0, p = 0, smear = 0, fadeInNext = 0;
  for (const e of events) {
    if (e.at < t + half || e.at >= length) continue;
    const musicEnd = Math.min(length, e.at + half);
    music(t, musicEnd - t, p, smear, Math.min(brakeLength, e.at - t), fadeInNext, fadeLength);
    const position = p + advance(e.at - t, smear), scratch = Math.min(e.scratch, length - e.at);
    for (let j = -half; j < scratch + half; j++) {
      const at = e.at + j;
      if (at < 0 || at >= length) continue;
      let value;
      if (j < 0) value = readAt(position + j);
      else if (j >= scratch) value = 0;
      else if (sound === 'fx' || (sound === 'auto' && fx)) {
        const f = Math.min(fade, Math.trunc(scratch / 4));
        value = j < fx.length ? fx[j] * (f > 0 && scratch - 1 - j < f ? (scratch - 1 - j) / f : 1) : 0;
      } else if (sound === 'silence') value = 0;
      else value = readAt(position - 4 * (j - j * j / (2 * Math.max(1, scratch)))) * (scratch - j) / Math.max(1, scratch);
      let edge = 1;
      if (overlap > 0) edge = Math.min(1, (j + half + 1) / overlap) * Math.min(1, (scratch + half - j) / overlap);
      else if (fade > 0 && j >= 0 && j < scratch) edge = Math.min(1, (j + 1) / fade) * Math.min(1, (scratch - j) / fade);
      mix[at] += value * edge;
      if (j >= 0 && j < scratch) region[at] += value * edge;
    }
    p = Math.max(0, position - e.back);
    t = Math.max(0, e.at + scratch - half);
    smear = smearLength;
    fadeInNext = fadeLength;
  }
  music(t, length - t, p, smear, 0, fadeInNext, 0);
  const clip = (a) => { const out = new Float32Array(length); for (let i = 0; i < length; i++) out[i] = clamp(a[i], -1, 1); return out; };
  return { out: clip(mix), region: clip(region) };
}

/** Rewinds every channel of a decoded audio: { sampleRate, channels }. `fx` is a decoded scratch sample or null. Returns the audio and the scratches alone as a second audio. */
export function rewindAudio(audio, params, samplesPerBar, fx, signal) {
  params = Object.fromEntries(Object.entries(params || {}).map(([k, v]) => [k, typeof v === 'string' ? v.trim().replace(/^"|"$/g, '').trim() : v]));   // the song text may keep the quotes
  if (params.enabled === 'off') return { audio, region: { sampleRate: audio.sampleRate, channels: audio.channels.map((c) => new Float32Array(c.length)) } };
  const events = parseRewindEvents(params.events, audio.sampleRate, samplesPerBar);
  if (!events.length) return { audio, region: { sampleRate: audio.sampleRate, channels: audio.channels.map((c) => new Float32Array(c.length)) } };
  if (params.sound === 'fx' && !fx) throw new Error('A rewind block with sound fx needs a sample on its fx input.');
  const done = audio.channels.map((channel, i) => rewindChannel(channel, fx ? fx.channels[Math.min(i, fx.channels.length - 1)] : null, events, params, audio.sampleRate, signal));
  return { audio: { sampleRate: audio.sampleRate, channels: done.map((d) => d.out) }, region: { sampleRate: audio.sampleRate, channels: done.map((d) => d.region) } };
}
