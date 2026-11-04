// The "Random" button next to a seed field: fills it with a random whole number and commits it like a typed value.
import { el } from './ui.js';
import { randomSeed } from '../lib/random-seed.mjs';

/** getInput returns the text input of the seed field (looked up when clicked, so a re-rendered field still works). */
export function seedDice(getInput, { disabled = false } = {}) {
  return el('button', { type: 'button', className: 'seed-dice', textContent: '🎲 Random', title: 'Fill in a random seed', 'aria-label': 'Random seed', disabled, onclick: () => {
    const input = getInput();
    if (!input) return;
    input.value = randomSeed();
    input.dispatchEvent(new Event('change', { bubbles: true }));
    input.focus();
  } });
}
