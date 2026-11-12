# AMPLE

**Write music as code. Build it, hear it, change it, keep it.**

AMPLE (Assisted Music Programming Language Experience) is a language and a studio for songs
that you can read, version and build again. A song is a text file; the same text builds the same sound.

> **Status: early.** This repository is being filled step by step. Every part that appears here has
> been reviewed by a person first, so for now it holds only a small part of the project.
> There is no release yet and nothing here is ready to use.

## What it is

- **A language for songs.** Blocks make or change sound, and lines say what feeds what.
- **A studio.** The same song as text, as blocks, on a timeline and on a piano roll.
- **Reproducible by default.** A song you like can always be returned to.

## External dependencies

AMPLE is MIT licensed. It builds on the open-source software and sound sets below; the full texts are in [LICENSES/](LICENSES/) and the copyright lines are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). "Since" is the day it was first added to the project; everything dated before 2026-11-05 has been part of every release since 0.1.0.

### Runtime (shipped with AMPLE)

| Dependency | What and why | Licence | Since |
| --- | --- | --- | --- |
| [.NET runtime](https://github.com/dotnet/runtime) | Runs AMPLE; the release is self-contained. | [MIT](LICENSES/dotnet-runtime.txt) | 2026-10-03 |
| [Markdig](https://github.com/xoofx/markdig) | Turns the Markdown pages into the help. | [BSD-2-Clause](LICENSES/markdig.txt) | 2026-10-02 |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore), [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw), [SQLite](https://www.sqlite.org/copyright.html) | The database of takes and library. | [MIT](LICENSES/dotnet-runtime.txt), [Apache-2.0](LICENSES/Apache-2.0.txt), public domain | 2026-10-02 |
| [NVorbis](https://github.com/NVorbis/NVorbis) | Decodes the Ogg Vorbis samples of SF3 SoundFonts. | [MIT](LICENSES/nvorbis.txt) | 2026-11-03 |
| [PdfPig](https://github.com/UglyToad/PdfPig) | Reads PDF files. | [Apache-2.0](LICENSES/Apache-2.0.txt) | 2026-10-10 |
| [System.CommandLine](https://github.com/dotnet/command-line-api) | The command line of `ample`. | [MIT](LICENSES/dotnet-runtime.txt) | 2026-10-02 |
| [Microsoft.Extensions.Logging.Abstractions](https://github.com/dotnet/runtime) | Logging interfaces. | [MIT](LICENSES/dotnet-runtime.txt) | 2026-10-02 |
| [System.Security.Cryptography.ProtectedData](https://github.com/dotnet/runtime) | Protects a secret with the Windows user account. | [MIT](LICENSES/dotnet-runtime.txt) | 2026-10-17 |
| [Playwright for .NET](https://github.com/microsoft/playwright-dotnet) | The assistant's browser. | [MIT](LICENSES/playwright-dotnet.txt) | 2026-10-17 |
| [Playwright driver](https://github.com/microsoft/playwright) and [Node.js](https://nodejs.org) | The optional `koda-browser` pack. | [Apache-2.0](LICENSES/Apache-2.0.txt) ([NOTICE](LICENSES/playwright-NOTICE.txt)), [MIT](LICENSES/nodejs.txt) | 2026-11-05 |
| [CodeMirror 6](https://codemirror.net) and [Lezer](https://lezer.codemirror.net) (state, view, language, commands, common, highlight, lr, style-mod, w3c-keyname, crelt) | The source editor and highlighter, bundled in `web/vendor/`. | MIT: [CodeMirror](LICENSES/codemirror.txt), [Lezer](LICENSES/lezer.txt), [style-mod](LICENSES/style-mod.txt), [w3c-keyname](LICENSES/w3c-keyname.txt), [crelt](LICENSES/crelt.txt) | 2026-10-10 |
| [vscode-languageclient](https://github.com/microsoft/vscode-languageserver-node) | The language client of the VS Code extension. | [MIT](LICENSES/vscode-languageclient.txt) | 2026-10-03 |
| [WiX Toolset DTF](https://github.com/wixtoolset/wix) | The custom action of the Windows installer (MSI). | [WiX OSMF EULA](LICENSES/wix-osmfeula.txt) (to verify) | 2026-11-05 |

### Sounds and models

| Dependency | What and why | Licence | Since |
| --- | --- | --- | --- |
| Free sample packs: Karoryfer Samples, Osiris Piano, Virtuosity Drums, [VSCO 2 CE](https://github.com/sgossner/VSCO-2-CE) and others from [sfzinstruments](https://github.com/sfzinstruments) | Drums, basses, guitars, pianos and orchestra; fetched with `ample packs install`. | [CC0-1.0](LICENSES/CC0-1.0.txt) | 2026-10-03 |
| Naked Drums (Wilkinson Audio), Headroom Piano (Bengt Nilsson) | Optional drums and piano; credit required. | [CC-BY-4.0](LICENSES/CC-BY-4.0.txt) | 2026-10-03 |
| [Salamander Grand Piano](https://archive.org/details/SalamanderGrandPianoV3) (Alexander Holm) | Optional piano; credit required. | [CC-BY-3.0](LICENSES/CC-BY-3.0.txt) | 2026-10-03 |
| [MuseScore General](https://musescore.org) SoundFont | The General MIDI bank; fetched on request. | [MIT](LICENSES/musescore-general.txt) | 2026-10-04 |
| Freesound recordings (impulse responses, rain and car sounds) | Convolution reverb and the Remix demos. | [CC0-1.0](LICENSES/CC0-1.0.txt) | 2026-10-14 |
| [ACE-Step 1.5](https://github.com/ace-step/ACE-Step-1.5), [acestep.cpp](https://github.com/ServeurpersoCom/acestep.cpp) | Optional music and voice generation, run as a separate server; not included. | MIT | 2026-10-03 |
| [Demucs](https://github.com/facebookresearch/demucs), [faster-whisper](https://github.com/SYSTRAN/faster-whisper), [Chatterbox](https://github.com/resemble-ai/chatterbox) | Optional voice separation, transcription and voice synthesis in your own Python; not included. | MIT | 2026-10-03 |
| [Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS) | Optional voice synthesis; not included. | Apache-2.0 | 2026-10-10 |
| [XTTS-v2](https://huggingface.co/coqui/XTTS-v2) (Coqui) | Optional voice synthesis; not included. Non-commercial. | Coqui Public Model License | 2026-10-10 |
| [FFmpeg](https://ffmpeg.org), [MuseScore](https://musescore.org), [Audiveris](https://audiveris.org) | Optional programs that AMPLE starts for mp3/flac, score import and music recognition; not included. | LGPL/GPL, GPL-3.0, AGPL-3.0 | 2026-10-03 |

### Build and development tools (not shipped)

| Dependency | What and why | Licence | Since |
| --- | --- | --- | --- |
| [xUnit](https://github.com/xunit/xunit) | Unit tests. | [Apache-2.0](LICENSES/Apache-2.0.txt) | 2026-10-02 |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | Test host. | [MIT](LICENSES/dotnet-runtime.txt) | 2026-10-02 |
| [Verify](https://github.com/VerifyTests/Verify) | Golden-file tests. | [MIT](LICENSES/verify.txt) | 2026-10-02 |
| [NetArchTest.Rules](https://github.com/BenMorris/NetArchTest) | Tests that keep the layers apart. | [MIT](LICENSES/netarchtest.txt) | 2026-10-02 |
| [Playwright Test](https://github.com/microsoft/playwright) | Browser tests of the studio. | [Apache-2.0](LICENSES/Apache-2.0.txt) | 2026-10-03 |
| [esbuild](https://github.com/evanw/esbuild) | Builds the editor bundle. | [MIT](LICENSES/esbuild.txt) | 2026-10-10 |
| [vscode-textmate](https://github.com/microsoft/vscode-textmate), [vscode-oniguruma](https://github.com/microsoft/vscode-oniguruma) | Tests of the editor grammar. | MIT: [textmate](LICENSES/vscode-textmate.txt), [oniguruma](LICENSES/vscode-oniguruma.txt) | 2026-10-03 |
| [WiX Toolset 4.0.6](https://github.com/wixtoolset/wix) | Builds the Windows installer. | MS-RL | 2026-11-05 |

The sounds and texts of the documentation site are [CC0](docs-site/LICENSE). All dependency licences are listed with their copyright lines in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

The code is under the MIT License.

The names AMPLE and Koda, and their logos, are not part of that license: please do not use them
to name a product that is derived from this project.

## Contributing

Not yet. See CONTRIBUTING.md.
