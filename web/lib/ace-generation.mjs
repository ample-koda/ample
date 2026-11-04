/**
 * Model family of the ACE synth model: 'sft' | 'base' | 'turbo' | 'unknown'.
 * One rule shared with the compiler (Parser.cs TurboGuidance): the file name wins (family followed by - . _ or the end);
 * without a file the synthModel setting counts (xl- prefix ignored); anything else is unknown, and unknown is free (never fixed).
 */
export function aceFamily(settings) {
  const file = String(settings['acestep.synthModelFile'] || '').replaceAll('"', '');
  if (file) return /(?:^|[-._])(sft|base|turbo)(?:[-._]|$)/i.exec(file)?.[1].toLowerCase() || 'unknown';
  const selected = String(settings['acestep.synthModel'] || '').replaceAll('"', '').replace(/^xl-/i, '').toLowerCase();
  return ['sft', 'base', 'turbo'].includes(selected) ? selected : 'unknown';
}

/** Resolved ACE baseline shown by Studio; explicit per-block values override it. */
export function aceDefaults(settings) {
  const family = aceFamily(settings);
  const guided = family === 'sft' || family === 'base';
  // Only a known turbo model is distilled for CFG 1 (the plugin refuses another value): fixed. Unknown shows the plugin's turbo-like defaults but stays editable.
  return { guidance: guided ? 7 : 1, steps: guided ? 50 : 8, shift: family === 'sft' ? 1 : 3, solver: 'euler', fixed: family === 'turbo' ? ['guidance'] : [], family };
}

/** The settings with the model a block names in its engine parameter (route:model): it wins over the song, a family replaces a model file of the song (the same rule as the build and the compiler). */
export function withEngineModel(settings, engine) {
  const text = String(engine ?? '').trim().replace(/^"|"$/g, '').trim();
  const colon = text.indexOf(':');
  const model = colon < 0 ? '' : text.slice(colon + 1).trim();
  if (!model) return settings;
  const next = { ...settings };
  if (/\.gguf$/i.test(model)) next['acestep.synthModelFile'] = model;
  else { next['acestep.synthModel'] = model; delete next['acestep.synthModelFile']; }
  return next;
}
