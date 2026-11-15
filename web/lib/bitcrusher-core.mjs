// The bitcrusher as a streaming core: AMPLE's bit-depth quantization and sample hold, shared by the offline preview (clientside-bitcrusher.mjs) and the live AudioWorklet
// (bitcrusher-worklet.mjs), so the sound you hear while a knob moves is the sound of the render (issue 74). The sample counter that decides which sample is held runs
// across blocks, and every channel has its own held value.

const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
const read = (value, fallback, min, max) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/dBFS|dB|%/gi, '').trim());
  return clamp(Number.isFinite(parsed) ? parsed : fallback, min, max);
};

export class BitcrusherCore {
  constructor(channels = 2) {
    this.held = new Float64Array(channels);
    this.index = 0;
    this.configure({});
  }

  /** Reads bits (2 to 16), downsample (the hold, 1 to 64), mix (0 to 100 %) and enabled. Moving a knob keeps the counter and the held values: no click. */
  configure(params) {
    this.off = params.enabled === 'off';
    this.shift = 16 - Math.round(read(params.bits, 8, 2, 16));
    this.hold = Math.round(read(params.downsample, 1, 1, 64));
    this.mix = read(params.mix, 100, 0, 100) / 100;
  }

  /** True when the block changes nothing: bypassed, or the mix is dry. */
  get passes() { return this.off || this.mix === 0; }

  /** Processes the samples [from, to) of every channel; inputs and outputs are lists of arrays of the same length. */
  process(inputs, outputs, from, to) {
    const count = to - from;
    for (let c = 0; c < inputs.length; c++) {
      const source = inputs[c], out = outputs[c];
      if (this.passes) { for (let n = from; n < to; n++) out[n] = source[n]; continue; }
      let held = this.held[c];
      for (let n = from, i = this.index; n < to; n++, i++) {
        const dry = Math.trunc(clamp(source[n], -1, 1) * 32768);
        if (i % this.hold === 0) held = (dry >> this.shift) << this.shift;
        out[n] = clamp((dry * (1 - this.mix) + held * this.mix) / 32768, -1, 1);
      }

      this.held[c] = held;
    }

    this.index += count;
  }
}
