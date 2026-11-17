// Review of #720: the precedence of the layers that decide whether assertions are on in the browser.
// The browser evaluates the helpers and expressions of the song itself, never the process of a component, and the instance layer
// (the diagnostics options of a block) exists only on component instances (AMP6032 elsewhere), so no browser evaluation runs under it today:
// the layers that count there are the defaults, the song and the session. The directives parameter is kept so the same resolver serves a caller that has them.
import test from 'node:test';
import assert from 'node:assert/strict';
import { assertionsOf } from '../observation-config.mjs';

const on = { name: 'diagnostics.assertions', value: 'on', line: 6 };

test('defaults, then the song, then the session decide in that order', () => {
  assert.equal(assertionsOf(''), false);
  assert.equal(assertionsOf('settings { diagnostics.assertions on }'), true);
  assert.equal(assertionsOf('settings { diagnostics.assertions on }', { session: false }), false);
  assert.equal(assertionsOf('settings { diagnostics.assertions off }', { session: true }), true);
  assert.equal(assertionsOf('', { session: null }), false);   // null: as the song says
});

test('an instance layer, when a caller has one, wins over the song and loses to the session; inherit skips a layer', () => {
  assert.equal(assertionsOf('settings { diagnostics.assertions off }', { directives: [on] }), true);
  assert.equal(assertionsOf('', { directives: [on], session: false }), false);
  assert.equal(assertionsOf('settings { diagnostics.assertions on }', { directives: [{ ...on, value: 'inherit' }] }), true);
  assert.equal(assertionsOf('', { directives: [{ name: 'logging.level', value: 'trace', line: 1 }] }), false);   // another option is not this one
});

test('a value that is not on or off never switches assertions on by accident', () => {
  assert.equal(assertionsOf('settings { diagnostics.assertions maybe }'), false);
  assert.equal(assertionsOf('settings { diagnostics.assertions "on" }'), true);
});
