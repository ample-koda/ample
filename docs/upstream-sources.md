# Where things came from

What AMPLE implements from specifications, learned from other projects, fetches from other places, or builds on, so that it can be checked again later: **has anything upstream changed that we should take over?** First version: **2026-11-04**. Every row says what we used, which version we followed ("not recorded" when we did not write it down at the time: then the first check below has to find out and write it in), the files that hold it, and what to watch.

## How to check for updates

1. Go through the tables below, one row at a time. For each row open the *Watch for* column and compare it with the upstream project or specification as it is today.
2. Compare the version in the row with the current one. If they differ, read the upstream change log and decide: take it over, note why not, or ignore it.
3. Write the date and the version you checked in the row (the *Checked* column), also when nothing changed. A row without a recent date has not been looked at.
4. When a row is taken over, run the tests of the files named in the row, and say in the commit what was taken from which upstream version.
5. A licence changing upstream matters more than a feature: read the licence text again for every row whose licence is not CC0.

## Specifications we implement

| What | Version we followed | Files | Watch for | Checked |
|---|---|---|---|---|
| **SoundFont 2 (SF2)** (E-mu / Creative): the file layout, generators, the default and custom modulators, the 24-bit samples (`sm24`) | SoundFont 2.04 (named in the code comments: modulators in section 8.2, NRPN in 9.6.3, `sm24` in 2.04) | `src/Ample.Audio/Instruments/Sf2Reader.cs`, `Sf2Program.cs`, `Sf2Modulators.cs`, `Sf2VolEnvelope.cs`, `Sf2ModEnvelope.cs`, `Sf2Lfo.cs`, `Sf2Subset.cs`, `SoundFontAsset.cs`; the renderer in `src/Ample.Audio/Engine/SamplerRenderers.cs` | A newer revision of the specification (unlikely); where the specification leaves a choice open we followed FluidSynth (see below), so its release notes are the better signal. | 2026-11-04 (first list) |
| **SF3**: an SF2 whose samples are Ogg Vorbis streams (flag `0x10` in the sample type), as MuseScore writes it | no official specification; read the way FluidSynth and MuseScore read it (the loop points relative to the start of the decoded sample); version of the Vorbis decoder: see *Libraries* | `src/Ample.Audio/Instruments/Sf2Reader.cs` (`Compressed`), `src/Ample.Audio/IO/OggVorbis.cs` | Changes in how MuseScore or FluidSynth write or read SF3; the version of NVorbis. SF3 decoding uses floating point, so it is only allowed with `engine float`. | 2026-11-04 |
| **SFZ** (sfzformat.com), a subset of the opcodes for sampled instruments | the format has no versioned specification; the subset is what the CC0 packs below need | `src/Ample.Audio/Instruments/SfzReader.cs`, `SfzModel.cs`, `src/Ample.Audio/Engine/Sfz/SfzReader.cs`, `SfzDocument.cs` | New opcodes in the sets we fetch (a new pack revision); the opcode lists of sfzformat.com. | 2026-11-04 |
| **Standard MIDI Files** (format 0 and 1) and **General MIDI** (programs, the drum map) | MIDI 1.0 files, General MIDI level 1 | `src/Ample.Audio/Midi/SmfReader.cs`, `SmfWriter.cs`, `SmfFile.cs`, `src/Ample.Audio/Engine/Sfz/GmDrumMap.cs` | Nothing expected; General MIDI 2 is not implemented. | 2026-11-04 |
| **MusicXML** (score import; `.mxl` is unzipped by AMPLE itself) | not recorded | `src/Ample.Audio/Midi/ScoreImport.cs`, `docs/score-import.md` | MusicXML 4.x changes; a score that stopped importing after an update of MuseScore or Audiveris (both run as separate programs, see *Programs we call*). | 2026-11-04 |
| **Loudness**: EBU R128 with the ITU-R BS.1770-4 K-weighting, gates at -70 LUFS and -10 LU, the loudness range after EBU Tech 3342 | BS.1770-4 | `src/Ample.Audio/Engine/Loudness.cs`, `src/Ample.Plugins/Analysis/Analyzers.cs`, `src/Ample.Storage/Quality/AudioAnalysis.cs`, `docs/analysis.md` | A newer BS.1770 revision (the 4 is the current one at the time of writing; check). | 2026-11-04 |
| **YIN** pitch tracking (de Cheveigne and Kawahara, 2002): difference function, cumulative mean normalisation, absolute threshold 0.15, parabolic interpolation | the 2002 paper; plain YIN, not pYIN | `src/Ample.Audio/Engine/AudioAnalyzer.cs`, `src/Ample.Audio/Engine/ExtraAutotune.cs`, `src/Ample.Plugins/Analysis/Analyzers.cs`, `src/Ample.Server/HumTranscriber.cs`, `docs/analysis.md` | Only if we replace it: `docs/analysis.md` says pYIN is the planned step for real voices. | 2026-11-04 |
| **Key profiles of Krumhansl and Kessler** for the key analysis | the classic profiles | `src/Ample.Audio/Engine/AudioAnalyzer.cs`, `src/Ample.Audio/Engine/ExtraAutotune.cs`, `src/Ample.Plugins/Analysis/Analyzers.cs` | Nothing expected. | 2026-11-04 |
| **SipHash-2-4** (Aumasson and Bernstein), **SplitMix64** and **xoshiro256**\*\* for the stable random numbers and the seed derivation | the published algorithms; the reference vectors are in `spec-tests/prng/vectors.json` and run in C# and in JavaScript | `src/Ample.Audio/Random/`, `web/lib/stable-rng.mjs` | **Never change these**: a song must build the same sound forever. Only the reference vectors count. | 2026-11-04 |
| **OpenAPI 3.0** for the description of the server's routes | 3.0 | `docs/api/openapi.json` (written by `ample serve --openapi` from the routes) | OpenAPI 3.1 (not used). | 2026-11-04 |
| **Language Server Protocol** | not recorded | `src/Ample.Lsp/` | The protocol version the editors speak; find it in `src/Ample.Lsp/LspServer.cs` and write it here. | 2026-11-04 |
| **TextMate grammar format** for the editor colours: the grammar is generated from `spec/` and names the schema of `martinring/tmlanguage` as its `$schema` | the schema at `https://raw.githubusercontent.com/martinring/tmlanguage/master/tmlanguage.json` (not pinned) | `tools/gen-editor-assets.mjs`, `editors/vscode/` | A change of that schema, or of how VS Code reads grammars. | 2026-11-04 |

## Code and ideas from other projects

| What | What we took | Version at the time | Files | Licence | Watch for | Checked |
|---|---|---|---|---|---|---|
| **MonkSynth** by Jonathan Taylor | Read as a reference on 2026-10-25 for the FOF principle (formant wave functions, Rodet/IRCAM), the vowel table and the way voices are laid out; the Delay Llama is written new, in AMPLE's own integer arithmetic. | the repository as it was on 2026-10-25 (commit not recorded) | `src/Ample.Audio/Engine/DelayLlama.cs`, `web/lib/clientside-delay-llama.mjs` | MIT (the notice is in `docs/third-party-credits.md`) | New vowel data, fixes in the FOF maths, a licence change. Any change we take must be made in both files (the C# engine and its browser twin) and keep the twin identical number for number. | 2026-11-04 |
| **Delay Lama** by AudioNerdz (2002) | The name and the idea only. Nothing of it is used. | - | the name of the Delay Llama | inspiration only | Nothing. | 2026-11-04 |
| **FluidSynth** (the player the MuseScore soundfonts are made with) | Behaviour, not code: where SF2 leaves a choice open we match it: the attenuation generator counts at 0.4 of its value; a resonant filter is turned down by 1 / sqrt(Q); the default modulators (velocity, pressure and CC 1 to vibrato, CC 7 and 11 as concave 960 cB curves, the reverb and chorus sends, CC 10 pan, the pitch wheel); the table of the size of one NRPN step per generator; a negative linear source as 1 - raw / 128; the sign of the modulation LFO to volume. | not recorded (the release current in October 2026) | `src/Ample.Audio/Instruments/Sf2Modulators.cs`, `Sf2Program.cs`, `Sf2VolEnvelope.cs`, `src/Ample.Audio/Engine/SamplerRenderers.cs` | LGPL-2.1 (nothing of the code is used; this is behaviour matched from its documented behaviour and the public SF2 text) | Release notes of FluidSynth about modulators, NRPN, the filter and envelope shapes. A change there is a reason to listen to a few presets again, not to change the engine: the engine is versioned (the renderer `Version`), and a change makes new takes. | 2026-11-04 |

## What we fetch (not part of this repository)

The audio itself is not in the repository; scripts fetch it and write a `LICENSE.txt` and a `SOURCE.md` next to it. A file is only used when its own licence text was read.

| What | From | Licence | Used for | Version at the time | Watch for | Checked |
|---|---|---|---|---|---|---|
| **MuseScore General** SoundFont (S. Christian Collins, adapted from FluidR3 and FluidR3Mono by Frank Wen and Michael Cowgill; Temple Blocks by Ethan Winer; Drumline Cymbals by Michael Schorsch) | `https://ftp.osuosl.org/pub/musescore/soundfont/MuseScore_General/` (the `.sf2`, the licence file and the sample sources list) | MIT, with the separately credited parts (the licence file and `MuseScore_General_Sample_Sources.csv` are kept with it) | the General MIDI bank of the `gm` block (`pack:musescore-general`) | the file of 2026-10; its SHA-256 is printed by the fetch script and is **not written here**: record it at the first check | A new release of the SoundFont (new samples, a changed licence list). A new file changes every song that plays it: treat it as a new renderer version. | 2026-11-04 |
| **The free packs** (CC0 sets from the `sfzinstruments` organisation on GitHub): Growlybass, Shinyguitar, The Hat with the Phat, Frankensnare, Swirly Drums, Unruly Drums, Big Rusty Drums, Black and Green Guitars, Meatbass, Osiris Piano, Virtuosity Drums, Black and Blue Basses, Emily Guitar (Karoryfer Samples and others) and **VSCO 2 CE** (`sgossner/VSCO-2-CE`) | `https://github.com/sfzinstruments/<repository>` and `https://github.com/sgossner/VSCO-2-CE`; the exact list, repositories and licence files are in `audio-packs/packs.json` | CC0 1.0, from each repository's own licence file | drum kits, basses, guitars, pianos and orchestral samples named as `pack:free/<pack>/<folder>` | the files of 2026-10 (the repository commits were not recorded) | New commits or a re-licensed set; renamed folders (a song names a folder); a set that changes its samples changes every song that plays it. | 2026-11-04 |
| **The licensed packs** (attribution required): Naked Drums (Wilkinson Audio, CC BY 4.0), Salamander Grand Piano (CC BY 3.0), Headroom Piano (Bengt Nilsson, CC BY 4.0) | `sfzinstruments` on GitHub (see `audio-packs/packs.json`) | CC BY: the credit line has to be published with a song that uses them (`ATTRIBUTION.md` next to the pack) | optional extra sounds | not recorded | A licence change; the credit text. | 2026-11-04 |
| **Real impulse responses** (five, CC0, from Freesound): "Stairwell impulse response wood spring" by wildestshockt (706562), "IR_Two_Car_Garage_HandClap_02" by loganzsound (868385), "Car Interior Impulse Response 2" by jamespeacock4616 (555787), "Little Basement (iR - Impulse Response).wav" by Uzbazur (382908), "Alesis Microverb IR Small 4" by joeribraams (414189) | the Freesound page of each sound; the licence was read from each page on 2026-10-14 | CC0 | the convolution block (`file pack:impulse-responses-real/<name>`, `ir real-<name>`) | the high-quality preview of each sound, converted to 16-bit mono 44.1 kHz | A sound removed or re-licensed on Freesound. | 2026-11-04 |
| **Synthetic impulse responses** (room, hall, plate, church, street) | made by `tools/make-impulse-responses.mjs` from a seeded noise source: ours, no recording | CC0 | the convolution block (`ir hall` and the others) | the script | Nothing: they are deterministic. | 2026-11-04 |
| **The sounds of the Remix demos** | `web/remix/assets/<folder>/SOURCE.md` and `LICENSE.txt` name the source of each folder: pieces of the CC0 packs above (`bass-meat`, `drums-rusty`, `guitar-emily`), a small SoundFont made from VSCO 2 CE samples (`piano-sf2`), and "Light rain on street" by BonnyOrbit (380651) and "Passing Car (Wet road)" by Breviceps (462862), both CC0 from Freesound (`rainy-cars`) | CC0 | the demos of the Remix page | as fetched, cut and converted on the date in each `SOURCE.md` | A folder whose `SOURCE.md` no longer matches its pack. | 2026-11-04 |

## Programs we call (not bundled)

| Program | Used for | Licence | Watch for | Checked |
|---|---|---|---|---|
| **MuseScore** (musescore.org) | reading MuseScore, Guitar Pro, Capella and Overture scores: it exports MusicXML (`<file> -o <file>.musicxml`) | GPL-3.0 (a separate program; AMPLE only starts it) | Changes in its command line or export. | 2026-11-04 |
| **Audiveris** (audiveris.org) | optical music recognition of a PDF or image of printed music, in batch mode | AGPL-3.0 (a separate program; AMPLE only starts it) | Changes in its batch options and output. | 2026-11-04 |
| **ACE-Step 1.5** and **acestep.cpp** (a server for it), the GGUF models of `Serveurperso/ACE-Step-1.5-GGUF` on Hugging Face | the `ace-music` and `ai-voice` blocks through the ACE-Step plug-in | see each project | The server API the plug-in assumes; the model file names. A measured run was made against acestep.cpp `b7ba6d9` (2026-10-22). | 2026-11-04 |
| **Demucs** (`htdemucs`) | separating the voice from the music (optional, in a Python of your own) | MIT | Changes of the model or the command. | 2026-11-04 |
| **faster-whisper**, **Qwen3-TTS**, **XTTS** | the voice-clone plug-in (experimental) | see each project | Versions pinned by the plug-in; see `plugins/voice-clone/`. | 2026-11-04 |

## Libraries (versions at the time)

NuGet packages, named once in `Directory.Packages.props` (this table is taken from it on 2026-11-04; the file is the truth):

| Package | Version | Used for | Referenced by |
|---|---|---|---|
| `Markdig` | 1.4.0 | turning the Markdown pages into the help | `Ample.Docs` |
| `Microsoft.Data.Sqlite` | 10.0.12 | the SQLite database of the takes and the library | `Ample.Storage` |
| `Microsoft.Extensions.Logging.Abstractions` | 8.0.3 | - | `Ample.Core.Abstractions` |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | running the tests | `Ample.Audio.Tests`, `Ample.Cli.Tests`, `Ample.Core.Abstractions.Tests`, `Ample.Core.Tests`, `Ample.Docs.Tests`, `Ample.Engine.Tests`, `Ample.Language.Tests`, `Ample.Lsp.Tests`, `Ample.Plugins.Tests`, `Ample.Server.Tests`, `Ample.Storage.Tests` |
| `Microsoft.Playwright` | 1.63.0 | the assistant's browser and the tests that drive one | `Ample.Server` |
| `NetArchTest.Rules` | 1.3.2 | the tests that keep the layers apart | `Ample.Core.Tests` |
| `NVorbis` | 0.10.5 | decoding the Ogg Vorbis samples of SF3 SoundFonts | `Ample.Audio` |
| `PdfPig` | 0.1.12 | reading PDF files | `Ample.Server` |
| `System.Security.Cryptography.ProtectedData` | 9.0.0 | protecting a secret with the Windows user account | `Ample.Server` |
| `System.CommandLine` | 2.0.12 | the command line of `ample` | `Ample.Cli` |
| `Verify.Xunit` | 31.12.5 | golden-file tests | `Ample.Cli.Tests`, `Ample.Golden` |
| `xunit` | 2.9.3 | the tests | `Ample.Audio.Tests`, `Ample.Cli.Tests`, `Ample.Core.Abstractions.Tests`, `Ample.Core.Tests`, `Ample.Docs.Tests`, `Ample.Engine.Tests`, `Ample.Language.Tests`, `Ample.Lsp.Tests`, `Ample.Plugins.Tests`, `Ample.Server.Tests`, `Ample.Storage.Tests` |
| `xunit.runner.visualstudio` | 3.1.5 | running the tests from Visual Studio and `dotnet test` | `Ample.Audio.Tests`, `Ample.Cli.Tests`, `Ample.Core.Abstractions.Tests`, `Ample.Core.Tests`, `Ample.Docs.Tests`, `Ample.Engine.Tests`, `Ample.Language.Tests`, `Ample.Lsp.Tests`, `Ample.Plugins.Tests`, `Ample.Server.Tests`, `Ample.Storage.Tests` |

npm packages of `package.json` (development only; the lockfile pins every transitive version):

| Package | Version | Used for |
|---|---|---|
| `@codemirror/commands` | 6.11.1 | the highlighter and editor bundle |
| `@codemirror/language` | 6.13.1 | the highlighter and editor bundle |
| `@codemirror/state` | 6.7.6 | the highlighter and editor bundle |
| `@codemirror/view` | 6.43.14 | the highlighter and editor bundle |
| `@lezer/highlight` | 1.2.5 | the highlighter and editor bundle |
| `@playwright/test` | 1.63.0 | the browser tests of the studio |
| `esbuild` | 0.28.2 | building the highlighter bundle (`tools/build-codemirror.mjs`) |
| `vscode-oniguruma` | ^2.0.1 | testing the TextMate grammar |
| `vscode-textmate` | ^9.3.2 | testing the TextMate grammar |

*Watch for:* security advisories (the build prints NU1903 and similar), a new major version of a package whose file format we read or write (Sqlite, NVorbis), and the licence of a new version. The licences of what we ship in the highlighter bundle are in `web/vendor/NOTICE.md`; the notices of the NuGet packages are in `THIRD-PARTY-NOTICES.md`. *Checked:* 2026-11-04 (first list).


## What changes when you take an update

- **A renderer or an effect changes the sound.** The renderers carry a `Version`. Raise it, so that old takes are not taken for new ones, and read `docs/determinism.md` first.
- **A specification or a set we follow changes in a way that makes old songs sound different:** do not change the old behaviour silently. Add it as a new version, and keep the old one for songs that name it.
- **A file we vendor** (the highlighter in `web/vendor/`): rebuild it with `tools/build-codemirror.mjs` from the versions in `package.json`, keep `web/vendor/NOTICE.md` in step with the packages, and test the editor.
- **A package moves up:** change it once, in `Directory.Packages.props` (NuGet) or `package.json` (npm), build with warnings as errors and run every test project.
