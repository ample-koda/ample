// The live mixer of a mixer or bus block: one gain and one balance per input lane, a sum, and (bus) one gain, a mute and the song-wide solo. Persistent Web Audio nodes: a fader
// only sets AudioParams, so it is heard while it moves. The numbers are those of the server (Renderers.cs Mixer, PanTable): gains in whole dB, pan l/c/r or -32..32 per input.
const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
const words = (value, fallback) => String(value ?? fallback).replace(/^"|"$/g, '').trim().split(/\s+/).filter(Boolean);

/** The pan of one lane as -32..32: l and left are -32, r and right 32, c, centre and center 0, or a number. */
export function panValue(token) {
  const text = String(token ?? 'c').toLowerCase();
  if (text === 'l' || text === 'left') return -32;
  if (text === 'r' || text === 'right') return 32;
  const number = Number.parseInt(text, 10);
  return Number.isFinite(number) ? clamp(number, -32, 32) : 0;
}

/** Left and right scale of a lane for a pan, like the offline balance: the far side gets quieter, the near side is left alone (equal power around the centre). */
export function balanceGains(pan) {
  const position = (clamp(pan, -32, 32) + 32) / 64;
  return { left: pan > 0 ? Math.cos(position * Math.PI / 2) / Math.cos(Math.PI / 4) : 1, right: pan < 0 ? Math.sin(position * Math.PI / 2) / Math.sin(Math.PI / 4) : 1 };
}

/** The lane settings of a mixer block's parameters: [{ gainDb, pan }] for `lanes` lanes. */
export function laneSettings(params, lanes) {
  const gains = words(params.gains, '0'), pans = words(params.pan, 'c');
  return Array.from({ length: lanes }, (_, k) => ({ gainDb: clamp(Number.parseInt(gains[k], 10) || 0, -60, 48), pan: panValue(pans[k]) }));
}

/** Whether a bus is silent: its own mute, or another bus has solo and this one has not. */
export const busMuted = (params, anySolo) => params.mute === 'on' || (anySolo && params.solo !== 'on');

/**
 * Builds the mixer into the graph. Returns { lanes: [GainNode inputs], update(params, { anySolo }), setLane(k, { gainDb, pan }), output, dispose }.
 * setLane moves one lane right now (a fader being dragged); update takes all the parameters of the block (after a write to the song).
 */
export function mixerGraph(context, lanes, { bus = false } = {}) {
  const nodes = [], make = (node) => { nodes.push(node); return node; };
  const output = make(context.createGain()), master = make(context.createGain());
  master.connect(output);
  const smooth = (param, value) => { param.cancelScheduledValues(context.currentTime); param.setTargetAtTime(value, context.currentTime, .01); };
  const strips = Array.from({ length: lanes }, () => {
    const input = make(context.createGain()), gain = make(context.createGain()), splitter = make(context.createChannelSplitter(2)), merger = make(context.createChannelMerger(2));
    const left = make(context.createGain()), right = make(context.createGain());
    input.channelCount = 2; input.channelCountMode = 'explicit'; input.connect(gain).connect(splitter);   // a mono lane is spread over both sides first
    splitter.connect(left, 0); splitter.connect(right, 1); left.connect(merger, 0, 0); right.connect(merger, 0, 1); merger.connect(master);
    return { input, gain, left, right };
  });
  const setLane = (k, { gainDb, pan }) => {
    const strip = strips[k]; if (!strip) return;
    const { left, right } = balanceGains(pan);
    smooth(strip.gain.gain, 10 ** (gainDb / 20)); smooth(strip.left.gain, left); smooth(strip.right.gain, right);
  };
  const update = (params, { anySolo = false } = {}) => {
    laneSettings(params, lanes).forEach((lane, k) => setLane(k, lane));
    if (bus) smooth(master.gain, busMuted(params, anySolo) ? 0 : 10 ** (clamp(Number.parseInt(params.gain, 10) || 0, -60, 48) / 20));
  };
  return { lanes: strips.map((strip) => strip.input), update, setLane, output, dispose: () => nodes.forEach((node) => node.disconnect()) };
}
