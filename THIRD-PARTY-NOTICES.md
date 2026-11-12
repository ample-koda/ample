# Third-party notices

AMPLE is MIT licensed. It uses the following third-party software and sound sets. Each entry gives the copyright line and the licence; the full licence texts are in [LICENSES/](LICENSES/). The short overview with the reason for each dependency is in the [README](README.md#external-dependencies). Versions are the ones in `Directory.Packages.props`, `package.json` and `audio-packs/packs.json`.

## Shipped with AMPLE

| Dependency | Version | Copyright | Licence |
| --- | --- | --- | --- |
| .NET runtime (self-contained release) | .NET 9 | .NET Foundation and Contributors | [MIT](LICENSES/dotnet-runtime.txt); its own third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT |
| Markdig | 1.4.0 | Alexandre Mutel | [BSD-2-Clause](LICENSES/markdig.txt) |
| Microsoft.Data.Sqlite | 10.0.12 | Microsoft Corporation | [MIT](LICENSES/dotnet-runtime.txt) |
| SQLitePCLRaw | 2.1.12 | 2014-2024 SourceGear, LLC | [Apache-2.0](LICENSES/Apache-2.0.txt) |
| SQLite | bundled by SQLitePCLRaw | none: public domain | https://www.sqlite.org/copyright.html |
| NVorbis | 0.10.5 | 2020 Andrew Ward | [MIT](LICENSES/nvorbis.txt) |
| spessasynth_core (vendored in `web/vendor/spessasynth/`) | 4.4.10 | spessasus | [Apache-2.0](LICENSES/Apache-2.0.txt) |
| stb-vorbis (vendored, with spessasynth_core) | 0.0.7 | spessasus | [Apache-2.0](LICENSES/Apache-2.0.txt) |
| fflate (vendored, with spessasynth_core) | 0.8.3 | 2026 Arjun Barrett | [MIT](LICENSES/fflate.txt) |
| PdfPig | 0.1.12 | UglyToad and contributors | [Apache-2.0](LICENSES/Apache-2.0.txt) |
| System.CommandLine | 2.0.12 | Microsoft Corporation | [MIT](LICENSES/dotnet-runtime.txt) |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | .NET Foundation and Contributors | [MIT](LICENSES/dotnet-runtime.txt) |
| System.Security.Cryptography.ProtectedData | 9.0.0 | .NET Foundation and Contributors | [MIT](LICENSES/dotnet-runtime.txt) |
| Microsoft.Playwright (.NET) | 1.63.0 | Microsoft Corporation | [MIT](LICENSES/playwright-dotnet.txt) |
| Playwright driver (`koda-browser` pack) | 1.63.0 | Microsoft Corporation | [Apache-2.0](LICENSES/Apache-2.0.txt), [NOTICE](LICENSES/playwright-NOTICE.txt); the driver's own list of third-party licences is `ThirdPartyNotices.txt` inside the pack |
| Node.js (inside the `koda-browser` pack) | as shipped by the Playwright driver | Node.js contributors | [MIT, with bundled components](LICENSES/nodejs.txt) |
| @codemirror/state 6.7.6, view 6.43.14, language 6.13.1, commands 6.11.1 | bundled in `web/vendor/codemirror-ample.js` | 2018-2021 Marijn Haverbeke and others | [MIT](LICENSES/codemirror.txt) |
| @lezer/common 1.5.5, highlight 1.2.5, lr 1.4.12 | bundled | 2018 Marijn Haverbeke and others | [MIT](LICENSES/lezer.txt) |
| style-mod | 4.1.4, bundled | 2018 Marijn Haverbeke and others | [MIT](LICENSES/style-mod.txt) |
| w3c-keyname | 2.2.8, bundled | 2016 Marijn Haverbeke and others | [MIT](LICENSES/w3c-keyname.txt) |
| crelt | 1.0.7, bundled | 2020 Marijn Haverbeke | [MIT](LICENSES/crelt.txt) |
| vscode-languageclient | ^9.0.1 (VS Code extension) | Microsoft Corporation | [MIT](LICENSES/vscode-languageclient.txt) |
| WixToolset.Dtf.CustomAction | 6.0.2 (Windows installer) | .NET Foundation and contributors | [WiX Open Source Maintenance Fee EULA](LICENSES/wix-osmfeula.txt) |

## Sounds, models and programs

| Dependency | Copyright | Licence |
| --- | --- | --- |
| Karoryfer Samples packs (Growlybass, Shinyguitar, The Hat with the Phat, Frankensnare, Swirly Drums, Unruly Drums, Big Rusty Drums, Black and Green Guitars, Meatbass, Black and Blue Basses, Emily Guitar), Osiris Piano, Virtuosity Drums, VSCO 2 Community Edition | their authors, see `audio-packs/packs.json` | [CC0-1.0](LICENSES/CC0-1.0.txt) |
| Naked Drums | Wilkinson Audio | [CC-BY-4.0](LICENSES/CC-BY-4.0.txt): credit required |
| Headroom Piano | Bengt Nilsson | [CC-BY-4.0](LICENSES/CC-BY-4.0.txt): credit required |
| Salamander Grand Piano V3 | Alexander Holm | [CC-BY-3.0](LICENSES/CC-BY-3.0.txt): credit required |
| MuseScore General SoundFont | S. Christian Collins; FluidR3 by Frank Wen, FluidR3Mono by Michael Cowgill; Temple Blocks by Ethan Winer; Drumline Cymbals by Michael Schorsch | [MIT](LICENSES/musescore-general.txt) |
| Freesound recordings used for impulse responses and the Remix demos | their authors, see `web/remix/assets/*/SOURCE.md` and `docs/upstream-sources.md` | [CC0-1.0](LICENSES/CC0-1.0.txt) |
| ACE-Step 1.5 and the GGUF models, acestep.cpp (optional, separate server) | their authors | MIT |
| Demucs, faster-whisper, Chatterbox (optional, in your own Python) | their authors | MIT |
| Qwen3-TTS (optional) | Alibaba Cloud / Qwen team | Apache-2.0 |
| XTTS-v2 (optional; runs only after the user accepts the licence in the settings, never chosen by default) | Coqui | Coqui Public Model License (non-commercial) |
| FFmpeg, MuseScore, Audiveris (optional programs AMPLE starts; not distributed with it) | their authors | LGPL/GPL (depends on the build), GPL-3.0, AGPL-3.0 |
| MonkSynth (read as a reference for the Delay Llama, no code copied) | 2026 Jonathan Taylor | MIT, see [docs/third-party-credits.md](docs/third-party-credits.md) |

The optional models and programs are not part of an AMPLE release: you install them yourself and their licences apply to your use of them.

## Build and development tools (not shipped)

| Dependency | Version | Copyright | Licence |
| --- | --- | --- | --- |
| xunit, xunit.runner.visualstudio | 2.9.3, 3.1.5 | .NET Foundation | [Apache-2.0](LICENSES/Apache-2.0.txt) |
| Microsoft.NET.Test.Sdk | 17.14.1 | Microsoft Corporation | [MIT](LICENSES/dotnet-runtime.txt) |
| Verify.Xunit | 31.12.5 | .NET Foundation and Contributors | [MIT](LICENSES/verify.txt) |
| NetArchTest.Rules | 1.3.2 | 2018 Ben Morris | [MIT](LICENSES/netarchtest.txt) |
| @playwright/test | 1.63.0 | Microsoft Corporation | [Apache-2.0](LICENSES/Apache-2.0.txt) |
| esbuild | 0.28.2 | 2020 Evan Wallace | [MIT](LICENSES/esbuild.txt) |
| vscode-textmate | 9.3.2 | Microsoft Corporation | [MIT](LICENSES/vscode-textmate.txt) |
| vscode-oniguruma | 2.0.1 | Microsoft Corporation | [MIT](LICENSES/vscode-oniguruma.txt) |
| Microsoft.NETFramework.ReferenceAssemblies | 1.0.3 | Microsoft Corporation | [MIT](LICENSES/microsoft-netframework-referenceassemblies.txt) |
| WiX Toolset | 4.0.6 | .NET Foundation and contributors | [MS-RL](LICENSES/wix-ms-rl.txt) |
