// The LFO waves of the server's modulation effects, sample for sample, as value functions of the sample index counted from the start of the take (local-preview follow-up F4).
// The live graph plays them from a looping buffer that starts with the loop, so the sweep of a chorus, flanger, phaser, tremolo or filter LFO has the phase it has in the render
// (an OscillatorNode starts wherever the graph was built). Values are -1..1, the same signal the oscillator had.

/** The triangle of ModDelay and the phaser: period in whole samples, 0 at the start rising to 1 at half a period; as -1..1 it starts at -1. */
export function triangleWave(periodSamples, offset = 0) {
  const period = Math.max(2, Math.floor(periodSamples)), half = Math.max(1, Math.floor(period / 2));
  return (i) => { const phase = (i + offset) % period; return (phase < half ? phase / half : (period - phase) / half) * 2 - 1; };
}

/** The phaser's integer triangle (0..32767 truncated), as -1..1. */
export function phaserWave(periodSamples) {
  const period = Math.max(2, Math.floor(periodSamples)), half = Math.max(1, Math.floor(period / 2));
  return (i) => { const phase = i % period, t = Math.trunc(phase < half ? phase * 32767 / half : (period - phase) * 32767 / half); return t / 32767 * 2 - 1; };
}

/** The filter LFO: `cycles` per bar of `barSamples`, shape sine (starts low), triangle, saw or square (0..1 in the render, -1..1 here). */
export function filterWave(shape, cycles, barSamples) {
  return (i) => {
    const phase = (((i / barSamples * cycles) % 1) + 1) % 1;
    const wave = shape === 'triangle' ? (phase < .5 ? phase * 2 : 2 - phase * 2) : shape === 'saw' ? phase : shape === 'square' ? (phase < .5 ? 0 : 1) : .5 - .5 * Math.cos(2 * Math.PI * phase);
    return wave * 2 - 1;
  };
}

/** The tremolo LFO: hz, shape sine, triangle (starts at +1) or square, phase in degrees. */
export function tremoloWave(shape, hz, rate, phaseDegrees = 0) {
  const phase = phaseDegrees / 360;
  return (i) => {
    const t = i * hz / rate + phase, frac = t - Math.floor(t);
    return shape === 'triangle' ? 4 * Math.abs(frac - .5) - 1 : shape === 'square' ? (frac < .5 ? 1 : -1) : Math.sin(2 * Math.PI * frac);
  };
}

/** Fills a mono buffer of `length` samples with a wave (value function of the sample index). */
export function fillWave(target, wave) {
  for (let i = 0; i < target.length; i++) target[i] = wave(i);
  return target;
}
