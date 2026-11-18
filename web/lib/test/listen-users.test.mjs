// Step 5 of plan/live-preview/note-origin.md: colours, numbers and the "used by" list for the users of a source.
import test from 'node:test';
import assert from 'node:assert/strict';
import { MAX_USERS, chainText, colourMarks, slotClass, slotLabel, slotsFor, usersBySource } from '../listen-users.mjs';

const NAMES = ['high', 'low', 'echo', 'bass', 'pad', 'lead'];

test('a slot depends on the names only, not on the order they come in', () => {
  const a = slotsFor(NAMES.slice(0, 3)), b = slotsFor([...NAMES.slice(0, 3)].reverse());
  assert.deepEqual([...a], [...b].sort((x, y) => a.get(x[0]) - a.get(y[0])).map(([k]) => [k, a.get(k)]).sort((x, y) => x[1] - y[1]));
  assert.deepEqual(new Set(a.values()), new Set([0, 1, 2]), 'three users, three different slots');
  assert.deepEqual(slotsFor(['high', 'high', 'low']), slotsFor(['low', 'high']), 'a name counts once');
});

test('there are at most four colours; the rest share a neutral tint', () => {
  const slots = slotsFor(NAMES);
  const coloured = [...slots.values()].filter((s) => s !== null);
  assert.equal(coloured.length, MAX_USERS);
  assert.deepEqual([...new Set(coloured)].sort(), [0, 1, 2, 3]);
  assert.equal([...slots.values()].filter((s) => s === null).length, 2);
});

test('a user keeps its colour when a user that sorts after it comes or goes', () => {
  const before = slotsFor(['high', 'low']);
  const all = slotsFor(['high', 'low', 'echo']);
  const later = [...all].map(([k]) => k).filter((k) => !before.has(k));
  assert.equal(later.length, 1);
  // only users that sort after the new one can move; the ones before it keep their slot
  const order = [...all].sort((a, b) => a[1] - b[1]).map(([k]) => k), at = order.indexOf('echo');
  for (const name of order.slice(0, at)) assert.equal(all.get(name), before.get(name));
});

test('the colour is never the only sign: a number or a plus and a class per slot', () => {
  assert.deepEqual([0, 1, 2, 3, null].map(slotLabel), ['1', '2', '3', '4', '+']);
  assert.deepEqual([0, 3, null].map(slotClass), ['lp-user-1', 'lp-user-4', 'lp-user-n']);
});

test('marks of users get the class, the number and the name; other marks stay', () => {
  const slots = slotsFor(NAMES);
  const marks = [{ from: 1, to: 2, cls: 'lp-now', user: 'high', lap: 0 }, { from: 5, to: 6, cls: 'lp-now' }, { from: 1, to: 2, cls: 'lp-now', user: 'lead', lap: 1 }];
  const out = colourMarks(marks, slots);
  assert.equal(out[1], marks[1], 'a mark without a user is untouched');
  assert.match(out[0].cls, /^lp-now lp-user-[1-4n]$/);
  assert.equal(out[0].attrs['data-lp-name'], 'high');
  assert.equal(out[0].attrs['data-lp-user'], slotLabel(slots.get('high')));
  assert.equal(out[2].attrs['data-lp-user'], slotLabel(slots.get('lead')));
});

const SONG = 'midi-clip theme {\n  melody "c4:1 d4:1"\n}\n\nmidi-clip high at 1 for auto {\n  notes theme.notes |> transpose(+12) |> bars(1..2)\n}\n\nmidi-clip plain at 1 for auto {\n  notes theme.notes\n}\n';

test('the chain text is what the clip does to its source', () => {
  assert.equal(chainText(SONG, 'high'), 'transpose(+12) > bars(1..2)');
  assert.equal(chainText(SONG, 'plain'), '');
  assert.equal(chainText(SONG, 'missing'), '');
});

test('the used-by list groups the users per source, in slot order, and counts those without a colour', () => {
  const origins = Object.fromEntries(NAMES.map((n) => [n, [{ source: 'theme', index: 0 }]]));
  origins.single = [{ source: 'other', index: 1 }];
  origins.made = [{ source: null, index: null }];
  const list = usersBySource(SONG, origins);
  assert.deepEqual(list.map((s) => s.source), ['other', 'theme']);
  const theme = list.find((s) => s.source === 'theme');
  assert.equal(theme.users.length, 6);
  assert.equal(theme.users.filter((u) => u.slot === null).length, theme.extra);
  assert.ok(theme.extra >= 1, 'with more than four users some have no colour');
  const slots = theme.users.map((u) => u.slot);
  assert.deepEqual(slots.filter((s) => s !== null), [...slots.filter((s) => s !== null)].sort(), 'coloured users come first, in slot order');
  assert.equal(theme.users.find((u) => u.user === 'high').chain, 'transpose(+12) > bars(1..2)');
  assert.ok(!list.some((s) => s.users.some((u) => u.user === 'made')), 'a note an operator made has no source');
});

test('chain clips: only midi-clips whose notes are a chain, at most MAX_CHAINS, in the order given', async () => {
  const { chainClips, MAX_CHAINS } = await import('../listen-users.mjs');
  const text = 'midi-clip theme {\n  melody "c4:1"\n}\nmidi-clip lit at 1 for 1 {\n  notes 1:0:1:60:100\n}\nmidi-clip high at 1 for auto {\n  notes theme.notes |> transpose(+12)\n}\nsynth lead {\n  from high.notes -> notes\n}\n';
  assert.deepEqual(chainClips(text, ['theme', 'lit', 'high', 'lead', 'nobody']), ['high']);
  const many = Array.from({ length: 20 }, (_, i) => `midi-clip c${i} at 1 for auto {\n  notes theme.notes |> transpose(${i + 1})\n}\n`).join('');
  assert.equal(chainClips(many, Array.from({ length: 20 }, (_, i) => `c${i}`)).length, MAX_CHAINS);
});
