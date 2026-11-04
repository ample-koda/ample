const seconds = (text) => {
  const value = Number.parseFloat(String(text ?? '').trim().replace(/s$/i, '').trim());
  return Number.isFinite(value) && value >= 0 ? value : null;
};

const signedOffset = (text, samplesPerBar, sampleRate) => {
  const value = String(text ?? '').trim().replace(/^([\d.+-]+).*$/, '$1');
  const amount = Number.parseFloat(value);
  if (!Number.isFinite(amount)) return 0;
  return /beats?$/i.test(String(text ?? '').trim()) ? Math.round(amount * samplesPerBar / 4) : Math.round(amount * sampleRate);
};

/** Places decoded project audio on the song timeline, approximating ImportAudioRenderer's crop, offset and loop rules. */
export function placeImportedAudio(audio, params, samplesPerBar, totalBars, range, partAt, sampleRate) {
  if (!audio.channels.length || audio.channels.some((channel) => channel.length !== audio.channels[0].length)) throw new Error('The imported audio channels are not aligned.');
  const firstBar = range?.firstBar || 1, bars = range?.bars || totalBars;
  const length = Math.max(0, samplesPerBar * bars), inputLength = audio.channels[0].length;
  const from = Math.min(inputLength, Math.round((seconds(params.start) || 0) * sampleRate));
  const requestedEnd = seconds(params.end), to = requestedEnd && requestedEnd > 0 ? Math.min(inputLength, Math.round(requestedEnd * sampleRate)) : inputLength;
  const source = audio.channels.map((channel) => channel.subarray(from, Math.max(from, to)));
  const segmentLength = source[0].length;
  const rawFade = Math.round(Math.min(10, Math.max(0, seconds(params.crossfade) || 0)) * sampleRate);
  const fade = params.loop === 'on' ? Math.min(rawFade, Math.floor(segmentLength / 2)) : 0;
  const period = segmentLength - fade;
  const offset = (firstBar - partAt) * samplesPerBar - signedOffset(params.offset, samplesPerBar, sampleRate);
  const channels = source.map(() => new Float32Array(length));
  if (segmentLength > 0) for (let frame = 0; frame < length; frame++) {
    const at = offset + frame;
    if (at < 0) continue;
    if (params.loop === 'on') {
      const position = at % period;
      for (let channel = 0; channel < channels.length; channel++) {
        channels[channel][frame] = fade > 0 && position < fade
          ? (source[channel][position] * position + source[channel][period + position] * (fade - position)) / fade
          : source[channel][position];
      }
    } else if (at < segmentLength) for (let channel = 0; channel < channels.length; channel++) channels[channel][frame] = source[channel][at];
  }
  return { sampleRate, channels };
}
