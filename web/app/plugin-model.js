// Plugin settings: defaults and value checks from the JSON Schema of the manifest. Pure, no DOM. @ample:plugins-ui

export const defaultsOf = (schema) => Object.fromEntries(Object.entries(schema.properties || {}).filter(([, r]) => 'default' in r).map(([k, r]) => [k, r.default]));

export function checkValue(name, v, rule) {
  if (rule.type === 'integer' && !Number.isInteger(v)) return `${name} must be a whole number`;
  if ((rule.type === 'number' || rule.type === 'integer') && typeof v !== 'number') return `${name} must be a number`;
  if (typeof v === 'number' && rule.minimum !== undefined && v < rule.minimum) return `${name} must be at least ${rule.minimum}`;
  if (typeof v === 'number' && rule.maximum !== undefined && v > rule.maximum) return `${name} must be at most ${rule.maximum}`;
  if (rule.type === 'string' && typeof v !== 'string') return `${name} must be text`;
  if (rule.enum && !rule.enum.includes(v)) return `${name} must be one of ${rule.enum.join(', ')}`;
  return null;
}
