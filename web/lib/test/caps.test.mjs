import test from 'node:test';
import assert from 'node:assert/strict';
import { capabilities, whyNot } from '../caps.mjs';

class Full { get audioWorklet() { return {}; } get outputLatency() { return 0; } }
Object.defineProperty(Full.prototype, 'audioWorklet', { get() { return {}; } });

test('a full browser has everything it can report', () => {
  const caps = capabilities({ AudioContext: Full, AudioWorkletNode: class {}, OfflineAudioContext: class {}, navigator: { requestMIDIAccess() {} } });
  assert.equal(caps.webAudio, true); assert.equal(caps.worklet, true); assert.equal(caps.midi, true); assert.equal(caps.outputLatency, true);
  assert.equal(whyNot('worklet', caps), '');
});

test('stubbed-out APIs give a message instead of a crash', () => {
  const none = capabilities({});
  assert.equal(none.webAudio, false); assert.equal(none.worklet, false); assert.equal(none.midi, false);
  assert.match(whyNot('webAudio', none), /Web Audio/);
  assert.match(whyNot('midi', none), /Chrome or Edge/);
  const noWorklet = capabilities({ AudioContext: class {}, navigator: {} });
  assert.equal(noWorklet.webAudio, true); assert.equal(noWorklet.worklet, false); assert.equal(noWorklet.outputLatency, false);
  assert.match(whyNot('outputLatency', noWorklet), /Safari/);
});
