import test from 'node:test';
import assert from 'node:assert/strict';
import { changedSection, freshName, sectionAt, sectionProblem, withSection, withoutSection } from '../view-sections.mjs';

const list = [{ name: 'Intro', at: 1, length: 8 }, { name: 'Drop', at: 9, length: 16 }];

test('sections must lie in bars 1 to 256, have a length and a name', () => {
  assert.equal(sectionProblem(list), null);
  assert.match(sectionProblem([{ name: 'A', at: 0, length: 4 }]), /inside the bars 1 to 256/);
  assert.match(sectionProblem([{ name: 'A', at: 250, length: 8 }]), /inside the bars 1 to 256/);
  assert.match(sectionProblem([{ name: 'A', at: 1, length: 0 }]), /inside the bars/);
  assert.match(sectionProblem([{ name: ' ', at: 1, length: 4 }]), /needs a name/);
  assert.equal(sectionProblem([{ name: 'A', at: 253, length: 4 }]), null);
});

test('two sections that share a bar overlap, two next to each other do not', () => {
  assert.match(sectionProblem([...list, { name: 'X', at: 20, length: 4 }]), /"Drop" and "X" overlap/);
  assert.equal(sectionProblem([...list, { name: 'X', at: 25, length: 4 }]), null);
});

test('adding, changing and removing give a sorted list or the reason', () => {
  assert.deepEqual(withSection(list, { name: 'Outro', at: 25, length: 4 }).sections.map((s) => s.name), ['Intro', 'Drop', 'Outro']);
  assert.ok(withSection(list, { name: 'Bad', at: 5, length: 4 }).problem);
  assert.deepEqual(changedSection(list, 0, { name: 'Start' }).sections[0].name, 'Start');
  assert.ok(changedSection(list, 0, { length: 20 }).problem);
  assert.deepEqual(withoutSection(list, 0).map((s) => s.name), ['Drop']);
});

test('a fresh name is not in use, and a bar finds its section', () => {
  assert.equal(freshName([]), 'Section 1');
  assert.equal(freshName([{ name: 'Section 2', at: 1, length: 1 }]), 'Section 3');
  assert.equal(sectionAt(list, 9), 1);
  assert.equal(sectionAt(list, 8), 0);
  assert.equal(sectionAt(list, 30), -1);
});
