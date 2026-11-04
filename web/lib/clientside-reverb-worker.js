import { processReverbBuffer } from './clientside-reverb.mjs';

self.onmessage = ({ data }) => {
  try {
    const channels = processReverbBuffer(data.channels.map((channel) => new Float32Array(channel)), data.params, data.sampleRate);
    self.postMessage({ channels: channels.map((channel) => channel.buffer) }, channels.map((channel) => channel.buffer));
  } catch (error) { self.postMessage({ error: error.message || 'The local reverb preview failed.' }); }
};
