// The sound of a take as the browser works with it ({ sampleRate, channels }, at most two channels, cut to a maximum length), decoded once per take. Shared by the windows that play a block live.
import { decodePreviewBlob, fetchTakeAudio } from '../preview/audio.js';

const decoded = new Map();   // take id -> Promise

/** The sound of a take; `long` says that it was cut at maxSeconds. */
export function takeAudio(takeId, maxSeconds = 40) {
  const key = `${takeId}:${maxSeconds}`;
  if (!decoded.has(key)) {
    decoded.set(key, fetchTakeAudio(takeId).then(decodePreviewBlob).then((buffer) => {
      const frames = Math.min(buffer.length, Math.floor(maxSeconds * buffer.sampleRate));
      return { sampleRate: buffer.sampleRate, channels: Array.from({ length: Math.min(2, buffer.numberOfChannels) }, (_, i) => buffer.getChannelData(i).slice(0, frames)), long: buffer.length > frames };
    }).catch((error) => { decoded.delete(key); throw error; }));
  }
  return decoded.get(key);
}
