import assert from 'node:assert/strict';
import { test } from 'node:test';
import { reportEmail, reportLink, setReportEmail } from '../lite-contact.mjs';

test('an instance without an address has no complaints link', () => {
  setReportEmail(undefined);
  assert.equal(reportEmail(), '');
  assert.equal(reportLink('abc', 'https://remix.example.test'), null);
  setReportEmail('   ');
  assert.equal(reportLink('abc', 'https://remix.example.test'), null);
});

const ADDRESS = ['complaints', 'remix.example.test'].join(String.fromCharCode(64));

test('the complaints link names the share and carries its link', () => {
  setReportEmail(' ' + ADDRESS + ' ');
  assert.equal(reportEmail(), ADDRESS);
  const link = reportLink('abc', 'https://remix.example.test');
  assert.ok(link.startsWith('mailto:' + ADDRESS + '?subject=AMPLE%20Remix%20share%20abc&body='));
  assert.ok(decodeURIComponent(link).includes('https://remix.example.test/mix/abc'));
});
