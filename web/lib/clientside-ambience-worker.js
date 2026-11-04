import { processAmbienceBuffer } from './clientside-ambience.mjs';

self.onmessage = ({ data }) => {
  try {
    const channels = processAmbienceBuffer(data.channels.map((channel) => new Float32Array(channel)), data.params, data.seed, data.sampleRate, data.outputFrames, data.offset);
    self.postMessage({ channels: channels.map((channel) => channel.buffer) }, channels.map((channel) => channel.buffer));
  } catch (error) { self.postMessage({ error: error.message || 'The local ambience preview failed.' }); }
};
