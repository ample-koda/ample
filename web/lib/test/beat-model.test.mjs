import test from 'node:test';
import assert from 'node:assert/strict';
import { blockNameOf, drumsOps, fillGridOf, gridOnKit, mapVoices, parseGrid, stepGain, tempoFit } from '../beat-model.mjs';

test('a grid is read voice by voice and a modifier belongs to the voice before it', () => {
  assert.deepEqual(parseGrid('kick:x...x... @l-6 hat:..x...x. '), [{ voice: 'kick', steps: 'x...x...', mods: ['@l-6'] }, { voice: 'hat', steps: '..x...x.', mods: [] }]);
  assert.deepEqual(parseGrid(''), []);
});

test('a fill of the catalogue is the grid before its words, or nothing when it is only words', () => {
  assert.deepEqual(fillGridOf('snare:xxxxxxxxxxxxxxxx on the last bar before a section'), [{ voice: 'snare', steps: 'xxxxxxxxxxxxxxxx', mods: [] }]);
  assert.deepEqual(fillGridOf('none; let it breathe'), []);
});

test('the voices of the beat find the voices of a kit, by name first and then by a likely alternative', () => {
  const kit = ['kick', 'snare', 'hat'];
  assert.deepEqual(mapVoices(['kick', 'clap', 'hat', 'cowbell'], kit), { kick: 'kick', clap: 'snare', hat: 'hat', cowbell: null });
  assert.equal(mapVoices(['openhat'], ['Kick', 'Hat']).openhat, 'Hat');
});

test('the grid on a kit renames voices, puts two that meet together and lists what has no place', () => {
  const entries = parseGrid('kick:x...x... clap:....x... snare:..x....x cowbell:x.......');
  const { grid, skipped } = gridOnKit(entries, { kick: 'kick', clap: 'snare', snare: 'snare', cowbell: null });
  assert.equal(grid, 'kick:x...x... snare:..x.x..x');
  assert.deepEqual(skipped, ['cowbell']);
});

test('a beat fits a tempo, in double time, in half time or not', () => {
  const tempo = { min: 118, max: 130 };
  assert.equal(tempoFit(124, tempo).kind, 'in range');
  assert.equal(tempoFit(62, tempo).kind, 'double');
  assert.equal(tempoFit(250, tempo).kind, 'half');
  assert.equal(tempoFit(90, tempo).kind, 'outside');
});

test('the operations add one drums block with its kit, patterns, song and place', () => {
  const ops = drumsOps({ name: 'house', kit: 'samples/kit', grid: 'kick:x...', fill: 'snare:xxxx', bar: 5, bars: 8, swing: 12, humanize: 10, spot: { x: 40, y: 40 } });
  assert.deepEqual(ops.map((o) => o.type), ['addBlock', 'setParameter', 'setParameter', 'setParameter', 'setParameter', 'setParameter', 'setParts', 'setLayout']);
  assert.equal(ops.find((o) => o.name === 'patterns').value, 'groove=kick:x... / fill=snare:xxxx');
  assert.equal(ops.find((o) => o.name === 'song').value, 'groove*7 fill');
  assert.deepEqual(ops.find((o) => o.type === 'setParts').parts, [{ at: 5, length: 8, name: '' }]);
  const plain = drumsOps({ name: 'a', kit: '', grid: 'kick:x...', fill: '', bar: 1, bars: 4, swing: 0, humanize: 0 });
  assert.equal(plain.find((o) => o.name === 'song').value, 'groove*4');
  assert.equal(plain.some((o) => o.name === 'kit' || o.name === 'swing' || o.name === 'humanize'), false);
});

test('a name from an id and the gain of a step', () => {
  assert.equal(blockNameOf('House: four on the floor'), 'house-four-on-the-floor');
  assert.equal(stepGain('.'), 0);
  assert.ok(stepGain('x') > stepGain('3'));
  assert.ok(stepGain('x', ['@l-6']) < stepGain('x'));
});
