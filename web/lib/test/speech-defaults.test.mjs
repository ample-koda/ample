import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { FLITE_PITCH_DEFAULT, LEVEL_DEFAULT, SPEECH_DEFAULTS, speechShown } from '../speech-defaults.mjs';

const template = JSON.parse(readFileSync(new URL('../../../spec/templates/speech.json', import.meta.url), 'utf8'));
const numeric = (text) => Number.parseFloat(String(text));

test('the knobs of the speech panel start from the defaults of the speech block', () => {
  for (const [name, shown] of Object.entries(SPEECH_DEFAULTS)) {
    const parameter = template.parameters.find((p) => p.name === name);
    assert.ok(parameter, `${name} is a parameter of the speech block`);
    assert.equal(numeric(shown), numeric(parameter.default), `${name}: the panel shows ${shown}, the block does ${parameter.default}`);
    assert.ok(numeric(shown) >= parameter.min && numeric(shown) <= parameter.max, `${name} is inside its range`);
  }
});

test('a written parameter wins, and flite has its own pitch', () => {
  assert.equal(speechShown({}, 'humanico').pitch_hz, '105 Hz');
  assert.equal(speechShown({}, 'flite').pitch_hz, FLITE_PITCH_DEFAULT);
  assert.equal(speechShown({ pitch_hz: '120 Hz' }, 'flite').pitch_hz, '120 Hz');
  assert.equal(speechShown({ rate: '0.8' }, 'retro').rate, '0.8');
  assert.equal(speechShown({}, 'retro').gain, '0 dB');
});

test('the loudness choice of the panel is the one of the block', () => {
  assert.equal(LEVEL_DEFAULT, template.parameters.find((p) => p.name === 'level').default);
});
