// The live Delay Llama of the browser: one AudioWorklet per audio context (loaded once), with notes that start and stop at any moment and settings that can be turned while it sings.
// Used by Jam (the clip is sung live, nothing rendered in advance), the window of the block (the pad) and anything else that wants to hear the voice. @ample:delay-llama

const loaded = new WeakMap();

/** The live llama of an audio context: { node, set(settings), noteOn(pitch, velocity, at), noteOff(pitch, at), allOff(), connect(destination) } (a promise, the first time it loads the worklet). */
export function llamaLive(ctx) {
  if (!loaded.has(ctx)) {
    loaded.set(ctx, ctx.audioWorklet.addModule('/lib/llama-worklet.mjs').then(() => {
      const node = new AudioWorkletNode(ctx, 'delay-llama', { numberOfInputs: 0, outputChannelCount: [2] });
      const wired = new Set();
      let last = '';
      const send = (message) => node.port.postMessage(message);
      return {
        node,
        set(settings) { const text = JSON.stringify(settings); if (text !== last) { last = text; send({ type: 'settings', settings }); } },
        noteOn(pitch, velocity = 96, at = 0, fine = 0) { send({ type: 'on', pitch, velocity, at, fine }); },
        noteOff(pitch, at = 0) { send({ type: 'off', pitch, at }); },
        allOff() { send({ type: 'all-off' }); },
        /** The pitch wheel, in semitones (positive is up). */
        bend(semitones) { send({ type: 'bend', semitones }); },
        connect(destination) { if (!wired.has(destination)) { node.connect(destination); wired.add(destination); } },
      };
    }));
  }
  return loaded.get(ctx);
}

const tweaks = new Map(), listeners = new Set();
/** A setting of a block turned by hand (a knob of its window), before it is written into the song: what the live llamas of that block use from now on. */
export function tweakLlama(block, patch) {
  tweaks.set(block, { ...(tweaks.get(block) || {}), ...patch });
  for (const listener of listeners) listener(block, tweaks.get(block));
}
export const llamaTweaks = (block) => tweaks.get(block) || {};
/** Hears every tweak: (block, all tweaks of that block); returns the way to stop listening. */
export function onLlamaTweak(listener) { listeners.add(listener); return () => listeners.delete(listener); }
