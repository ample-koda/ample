import test from 'node:test';
import assert from 'node:assert/strict';
import { LEVELS, logRows, levelCounts, whereText, messageText, inSong, emptyText } from '../log-view.mjs';

const events = [
  { seq: 3, level: 'debug', message: 'step', line: 5, col: 3, origin: 'song' },
  { seq: 1, level: 'info', message: 'start', code: 'GO', context: { count: 2 }, line: 4, col: 3, origin: 'song' },
  { seq: 2, level: 'debug', message: 'step', line: 5, col: 3, origin: 'song' },
  { seq: 4, level: 'warning', message: 'loud', line: 9, col: 5, origin: 'component:koda.fx.echo' },
  { seq: 5, level: 'trace', message: 'fine', line: 6, col: 3, origin: 'song' },
  { seq: 6, level: 'warning', message: '12 events were left out', code: 'AMP6007', line: 0 },
];

test('rows follow the order of the events and fold repeats into one row with a count', () => {
  const rows = logRows(events);
  assert.deepEqual(rows.map((r) => [r.event.message, r.count]), [['start', 1], ['step', 2], ['loud', 1], ['fine', 1], ['12 events were left out', 1]]);
  assert.equal(rows.at(-1).kind, 'budget');
});

test('a level filter hides the other levels but never the budget notice', () => {
  const rows = logRows(events, ['warning']);
  assert.deepEqual(rows.map((r) => r.event.message), ['loud', '12 events were left out']);
  assert.deepEqual(levelCounts(events), { warning: 1, info: 1, debug: 2, trace: 1 });
  assert.deepEqual(LEVELS, ['warning', 'info', 'debug', 'trace']);
});

test('the place, the text and whether an event leads to the song', () => {
  assert.equal(whereText(events[1]), '4:3');
  assert.equal(whereText(events[3]), '9:5 in koda.fx.echo');
  assert.equal(messageText(events[1]), 'start [GO] {"count":2}');
  assert.equal(messageText({ message: 'x', context: '{"a":1}' }), 'x {"a":1}');   // an older server sent the context as text
  assert.equal(inSong(events[1]), true);
  assert.equal(inSong(events[3]), false);
});

test('an empty log view says why', () => {
  assert.match(emptyText({ level: '' }), /info, debug or trace/);
  assert.match(emptyText({ level: 'off' }), /off/);
  assert.match(emptyText({ level: 'debug' }), /nothing at the level debug/);
  assert.match(emptyText({ events, level: 'debug' }), /levels that are shown/);
});
