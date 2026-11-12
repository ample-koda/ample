import test from 'node:test';
import assert from 'node:assert/strict';
import { latencyReport } from '../chain-latency.mjs';

test('the figure is the sum of the context and the look-ahead of the blocks', () => {
  const report = latencyReport({ base: 0.01, output: 0.02, blocks: [{ id: 'lim', title: 'Limiter', seconds: 0.005 }, { id: 'eq', title: 'EQ', seconds: 0 }] });
  assert.ok(Math.abs(report.totalMs - 35) < 1e-9);
  assert.equal(report.blocksMs, 5);
  assert.equal(report.label, 'Latency 35 ms');
  assert.deepEqual(report.parts.map((part) => part.name), ['audio processing', 'output device', 'Limiter']);
  assert.match(report.tooltip, /Limiter 5\.0 ms/);
});

test('a browser that does not report the output latency says so instead of showing 0 as a fact', () => {
  const report = latencyReport({ base: 0.005, output: 0 });
  assert.match(report.tooltip, /output device not reported by this browser/);
  assert.equal(report.label, 'Latency 5.0 ms');
});

test('nothing at all gives zero without NaN', () => {
  const report = latencyReport();
  assert.equal(report.totalMs, 0);
  assert.equal(report.label, 'Latency 0 ms');
});
