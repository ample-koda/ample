// The live chain loops every source on its own length. A sound shorter than the longest one in the chain (a 160 s recording in a 254 s song) must not restart inside the loop:
// the server mixes a shorter sound with silence after its end, so the live buffer is the sound followed by silence up to the loop.
/** Channel arrays padded with silence to `frames` (the same arrays when nothing is missing; longer sounds are not cut). */
export function padChannels(channels, frames) {
  const have = channels[0]?.length ?? 0;
  if (have >= frames) return channels;
  return channels.map((channel) => { const out = new Float32Array(frames); out.set(channel); return out; });
}
