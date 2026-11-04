import { pause } from './yield-slice.mjs';
import { barBeats } from './meter.mjs';

/** Reverses every decoded channel independently without changing its duration or sample rate. */
export async function reverseAudio(audio, signal) {
  const channels = [];
  for (const source of audio.channels) {
    const reversed = new Float32Array(source.length);
    for (let start = 0; start < source.length; start += 65536) {
      signal?.throwIfAborted();
      const end = Math.min(source.length, start + 65536);
      for (let i = start; i < end; i++) reversed[i] = source[source.length - i - 1];
      await pause();
    }
    channels.push(reversed);
  }
  return { sampleRate: audio.sampleRate, channels };
}

const seconds = (text) => {
  const value = Number.parseFloat(String(text ?? '').trim().replace(/s$/i, '').trim());
  return Number.isFinite(value) && value >= 0 ? value : null;
};

/** Crops seconds/first/last from each channel, then plays or repeats the crop for the original duration. */
export async function cutAudio(audio, params, signal) {
  const length = audio.channels[0]?.length || 0, rate = audio.sampleRate;
  if (audio.channels.some((channel) => channel.length !== length)) throw new Error('The cut input channels are not aligned.');
  if (params.enabled === 'off') return audio;
  const first = seconds(params.first), last = seconds(params.last), start = first !== null && first > 0 ? 0 : last !== null && last > 0 ? Math.max(0, length - Math.round(last * rate)) : Math.min(length, Math.round((seconds(params.start) || 0) * rate));
  const end = first !== null && first > 0 ? Math.min(length, Math.round(first * rate)) : last !== null && last > 0 ? length : Math.max(start, seconds(params.end) > 0 ? Math.min(length, Math.round(seconds(params.end) * rate)) : length);
  const result = audio.channels.map((channel) => {
    const source = channel.subarray(start, end), output = new Float32Array(length);
    if (source.length) for (let i = 0; i < length; i++) { if ((i & 65535) === 0) signal?.throwIfAborted(); output[i] = params.loop === 'on' ? source[i % source.length] : source[i] || 0; }
    return output;
  });
  return { sampleRate: rate, channels: result };
}

const fadePosition = (text, sampleRate, samplesPerBar, length) => {
  const match = /^(\d{1,6})(?:\.(\d{1,6}))?\s*(s)?$/i.exec(String(text ?? '').trim());
  if (!match) throw new Error('Fade start and length must be bars or seconds.');
  const value = Number(`${match[1]}${match[2] ? `.${match[2]}` : ''}`);
  const samples = match[3] ? Math.round(value * sampleRate) : Math.round((value - 1) * samplesPerBar);
  return { samples, seconds: !!match[3], value, valid: match[3] || value >= 1 };
};

/** Applies the AMPLE fade envelope over the song-relative sample timeline. */
export async function fadeAudio(audio, params, tempo, firstBar, signal) {
  if (params.enabled === 'off') return audio;
  const samplesPerBar = Math.floor(audio.sampleRate * (barBeats() * 60) / tempo), start = fadePosition(params.start, audio.sampleRate, samplesPerBar), duration = fadePosition(params.length, audio.sampleRate, samplesPerBar);
  if (!start.valid || !duration.valid || duration.samples <= 0) throw new Error('Fade start must be at least bar 1 and fade length must be positive.');
  const end = start.samples + duration.samples, incoming = params.direction === 'in', shape = params.shape || 'linear';
  if (!['linear', 'lin', 'equal', 'exp'].includes(shape)) throw new Error(`Unsupported fade shape “${shape}”.`);
  const channels = [];
  for (const source of audio.channels) {
    const output = new Float32Array(source.length);
    for (let base = 0; base < source.length; base += 65536) {
      signal?.throwIfAborted();
      const limit = Math.min(source.length, base + 65536);
      for (let i = base; i < limit; i++) {
        const at = (firstBar - 1) * samplesPerBar + i;
        let p = at <= start.samples ? 0 : at >= end ? 32768 : Math.floor((at - start.samples) * 32768 / duration.samples);
        if (!incoming) p = 32768 - p;
        if (shape === 'exp') p = Math.floor(p * p / 32768);
        else if (shape === 'equal') p = Math.round(Math.sqrt(p / 32768) * 32768);
        output[i] = source[i] * p / 32768;
      }
      await pause();
    }
    channels.push(output);
  }
  return { sampleRate: audio.sampleRate, channels };
}

/** Moves audio by signed seconds/beats and clips it to the block timeline, matching ShiftRenderer's zero-fill rules. */
export async function shiftAudio(audio, params, tempo, bars, signal) {
  if (params.enabled === 'off') return audio;
  const samplesPerBar = Math.floor(audio.sampleRate * (barBeats() * 60) / tempo), amount = (text) => {
    const match = /^\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+))\s*(.*)\s*$/.exec(String(text ?? ''));
    if (!match) return 0;
    const value = Number(match[1]);
    return /beats?$/i.test(match[2]) ? Math.round(value * samplesPerBar / 4) : Math.round(value * audio.sampleRate);
  };
  const shift = amount(params.seconds) + amount(params.beats), inputLength = audio.channels[0]?.length || 0;
  if (audio.channels.some((channel) => channel.length !== inputLength)) throw new Error('The shift input channels are not aligned.');
  const length = Math.max(inputLength, Math.max(0, bars * samplesPerBar)), channels = [];
  for (const source of audio.channels) {
    const output = new Float32Array(length);
    for (let base = 0; base < length; base += 65536) {
      signal?.throwIfAborted();
      const limit = Math.min(length, base + 65536);
      for (let i = base; i < limit; i++) { const from = i - shift; if (from >= 0 && from < source.length) output[i] = source[from]; }
      await pause();
    }
    channels.push(output);
  }
  return { sampleRate: audio.sampleRate, channels };
}

/** Overlaps two aligned inputs using the shared AMPLE fade curves; missing inputs are silence. */
export async function crossfadeAudio(a, b, params, tempo, firstBar, bars, sampleRate, signal) {
  const samplesPerBar = Math.floor(sampleRate * (barBeats() * 60) / tempo), length = Math.max(a?.channels[0]?.length || 0, b?.channels[0]?.length || 0, Math.max(0, bars * samplesPerBar));
  const count = Math.max(a?.channels.length || 0, b?.channels.length || 0, 1), align = (source) => ({ sampleRate, channels: Array.from({ length: count }, (_, channel) => {
    const output = new Float32Array(length), input = source?.channels[Math.min(channel, (source.channels.length || 1) - 1)];
    if (input) output.set(input.subarray(0, length));
    return output;
  }) });
  const fadeParams = { ...params, enabled: 'on' }, outgoing = await fadeAudio(align(a), { ...fadeParams, direction: 'out' }, tempo, firstBar, signal), incoming = await fadeAudio(align(b), { ...fadeParams, direction: 'in' }, tempo, firstBar, signal);
  return { sampleRate, channels: outgoing.channels.map((channel, index) => Float32Array.from(channel, (sample, frame) => Math.max(-1, Math.min(1, sample + incoming.channels[index][frame])))) };
}

/** Concatenates connected audio lanes in input order with the renderer's seconds-plus-beats silence gap. */
export async function joinAudio(inputs, params, tempo, bars, sampleRate, signal) {
  if (!inputs.length) return { sampleRate, channels: [new Float32Array(Math.max(0, bars * Math.floor(sampleRate * (barBeats() * 60) / tempo)))] };
  if (params.enabled === 'off') return inputs[0].audio;
  if (inputs.some(({ audio }) => audio.sampleRate !== sampleRate)) throw new Error('Connected join inputs use different sample rates and cannot be combined locally.');
  const samplesPerBar = Math.floor(sampleRate * (barBeats() * 60) / tempo), number = (text) => Number.parseFloat(String(text ?? '').trim().replace(/s$/i, '').trim()) || 0;
  const gap = Math.max(0, Math.round(number(params.seconds) * sampleRate + number(params.beats) * samplesPerBar / 4));
  const length = inputs.reduce((total, { audio }) => total + audio.channels[0].length, 0) + gap * (inputs.length - 1);
  if (length > 600 * sampleRate) throw new Error('The joined sound would be longer than ten minutes.');
  const count = inputs.some(({ audio }) => audio.channels.length > 1) ? 2 : 1;
  if (length * count * Float32Array.BYTES_PER_ELEMENT > 512 * 1024 * 1024) throw new Error('This joined client preview exceeds the 512 MiB audio buffer limit.');
  const channels = Array.from({ length: count }, () => new Float32Array(length));
  let at = 0;
  for (const { audio } of inputs) {
    signal?.throwIfAborted();
    for (let channel = 0; channel < count; channel++) channels[channel].set(audio.channels[Math.min(channel, audio.channels.length - 1)], at);
    at += audio.channels[0].length + gap;
    await pause();
  }
  return { sampleRate, channels };
}

/** Exposes independent mono channel buffers while preserving an unchanged audio passthrough. */
export function splitAudio(audio) {
  const left = audio.channels[0] || new Float32Array(0), right = audio.channels[1] || left;
  return { left: { sampleRate: audio.sampleRate, channels: [left.slice()] }, right: { sampleRate: audio.sampleRate, channels: [right.slice()] } };
}
