import test from 'node:test';
import assert from 'node:assert/strict';
import { parseMeterReport, meterLine } from '../meter-report.mjs';

test('a meter report is read into numbers', () => {
  const r = parseMeterReport('peak -4.0 dBFS | rms -22.2 dBFS | loudness -26.3 LUFS | crest 18.1 dB | clipped 0 | 32.0 s');
  assert.deepEqual(r, { peak: -4, rms: -22.2, loudness: -26.3, crest: 18.1, clipped: 0, stereo: false, correlation: null });
  assert.equal(meterLine(r).warn, false);
  assert.match(meterLine(r).text, /peak -4\.0 · -26\.3 LUFS · crest 18\.1/);
});

test('silence and clipping are handled, other text is refused', () => {
  const r = parseMeterReport('peak silence | rms silence | loudness silence | crest n/a | clipped 3 | 1.0 s');
  assert.equal(r.peak, null);
  assert.equal(meterLine(r).warn, true);
  assert.equal(parseMeterReport('rendered'), null);
  assert.equal(parseMeterReport(undefined), null);
});

test('a stereo report carries the correlation and a negative one is a warning', () => {
  const r = parseMeterReport('peak -4.0 dBFS | rms -22.2 dBFS | loudness -26.3 LUFS | crest 18.1 dB | clipped 0 | 32.0 s | stereo L -4.0 dBFS R -4.3 dBFS | correlation 0.62');
  assert.equal(r.stereo, true);
  assert.equal(r.correlation, 0.62);
  assert.match(meterLine(r).text, /corr 0.62/);
  assert.equal(meterLine(r).warn, false);
  assert.equal(meterLine({ ...r, correlation: -0.5 }).warn, true);
});
