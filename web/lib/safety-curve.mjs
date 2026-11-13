// The safety stage at the very end of every browser output: transparent up to KNEE (a built take limited to -1 dBFS is untouched), then a smooth tanh shoulder that never passes CEILING.
// Without it a live sum (mixer strips with positive dB gains, a bus, many SoundFont voices) goes past 1.0 and the device clips it hard, which is heard as a full, harsh sound with noise.
export const KNEE = 0.9, CEILING = 0.98, PRESCALE = 0.25;   // the shaper sees x * PRESCALE, so inputs up to 4.0 stay on the curve
export const safetyOf = (x) => { const a = Math.abs(x); return a <= KNEE ? x : Math.sign(x) * (KNEE + (CEILING - KNEE) * Math.tanh((a - KNEE) / (CEILING - KNEE))); };
export function safetyCurve(length = 8193) {
  const curve = new Float32Array(length);
  for (let i = 0; i < length; i++) curve[i] = safetyOf(((i / (length - 1)) * 2 - 1) / PRESCALE);
  return curve;
}
