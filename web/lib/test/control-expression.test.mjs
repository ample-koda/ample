import test from 'node:test';
import assert from 'node:assert/strict';
import { expressionToInt, looksLikeExpression, parseExpression } from '../control-expression.mjs';

const valueOfLevels = (...levels) => (i) => levels[i];

test('an expression over a control is worked out in fixed point like ControlExpression.cs', () => {
  const expression = parseExpression('600 + 3000 * traffic.level');
  assert.deepEqual(expression.references.map((r) => `${r.block}.${r.output}`), ['traffic.level']);
  assert.equal(expressionToInt(expression.evaluate(valueOfLevels(0))), 600);
  assert.equal(expressionToInt(expression.evaluate(valueOfLevels(500))), 2100);
  assert.equal(expressionToInt(expression.evaluate(valueOfLevels(1000))), 3600);
  assert.equal(expressionToInt(expression.evaluate(valueOfLevels(333))), 1599);   // 3000 * 0.333 = 999, plus 600
});

test('brackets, minus, decimals, units and block names with a hyphen', () => {
  assert.equal(expressionToInt(parseExpression('(1 + 2) * 3').evaluate(valueOfLevels())), 9);
  assert.equal(expressionToInt(parseExpression('-6 dB + 12 * wind-gust.gate').evaluate(valueOfLevels(500))), 0);
  assert.deepEqual(parseExpression('-6 dB + 12 * wind-gust.gate').units, ['dB']);
  assert.equal(parseExpression('1.5 * 3').evaluate(valueOfLevels()), 4500);
  assert.equal(parseExpression('0.0005 * 1').evaluate(valueOfLevels()), 0);   // only three decimals are kept
  assert.equal(parseExpression('2 * -a.level').evaluate(valueOfLevels(250)), -500);
});

test('rounding goes half away from zero, and a product is rounded to three decimals', () => {
  assert.equal(expressionToInt(1500), 2);
  assert.equal(expressionToInt(-1500), -2);
  assert.equal(expressionToInt(499), 0);
  assert.equal(parseExpression('0.001 * a.level').evaluate(valueOfLevels(500)), 1);    // 1 * 500 = 500 -> (500 + 500) / 1000
  assert.equal(parseExpression('0.001 * a.level').evaluate(valueOfLevels(499)), 0);
  assert.equal(parseExpression('-0.001 * a.level').evaluate(valueOfLevels(500)), -1);
});

test('text that is not an expression is refused, and looksLikeExpression tells automation functions apart', () => {
  assert.equal(parseExpression('traffic'), null);
  assert.equal(parseExpression('600 +'), null);
  assert.equal(parseExpression('(1 + 2'), null);
  assert.equal(parseExpression('600 Hz Hz'), null);
  assert.equal(looksLikeExpression('600 + 3000 * traffic.level'), true);
  assert.equal(looksLikeExpression('auto(1:0, 2:100)'), false);
  assert.equal(looksLikeExpression('600'), false);
  assert.equal(looksLikeExpression('traffic.level'), true);
  assert.equal(looksLikeExpression('fadein(2)'), false);
});
