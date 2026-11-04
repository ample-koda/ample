import { processScratchBuffer } from './clientside-scratch.mjs';

self.onmessage = ({ data }) => {
  try {
    const channels = processScratchBuffer(data.channels.map((buffer) => new Float32Array(buffer)), data.params, data.sampleRate, data.samplesPerBar, data.firstBar);
    self.postMessage({ channels: channels.map((channel) => channel.buffer) }, channels.map((channel) => channel.buffer));
  } catch (error) { self.postMessage({ error: error.message || String(error) }); }
};
