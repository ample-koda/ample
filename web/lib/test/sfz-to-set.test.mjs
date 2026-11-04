import test from 'node:test';
import assert from 'node:assert/strict';
import { noteNumber, noteName, parseSfz, planSet } from '../../../tools/sfz-to-set.mjs';

test('note numbers and names', () => {
  assert.equal(noteNumber('60'), 60);
  assert.equal(noteNumber('c4'), 60);
  assert.equal(noteNumber('db4'), 61);
  assert.equal(noteName(66), 'f#4');
});

test('layers and round robins become the AMPLE file names', () => {
  const sfz = `<global> default_path=samples/
<group> lovel=1 hivel=63
<region> sample=c4_soft_a.wav pitch_keycenter=c4 seq_position=1
<region> sample=c4_soft_b.wav pitch_keycenter=c4 seq_position=2
<group> lovel=64 hivel=127
<region> sample=c4_hard.wav pitch_keycenter=c4
<region> sample=d4.wav key=62 lovel=1 hivel=127
<region> sample=skip.flac key=63
`;
  const { files, warnings } = planSet(parseSfz(sfz));
  const names = files.map((f) => `${f.from} -> ${f.to}`).sort();
  assert.deepEqual(names, [
    'samples/c4_hard.wav -> c4_v2.wav',
    'samples/c4_soft_a.wav -> c4_v1_r1.wav',
    'samples/c4_soft_b.wav -> c4_v1_r2.wav',
    'samples/d4.wav -> d4.wav',
  ]);
  assert.equal(warnings.length, 1);
});
