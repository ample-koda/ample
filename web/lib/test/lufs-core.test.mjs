import assert from 'node:assert/strict';
import test from 'node:test';
import { LoudnessMeter } from '../lufs-core.mjs';

const sine = (hz, amplitude, seconds, rate) => Float32Array.from({ length: Math.round(seconds * rate) }, (_, i) => amplitude * Math.sin(2 * Math.PI * hz * i / rate));

test('a 997 Hz stereo sine at -20 dBFS peak reads -20.0 LUFS (the -0.691 offset of BS.1770 cancels the +0.69 dB of the K-weighting at 1 kHz)', () => {
  for (const rate of [44100, 48000]) {
    const meter = new LoudnessMeter(rate, 2), tone = sine(997, 0.1, 5, rate);
    for (let start = 0; start < tone.length; start += 128) meter.process([tone, tone], start, Math.min(tone.length, start + 128));
    assert.ok(Math.abs(meter.momentary() - -20.0) < 0.1, `momentary ${meter.momentary()}`);
    assert.ok(Math.abs(meter.shortTerm() - -20.0) < 0.1, `short-term ${meter.shortTerm()}`);
    assert.ok(Math.abs(meter.integrated() - -20.0) < 0.1, `integrated ${meter.integrated()}`);
  }
});

test('K-weighting: a low tone counts for less, a loud tone for more, silence is -Infinity', () => {
  const rate = 48000, loud = new LoudnessMeter(rate, 1), low = new LoudnessMeter(rate, 1), quiet = new LoudnessMeter(rate, 1);
  const a = sine(997, 0.2, 3, rate), b = sine(40, 0.2, 3, rate), c = new Float32Array(rate * 3);
  loud.process([a], 0, a.length); low.process([b], 0, b.length); quiet.process([c], 0, c.length);
  assert.ok(low.momentary() < loud.momentary() - 3);
  assert.equal(quiet.momentary(), -Infinity);
  const double = new LoudnessMeter(rate, 1); const d = sine(997, 0.4, 3, rate); double.process([d], 0, d.length);
  assert.ok(Math.abs(double.momentary() - loud.momentary() - 6.02) < 0.05);
});

test('the integrated value leaves out silence by the gates', () => {
  const rate = 48000, meter = new LoudnessMeter(rate, 2), tone = sine(997, 0.1, 4, rate), silence = new Float32Array(rate * 8);
  meter.process([tone, tone], 0, tone.length); meter.process([silence, silence], 0, silence.length);
  assert.ok(Math.abs(meter.integrated() - -20.0) < 0.3, `integrated ${meter.integrated()}`);
});
