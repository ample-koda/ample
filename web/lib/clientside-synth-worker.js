import { renderSynthNotes } from './synth-core.mjs';

// The synth render of the build preview (a worker): the notes are played by the SynthVoice of synth-core.mjs, the same class the live AudioWorklet runs.
self.onmessage = ({ data }) => {
  try {
    const { notes, params, sampleRate: rate, firstBar, bars, samplesPerBar } = data;
    const [left, right] = renderSynthNotes(notes, params, rate, firstBar, bars, samplesPerBar);
    self.postMessage({ channels: [left.buffer, right.buffer] }, [left.buffer, right.buffer]);
  } catch (error) { self.postMessage({ error: error.message || 'Local synth rendering failed.' }); }
};
