import test from 'node:test';
import assert from 'node:assert/strict';
import { adjustableValues, appendSource, blockRanges, extractBlocks, fill, makeTemplate, parametrize, renameBlocks, shiftBars, templateId, templateProblems } from '../template-model.mjs';

const song = [
  'composition "x" { ample 1  tempo 100 }',
  'midi-clip chords {',
  '  chords Am F C G',
  '  bars 8',
  '}',
  'piano keys pinned@2.1 at 1 for 8 {',
  '  gain -9',
  '  humanize 18',
  '  from chords.notes -> notes',
  '}',
  'reverb room at 1 for 8 {',
  '  size 0.6',
  '  from keys.audio -> in',
  '}',
  'master master {',
  '  from room.out -> mix',
  '}',
].join('\n');

test('the blocks of a song are found with their first and last line', () => {
  assert.deepEqual(blockRanges(song).map((r) => [r.type, r.name, r.start, r.end]), [['midi-clip', 'chords', 1, 4], ['piano', 'keys', 5, 9], ['reverb', 'room', 10, 13], ['master', 'master', 14, 16]]);
});

test('some blocks are taken out in the order of the song; a link to a block left behind is dropped and listed', () => {
  const { source, names, external } = extractBlocks(song, ['room', 'keys']);
  assert.deepEqual(names, ['keys', 'room']);
  assert.match(source, /piano keys pinned@2\.1/);
  assert.match(source, /from keys\.audio -> in/);
  assert.doesNotMatch(source, /from chords\.notes/);
  assert.deepEqual(external, [{ block: 'keys', from: 'chords' }]);
});

test('values become settings with their old value as default, and fill puts them back', () => {
  const { source } = extractBlocks(song, ['room', 'keys']);
  assert.ok(adjustableValues(source).some((v) => v.block === 'keys' && v.param === 'humanize' && v.value === '18'));
  const { source: s2, settings } = parametrize(source, [{ block: 'keys', param: 'humanize', key: 'feel', label: 'Human feel' }]);
  assert.match(s2, /humanize \{\{feel\}\}/);
  assert.deepEqual(settings, [{ name: 'feel', label: 'Human feel', default: '18' }]);
  assert.match(fill(s2, settings, { feel: '30' }), /humanize 30/);
  assert.match(fill(s2, settings), /humanize 18/);
});

test('bars move by an offset, also in a header with several parts', () => {
  assert.match(shiftBars('piano keys pinned@2.1 at 1 for 8 {\n}', 4), /at 5 for 8/);
  assert.match(shiftBars('piano keys at 1 for 8 as verse, 9 for 8 as chorus {\n}', 8), /at 9 for 8 as verse, 17 for 8 as chorus/);
  assert.equal(shiftBars('x', 0), 'x');
});

test('blocks get free names and the links inside follow', () => {
  const { source } = extractBlocks(song, ['room', 'keys']);
  const { source: renamed, map } = renameBlocks(source, ['keys', 'room-2']);
  assert.deepEqual(map, { keys: 'keys-2', room: 'room' });
  assert.match(renamed, /piano keys-2 pinned@2\.1/);
  assert.match(renamed, /from keys-2\.audio -> in/);
  assert.match(renamed, /reverb room/);
});

test('the blocks are added at the end of a song, and a template is checked', () => {
  assert.equal(appendSource('a\n\n', ' b '), 'a\n\nb\n');
  const t = makeTemplate({ name: 'My chain', source: 'piano keys {\n  humanize {{feel}}\n}', settings: [{ name: 'feel', label: 'Feel', default: '10' }] });
  assert.equal(t.id, 'my-chain');
  assert.deepEqual(templateProblems(t), []);
  assert.ok(templateProblems({ ...t, settings: [] }).some((p) => /no setting/.test(p)));
  assert.ok(templateProblems({ format: 2 }).length > 0);
  assert.equal(templateId('  Dust & Iron! '), 'dust-iron');
});

test('the bars of an automation move with the blocks (they are bars of the song); seconds stay', () => {
  const source = 'volume rain-level at 1 for 24 {\n  gain auto(1:-8, 2:-5, 8.5:-12, 24:-6)\n  from rain.audio -> in\n}\n\nfilter tone at 3 for 4 {\n  cutoff auto(0.5s:200, 1s:400)\n}';
  const moved = shiftBars(source, 8);
  assert.match(moved, /at 9 for 24/);
  assert.match(moved, /gain auto\(9:-8, 10:-5, 16\.5:-12, 32:-6\)/);
  assert.match(moved, /cutoff auto\(0\.5s:200, 1s:400\)/);
  assert.match(moved, /at 11 for 4/);
  assert.equal(shiftBars(source, 0), source);
});

test('a block can be made a switch: on or off when the template is used', async () => {
  const { switchableBlocks, parametrize, fill } = await import('../template-model.mjs');
  const source = 'synth tone at 1 for 24 {\n  notes x\n}\n\nsynth bed at 1 for 24 disabled {\n  notes y\n}';
  assert.deepEqual(switchableBlocks(source), [{ block: 'tone', on: true }, { block: 'bed', on: false }]);
  const made = parametrize(source, [], [{ block: 'tone', key: 'use-tone', label: 'Tone' }, { block: 'bed', key: 'use-bed', label: 'Bed' }]);
  assert.match(made.source, /for 24\{\{use-tone\}\} \{/);
  assert.deepEqual(made.settings.map((s) => [s.name, s.kind, s.default]), [['use-tone', 'switch', ''], ['use-bed', 'switch', ' disabled']]);
  assert.match(fill(made.source, made.settings, {}), /synth tone at 1 for 24 \{/);   // on: nothing is added
  assert.match(fill(made.source, made.settings, {}), /synth bed at 1 for 24 disabled \{/);   // it started off
  assert.match(fill(made.source, made.settings, { 'use-tone': ' disabled' }), /for 24 disabled \{\n  notes x/);
});

test('a template master is merged into the master of the song: a free mixer input, or a new mixer', async () => {
  const { mergeMaster, hasMaster } = await import('../template-model.mjs');
  const tpl = 'synth tone at 1 for 8 {\n}\n\nmaster m2 {\n  from tone.out -> mix\n}';
  assert.equal(hasMaster(tpl), true);
  const withMixer = 'mixer bus {\n  from a.out -> in1\n}\n\nmaster final {\n  from bus.mix -> mix\n}';
  const a = mergeMaster(withMixer, tpl);
  assert.doesNotMatch(a.source, /master/);
  assert.match(a.songText, /from a\.out -> in1\n  from tone\.out -> in2\n\}/);
  assert.equal(a.mixer, '');
  const plain = 'master final {\n  from a.out -> mix\n}';
  const b = mergeMaster(plain, tpl);
  assert.equal(b.mixer, 'final-mix');
  assert.match(b.songText, /from final-mix\.mix -> mix/);
  assert.match(b.songText, /mixer final-mix \{\n  from a\.out -> in1\n  from tone\.out -> in2\n\}/);
  assert.equal(mergeMaster('synth s at 1 for 1 {\n}', tpl).source, tpl);
});
