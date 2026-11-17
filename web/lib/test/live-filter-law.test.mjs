import test from 'node:test';
import assert from 'node:assert/strict';
import { filterLaw, cutoffHz } from '../live-filter-law.mjs';

// RBJ biquad (what Web Audio runs) magnitude at `hz`; lowpass / highpass read Q in dB
const magnitude = ({ kind, scale, q }, cutoff, hz, rate = 48000) => {
  const f0 = cutoff * scale, w0 = 2 * Math.PI * f0 / rate, Q = kind === 'lowpass' || kind === 'highpass' ? 10 ** (q / 20) : q, alpha = Math.sin(w0) / (2 * Q), c = Math.cos(w0);
  const [b0, b1, b2] = kind === 'highpass' ? [(1 + c) / 2, -(1 + c), (1 + c) / 2] : [(1 - c) / 2, 1 - c, (1 - c) / 2];
  const a0 = 1 + alpha, a1 = -2 * c, a2 = 1 - alpha, w = 2 * Math.PI * hz / rate;
  const re = (x0, x1, x2) => x0 + x1 * Math.cos(w) + x2 * Math.cos(2 * w), im = (x1, x2) => -x1 * Math.sin(w) - x2 * Math.sin(2 * w);
  const nr = re(b0, b1, b2), ni = im(b1, b2), dr = re(a0, a1, a2), di = im(a1, a2);
  return Math.sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
};
const db = (x) => 20 * Math.log10(x);

test('without resonance a low pass is the server pair: -3 dB at the cutoff, no peak, 12 dB per octave', () => {
  const law = filterLaw({ type: 'lowpass', cutoff: '3000 Hz', slope: '12', resonance: '0%' });
  assert.equal(law.passes, 1);
  assert.ok(Math.abs(db(magnitude(law, 3000, 3000)) + 3.01) < 0.35, 'about -3 dB at the cutoff');
  assert.ok(db(magnitude(law, 3000, 300)) > -0.5 && db(magnitude(law, 3000, 300)) <= 0.001, 'flat below, no bump');
  assert.ok(db(magnitude(law, 3000, 12000)) < -10, 'rolls off above');
});
test('without resonance a high pass is the server pair at 0.644 times the cutoff', () => {
  const law = filterLaw({ type: 'highpass', cutoff: '800 Hz' });
  assert.equal(law.scale, 0.644);
  assert.ok(Math.abs(db(magnitude(law, 800, 800)) + 3.01) < 0.35);
});
test('resonance, extra slope, band pass and notch use the state-variable Q (10^(resonance * 0.18 / 20)) at the cutoff, once per 12 dB', () => {
  assert.deepEqual(filterLaw({ type: 'lowpass', slope: '24' }), { kind: 'lowpass', passes: 2, scale: 1, pair: false, q: 0 });
  const resonant = filterLaw({ type: 'lowpass', resonance: '50%' });
  assert.equal(resonant.scale, 1); assert.ok(Math.abs(resonant.q - 9) < 1e-9, 'Q in dB is resonance * 0.18');
  assert.ok(db(magnitude(resonant, 1000, 1000)) > 8.9);
  assert.equal(filterLaw({ type: 'bandpass', resonance: '0' }).q, 1);
  assert.equal(filterLaw({ type: 'notch', slope: '36' }).passes, 3);
});

test('only the plain filter is the server pair of one-poles (run in the worklet); resonance, slope, band pass and notch are state-variable (biquads)', () => {
  assert.equal(filterLaw({ cutoff: '800 Hz' }).pair, true);
  assert.equal(filterLaw({ type: 'highpass' }).pair, true);
  assert.equal(filterLaw({ resonance: '10' }).pair, false);
  assert.equal(filterLaw({ slope: '24' }).pair, false);
  assert.equal(filterLaw({ type: 'bandpass' }).pair, false);
  assert.equal(filterLaw({ type: 'notch' }).pair, false);
});

test('the cutoff is read like the render: Hz, kHz, 20..20000, 1000 for an expression', () => {
  assert.equal(cutoffHz('800 Hz'), 800);
  assert.equal(cutoffHz('1.2 kHz'), 1200);
  assert.equal(cutoffHz('5'), 20);
  assert.equal(cutoffHz('99999'), 20000);
  assert.equal(cutoffHz('auto(1:200, 5:4000)'), 1000);
  assert.equal(cutoffHz(undefined), 1000);
});
