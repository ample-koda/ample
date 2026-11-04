import { stepsPerBar } from './meter.mjs';
const NOTES = { c: 0, d: 2, e: 4, f: 5, g: 7, a: 9, b: 11 };
const text = (value) => String(value ?? '').trim().replace(/^"|"$/g, '');

export function guitarSampleNotes(files) {
  const notes = new Map();
  for (const file of files) {
    const match = /^([a-g])([#b]?)(-?\d+)\.wav$/i.exec(file.name);
    if (!match) {
      if (/^[a-g][#b]?-?\d+_(?:v\d+|r\d+|rel)(?:_r\d+)?\.wav$/i.test(file.name)) return null;
      continue;
    }
    if (notes.size >= 128) return null;
    const accidental = match[2] === '#' ? 1 : match[2] === 'b' ? -1 : 0;
    const pitch = (Number(match[3]) + 1) * 12 + NOTES[match[1].toLowerCase()] + accidental;
    if (!Number.isInteger(pitch) || pitch < 0 || pitch > 127 || notes.has(pitch)) return null;
    notes.set(pitch, file);
  }
  return notes.size ? notes : null;
}

export function guitarVoice(pitches, voicing) {
  if (!pitches.length) return [];
  const root = pitches[0] % 12, third = pitches.length > 1 ? ((pitches[1] - pitches[0]) % 12 + 12) % 12 : 4;
  const low = 40 + ((root - 40) % 12 + 12) % 12;
  const triad = 48 + ((root - 48) % 12 + 12) % 12;
  if (voicing === 'power') return [low, low + 7, low + 12];
  if (voicing === 'triad') return [triad, triad + third, triad + 7];
  if (voicing === 'open') return [low, low + 7, low + 12, low + 12 + third, low + 19];
  return null;
}

export function guitarWorkingSetBytes(outputFrames, sampleFrames) { return outputFrames * 8 + sampleFrames * 8; }

export async function renderGuitarChannels(sampleNotes, notes, params, sampleRate, firstBar, bars, samplesPerBar, activeBars, signal) {
  const frames = bars * samplesPerBar, sampleFrames = sampleNotes.reduce((sum, sample) => sum + sample.channels[0].length, 0);
  if (!Number.isSafeInteger(frames) || frames < 1 || guitarWorkingSetBytes(frames, sampleFrames) > 128 * 1024 * 1024) throw new Error('The local guitar preview exceeds its 128 MiB working-set limit.');
  if (Number(text(params.humanize)) !== 0) throw new Error('Local guitar preview currently requires humanize 0.');
  if (text(params.patterns) || text(params.song)) throw new Error('Local guitar preview currently supports the plain strum pattern only.');
  const staggerValue = text(params.stagger);
  const normalized = { ...params, voicing: text(params.voicing) || 'open', stagger: staggerValue === '' ? 22 : Number(staggerValue), gain: Number(text(params.gain)) || 0 };
  const strums = text(params.strum || 'D...D...D.u.D.u.').split(/[| ]/).filter(Boolean);
  if (!strums.length || strums.some((pattern) => [...pattern].some((stroke) => !'.xDdUu'.includes(stroke)))) throw new Error('The guitar strum pattern needs only D, d, U, u, x and . characters.');
  if (!sampleNotes.length || sampleNotes.some((sample) => sample.channels.length !== 1 || !sample.channels[0].length)) throw new Error('Local guitar preview needs mono note-named WAV samples.');
  signal?.throwIfAborted();
  const worker = new Worker(new URL('./clientside-guitar-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  try {
    return await new Promise((resolve, reject) => {
      abort = () => reject(new DOMException('The local guitar preview was cancelled.', 'AbortError'));
      signal?.addEventListener('abort', abort, { once: true });
      worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(data.channels.map((channel) => new Float32Array(channel)));
      worker.onerror = (event) => reject(new Error(event.message || 'The local guitar preview failed.'));
      const samples = sampleNotes.map((sample) => ({ pitch: sample.pitch, sourceRate: sample.sourceRate, samples: sample.channels[0].slice().buffer }));
      const transfers = samples.map((sample) => sample.samples);
      worker.postMessage({ samples, notes, params: normalized, sampleRate, firstBar, bars, samplesPerBar, strums, activeBars, stepsPerBar: Math.min(64, Math.max(1, Math.round(Number(text(params.steps)) || 0))) || stepsPerBar() }, transfers);
    });
  } finally { signal?.removeEventListener('abort', abort); worker.terminate(); }
}
