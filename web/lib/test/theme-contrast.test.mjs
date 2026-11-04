import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { contrastRatio, validateThemePairs } from '../theme-contrast.mjs';

const stylesheet = readFileSync(new URL('../../styles/tokens.css', import.meta.url), 'utf8');
const valuesOf = (body) => Object.fromEntries([...body.matchAll(/--([\w-]+):\s*([^;\s]+)/g)].map((match) => [`--${match[1]}`, match[2]]));
const color = (value) => value.replace(/^#([\da-f])([\da-f])([\da-f])$/i, '#$1$1$2$2$3$3');
const tokenBlock = (selector) => {
  const match = stylesheet.match(new RegExp(`${selector}\\s*\\{([^}]*)\\}`));
  assert.ok(match, `Missing theme token block: ${selector}`);
  return valuesOf(match[1]);
};
const base = tokenBlock(':root');
const light = tokenBlock(':root\\[data-theme="light"\\]');
const highContrast = tokenBlock(':root\\[data-theme="high-contrast"\\]');
const classic = tokenBlock(':root\\[data-theme="classic"\\]');
const autoDark = tokenBlock(':root\\[data-theme="auto"\\]');
const autoLightRule = stylesheet.match(/@media\s*\(prefers-color-scheme:\s*light\)\s*\{\s*:root\[data-theme="auto"\]\s*\{([^}]*)\}\s*\}/);
assert.ok(autoLightRule, 'Missing light auto-theme overrides');
const autoLight = { ...base, ...autoDark, ...valuesOf(autoLightRule[1]) };
const themes = [
  ['studio-dark', base],
  ['light', { ...base, ...light }],
  ['high-contrast', { ...base, ...highContrast }],
  ['classic', { ...base, ...classic }],
  ['auto dark', { ...base, ...autoDark }],
  ['auto light', autoLight],
];

test('every shipped theme text token contrasts against every app surface', () => {
  const foregrounds = ['--text', '--dim', '--hi'];
  const backgrounds = ['--bg', '--panel', '--panel2', '--panel3'];
  for (const [name, theme] of themes) {
    const pairs = foregrounds.flatMap((foreground) => backgrounds.map((background) => ({ foreground: color(theme[foreground]), background: color(theme[background]) })));
    assert.equal(validateThemePairs(pairs, 4.5), true, `${name} theme must meet WCAG AA`);
  }
});

test('contrast ratio rejects malformed colors', () => {
  assert.throws(() => contrastRatio('not-a-color', '#ffffff'), TypeError);
});
