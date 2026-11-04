import { processGranularBuffer } from './clientside-granular.mjs';

self.onmessage = ({ data }) => {
  try {
    const channels = processGranularBuffer(data.channels.map((buffer) => new Float32Array(buffer)), data.rate, data.params, data.seed);
    self.postMessage({ channels: channels.map((channel) => channel.buffer) }, channels.map((channel) => channel.buffer));
  } catch (error) { self.postMessage({ error: error.message || String(error) }); }
};
