import { processConvolutionBuffer } from './clientside-convolution.mjs';

self.onmessage = ({ data }) => {
  try {
    const channels = processConvolutionBuffer(data.channels.map((buffer) => new Float32Array(buffer)), new Float32Array(data.impulse), data.params, data.sampleRate);
    self.postMessage({ channels: channels.map((channel) => channel.buffer) }, channels.map((channel) => channel.buffer));
  } catch (error) { self.postMessage({ error: error.message || String(error) }); }
};
