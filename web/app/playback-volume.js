// Tab-local listening volume (left and right, optionally linked), downstream of audio processing. Never saved in the song.
// Every output goes through playbackOutput(), so this is also the one place that can meter whatever is audible.
import { getContext, hasAudio } from '../lib/audio-engine.mjs';
let left = 1, right = 1, linked = true, mediaContext = null;
const chains = new Set(), outputs = new WeakMap(), listeners = new Set(), registered = new WeakSet();
const clamp = (value) => Math.max(0, Math.min(1.5, Number.isFinite(value) ? value : 1));
/** The louder of the two channels (what the single-number users of this module care about). */
export const playbackVolume = () => Math.max(left, right);
export const playbackChannels = () => ({ left, right, linked });
export const onPlaybackVolume = (listener) => { listeners.add(listener); return () => listeners.delete(listener); };
function apply() {
  for (const reference of chains) {
    const chain = reference.deref();
    if (!chain || chain.context.state === 'closed') { chains.delete(reference); continue; }
    chain.left.gain.setTargetAtTime(left, chain.context.currentTime, .015);
    chain.right.gain.setTargetAtTime(right, chain.context.currentTime, .015);
  }
  listeners.forEach((listener) => listener(playbackVolume()));
}
/** Sets both channels. */
export function setPlaybackVolume(value) { left = right = clamp(value); apply(); }
/** Sets one channel; while the channels are linked the other one follows. */
export function setChannelVolume(side, value) {
  if (linked) left = right = clamp(value);
  else if (side === 'left') left = clamp(value);
  else right = clamp(value);
  apply();
}
export function setVolumeLinked(on) { linked = !!on; if (linked) left = right = Math.max(left, right); apply(); }
export function playbackOutput(context) {
  if (outputs.has(context)) return outputs.get(context).input;
  const input = context.createGain();
  input.channelCount = 2; input.channelCountMode = 'explicit'; input.channelInterpretation = 'speakers';   // mono sources are spread over both channels
  const split = context.createChannelSplitter(2), merge = context.createChannelMerger(2);
  const channel = () => { const gain = context.createGain(), analyser = context.createAnalyser(); analyser.fftSize = 1024; gain.connect(analyser); return { gain, analyser }; };
  const l = channel(), r = channel();
  l.gain.gain.value = left; r.gain.gain.value = right;
  input.connect(split); split.connect(l.gain, 0); split.connect(r.gain, 1);
  l.gain.connect(merge, 0, 0); r.gain.connect(merge, 0, 1); merge.connect(context.destination);
  const chain = { context, input, left: l.gain, right: r.gain, analysers: [l.analyser, r.analyser], buffer: new Float32Array(1024) };
  outputs.set(context, chain); chains.add(new WeakRef(chain));
  return input;
}
/** The peak of the last ~20 ms that left each channel (0..1+), over every audio context that is playing. */
export function outputLevels() {
  const peaks = [0, 0];
  for (const reference of chains) {
    const chain = reference.deref();
    if (!chain || chain.context.state !== 'running') continue;
    chain.analysers.forEach((analyser, side) => {
      analyser.getFloatTimeDomainData(chain.buffer);
      let peak = 0;
      for (let i = 0; i < chain.buffer.length; i++) { const v = Math.abs(chain.buffer[i]); if (v > peak) peak = v; }
      if (peak > peaks[side]) peaks[side] = peak;
    });
  }
  return { left: peaks[0], right: peaks[1] };
}
/** Attach once, on actual playback; native elements need Web Audio to exceed 100%. */
export function registerPlaybackAudio(audio) {
  if (registered.has(audio)) return audio;
  registered.add(audio);
  let connected = false;
  audio.addEventListener('play', () => {
    if (!hasAudio()) { audio.volume = Math.min(1, playbackVolume()); return; }
    mediaContext ||= getContext();
    if (!connected) { mediaContext.createMediaElementSource(audio).connect(playbackOutput(mediaContext)); connected = true; }
    if (mediaContext.state === 'suspended') mediaContext.resume();
  });
  return audio;
}

const spectrum = new Uint8Array(512);
/** The loudness (0..1) of n bands from low to high (spaced like hearing, not like the bins) of what plays now, over every audio context that is playing; all zero when nothing plays. */
export function outputBands(n = 9) {
  const bands = new Array(n).fill(0);
  for (const reference of chains) {
    const chain = reference.deref();
    if (!chain || chain.context.state !== 'running') continue;
    for (const analyser of chain.analysers) {
      const bins = Math.min(analyser.frequencyBinCount, spectrum.length);
      analyser.getByteFrequencyData(spectrum);
      for (let b = 0; b < n; b++) {
        const from = Math.max(1, Math.round(Math.pow(bins * 0.7, b / n))), to = Math.max(from + 1, Math.round(Math.pow(bins * 0.7, (b + 1) / n)));
        let peak = 0;
        for (let i = from; i < to && i < bins; i++) if (spectrum[i] > peak) peak = spectrum[i];
        if (peak / 255 > bands[b]) bands[b] = peak / 255;
      }
    }
  }
  return bands;
}
