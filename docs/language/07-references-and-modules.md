# 07 References and modules

## Names and references
- Block names are unique in a song (`AMP2002`) and are the only identifiers references use. A `from` line names `<block>.<output>`; both parts may be quoted when they contain spaces.
- Reference resolution is by **exact name**. A near miss gets a hint ("Did you mean ...?") and a quick fix, never an automatic correction.
- Apply (the editor's action) matches text blocks to the existing graph **by name and type**: the same name and type keeps its identity, its inside and its cache state; a new name creates a block; a name that is gone removes the block (source-and-views).

## Files
Blocks that point at files (for example `lyrics` with `file lyrics/chorus.lyr`) use **project-relative paths with `/` separators**; a path never escapes the project or a registered link. Resolution order: project, linked folders, the user library, installed packs. A missing file is a diagnostic with a "relocate" fix. (Built as a service, not yet as a static diagnostic; see the library manager. Names are written without the file extension; the order is the project folder (`<name>` or `assets/<name>`), the project's linked folders, the user's linked folders, installed packs; codes AMP5290, AMP5291, AMP5294.)

## Plugins, packs, locks
- Plugins and packs are referenced by name and version in the project and pinned by hash in `ample.lock`, which the tool writes and people do not edit.
- A template that needs a plugin that is not installed is `AMP5001` (a warning).

## Modules (spec 0.1)
`features { ... }` switches opt-in modules on or off: `quality`, `credits`, `signing`, `secrets`, `lyrics`, `git-mirror`; `assist` sets how much help the editor gives (`off diff button auto`). Reusable block modules are defined below (added in chunk 94).

## Not yet specified
`include` of other `.amplet` files, imports of audio, MIDI or lyrics files, and the statement style of the older examples. They are tracked in open-questions.

## Reusable modules (use and module files)
A **module** is a file `modules/<name>.module` in the project (or in a linked folder or pack, found the same way as other assets). Its first line says what it is, then it declares its knobs and doors, then it holds ordinary blocks:

```
module groove version 1.2
param kit kit/tape        # a parameter with its default
input chords              # a door the song connects
output audio mix.mix      # what the song can read: <block>.<port> inside the module

bass bass {
  from in.chords -> notes # "in.<input>" reads the module's input
}
drums drums {
  kit $kit                # $name stands for the parameter
}
mixer mix {
  from drums.audio -> audio1
  from bass.audio -> audio2
}
```

The song uses it with `use groove@1.0 as intro { kit kit/808  from chords.notes -> chords }` and reads `from intro.audio -> mix`.

- **Expansion.** Before a song is built each `use` becomes plain blocks named `<instance>/<block>` (nested instances: `outer/inner/block`). The tool expands in memory; the song file keeps the short `use`. `ample modules expand song.amplet` prints the result.
- **Seeds.** A block's seed is derived from its name, so the seeds of an instance depend on the instance path: the same module used twice gives two different sets of seeds, each stable, and renaming an instance changes its seeds (like renaming a block).
- **Parameters.** The use may set any declared parameter; the others keep their default. `$name` is replaced in parameter values only.
- **Versions.** `@1.0` asks for the same major version and a minor version that is not older; no `@` takes any. The versions and hashes that were used are pinned in `ample.lock` (a `modules` section); a changed module is AMP5328 until `ample lock update`.
- **Not in a module:** placement (`at ... for ...`) works as written but cannot be moved by the use, and a module cannot read a block of the song except through its inputs.
- **Diagnostics** (runtime): AMP5320 to AMP5328 are reported at the `use` line. The editor (parser) does not load modules; it accepts the shape of `use` and references to an instance.
