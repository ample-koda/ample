// One table of what the browser can do with each parameter of each block while a knob moves: 'param' (an AudioParam or worklet message: heard while it moves), 'rebuild' (the block
// is made again in the browser, heard a moment after the knob stops) or 'offline' (only a build changes it). It is read by the badge in the Advanced window, and a test checks that every
// name in it is a real parameter of the block's template. Blocks that are not in the table are 'offline': they need a build.
const only = (...names) => ({ param: names });
const peqNames = Array.from({ length: 6 }, (_, k) => ['type', 'gain', 'hz', 'q', 'enabled'].map((part) => `band${k + 1}_${part}`)).flat();

export const LIVE_PARAMETERS = {
  volume: only('gain', 'left', 'right', 'left_gain', 'right_gain', 'mono'),
  eq: only('low_hz', 'high_hz', 'low', 'mid', 'high'),
  peq: only(...peqNames),
  filter: only('type', 'slope', 'cutoff', 'resonance', 'lfo_rate', 'lfo_sync', 'lfo_depth', 'lfo_shape'),
  compressor: only('threshold', 'ratio', 'attack', 'release', 'makeup', 'keyFilter'),
  duck: only('pattern', 'depth', 'release'),
  tremolo: only('shape', 'pattern', 'depth', 'sync', 'rate'),
  distortion: only('drive', 'mode', 'tone', 'level', 'mix'),
  delay: only('time', 'feedback', 'mix'),
  reverb: only('size', 'decay', 'mix'),
  chorus: only('depth', 'period', 'mix'),
  convolution: { param: ['predelay', 'level', 'mix'], rebuild: ['length', 'normalize', 'ir', 'file'] },
  pan: only('pan'),
  width: only('width'),
  phaser: only('stages', 'period', 'mix'),
  flanger: only('depth', 'period', 'feedback', 'mix'),
  gate: only('threshold', 'attack', 'hold', 'release'),
  limiter: only('ceiling', 'lookahead', 'release'),
  deesser: only('frequency', 'threshold', 'ratio'),
  mixer: only('gains', 'pan'),
  bus: only('gains', 'pan', 'gain', 'mute', 'solo'),
  // made again in the browser (live chain, live-local.js): every knob re-renders the block, a moment after you stop
  drums: { rebuild: '*' }, sampler: { rebuild: '*' }, cut: { rebuild: '*' }, fade: { rebuild: '*' }, shift: { rebuild: '*' }, reverse: { rebuild: '*' }, chop: { rebuild: '*' }, rewind: { rebuild: '*' }, scratch: { rebuild: '*' },
  transpose: { rebuild: '*' }, chord: { rebuild: '*' }, quantize: { rebuild: '*' }, 'midi-humanize': { rebuild: '*' }, arpeggiator: { rebuild: '*' },
};

/** 'param' | 'rebuild' | 'offline' for one parameter of a block type. */
export function parameterLive(type, name) {
  const entry = LIVE_PARAMETERS[type];
  if (!entry) return 'offline';
  if (entry.param?.includes(name) || (name === 'enabled' && entry.param)) return 'param';   // the bypass of a live block is a crossfade
  if (entry.rebuild === '*' || entry.rebuild?.includes(name)) return 'rebuild';
  return 'offline';
}

export const LIVE_BADGES = {
  param: { text: 'live', title: 'Moves live: you hear it while the knob turns (approximate; the build is the truth)' },
  rebuild: { text: 're-renders', title: 'Re-renders in the browser: you hear the change a moment after the knob stops' },
  offline: { text: 'needs build', title: 'Heard after a build: the browser cannot play this parameter live' },
};
