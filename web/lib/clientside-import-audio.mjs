import { curveOf, placeLoop, samplesOf, splitSeam } from './loop-place.mjs';

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

/**
 * Places decoded project audio on the song timeline the way ImportAudioRenderer does: crop, offset, and a loop with the crossfade, its curve and the intro and outro of loop_start and loop_end (issue 79).
 * Returns the whole sound as `audio` and `seam`, a 1 at every sample that was blended (the seam of the loop); `importedPort` turns that into the ports of the block.
 */
export function placeImportedPorts(audio, params, samplesPerBar, totalBars, range, partAt, sampleRate, beatsPerBar = 4) {
  if (!audio.channels.length || audio.channels.some((channel) => channel.length !== audio.channels[0].length)) throw new Error('The imported audio channels are not aligned.');
  const firstBar = range?.firstBar || 1, bars = range?.bars || totalBars;
  const length = Math.max(0, samplesPerBar * bars), inputLength = audio.channels[0].length;
  const from = Math.min(inputLength, Math.round((seconds(params.start) || 0) * sampleRate));
  const requestedEnd = seconds(params.end), to = requestedEnd && requestedEnd > 0 ? Math.min(inputLength, Math.round(requestedEnd * sampleRate)) : inputLength;
  const source = audio.channels.map((channel) => channel.subarray(from, Math.max(from, to)));
  const keptLength = source[0].length;
  const loop = params.loop === 'on';
  const crossfade = loop ? Math.min(samplesOf(params.crossfade, sampleRate, samplesPerBar, beatsPerBar) ?? 0, 10 * sampleRate) : 0;
  // loop_start and loop_end (seconds in the file): what lies before repeats never (an intro), what lies after plays once at the end of the block
  const loopStart = seconds(params.loop_start), loopEnd = seconds(params.loop_end);
  const loopFrom = loopStart === null ? 0 : Math.min(keptLength, Math.max(0, Math.round(loopStart * sampleRate) - from));
  const rawTo = loopEnd && loopEnd > 0 ? Math.min(keptLength, Math.max(0, Math.round(loopEnd * sampleRate) - from)) : -1;
  const loopTo = rawTo > loopFrom ? rawTo : -1;
  const offset = (firstBar - partAt) * samplesPerBar - signedOffset(params.offset, samplesPerBar, sampleRate);
  const seam = new Uint8Array(length), curve = curveOf(params.crossfade_curve);
  const channels = source.map((channel, index) => placeLoop(channel, { offset, length, loop, crossfade, curve, loopFrom, loopTo, seam: index === 0 ? seam : null }));
  return { audio: { sampleRate, channels }, seam };
}

/** Places decoded project audio on the song timeline: the whole sound (the `audio` port of the block). */
export const placeImportedAudio = (...args) => placeImportedPorts(...args).audio;

/** One output of an import-audio block from what `placeImportedPorts` made: `audio` (the whole loop), `body` (the loop with its seam silent) or `region` (only the seam, silent elsewhere). */
export function importedPort(placed, port) {
  if (port === 'body' || port === 'region') return { sampleRate: placed.audio.sampleRate, channels: splitSeam(placed.audio.channels, placed.seam)[port] };
  return placed.audio;
}
