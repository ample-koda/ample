---
title: Versions, pins and seals
order: 30
lead: How a song keeps using exactly the component it was made with.
---
## Asking for a version

An instance may request a version of its component with `pinned@` in the header:

- `pinned@1.8` the 1.8 line, `pinned@1.8.0` exactly that one;
- `pinned@^1.2` anything compatible, `pinned@~1.2` patch-level updates only;
- `pinned@4.0.0-beta.1` a pre-release, by name.

Without a request the instance follows the **current** version: the one marked `current`, otherwise the highest release. A pre-release is never chosen unless it is the only one or you ask for it by name. Several versions of one component can live in the same song.

## Pins

The lock file of a project (`ample.lock`) records every component a song uses as `id@version` with a fingerprint of its code, called the **semantic hash**. The first use pins it. If the code differs later, the build reports it and asks you to accept the change, which pins the new fingerprint. The fingerprint covers what changes the sound (the contract, the process, the helpers it uses) and not the name, the description or the documentation, so rewording never forces a rebuild.

## Seals

A **seal** locks a version: it is written by the engine, never by hand, and a sealed version is immutable. If the component changes afterwards, the seal no longer matches and the component counts as unsealed again; the way forward is a new version. An **unsealed** component is not an error. It builds, it just does not count as trusted (you will see an information message saying so). A seal can also carry the signature of the publisher.

## Deprecation and migration

A component can say it is deprecated and name a replacement. A deprecated version warns and never stops a build. When a new version changes the properties or ports, the component can include a small migration, for example renaming or dropping a property. The migration says how big the change is (`none`, `equivalent`, `musicalChange` or `destructive`), and one that changes the sound is never applied without your acknowledgement. The preview shows the changed properties, the resulting notes and the new fingerprint first.

{{ample The general rules for language versions. | /learn/ample/language/versions=Versions}}
