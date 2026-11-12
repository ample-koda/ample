import { assembleDrums, drumHits, hitAudio } from './drum-core.mjs';

self.onmessage = ({ data }) => {
  try {
    const { params, seed, sampleRate, firstBar, bars, activeBars, beatsPerBar = 4 } = data;   // the worker has its own module state: the beats of the bar come in the message
    const channel = hitAudio(assembleDrums(drumHits({ params, sampleRate, firstBar, bars, activeBars, beatsPerBar }), seed));
    self.postMessage({ channels: [channel.buffer] }, [channel.buffer]);
  } catch (error) { self.postMessage({ error: error.message || 'The built-in drum preview failed.' }); }
};
