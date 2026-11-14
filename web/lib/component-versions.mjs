// Which version of a component a block gets for the version request in its header. The rules are the build's: 1.2 is the highest 1.2.x, ^1.2 is the highest
// version from 1.2.0 below 2.0.0 (below 0.3.0 for ^0.2), ~1.2 the highest from 1.2.0 below 1.3.0, a leading v is ignored, and no request is the default.
// Pure, so node --test checks it.

const parts = (text) => { const m = /^(\d+)(?:\.(\d+))?(?:\.(\d+))?/.exec(String(text)); return m ? [Number(m[1]), Number(m[2] ?? 0), Number(m[3] ?? 0), m[2] !== undefined, m[3] !== undefined] : null; };
const compare = (a, b) => b.localeCompare(a, 'en', { numeric: true });

/**
 * @param {Array<{version: string}>} versions the versions the component has
 * @param {string} request the version text of the block header ("", "1.2", "^1.2", "~1.2.3", "v2")
 * @param {string} [defaultVersion] the current version, for no request
 */
/**
 * The template of a block: a component instance shows the ports, properties and layout of the version it resolves to, not the ones of the default
 * version (issue 111). A version carries them as `view`; a template without versions, or a version without a view, stays as it is.
 * @param {object} template the template of the family (the default version)
 * @param {string} request the version text of the block header
 */
export function templateForVersion(template, request) {
  if (!template || !template.componentVersions?.length) return template;
  const picked = pickVersion(template.componentVersions, request, template.version);
  if (!picked?.view) return template;
  const cached = viewCache.get(picked);
  if (cached && cached.base === template) return cached.result;
  const result = { ...template, ...picked.view, version: picked.version };
  viewCache.set(picked, { base: template, result });
  return result;
}

const viewCache = new WeakMap();

export function pickVersion(versions, request, defaultVersion) {
  const list = versions ?? [];
  const raw = String(request ?? '').replace(/"/g, '').trim();
  const kind = raw.startsWith('^') ? 'caret' : raw.startsWith('~') ? 'tilde' : 'plain';
  const text = raw.replace(/^[v^~]+/, '');
  if (!text) return list.find((v) => v.version === defaultVersion) || list[0] || null;
  const want = parts(text);
  const fits = (v) => {
    if (v.version === text) return true;
    const have = parts(v.version);
    if (!want || !have) return false;
    if (kind === 'plain') return v.version.startsWith(`${text}.`);
    const atLeast = have[0] > want[0] || (have[0] === want[0] && (have[1] > want[1] || (have[1] === want[1] && have[2] >= want[2])));
    if (!atLeast) return false;
    if (kind === 'tilde') return have[0] === want[0] && have[1] === want[1];
    if (want[0] > 0) return have[0] === want[0];
    return have[0] === 0 && (want[1] > 0 ? have[1] === want[1] : have[1] === 0 && have[2] === want[2]);
  };
  return list.filter(fits).sort((x, y) => compare(x.version, y.version))[0] || null;
}
