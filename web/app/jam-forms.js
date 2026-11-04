// A small settings card inside the AMPLE Jam window (arpeggiate, riff machine, chord stamp): labelled fields, Enter applies, Escape closes, no browser dialog. @ample:pianoroll-lite
import { el } from './ui.js';

/**
 * Opens one card over the roll. fields: { key, label, type: 'number' | 'select' | 'check', value, min, max, step, options: [{ value, label }] }.
 * onApply(values) is called with the field values by key; the card closes unless it returns false. Returns the card (call .close() to remove it).
 */
export function openJamForm(host, { title, hint, fields, apply = 'Apply', onApply, onClose, secondary }) {
  host.querySelector(':scope > .mls-form')?.remove();
  const inputs = new Map();
  const rows = fields.map((f) => {
    let input;
    if (f.type === 'select') input = el('select', { className: 'mls-form-input', 'aria-label': f.label }, f.options.map((o) => el('option', { value: String(o.value), textContent: o.label })));
    else if (f.type === 'check') input = el('input', { type: 'checkbox', className: 'mls-form-check', 'aria-label': f.label });
    else input = el('input', { type: 'number', className: 'mls-form-input', 'aria-label': f.label, ...(f.min !== undefined ? { min: String(f.min) } : {}), ...(f.max !== undefined ? { max: String(f.max) } : {}), ...(f.step !== undefined ? { step: String(f.step) } : {}), ...(f.placeholder ? { placeholder: f.placeholder } : {}) });
    if (f.type === 'check') input.checked = !!f.value; else if (f.value !== undefined && f.value !== null) input.value = String(f.value);
    inputs.set(f.key, { input, def: f });
    return el('label', { className: 'mls-form-row' }, [el('span', { className: 'mls-form-label', textContent: f.label }), input]);
  });
  const values = () => Object.fromEntries([...inputs].map(([key, { input, def }]) => [key, def.type === 'check' ? input.checked : def.type === 'select' ? input.value : input.value === '' ? null : Number(input.value)]));
  const close = () => { card.remove(); onClose?.(); };
  const run = () => { if (onApply(values()) !== false) close(); };
  const runSecondary = async () => { if ((await secondary.run(values())) !== false) close(); };
  const card = el('div', { className: 'mls-form', role: 'dialog', 'aria-label': title }, [
    el('div', { className: 'mls-form-title', textContent: title }),
    ...(hint ? [el('div', { className: 'mls-form-hint', textContent: hint })] : []),
    ...rows,
    el('div', { className: 'mls-form-actions' }, [el('button', { type: 'button', className: 'mls-form-cancel', textContent: 'Cancel', onclick: close }), ...(secondary ? [el('button', { type: 'button', className: 'mls-form-cancel', textContent: secondary.label, title: secondary.title ?? '', onclick: runSecondary })] : []), el('button', { type: 'button', className: 'mls-form-apply', textContent: apply, onclick: run })]),
  ]);
  card.addEventListener('keydown', (event) => { event.stopPropagation(); if (event.key === 'Escape') { event.preventDefault(); close(); } else if (event.key === 'Enter' && event.target.tagName !== 'SELECT') { event.preventDefault(); run(); } });
  card.close = close;
  host.append(card);
  (card.querySelector('input,select') ?? card).focus();
  return card;
}
