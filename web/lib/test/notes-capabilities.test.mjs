import test from 'node:test';
import assert from 'node:assert/strict';
import { CLIP_FORMS, NOTE_FIELDS, describeCapabilities } from '../notes-capabilities.mjs';
import { parseEntry } from '../notes-model.mjs';

test('the capability table names every field the notes model reads, and nothing else', () => {
  const read = new Set(Object.keys(parseEntry('1:0:1:60:96:3:0:5:7;pan=10;fine=5;rel=200;cut=80;slide=1;port=50;grp=3;customfoo=99', 1_000_000)));
  const named = new Set(Object.keys(NOTE_FIELDS).filter((k) => k !== 'start' || read.has('start')));
  for (const field of read) assert.ok(NOTE_FIELDS[field], `field "${field}" is read by the model but not in the capability table`);
  for (const field of Object.keys(NOTE_FIELDS)) assert.ok(read.has(field), `field "${field}" is in the capability table but the model does not read it`);
  assert.equal(named.size, read.size);
});

test('what the roll cannot edit is said to be edited in the text', () => {
  for (const [name, c] of Object.entries(NOTE_FIELDS)) if (!c.edit) assert.match(c.how, /text/, name);
  assert.ok(CLIP_FORMS['expanded notes'].roll === 'code' && CLIP_FORMS.chords.roll === 'convert');
  assert.equal(describeCapabilities().length, Object.keys(NOTE_FIELDS).length + Object.keys(CLIP_FORMS).length);
});
