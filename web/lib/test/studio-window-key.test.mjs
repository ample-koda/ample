import test from 'node:test';
import assert from 'node:assert/strict';
import { foreignKeys, keyBelongsTo, windowKey } from '../studio-window-key.mjs';

test('the same block id in two songs gives two different keys (issue 159)', () => {
  assert.notEqual(windowKey('a.amplet', null, 'fx'), windowKey('b.amplet', null, 'fx'));
});

test('the same block in the same song gives the same key', () => {
  assert.equal(windowKey('a.amplet', null, 'fx'), windowKey('a.amplet', null, 'fx'));
  assert.equal(windowKey('a.amplet', '', 'fx'), windowKey('a.amplet', undefined, 'fx'));
});

test('a block of a module is not the block of the song that has the same id', () => {
  assert.notEqual(windowKey('a.amplet', 'modules/verse.module', 'fx'), windowKey('a.amplet', null, 'fx'));
});

test('a different block id is a different key, and joining characters cannot make two keys equal', () => {
  assert.notEqual(windowKey('a.amplet', null, 'fx'), windowKey('a.amplet', null, 'fx2'));
  assert.notEqual(windowKey('a', 'b', 'c'), windowKey('a|b', '', 'c'));
  assert.notEqual(windowKey('a.amplet', null, 'fx'), windowKey('a.amplet|', null, 'fx'));
});

test('a key belongs to the song and the module that it was made for', () => {
  const key = windowKey('a.amplet', 'modules/verse.module', 'fx');
  assert.equal(keyBelongsTo(key, 'a.amplet', 'modules/verse.module'), true);
  assert.equal(keyBelongsTo(key, 'b.amplet', 'modules/verse.module'), false);
  assert.equal(keyBelongsTo(key, 'a.amplet', null), false);
  assert.equal(keyBelongsTo(windowKey('a.amplet', null, 'fx'), 'a.amplet', undefined), true);
});

test('a key that cannot be read belongs to nothing', () => {
  assert.equal(keyBelongsTo('not json', 'a.amplet', null), false);
  assert.equal(keyBelongsTo('', 'a.amplet', null), false);
});

test('after a switch to another song every window of the old one is to be closed, and none of the new one', () => {
  const keys = [windowKey('a.amplet', null, 'fx'), windowKey('a.amplet', null, 'eq'), windowKey('b.amplet', null, 'fx')];
  assert.deepEqual(foreignKeys(keys, 'b.amplet', null), [keys[0], keys[1]]);
  assert.deepEqual(foreignKeys(keys, 'a.amplet', null), [keys[2]]);
  assert.deepEqual(foreignKeys([], 'a.amplet', null), []);
});

test('opening or leaving a module closes the windows of the other context', () => {
  const song = windowKey('a.amplet', null, 'fx');
  const inModule = windowKey('a.amplet', 'modules/verse.module', 'fx');
  assert.deepEqual(foreignKeys([song, inModule], 'a.amplet', 'modules/verse.module'), [song]);
  assert.deepEqual(foreignKeys([song, inModule], 'a.amplet', null), [inModule]);
});
