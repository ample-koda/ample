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

## Build and debug

**What you need**

- The **.NET SDK 9.0.317 or newer in the 9.0.3xx line** (`global.json` names it). **Visual Studio 2022 version 17.14 or later**, or Visual Studio 2026, brings it; with an older Visual Studio, install the .NET 9 SDK yourself and Visual Studio will use it.
- In Visual Studio, the **ASP.NET and web development** workload (the server uses ASP.NET Core).
- To run the browser tests of the studio: Node.js, then `npm install` and `npx playwright install chromium`.

**In Visual Studio on Windows**

1. Open `Ample.sln`. It holds every project and every test project.
2. Set `Ample.Cli` as the startup project. Its launch profiles start the studio (`ample serve`), Remix (`ample serve --lite-only`, on port 5080) and the help (`ample --help`); press F5 to debug one.
3. Run the tests from **Test Explorer** (xUnit). The server tests drive a browser: after the first build, run `pwsh tests/Ample.Server.Tests/bin/Debug/net9.0/playwright.ps1 install chromium` once.

**From the command line**

```
dotnet build Ample.sln
dotnet test tests/Ample.Language.Tests
dotnet run --project src/Ample.Cli -- serve
```

The browser tests of the studio run with `npm run test:web` once the command line tool is built.

## License

The code is under the [MIT License](LICENSE).

The names AMPLE and Koda, and their logos, are not part of that license: please do not use them
to name a product that is derived from this project.

## Contributing

Not yet. See [CONTRIBUTING.md](CONTRIBUTING.md).
