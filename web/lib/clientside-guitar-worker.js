import { guitarVoice } from './clientside-guitar.mjs';

const STROKES = { D: [true, 32768], d: [true, 20000], U: [false, 26000], u: [false, 16000] };
const clip = (value) => Math.max(-1, Math.min(1, value));

self.onmessage = ({ data }) => {
  try {
    const { samples: rawSamples, notes, params, sampleRate, firstBar, bars, samplesPerBar, strums, activeBars, stepsPerBar = 16 } = data;   // the worker has its own module state: the steps of a bar come in the message
    const samples = rawSamples.map((sample) => ({ pitch: sample.pitch, rate: sample.sourceRate / sampleRate, data: new Float32Array(sample.samples) })).sort((a, b) => a.pitch - b.pitch);
    const output = new Float32Array(bars * samplesPerBar), stepLength = Math.floor(samplesPerBar / stepsPerBar), stagger = Math.max(0, Math.min(80, Number(params.stagger) || 0)) * sampleRate / 1000;
    const gain = 10 ** (Math.max(-24, Math.min(48, Number(params.gain) || 0)) / 20), events = [];
    const nearest = (pitch) => samples.reduce((best, sample) => Math.abs(sample.pitch - pitch) < Math.abs(best.pitch - pitch) ? sample : best);
    const chordFor = (bar) => {
      const pitches = notes.filter((note) => note.bar === bar && !note.slide).map((note) => note.pitch);
      return guitarVoice(pitches, params.voicing || 'open') || [];
    };
    for (const bar of activeBars) {
      const chord = chordFor(bar);
      if (!chord.length) continue;
      const pattern = strums[(bar - 1) % strums.length];
      for (let index = 0; index < 16; index++) {
        const stroke = pattern[index % pattern.length], at = (bar - firstBar) * samplesPerBar + index * stepLength;
        if (stroke === 'x') events.push({ at, chord: [], mute: true });
        else if (STROKES[stroke]) events.push({ at, chord, down: STROKES[stroke][0], velocity: STROKES[stroke][1] });
      }
    }
    const renderNote = (pitch, velocity, start, length) => {
      const sample = nearest(pitch), step = sample.rate * 2 ** ((pitch - sample.pitch) / 12), amplitude = 0.44 * gain * velocity / 32768;
      const count = Math.min(Math.ceil(sample.data.length / step), length, output.length - Math.max(0, start)), fade = Math.min(Math.floor(sampleRate * 0.12), count);
      for (let index = 0; index < count; index++) {
        const sourceAt = index * step, low = Math.floor(sourceAt), high = Math.min(sample.data.length - 1, low + 1), fraction = sourceAt - low;
        const tail = fade > 0 && index > count - fade ? (count - index) / fade : 1, at = start + index;
        if (at >= 0) output[at] = clip(output[at] + ((sample.data[low] || 0) * (1 - fraction) + (sample.data[high] || 0) * fraction) * amplitude * tail);
      }
    };
    for (let index = 0; index < events.length; index++) {
      const event = events[index];
      if (event.mute) continue;
      const until = events[index + 1]?.at ?? output.length, order = event.down ? event.chord : [...event.chord].reverse();
      order.forEach((pitch, voice) => { const start = event.at + voice * stagger; renderNote(pitch, event.velocity, start, Math.max(1, until - start + sampleRate / 8)); });
    }
    self.postMessage({ channels: [output.buffer] }, [output.buffer]);
  } catch (error) { self.postMessage({ error: error.message || 'Local guitar preview failed.' }); }
};

