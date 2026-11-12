// Loudness after ITU-R BS.1770-4 / EBU R128 (plan local-preview 3.1): K-weighting (a high shelf and a high-pass), mean square per channel, momentary (400 ms) and short-term (3 s)
// loudness and the gated integrated loudness. Streaming: process() takes blocks of any size. Pure (also run in an AudioWorklet, lufs-worklet.mjs). Channels are weighted 1.0 (left,
// right, centre); surround weights are not needed here. True peak is not measured.

/** The two K-weighting biquads for a sample rate: { shelf: {b, a}, high: {b, a} } (BS.1770-4 design equations, valid at any rate). */
export function kWeighting(rate) {
  const shelfGainDb = 3.999843853973347, shelfQ = 0.7071752369554196, shelfHz = 1681.9744509555319;
  const k = Math.tan(Math.PI * shelfHz / rate), vh = 10 ** (shelfGainDb / 20), vb = vh ** 0.4996667741545416, a0 = 1 + k / shelfQ + k * k;
  const shelf = { b: [(vh + vb * k / shelfQ + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / shelfQ + k * k) / a0], a: [1, 2 * (k * k - 1) / a0, (1 - k / shelfQ + k * k) / a0] };
  const highQ = 0.5003270373238773, highHz = 38.13547087613982, kh = Math.tan(Math.PI * highHz / rate), ah = 1 + kh / highQ + kh * kh;
  const high = { b: [1, -2, 1], a: [1, 2 * (kh * kh - 1) / ah, (1 - kh / highQ + kh * kh) / ah] };
  return { shelf, high };
}

const BLOCK_SECONDS = 0.1;   // the mean square is kept per 100 ms: 4 blocks make 400 ms, 30 blocks make 3 s, and gating blocks of 400 ms overlap by 75 percent

export class LoudnessMeter {
  constructor(rate, channels = 2) {
    this.rate = rate; this.channels = channels;
    const { shelf, high } = kWeighting(rate);
    this.shelf = shelf; this.high = high;
    this.state = Array.from({ length: channels }, () => ({ s: [0, 0, 0, 0], h: [0, 0, 0, 0] }));   // x1 x2 y1 y2 of each biquad
    this.blockLength = Math.round(rate * BLOCK_SECONDS); this.sum = 0; this.count = 0;
    this.blocks = [];   // mean square (summed over channels) of each finished 100 ms block
    this.gated = [];    // the 400 ms mean squares at every 100 ms, for the integrated value
  }

  /** ins: array of channel arrays; from/to: the sample range. */
  process(ins, from, to) {
    const { shelf, high } = this;
    for (let i = from; i < to; i++) {
      let power = 0;
      for (let c = 0; c < this.channels; c++) {
        const x = (ins[Math.min(c, ins.length - 1)] || [])[i] || 0, st = this.state[c];
        const y1 = shelf.b[0] * x + shelf.b[1] * st.s[0] + shelf.b[2] * st.s[1] - shelf.a[1] * st.s[2] - shelf.a[2] * st.s[3];
        st.s[1] = st.s[0]; st.s[0] = x; st.s[3] = st.s[2]; st.s[2] = y1;
        const y2 = high.b[0] * y1 + high.b[1] * st.h[0] + high.b[2] * st.h[1] - high.a[1] * st.h[2] - high.a[2] * st.h[3];
        st.h[1] = st.h[0]; st.h[0] = y1; st.h[3] = st.h[2]; st.h[2] = y2;
        power += y2 * y2;
      }
      this.sum += power; this.count++;
      if (this.count >= this.blockLength) {
        this.blocks.push(this.sum / this.count); this.sum = 0; this.count = 0;
        if (this.blocks.length > 4000) this.blocks.splice(0, this.blocks.length - 4000);
        if (this.blocks.length >= 4) { const w = this.#mean(4); this.gated.push(w); if (this.gated.length > 36000) this.gated.splice(0, this.gated.length - 36000); }
      }
    }
  }

  #mean(blocks) { const list = this.blocks.slice(-blocks); return list.reduce((a, b) => a + b, 0) / list.length; }
  static lufs(power) { return power > 0 ? -0.691 + 10 * Math.log10(power) : -Infinity; }

  /** Momentary loudness (the last 400 ms) in LUFS, or -Infinity before 400 ms have passed or in silence. */
  momentary() { return this.blocks.length >= 4 ? LoudnessMeter.lufs(this.#mean(4)) : -Infinity; }
  /** Short-term loudness (the last 3 s) in LUFS; the mean of what there is before 3 s have passed. */
  shortTerm() { return this.blocks.length >= 4 ? LoudnessMeter.lufs(this.#mean(30)) : -Infinity; }
  /** Integrated loudness with the absolute gate (-70 LUFS) and the relative gate (10 LU below the mean of what passed the first). */
  integrated() {
    const absolute = this.gated.filter((p) => LoudnessMeter.lufs(p) > -70);
    if (!absolute.length) return -Infinity;
    const relative = LoudnessMeter.lufs(absolute.reduce((a, b) => a + b, 0) / absolute.length) - 10;
    const kept = absolute.filter((p) => LoudnessMeter.lufs(p) > relative);
    return kept.length ? LoudnessMeter.lufs(kept.reduce((a, b) => a + b, 0) / kept.length) : -Infinity;
  }
}
