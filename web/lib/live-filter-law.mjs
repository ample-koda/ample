// The filter block of the live graph made to sound like the server (Effects.cs Filter, ResonantFilter.cs; the offline port is clientside-filter.mjs).
// Without resonance, a single pass and a low or high pass the server runs two equal one-pole stages ("the pair"): -3 dB at the cutoff, 12 dB per octave. Two equal real poles are one
// second-order section with Q 0.5 (-6.02 dB in the Web Audio dB reading of lowpass / highpass), at 1.554 times the cutoff for a low pass and 0.644 times for a high pass.
// With resonance, band pass, notch or a slope above 12 the server runs a state-variable filter whose Q is 10^(resonance * 0.18 / 20), once per 12 dB of slope; that is the RBJ biquad
// at the cutoff with the same Q (the same tan() warping). Web Audio reads the Q of a low pass and high pass in dB, of a band pass and notch as a plain number.
const PAIR_LOW = 1.554, PAIR_HIGH = 0.644, PAIR_Q = 0.5;
const PASSES = { 24: 2, 36: 3, 48: 4 };
const number = (value, fallback) => { const n = Number.parseFloat(String(value ?? '').replace('%', '')); return Number.isFinite(n) ? n : fallback; };

/** The cutoff of a filter block in Hz, read like the render (clientside-filter.mjs): "800", "800 Hz", "1.2 kHz"; 20..20000; 1000 when it is not a number (an expression). */
export function cutoffHz(value) {
  const text = String(value ?? '').trim(), parsed = Number.parseFloat(text.replace(/kHz/i, '')) * (/kHz/i.test(text) ? 1000 : 1);
  return Math.max(20, Math.min(20000, Number.isFinite(parsed) ? parsed : 1000));
}

/** The sections of a filter block: { kind, passes, scale (times the cutoff), q (value for BiquadFilterNode.Q of `kind`), pair (the server's plain pair of one-poles: the live graph runs it in a worklet) }. */
export function filterLaw(params) {
  const kind = ['highpass', 'bandpass', 'notch'].includes(params.type) ? params.type : 'lowpass';
  const passes = PASSES[String(params.slope ?? '12').trim()] || 1;
  const resonance = Math.max(0, Math.min(100, number(params.resonance, 0)));
  const linearQ = 10 ** (resonance * 0.18 / 20);
  const svf = resonance > 0 || kind === 'bandpass' || kind === 'notch' || passes > 1;
  if (!svf) return { kind, passes: 1, scale: kind === 'highpass' ? PAIR_HIGH : PAIR_LOW, q: 20 * Math.log10(PAIR_Q), pair: true };
  return { kind, passes, scale: 1, pair: false, q: kind === 'lowpass' || kind === 'highpass' ? 20 * Math.log10(linearQ) : linearQ };
}
