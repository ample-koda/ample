import test from 'node:test';
import assert from 'node:assert/strict';
import { linkAt, linksIn } from '../source-links.js';

const blocks = ['lead', 'words'];
const files = ['lyrics/twinkle-final.lyr', 'song.amplet'];

test('block names in a header and in a from line are links', () => {
  assert.deepEqual(linksIn('piano lead pinned@2.1 at 1 for 12 {', blocks, files), [{ start: 6, end: 10, kind: 'block', target: 'lead' }]);
  assert.equal(linksIn('  from words.words -> lyrics', blocks, files)[0].target, 'words');
  assert.deepEqual(linksIn('  from nobody.audio -> in1', blocks, files), []);
});

test('the path of a project file is a link, quoted or not', () => {
  assert.equal(linkAt('  file lyrics/twinkle-final.lyr', 10, blocks, files).kind, 'file');
  assert.equal(linkAt('  file "lyrics/twinkle-final.lyr"', 10, blocks, files).target, 'lyrics/twinkle-final.lyr');
  assert.equal(linkAt('  file lyrics/other.lyr', 10, blocks, files), null);
});
