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
