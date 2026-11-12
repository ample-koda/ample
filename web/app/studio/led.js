import { el } from '../ui.js';

export function led(options) {
  const button = el('button', { type: 'button', role: 'switch', className: `studio-led${options.value ? ' on' : ''}`, 'aria-checked': String(Boolean(options.value)), 'aria-label': options.label, title: options.title || options.label, disabled: Boolean(options.disabled) });
  button.style.setProperty('--led-accent', options.accent || 'var(--fx-dyn)');
  button.onclick = () => { const next = button.getAttribute('aria-checked') !== 'true'; button.setAttribute('aria-checked', String(next)); button.classList.toggle('on', next); options.onCommit(next); };
  return button;
}
