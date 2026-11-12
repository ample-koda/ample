// The felt-piano voice of the `piano` block as a streaming core (plan local-preview 3.4): a note is a PianoVoice that adds its samples to a mono buffer with the clipping of the render
// (each voice added to what is there, kept inside -1..1). The build preview worker and the live AudioWorklet (piano-worklet.mjs) both run it. Pure: no Web Audio.

/** One note: pitch (MIDI), length in samples (the note sounds exactly that long, with a straight fade out). */
export class PianoVoice {
  constructor(rate, note) {
    this.rate = rate; this.length = Math.max(1, Math.floor(note.length)); this.frequency = 440 * 2 ** ((note.pitch - 69) / 12); this.i = 0;
  }

  get done() { return this.i >= this.length; }

  process(out, from, to) {
    const { rate, length, frequency } = this;
    for (let n = from; n < to && this.i < length; n++, this.i++) {
      const i = this.i, phase = ((i * frequency / rate) % 1 + 1) % 1, triangle = phase < 0.5 ? phase * 4 - 1 : 3 - phase * 4, saw = phase * 2 - 1;
      out[n] = Math.max(-1, Math.min(1, out[n] + ((triangle + saw) * 0.1375 * (length - i) / length)));
    }
  }
}

/** The offline render of a note list ({ bar, beat, beats, pitch }): a Float32Array of bars * samplesPerBar. */
export function renderPianoNotes(notes, rate, firstBar, bars, samplesPerBar) {
  const output = new Float32Array(bars * samplesPerBar);
  for (const note of notes) {
    if (note.bar < firstBar || note.bar >= firstBar + bars) continue;
    const start = Math.floor((note.bar - firstBar + note.beat / 4) * samplesPerBar);
    const voice = new PianoVoice(rate, { pitch: note.pitch, length: note.beats * samplesPerBar / 4 });
    voice.process(output, start, Math.min(output.length, start + voice.length));
  }
  return output;
}
