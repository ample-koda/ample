// The logging and assertion options of an evaluation, resolved from layers (plan/diagnostics/PLAN.md, D3). The twin of src/Ample.Language/ObservationConfig.cs: both run
// spec-tests/config-layers.json and must say the same value and the same layer for every option. `inherit` (or no value) takes the value of the layer below; `off` is a value.
// @ample:observation-config

export const LAYERS = ['server', 'project', 'song', 'instance', 'session'];

export const DEFAULTS = {
  'logging.level': 'warning',
  'logging.stack': 'errors',
  'logging.stackMaxDepth': '24',
  'logging.maxPerSite': '3',
  'logging.maxTotal': '200',
  'logging.traceMaxPerSite': '100',
  'logging.traceMaxTotal': '5000',
  'logging.exactLast': 'off',
  'logging.timeBudget': '2000',
  'logging.fuel': '200000',
  'logging.maxContextBytes': '4096',
  'diagnostics.assertions': 'off',
};

const CAPS = {
  'logging.stackMaxDepth': 64,
  'logging.maxPerSite': 1000,
  'logging.maxTotal': 10000,
  'logging.traceMaxPerSite': 10000,
  'logging.traceMaxTotal': 100000,
  'logging.timeBudget': 2000,
  'logging.fuel': 200000,
  'logging.maxContextBytes': 4096,
};

export const isOption = (key) => Object.hasOwn(DEFAULTS, key);

/** What is wrong with one value, or null. */
export function validate(key, value) {
  const v = String(value).trim().replace(/^"|"$/g, '');
  if (v === 'inherit') return null;
  switch (key) {
    case 'logging.level': return ['off', 'warning', 'info', 'debug', 'trace'].includes(v) ? null : `${key} is off, warning, info, debug, trace or inherit, not "${v}".`;
    case 'logging.stack': return ['off', 'errors', 'warnings', 'all'].includes(v) ? null : `${key} is off, errors, warnings, all or inherit, not "${v}".`;
    case 'diagnostics.assertions':
    case 'logging.exactLast': return ['on', 'off'].includes(v) ? null : `${key} is on, off or inherit, not "${v}".`;
    default: {
      if (!Object.hasOwn(CAPS, key)) return `${key} is not a logging or assertion option.`;
      if (!/^\d+$/.test(v) || Number(v) < 1) return `${key} is a whole number of at least 1, not "${v}".`;
      return Number(v) > CAPS[key] ? `${key} is at most ${CAPS[key]} (this is ${v}).` : null;
    }
  }
}

/**
 * Resolves the layers, lowest precedence first: [{ layer, origin, values: { key: [value, line] } }].
 * Returns { values: { key: { value, layer, origin, line } }, problems: [text] }.
 */
export function resolve(layers) {
  const values = Object.fromEntries(Object.entries(DEFAULTS).map(([k, v]) => [k, { value: v, layer: 'server', origin: null, line: 0 }]));
  const problems = [];
  for (const input of layers) {
    for (const key of Object.keys(input.values ?? {}).sort()) {
      if (!isOption(key)) continue;
      const [raw, line = 0] = input.values[key];
      const value = String(raw).trim().replace(/^"|"$/g, '');
      if (value === 'inherit') continue;
      const problem = validate(key, value);
      if (problem) { problems.push(`${problem} (${input.layer}${line > 0 ? `, line ${line}` : ''})`); continue; }
      values[key] = { value, layer: input.layer, origin: input.origin ?? null, line };
    }
  }
  return { values, problems };
}

/**
 * Whether assertions are on for an evaluation in the browser: the song layer (settings { diagnostics.assertions on } in the text), the instance layer
 * (the directive lines of the block that is evaluated, as { name, value, line }), and the session layer (the toggle of the editor: true, false or null)
 * are put through the same resolver as the server uses. An enabled assertion is never silently ignored: this is the one place that decides it.
 */
export function assertionsOf(songText, { directives = [], session = null } = {}) {
  const layers = [];
  const song = {};
  const text = String(songText ?? '');
  for (const block of text.matchAll(/(?:^|\n)\s*settings\s*\{([^}]*)\}/g)) {
    const line = text.slice(0, block.index).split('\n').length;
    for (const pair of block[1].matchAll(/(?:^|\s)(diagnostics\.assertions)\s+("?)([A-Za-z]+)\2/g)) song[pair[1]] = [pair[3], line];
  }
  if (Object.keys(song).length) layers.push({ layer: 'song', origin: 'song', values: song });
  const instance = {};
  for (const d of directives) if (d?.name === 'diagnostics.assertions') instance[d.name] = [String(d.value), d.line ?? 0];
  if (Object.keys(instance).length) layers.push({ layer: 'instance', origin: 'instance', values: instance });
  if (session === true || session === false) layers.push({ layer: 'session', origin: 'editor', values: { 'diagnostics.assertions': [session ? 'on' : 'off', 0] } });
  return resolve(layers).values['diagnostics.assertions'].value === 'on';
}

/** Why an option has its value. */
export function explain(config, key) {
  const v = config.values[key];
  return `${key} is ${v.value}: set by the ${v.layer}${v.origin ? ` ${v.origin}` : ''}${v.line > 0 ? ` (line ${v.line})` : ''}`;
}
