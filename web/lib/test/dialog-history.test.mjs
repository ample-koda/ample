import test from 'node:test';
import assert from 'node:assert/strict';
import { backAction, createDialogHistory } from '../dialog-history.mjs';

test('a dialog gets one history entry, closing it by hand gives it back', () => {
  const h = createDialogHistory();
  const a = {};
  assert.equal(h.opened(a), true);
  assert.equal(h.opened(a), false);
  assert.equal(h.depth, 1);
  assert.equal(h.closed(a), true);   // the caller calls history.back()
  assert.equal(h.popstate(), null);  // ... and that popstate is ours: ignored
  assert.equal(h.closed(a), false);
});

test('a real Back press closes the top dialog and the later close does not go back again', () => {
  const h = createDialogHistory();
  const a = {}, b = {};
  h.opened(a); h.opened(b);
  assert.equal(h.popstate(), b);
  assert.equal(h.closed(b), false);  // already removed by the popstate
  assert.equal(h.popstate(), a);
  assert.equal(h.popstate(), null);
});

test('backAction cancels like Escape, a refused cancel keeps the dialog open', () => {
  const make = (refuse) => {
    const d = new EventTarget();
    d.open = true;
    d.close = () => { d.open = false; };
    d.addEventListener('cancel', (e) => { if (refuse) e.preventDefault(); });
    return d;
  };
  const free = make(false), stuck = make(true);
  assert.equal(backAction(free), true);
  assert.equal(backAction(stuck), false);
  assert.equal(stuck.open, true);
});
