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

test('every voice of the flite engine has the pitch and the range that the panel shows, and the template names the same voices', async () => {
  const { FLITE_CG_VOICES, FLITE_VOICE_DEFAULTS } = await import('../speech-defaults.mjs');
  const voices = template.parameters.find((p) => p.name === 'voice').options;
  assert.deepEqual(voices, ['kal16', ...FLITE_CG_VOICES]);
  for (const voice of voices) {
    assert.ok(FLITE_VOICE_DEFAULTS[voice], `${voice} has defaults`);
    assert.equal(speechShown({}, 'flite', voice).pitch_hz, FLITE_VOICE_DEFAULTS[voice].pitch_hz);
    assert.equal(speechShown({}, 'flite', voice).pitch_range, FLITE_VOICE_DEFAULTS[voice].pitch_range);
    const pitch = numeric(FLITE_VOICE_DEFAULTS[voice].pitch_hz);
    assert.ok(pitch >= 45 && pitch <= 700, `${voice}: the pitch is inside the range of the block`);
  }

  assert.equal(speechShown({ pitch_hz: '150 Hz' }, 'flite', 'slt').pitch_hz, '150 Hz');
  assert.equal(speechShown({}, 'humanico', 'slt').pitch_hz, '105 Hz');
  assert.equal(speechShown({}, 'flite').pitch_hz, FLITE_PITCH_DEFAULT);
});

test('every voice of the flite engine names the range that it sings in words mode, wide enough to fold a note into it', async () => {
  const { FLITE_WORDS_RANGE } = await import('../speech-defaults.mjs');
  const voices = template.parameters.find((p) => p.name === 'voice').options;
  assert.deepEqual(Object.keys(FLITE_WORDS_RANGE).sort(), [...voices].sort());
  for (const [voice, [low, high]] of Object.entries(FLITE_WORDS_RANGE)) {
    assert.ok(low >= 45 && high <= 700, `${voice}: the range is inside the range of pitch_hz`);
    assert.ok(high >= 2 * low, `${voice}: the range is wider than an octave, so that every note can be folded into it`);
  }
});
