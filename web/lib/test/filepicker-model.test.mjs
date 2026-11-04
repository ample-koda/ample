import test from 'node:test';
import assert from 'node:assert/strict';
import { filterProjectFiles, parentFolder } from '../filepicker-model.mjs';

test('file picker filtering respects text and accepted extensions', () => {
  const files = [{ name: 'kick.wav', isDir: false }, { name: 'riff.mid', isDir: false }, { name: 'samples', isDir: true }];
  assert.deepEqual(filterProjectFiles(files, 'KICK', ['.wav']), [files[0]]);
  assert.deepEqual(filterProjectFiles(files, '', ['.mid']), [files[1]]);
});

test('the folders follow after the files when asked for', () => {
  const items = [{ name: 'samples', isDir: true }, { name: 'kick.wav', isDir: false }, { name: 'drums', isDir: true }, { name: 'riff.mid', isDir: false }];
  assert.deepEqual(filterProjectFiles(items, '', ['.wav'], true).map((i) => i.name), ['kick.wav', 'samples', 'drums']);
  assert.deepEqual(filterProjectFiles(items, 'dr', [], true).map((i) => i.name), ['drums']);
  assert.deepEqual(filterProjectFiles(items, '', ['.wav']).map((i) => i.name), ['kick.wav']);
});

test('file picker parent folder normalizes separators', () => {
  assert.equal(parentFolder('lyrics\\verse\\song.lyr'), 'lyrics/verse');
  assert.equal(parentFolder('song.amplet'), '');
});
