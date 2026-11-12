// The live synth of the browser: one AudioWorklet per audio context (loaded once) that plays the notes of a synth block with the voice of the render (synth-core.mjs). Used by the piano roll
// audition and playback of a clip that feeds a synth block. @ample:synth
const loaded = new WeakMap();

/** { noteOn({ pitch, velocity, duration, pan, fine }, at), set(params), allOff(), connect(destination) } for an audio context (a promise; the first call loads the worklet). */
export function synthLive(ctx) {
  if (!loaded.has(ctx)) {
    loaded.set(ctx, ctx.audioWorklet.addModule('/lib/synth-worklet.mjs').then(() => {
      const node = new AudioWorkletNode(ctx, 'ample-synth', { numberOfInputs: 0, outputChannelCount: [2] });
      const wired = new Set();
      let last = '';
      const send = (message) => node.port.postMessage(message);
      return {
        node,
        set(params) { const text = JSON.stringify(params); if (text !== last) { last = text; send({ type: 'settings', params }); } },
        noteOn({ pitch, velocity = 96, duration = .5, pan = 0, fine = 0 }, at = 0) { send({ type: 'on', at, pitch, velocity, duration, pan, fine }); },
        allOff() { send({ type: 'all-off' }); },
        /** The synth plays into one place at a time (the latest): the notes that still ring follow it. */
        connect(destination) { if (!wired.has(destination)) { try { node.disconnect(); } catch { /* nothing connected */ } wired.clear(); node.connect(destination); wired.add(destination); } },
      };
    }));
  }
  return loaded.get(ctx);
}
