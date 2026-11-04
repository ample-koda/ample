/** The word being typed at the end of `before`. In a settings line a name has a dot (acestep.backend), a value may be a quoted string or a path (C:\x\y, a/b): the range then starts after the opening quote or covers the whole path. Elsewhere a word is [\w@-]. */
export function wordRangeBefore(before) {
  if (!/^\s*settings\b/.test(before)) return /[\w@-]*$/.exec(before)[0];
  const quotes = (before.match(/"/g) || []).length;
  if (quotes % 2 === 1) return before.slice(before.lastIndexOf('"') + 1);
  const path = /[^\s"'{},;=]*$/.exec(before)[0];
  if (/[\\/]/.test(path) || /^[A-Za-z]:/.test(path)) return path;
  return /[\w@.-]*$/.exec(before)[0];
}
