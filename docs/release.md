# Release

Status (2026-10-03): chunk 98 is **partly done**. The parts that can be done and proven on the development machine are done; the rest needs a machine, a runner or a decision that was not there. This page says exactly which.

## Release criteria

| Version | Needs | State |
| --- | --- | --- |
| **v0.1** | milestones M1 to M3 done | done (chunks 00 to 60 and the engine) |
| **v1.0** | M4 and M5 done, parity complete, no open critical risk, security checklist signed off, Linux suite seen green, release artefacts verified | **not yet**: see "Open items" |

The spec version stays **0.1 (draft)** on purpose: it becomes 1.0 only with v1.0.

## What is built and measured

| Item | Result |
| --- | --- |
| Parity | 34 of 34 sketch features have an executable test (`node tools/parity-report.mjs --check --require-complete`, part of `check-all`) |
| Chaos | `Category=Chaos`: a crash injected at **every** mutating step of five kinds of command (file write, settings, dangerous act...) leaves either the old or the new file, a verifying black box and nothing unresolvable; clocks that jump back and forward 10 years; 3000 files through backup, verify and restore |
| Disk full and read-only media | the journal fails closed (AMP5230) and the command does not run (`BlackBoxTests`), a failing durable write leaves a truncated file that is never mistaken for a record |
| Performance gate | `Category=Performance` against `docs/perf-budgets.json` (below) |
| Packaging | `tools/package.ps1`: self-contained single-file zips for win-x64 and linux-x64 (with `web/`, `spec/`, `docs/` and the real plugin next to the program; the test plugins are left out), the dotnet tool (`Ample.Cli.0.1.0.nupkg`), the VS Code `.vsix`, `SHA256SUMS.txt`, optional signatures (`-Sign`, see [Security](security.md)) |
| Installer smoke test | `tools/smoke-package.ps1` unpacks the zip of this operating system into a clean folder outside the repository and checks `--version`, `new`, `check`, `build`, `plugins list` and that `serve` answers with the web UI and the help. **Passed on Windows 11 (2026-10-03).** |

### Performance baselines (measured 2026-10-03, development machine, Windows 11, not a CI runner)

| Measurement | Baseline | Budget |
| --- | --- | --- |
| parse a song of 2000 blocks | 33 ms | 500 ms |
| format it | 21 ms | 500 ms |
| 1000 black box appends (each one made durable) | 2359 ms | 25 000 ms |
| read and verify 1000 records | 26 ms | 500 ms |
| build 41 blocks (preview) | 1095 ms | 15 000 ms |
| the same build again, all from the cache | 161 ms | 3000 ms |
| list 1500 takes | 29 ms | 1000 ms |
| back up 64 MiB in 64 files | 591 ms | 10 000 ms |
| verify that backup | 650 ms | 10 000 ms |
| a second, unchanged backup | 285 ms | 4000 ms |

### Denoise baseline (measured 2026-10-17, development machine, Windows 11)

| Measurement | Before | After | Budget |
| --- | --- | --- | --- |
| auto denoise, 60 seconds of synthetic stereo audio at 44.1 kHz | 10 812 ms | 10 348 ms | 120 000 ms |

The optimized output is byte-identical to the previous renderer for both `noise=auto` and `noise=start`. The measured `auto` runtime improvement was about 4% on this run; this is a small gain, while flattening the stored spectra also removes thousands of per-frame array allocations. A known `noise=start` profile avoids analyzing every frame before the render pass.

The performance tests run **alone** (an xUnit collection that is not run in parallel): in the first full run, while other test classes used the same disk, the numbers were twenty times worse (23.7 s instead of 1.1 s for the build, 51 s instead of 2.4 s for the appends), which is why the gate must not share the machine with other tests. The budgets are about ten times the baseline (more for the disk-bound ones), so a slow runner passes and a real regression fails. Raise a budget only with a line here saying why. Not measured yet: the LSP under load, the web UI with a very large graph, a 10 GB take bank.

### Package sizes and times (2026-10-03)

| Artefact | Size | Time |
| --- | --- | --- |
| `ample-0.1.0-win-x64.zip` | 45 MB (the program is 105 MB, self-contained and not trimmed) | publish 24 s, zip 4 s |
| `ample-0.1.0-linux-x64.zip` | 44 MB | publish 7 s, zip 3 s |
| `Ample.Cli.0.1.0.nupkg` | 18 MB | 21 s |
| `ample-vscode-0.1.0.vsix` | 0.5 MB | 52 s (with `npm install`) |

## Open items (why this is not v1.0)

1. **The Linux test run has not been seen.** The CI matrix (`.github/workflows/ample.yml`) runs the fail-closed full suite on Ubuntu and Windows, including installed grammar and browser-parity dependencies, but this machine cannot run Linux and nobody has looked at a Linux result in this build-out. The Linux zip is built and not run.
2. **Golden audio hashes on Linux** are therefore unproven (the Windows hashes are tested; the integer DSP is designed to be platform independent).
3. **The VS Code extension is smoke-tested, but the `.vsix` is unsigned by a marketplace publisher.** On 2026-10-03, a fresh package was installed into an isolated VS Code 1.140.0 profile; activation, commands and an LSP completion passed. The completion used a local test server fixture; the AMPLE CLI stdio handshake passed separately.
4. **The security checklist is not signed off** ([Security review](security-review.md)).
5. **No licence file**: the owner has to choose.
6. **No clean-VM installer test**; the smoke test ran on the development machine in a clean folder, which is close but not the same.
7. Not measured: the LSP under load, a huge graph in the browser, a 10 GB take bank, a long soak run (the nightly workflow runs the chaos and performance tests; nobody has seen it yet).
8. The release workflow (`.github/workflows/release.yml`) has never run: it needs a tag, and signing needs the owner's key.

## How to make a release

1. `pwsh ample/tools/check-all.ps1 -Full` (or `bash ample/tools/check-all.sh --full`) is green on both systems. Full mode fails rather than silently omitting the VS Code grammar or browser-parity suites.
2. Raise `<Version>` in `ample/Directory.Build.props` and `ample/editors/vscode/package.json`; write the notes in CHANGELOG.
3. Re-run the audits of [Security review](security-review.md).
4. Tag `ample-v<version>`; the release workflow builds the artefacts on Windows, runs `tools/smoke-package.ps1`, and attaches the files and `SHA256SUMS.txt` to a **draft** release. Sign them with `pwsh ample/tools/package.ps1 -Sign` on the machine that has the signing key (`ample keys generate` once; the passphrase comes from `AMPLE_VAULT_PASSPHRASE`), and check with `ample verify --file <artefact>`.
5. The owner reads the draft, then publishes it.

### The sample pack zips (only when a pack is added or changed)

AMPLE Tune-up and `ample packs install` download the sample packs as zips that are **not** part of a release of the program. Steps: `node tools/fetch-audio-packs.mjs` (with `AMPLE_PACK_OUT` set to an empty working folder), then `node tools/build-sample-pack-zips.mjs <that folder> <zip folder>`, which writes one zip per pack and `packs-index.json` (SHA-256 and size of each zip) into `audio-packs/packs-index.json`, which the program embeds. Upload the zips to the release `audio-packs-1` of the public repository (`gh release create audio-packs-1 <zips> --repo ample-koda/ample`; a new set of zips gets a new tag and `SamplePacks.BaseUrl` follows) and copy them to get.ample-koda.dev/mirror/audio-packs/. The catalog in the program must be committed first: a zip whose hash is not in it never installs.

## Versions, channels and beta status (installer plan, INS00)

This section holds the rules a release follows.

### Version numbers

- **Program:** SemVer. `0.x` until the owner calls a release stable; that release is `1.0.0`. A pre-release suffix (`1.3.0-rc.1`) marks the `unstable` channel.
- **Language:** the `ample N` of a song and the spec version are separate numbers (see `docs/language/11-versioning-and-compat.md`). The release notes say which languages a program reads.
- **Renderer:** changing the SoundFont, a codec or the random generator is a minor or major change on purpose, and is named in the release notes.
- **Packs and data:** pinned by SHA-256; a release lists them in its manifest. No unpinned downloads.
- **Tags:** `ample-v<version>` in the public repository. A stable release is the unstable one that stayed good, re-tagged and rebuilt, never renamed.

### Channels

`stable` (everyone, by default) and `unstable` (opt-in). While everything is beta, `stable` means "published by the owner", not "finished".

### Beta status (the whole program, until the owner ends it)

AMPLE is beta: it works, and anything may still change, including the file formats, the sign-in (today one user and one password) and the server set-up. The install scripts print this sentence, and the notes of every release say it in the same words. The owner ends beta by announcing a release as no longer beta; the text is then removed everywhere in one change. Compatibility promise: we try to stay backwards compatible and say clearly when something will break; no guarantees and no fixed support window. Installers never switch on anything that opens AMPLE to a network or keeps it running without a plain "yes".

### Releases and keys

The product owner manages the releases and the relevant keys. A release is only ever published by the product owner.

The release files are signed with a release key. Its public part is built into AMPLE and published in `keys/`, so a downloaded release can be checked with
`ample verify --file <file> --release`, which accepts only those built-in keys and ignores any key written in the signature file.
