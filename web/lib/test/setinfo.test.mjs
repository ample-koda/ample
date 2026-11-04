import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ENGINE, layerHint, missingLine, regionRows } from '../setinfo.js';

test('the engine plays every layer and round robin', () => {
  assert.equal(layerHint({ layers: 4, roundRobins: 3 }), '4 layers on disk, engine plays 4; 3 round robins on disk, engine plays 3');
  assert.equal(layerHint({ layers: 1, roundRobins: 1 }), '');
  assert.equal(ENGINE.layers >= 16, true);
});

test('layer hint still reports a narrower engine honestly', () => {
  assert.equal(layerHint({ layers: 4, roundRobins: 1 }, { layers: 1, roundRobins: 1 }), '4 layers on disk, engine plays 1');
});

test('missing line compares source and disk; the engine plays all that is on disk', () => {
  assert.equal(missingLine({ layers: 2, roundRobins: 1, offered: { layersOffered: 6 } }), '4 layers left on the source (2 of 6 fetched)');
  assert.equal(missingLine({ layers: 2, roundRobins: 1, offered: { layersOffered: 6 } }, { layers: 1, roundRobins: 1 }), '4 layers left on the source (2 of 6 fetched); 1 layer on disk the engine does not play yet');
  assert.equal(missingLine({ layers: 1, roundRobins: 1 }), '');
});

test('region rows fall back to file names for old servers', () => {
  assert.deepEqual(regionRows({ files: ['kick'] }), [{ voice: 'kick', layers: 1, roundRobins: 1, plays: '1 x 1', loop: 'none' }]);
  assert.equal(regionRows({ voices: [{ name: 'a2', layers: 3, roundRobins: 2 }] })[0].plays, '3 x 2');
});
