const NOTES = { c: 0, d: 2, e: 4, f: 5, g: 7, a: 9, b: 11 };

export function pitchedSampleNotes(files) {
  const notes = new Map();
  for (const file of files) {
    const match = /^([a-g])([#b]?)(-?\d+)\.wav$/i.exec(file.name);
    if (!match) return null;
    const pitch = (Number(match[3]) + 1) * 12 + NOTES[match[1].toLowerCase()] + (match[2] === '#' ? 1 : match[2] === 'b' ? -1 : 0);
    if (!Number.isInteger(pitch) || pitch < 0 || pitch > 127 || notes.has(pitch)) return null;
    notes.set(pitch, file);
  }
  return notes.size ? notes : null;
}

export function pitchedSampleWorkingSetBytes(outputFrames, sourceFrames) { return outputFrames * 8 + sourceFrames * 8; }

export async function renderPitchedSamples(samples, notes, mode, gainDb, sampleRate, firstBar, bars, samplesPerBar, signal) {
  const frames = bars * samplesPerBar, sourceFrames = samples.reduce((sum, sample) => sum + sample.channels[0].length, 0);
  if (!Number.isSafeInteger(frames) || frames < 1 || pitchedSampleWorkingSetBytes(frames, sourceFrames) > 128 * 1024 * 1024) throw new Error('Pitched sample preview exceeds its 128 MiB working-set limit.');
  if (samples.some((sample) => sample.channels.length !== 1 || !sample.channels[0].length)) throw new Error('Pitched sample preview needs mono note-named WAV samples.');
  signal?.throwIfAborted();
  const worker = new Worker(new URL('./clientside-pitched-samples-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  try {
    return await new Promise((resolve, reject) => {
      abort = () => reject(new DOMException('The local sample preview was cancelled.', 'AbortError'));
      signal?.addEventListener('abort', abort, { once: true });
      worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(new Float32Array(data.channel));
      worker.onerror = (event) => reject(new Error(event.message || 'The local sample preview failed.'));
      const transfers = samples.map((sample) => sample.channels[0].slice().buffer);
      worker.postMessage({ samples: samples.map((sample, index) => ({ pitch: sample.pitch, sourceRate: sample.sourceRate, data: transfers[index] })), notes, mode, gainDb, sampleRate, firstBar, bars, samplesPerBar }, transfers);
    });
  } finally { signal?.removeEventListener('abort', abort); worker.terminate(); }
}
