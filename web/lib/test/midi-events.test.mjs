import test from 'node:test';
import assert from 'node:assert/strict';
import { midiEventLine, midiEventRecords, midiEventSummary, pageMidiEvents, pairMidiNoteEvents, tieGroups } from '../midi-events.js';

function generatedEvents(count) {
  return Array.from({ length: count }, (_, i) => `# source-event track=0 tick=${i} kind=ControlChange channel=${i % 16} data1=7 data2=100 meta=none payload=`).join('\n');
}

test('generated MIDI event comments parse, validate and roundtrip without changing track order', () => {
  const source = generatedEvents(4), events = midiEventRecords(source, 0);
  assert.equal(events.length, 4);
  assert.equal(midiEventLine(events[2]), '# source-event track=0 tick=2 kind=ControlChange channel=2 data1=7 data2=100 meta=none payload=');
  assert.deepEqual(events.map((event) => event.tick), [0, 1, 2, 3]);
});

test('MIDI event fields and payload are bounded before edits are serialized', () => {
  const [event] = midiEventRecords(generatedEvents(1), 0);
  assert.ok(midiEventLine(event));
  assert.equal(midiEventLine({ ...event, channel: 16 }), null);
  assert.equal(midiEventLine({ ...event, payload: '0z' }), null);
  assert.equal(midiEventLine({ ...event, payload: '0'.repeat(131072) }), null);
});

test('large generated event lists filter and page without creating a row per event', () => {
  const events = midiEventRecords(generatedEvents(2_145), 0);
  const page = pageMidiEvents(events, 'ControlChange', 'ch 5', 1, 50);
  assert.equal(page.total, 134);
  assert.equal(page.page, 1);
  assert.equal(page.events.length, 50);
});

test('note pairing links matched notes and leaves dangling or zero-length events repairable', () => {
  const events = midiEventRecords([
    '# source-event track=0 tick=0 kind=NoteOn channel=0 data1=60 data2=90 meta=none payload=',
    '# source-event track=0 tick=0 kind=NoteOff channel=0 data1=61 data2=12 meta=none payload=',
    '# source-event track=0 tick=120 kind=NoteOff channel=0 data1=60 data2=0 meta=none payload=',
    '# source-event track=0 tick=240 kind=NoteOn channel=1 data1=64 data2=70 meta=none payload=',
    '# source-event track=0 tick=240 kind=NoteOff channel=1 data1=64 data2=0 meta=none payload=',
  ].join('\n'), 0);
  const pairs = pairMidiNoteEvents(events);
  assert.equal(pairs.get(0), 2);
  assert.equal(pairs.get(2), 0);
  assert.equal(pairs.has(1), false);
  assert.equal(pairs.has(3), false);
  assert.equal(pairs.has(4), false);
});

test('event summaries expose named field values and bounded payload previews', () => {
  const [event] = midiEventRecords('# source-event track=0 tick=5 kind=SystemExclusive channel=0 data1=247 data2=0 meta=none payload=7e7f0901aabbccdd', 0);
  assert.match(midiEventSummary(event), /SysEx · data1 247 · data2 0 · payload 8 B 7e7f0901aabbccdd/);
});

test('tieGroups lists notes split at bar lines by their tie-id', () => {
  const groups = tieGroups('"1:0:1:60:80:0:0:0:7 2:0:1:60:80:0:0:0:7 2:1:1:64:80:0:0:0:9 3:0:1:67:80"');
  assert.deepEqual(groups, [{ id: 7, pitch: 60, bars: [1, 2], parts: 2 }]);
  assert.deepEqual(tieGroups('1:0:1:60:80'), []);
});
