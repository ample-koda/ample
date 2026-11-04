import test from 'node:test';
import assert from 'node:assert/strict';
import { blockText, blocksFor, formatValue, freeBlockName, kindOf, paramAccepts } from '../file-kinds.mjs';
import { applyDrop, dropTargets, nearestTarget, scanBlocks } from '../drop-targets.mjs';
import { fuzzyScore, rankFiles } from '../fuzzy.mjs';
import { fileState, saveMenuState } from '../file-state.mjs';

const templates = [
  { id: 'midi', title: 'MIDI file', class: 'stable', version: '1.0', parameters: [{ name: 'file', kind: 'file', accept: ['.mid', '.midi'] }] },
  { id: 'import-audio', title: 'Import audio', class: 'stable', version: '1.0', parameters: [{ name: 'file', kind: 'file', accept: ['.wav', '.flac'] }] },
  { id: 'sampler', title: 'Sampler', class: 'stable', version: '1.0', parameters: [{ name: 'file', kind: 'file', accept: ['.wav'] }] },
  { id: 'gm', title: 'General MIDI', class: 'stable', version: '1.0', parameters: [{ name: 'file', kind: 'file', accept: ['.sf2'] }] },
  { id: 'drums', title: 'Drums', class: 'stable', version: '1.2', parameters: [{ name: 'kit', kind: 'sampleset' }, { name: 'fallback', kind: 'sampleset' }, { name: 'pattern', kind: 'text' }] },
];

const song = 'composition "s" { ample 1  tempo 90  master-seed 1 }\nmidi bass {\n  bars 4\n}\nimport-audio rain {\n  file samples/rain.wav\n}\ndrums beat {\n  kit samples/kit\n  pattern beat-A\n}\n';

test('a file is a kind by its extension, a folder is the folder kind', () => {
  assert.equal(kindOf('assets/midi/lead.MID').id, 'midi');
  assert.equal(kindOf('samples/kick.wav').id, 'audio');
  assert.equal(kindOf('samples/GM.sf2').id, 'soundfont');
  assert.equal(kindOf('samples/kit', true).id, 'folder');
  assert.equal(kindOf('notes/readme.md'), null);
  assert.equal(kindOf('noextension'), null);
});

test('a parameter takes a kind when its accepted extensions include one of the kind, sample sets take folders', () => {
  const midi = kindOf('a.mid'), wav = kindOf('a.wav'), folder = kindOf('x', true);
  assert.equal(paramAccepts(templates[0].parameters[0], midi), true);
  assert.equal(paramAccepts(templates[0].parameters[0], wav), false);
  assert.equal(paramAccepts(templates[4].parameters[0], folder), true);
  assert.equal(paramAccepts(templates[4].parameters[0], wav), false);
  assert.equal(paramAccepts(templates[4].parameters[2], folder), false);
});

test('the blocks for a kind come with the preferred block first', () => {
  const choices = blocksFor(kindOf('a.wav'), templates).map((c) => c.block);
  assert.deepEqual(choices, ['import-audio', 'sampler']);
  assert.deepEqual(blocksFor(kindOf('x', true), templates).map((c) => `${c.block}.${c.param}`), ['drums.fallback', 'drums.kit'].sort((a, b) => (b === 'drums.kit') - (a === 'drums.kit')));
  assert.deepEqual(blocksFor(kindOf('a.sf2'), templates).map((c) => c.block), ['gm']);
});

test('values are quoted only when they need it and block names are free names', () => {
  assert.equal(formatValue('samples/kit/kick.wav'), 'samples/kit/kick.wav');
  assert.equal(formatValue('samples/my kit/kick.wav'), '"samples/my kit/kick.wav"');
  assert.equal(freeBlockName('samples/Kick Drum.wav', []), 'Kick-Drum');
  assert.equal(freeBlockName('samples/kick.wav', ['kick', 'kick-2']), 'kick-3');
});

test('block text for a file, a folder and a module', () => {
  assert.equal(blockText(kindOf('a.mid'), 'assets/midi/lead.mid', { template: templates[0], name: 'lead' }), 'midi lead {\n  file assets/midi/lead.mid\n}\n');
  assert.equal(blockText(kindOf('x', true), 'samples/kit', { template: templates[4], name: 'kit' }), 'drums kit {\n  kit samples/kit\n}\n');
  assert.equal(blockText(kindOf('a.module'), 'modules/groove.module', { instance: 'groove' }), 'use groove as groove {\n}\n');
  assert.equal(blockText(null, 'x', {}), null);
});

test('scanning finds the blocks, their end and their parameter lines', () => {
  const { blocks } = scanBlocks(song);
  assert.deepEqual(blocks.map((b) => [b.type, b.name, b.line, b.end]), [['midi', 'bass', 1, 3], ['import-audio', 'rain', 4, 6], ['drums', 'beat', 7, 10]]);
  const rain = blocks[1].params[0];
  assert.equal(rain.value, 'samples/rain.wav');
  assert.equal(song.split('\n')[rain.line].slice(rain.valueStart, rain.valueEnd), 'samples/rain.wav');
});

test('drop targets: a line with the parameter, a block that has none yet, and the places between blocks', () => {
  const wav = dropTargets(song, templates, kindOf('a.wav'));
  assert.deepEqual(wav.fields.map((f) => [f.block, f.param, f.line, f.add]), [['rain', 'file', 5, false]]);
  assert.deepEqual(wav.inserts.map((i) => i.line), [1, 4, 7, 11]);
  const midi = dropTargets(song, templates, kindOf('a.mid'));
  assert.deepEqual(midi.fields.map((f) => [f.block, f.add, f.line]), [['bass', true, 3]]);
  const folder = dropTargets(song, templates, kindOf('x', true));
  assert.deepEqual(folder.fields.map((f) => [f.param, f.add]).sort(), [['fallback', true], ['kit', false]]);
  assert.deepEqual(dropTargets(song, templates, kindOf('a.txt')).inserts, []);   // no block takes a text file here (the lyrics block is not in these templates)
});

test('the nearest target: a field on the line wins, then a field next to it, then the closest place between blocks', () => {
  const wav = dropTargets(song, templates, kindOf('a.wav'));
  assert.equal(nearestTarget(wav, 5).type, 'field');
  assert.equal(nearestTarget(wav, 6).type, 'field');
  assert.equal(nearestTarget(wav, 8, 0), null);
  assert.deepEqual([nearestTarget(wav, 10, 2).type, nearestTarget(wav, 10, 2).line], ['insert', 11]);
});

test('a drop fills the line, asks before replacing a different file and adds a missing parameter', () => {
  const wav = dropTargets(song, templates, kindOf('a.wav'));
  const field = nearestTarget(wav, 5);
  assert.deepEqual(applyDrop(song, field, 'samples/rain.wav', templates).line, 5);   // the same file: nothing to ask
  const ask = applyDrop(song, field, 'samples/storm.wav', templates);
  assert.equal(ask.needsConfirm, true);
  assert.equal(ask.existing, 'samples/rain.wav');
  const replaced = applyDrop(song, field, 'samples/big storm.wav', templates, { replace: true });
  assert.match(replaced.text, /file "samples\/big storm.wav"/);
  const midi = nearestTarget(dropTargets(song, templates, kindOf('a.mid')), 3);
  assert.equal(applyDrop(song, midi, 'assets/midi/lead.mid', templates).text.split('\n')[3], '  file assets/midi/lead.mid');
});

test('a drop between blocks adds a block with a free name and keeps blocks apart', () => {
  const targets = dropTargets(song, templates, kindOf('a.mid'));
  const insert = targets.inserts.find((i) => i.line === 4);
  const result = applyDrop(song, { type: 'insert', ...insert }, 'assets/midi/rain.mid', templates);
  const lines = result.text.split('\n');
  assert.equal(lines[4], '');
  assert.equal(lines[5], 'midi rain-2 {');
  assert.equal(lines[6], '  file assets/midi/rain.mid');
  assert.equal(result.line, 5);
  assert.equal(applyDrop(song, { type: 'insert', line: 4 }, 'notes/x.md', templates).error, 'This kind of file cannot be used in a song.');
});

test('line endings of the song are kept', () => {
  const crlf = song.replace(/\n/g, '\r\n');
  const field = nearestTarget(dropTargets(crlf, templates, kindOf('a.wav')), 5);
  const out = applyDrop(crlf, field, 'samples/storm.wav', templates, { replace: true }).text;
  assert.ok(out.includes('\r\n') && !/[^\r]\n/.test(out));
});

test('quick search: letters in order, file names and word starts first, recent files with nothing typed', () => {
  assert.ok(fuzzyScore('chor', 'lyrics/chorus.lrc') > fuzzyScore('chor', 'archive/the-chord-sheet-orchestra.txt'));
  assert.equal(fuzzyScore('zzz', 'lyrics/chorus.lrc'), -1);
  const paths = ['song.amplet', 'lyrics/chorus.lrc', 'lyrics/verse1.lrc', 'modules/groove.module'];
  assert.deepEqual(rankFiles(paths, 'chor'), ['lyrics/chorus.lrc']);
  assert.deepEqual(rankFiles(paths, '', ['modules/groove.module', 'gone.txt']).slice(0, 2), ['modules/groove.module', 'lyrics/chorus.lrc']);
  assert.deepEqual(rankFiles(paths, 'nothing'), []);
});

test('the three states of a file and what the save menu offers in each', () => {
  assert.equal(fileState({}).id, 'saved');
  assert.equal(fileState({ changed: true }).id, 'changed');
  assert.equal(fileState({ typed: true, changed: true }).id, 'typed');
  const saved = saveMenuState(fileState({}), { hasSong: true, hasTake: false });
  assert.deepEqual([saved.save, saved.revert, saved.saveAs, saved.renderBlock], [false, false, true, false]);
  const changed = saveMenuState(fileState({ changed: true }), { hasSong: true, hasTake: true });
  assert.deepEqual([changed.save, changed.revert, changed.renderBlock], [true, true, true]);
  assert.equal(saveMenuState(fileState({}), { hasSong: false }).saveAs, false);
});
