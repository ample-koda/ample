import { renderPianoNotes } from './piano-core.mjs';

// The piano render of the build preview (a worker): the notes are played by the PianoVoice of piano-core.mjs, the class the live AudioWorklet runs.
self.onmessage = ({ data }) => {
  try {
    const { notes, sampleRate, firstBar, bars, samplesPerBar } = data;
    const output = renderPianoNotes(notes, sampleRate, firstBar, bars, samplesPerBar);
    self.postMessage({ channel: output.buffer }, [output.buffer]);
  } catch (error) { self.postMessage({ error: error.message || 'Local piano rendering failed.' }); }
};
