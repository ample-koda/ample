// The live felt piano of the browser: one AudioWorklet per audio context that plays notes with the voice of the render (piano-core.mjs). Used by the piano roll for a clip that feeds a piano block. @ample:piano
const loaded = new WeakMap();

/** { noteOn({ pitch, duration }, at), allOff(), connect(destination) } for an audio context (a promise; the first call loads the worklet). */
export function pianoLive(ctx) {
  if (!loaded.has(ctx)) {
    loaded.set(ctx, ctx.audioWorklet.addModule('/lib/piano-worklet.mjs').then(() => {
      const node = new AudioWorkletNode(ctx, 'ample-piano', { numberOfInputs: 0, outputChannelCount: [2] });
      let current = null;
      return {
        node,
        noteOn({ pitch, duration = .5 }, at = 0) { node.port.postMessage({ type: 'on', at, pitch, duration }); },
        allOff() { node.port.postMessage({ type: 'all-off' }); },
        /** The piano plays into one place at a time (the latest): the notes that still sound follow it. */
        connect(destination) { if (current !== destination) { try { node.disconnect(); } catch { /* nothing connected */ } node.connect(destination); current = destination; } },
      };
    }));
  }
  return loaded.get(ctx);
}
