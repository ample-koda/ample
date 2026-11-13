/** The live preview renders at most 90% of the 128 MiB working-set net of the offline renderers; the worst voice (the synth: 12 bytes per frame and channel, 2 channels) sets the bar cap. */
export const LIVE_BYTES = 128 * 1024 * 1024 * 0.9, LIVE_BYTES_PER_FRAME = 24;
/** The bars the live chain plays: all of the song, or the first bars that fit (at least 1). */
export function fitLiveBars(songBars, samplesPerBar) {
  return Math.min(songBars, Math.max(1, Math.floor(LIVE_BYTES / LIVE_BYTES_PER_FRAME / samplesPerBar)));
}
/** What the badge says when the live preview plays only the start of a long song, or ''. */
export const liveBarsNote = (songBars, liveBars) => (liveBars < songBars ? `previewing bars 1-${liveBars} of ${songBars}` : '');
