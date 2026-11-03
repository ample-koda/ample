# 11 Versioning and compatibility

## Spec version
`../../spec/version.json` holds `specVersion` (`major.minor`) and `status` (`provisional` or `settled`). A song may state the language version it was written for on its `composition` line (`ample 1`); it is optional, and `ample migrate` moves an older song to the current version.

- **Patch-level** wording fixes do not change the version.
- **Minor** versions add things in a backward-compatible way (a new template, a new optional parameter, a new diagnostic code).
- **Major** versions break compatibility and come with a migration note in [CHANGELOG.md](CHANGELOG.md).
- **Before 1.0** a minor version may break; every break has a migration note and the tool offers `ample migrate` once real songs exist.

## Stability of the data
- Diagnostic and lint codes are never reused or renumbered; retired codes stay listed.
- Template ids are never reused; a template is `provisional`, `stable`, `deprecated` or `retired`, with `since` and optionally `deprecated` (the version and the replacement).
- The canonical plan form and the stable random generator are frozen once released and carry their own version (`rngVersion`); see [06](06-seeds-and-determinism.md).

## Compatibility promise (at 1.0)
A song that is valid at 1.x stays valid at every later 1.y and builds to the same take references; deprecated features keep working with a warning for at least one minor version.

## File format versions
`.amplet`, `.amplelrc`, `.ample` exports and `plugin.json` each carry a format version; opening an older format runs a migration with a backup copy first (project-management).
