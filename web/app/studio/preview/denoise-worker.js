import { processDenoiseChannel } from './denoise.js';

self.onmessage = ({ data }) => {
  try {
    const channels = data.channels.map((buffer) => processDenoiseChannel(new Float32Array(buffer), data.sampleRate, data.params));
    self.postMessage({ channels: channels.map((channel) => channel.buffer) }, channels.map((channel) => channel.buffer));
  } catch (error) {
    self.postMessage({ error: error.message || 'The local denoise preview failed.' });
  }
};
