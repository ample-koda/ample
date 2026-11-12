// What this browser can do for the live preview (local-preview chunk 8.2). Pure: pass an environment object (default: the page) so tests can stub it.

/** Reads the capabilities. `env` is `{ AudioContext, AudioWorkletNode, navigator, OfflineAudioContext }`-like; missing parts mean "not available". */
export function capabilities(env = typeof window === 'undefined' ? {} : window) {
  const Ctx = env.AudioContext || env.webkitAudioContext;
  const proto = Ctx?.prototype;
  return {
    webAudio: !!Ctx,
    offline: !!(env.OfflineAudioContext || env.webkitOfflineAudioContext),
    worklet: !!(Ctx && env.AudioWorkletNode && proto && 'audioWorklet' in proto),
    midi: typeof env.navigator?.requestMIDIAccess === 'function',
    outputLatency: !!(proto && 'outputLatency' in proto),
    renderCapacity: !!(proto && 'renderCapacity' in proto),
  };
}

const NEEDS = {
  webAudio: 'Live sound needs Web Audio, which this browser does not offer.',
  worklet: 'This live block needs audio worklets, which this browser does not offer. Moves live in Chrome or Edge; here the sound is rebuilt on the server.',
  midi: 'MIDI input is not available in this browser (Firefox leaves it off by default). Moves live in Chrome or Edge.',
  outputLatency: 'This browser does not report the output latency (Safari), so the latency figure shows only the processing part.',
  renderCapacity: 'This browser has no render-capacity meter, so the CPU figure is estimated.',
};

/** A human message for a missing capability, or '' when it is there. */
export function whyNot(name, caps = capabilities()) { return caps[name] ? '' : NEEDS[name] || ''; }
