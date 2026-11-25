import test from 'node:test';
import assert from 'node:assert/strict';
import { syncSamples } from '../clientside-delay.mjs';
import { setBarBeats } from '../meter.mjs';

// Bug 26 (Studio bug hunt 2026-11-24): a tempo-synced note value was a part of the bar, so in 3/4 a 1/8 delay echoed 25 % early.
// A note value is a part of a whole note (four beats) in any meter, on the server (delay 2, filter 5, tremolo 2) and in the previews.
test('a synced delay of 1/8 is an eighth note in 4/4, 3/4 and 7/8', () => {
  try {
    setBarBeats(4);
    assert.equal(syncSamples({ sync: '1/4' }, 44100, 120), 22050);
    assert.equal(syncSamples({ sync: '1/8' }, 44100, 120), 11025);
    setBarBeats(3);
    assert.equal(syncSamples({ sync: '1/8' }, 44100, 120), 11025);
    assert.equal(syncSamples({ sync: '1/4' }, 44100, 120), 22050);
    setBarBeats(3.5);
    assert.equal(syncSamples({ sync: '1/8' }, 44100, 120), 11025);
    assert.equal(syncSamples({ sync: 'off' }, 44100, 120), 0);
  } finally { setBarBeats(4); }
});
